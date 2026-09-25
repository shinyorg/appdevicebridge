using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Everything the maps and directions bridges share: the regions on the device, the tiles and assets served from them
/// or from online, the catalog, downloads, and the routers that compute directions.
/// </summary>
sealed class MapsService : IDisposable
{
    internal const string DownloadEvent = "maps.download";

    readonly MapsOptions options;
    readonly ILogger logger;
    readonly HttpClient http;
    readonly ECDsa? catalogKey;
    readonly IOnDeviceRouterFactory? onDevice;
    readonly WebAppEventSource<MapPackDownload> downloadEvents;

    readonly Lock gate = new();
    readonly Dictionary<string, PmTilesArchive> archives = [];
    readonly Dictionary<string, IValhallaRouter> routers = [];
    readonly ConcurrentDictionary<string, DownloadJob> downloads = new();
    IReadOnlyList<InstalledRegion>? installed;

    readonly SemaphoreSlim onlineGate = new(1, 1);
    PmTilesArchive? onlineArchive;
    DateTimeOffset onlineRetryAt;

    (MapPackCatalog Catalog, DateTimeOffset FetchedAt)? catalog;

    // Traffic tiles by position, each kept for the layer's refresh interval. A map on screen asks for a few dozen.
    const int TrafficCacheLimit = 512;
    readonly ConcurrentDictionary<(int Z, int X, int Y), (TrafficTile? Tile, DateTimeOffset Expires)> trafficTiles = new();
    readonly SemaphoreSlim catalogGate = new(1, 1);

    long tileCacheBytes = -1;
    readonly Lock tileCacheGate = new();

    public MapsService(
        MapsOptions options,
        AppDeviceBridgeOptions hostOptions,
        WebAppEventHub events,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null
    )
    {
        // Checked here rather than in AddMapsBridge, which can be called again after the catalog is set.
        if (options.Catalog is not null && String.IsNullOrWhiteSpace(options.CatalogPublicKey))
            throw new InvalidOperationException("MapsOptions.CatalogPublicKey is required with a Catalog: regions are only installed when their signature checks out.");

        this.options = options;
        this.logger = (ILogger?)loggerFactory?.CreateLogger<MapsService>() ?? NullLogger.Instance;
        // HttpClientHandler, not SocketsHttpHandler: on iOS and Mac Catalyst it is NSURLSession, which speaks TLS 1.3. The
        // managed handler's TLS on Apple platforms stops at 1.2, and servers that only take 1.3 — FOSSGIS's public Valhalla,
        // for one — refuse it. macOS has no such redirect; a macOS app passes an NSUrlSessionHandler in the options.
        this.http = new HttpClient(options.HttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        this.onDevice = services.GetOptionalService<IOnDeviceRouterFactory>();
        this.downloadEvents = events.Source(DownloadEvent, MapsJsonContext.Default.MapPackDownload);
        this.Storage = new MapStorage(
            options.Directory ?? Path.Combine(hostOptions.ResolveDataDirectory(), "maps"),
            options.CacheDirectory ?? Path.Combine(Path.GetTempPath(), "appdevicebridge", hostOptions.AppId + "-maps")
        );

        if (!String.IsNullOrWhiteSpace(options.CatalogPublicKey))
            this.catalogKey = WebAppReleaseSignature.ImportPublicKey(options.CatalogPublicKey);

        this.OnlineRouter = options.Directions.OnlineRouteUrl is null ? null : new ValhallaHttpRouter(this.http, options.Directions);
    }

    public MapStorage Storage { get; }

    public MapsOptions Options => this.options;

    public bool OnDeviceDirections => this.onDevice is not null;

    public IValhallaRouter? OnlineRouter { get; }

    public IReadOnlyList<InstalledRegion> Installed
    {
        get
        {
            lock (this.gate)
                return this.installed ??= this.Storage.ReadRegions();
        }
    }

    /// <summary>Reads the installed regions from disk again, for a region placed there by something other than a download.</summary>
    internal void ReloadInstalled()
    {
        lock (this.gate)
            this.installed = null;
    }

    // ---------------------------------------------------------------- tiles

    /// <summary>A tile from the first installed region that has it, the tile cache, or online — or null when none does.</summary>
    public async Task<MapTile?> GetTileAsync(int z, int x, int y, CancellationToken cancellationToken)
    {
        if (!PmTilesArchive.IsValidTile(z, x, y))
            return null;

        foreach (var region in this.Installed)
        {
            if (region.MapVersion is null || this.GetArchive(region.Id) is not { } archive || !archive.Covers(z, x, y))
                continue;

            try
            {
                if (await archive.GetTileAsync(z, x, y, cancellationToken).ConfigureAwait(false) is { } tile)
                    return tile;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                this.logger.LogWarning(ex, "Region {Region} could not be read", region.Id);
            }
        }

        if (this.ReadCachedTile(z, x, y) is { } cached)
            return cached;

        if (this.options.OnlineTiles is null || z > this.options.OnlineMaxZoom)
            return null;

        try
        {
            var online = await this.GetOnlineTileAsync(z, x, y, cancellationToken).ConfigureAwait(false);
            if (online is not null)
                this.WriteCachedTile(z, x, y, online);

            return online;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Offline, or the source is down: the page gets a gap rather than an error for every tile.
            this.logger.LogDebug(ex, "Online tile {Z}/{X}/{Y} unavailable", z, x, y);
            return null;
        }
    }

    PmTilesArchive? GetArchive(string regionId)
    {
        lock (this.gate)
        {
            if (this.archives.TryGetValue(regionId, out var archive))
                return archive;
        }

        var path = this.Storage.MapFile(regionId);
        if (!File.Exists(path))
            return null;

        try
        {
            // Local and small: the header read is one syscall, so opening synchronously here is fine.
            var opened = PmTilesArchive.OpenFileAsync(path).GetAwaiter().GetResult();

            lock (this.gate)
            {
                if (this.archives.TryGetValue(regionId, out var raced))
                {
                    opened.Dispose();
                    return raced;
                }

                this.archives[regionId] = opened;
                return opened;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            this.logger.LogWarning(ex, "Region {Region}'s map could not be opened", regionId);
            return null;
        }
    }

    async Task<MapTile?> GetOnlineTileAsync(int z, int x, int y, CancellationToken cancellationToken)
    {
        var source = this.options.OnlineTiles!;

        if (source.Contains("{z}", StringComparison.Ordinal))
        {
            var url = source
                .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            this.options.ConfigureRequest?.Invoke(request);

            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var gzip = response.Content.Headers.ContentEncoding.Contains("gzip") || IsGzip(data);
            return new MapTile(data, gzip ? PmTilesCompression.Gzip : PmTilesCompression.None);
        }

        var archive = await this.GetOnlineArchiveAsync(cancellationToken).ConfigureAwait(false);
        return archive is null ? null : await archive.GetTileAsync(z, x, y, cancellationToken).ConfigureAwait(false);
    }

    async Task<PmTilesArchive?> GetOnlineArchiveAsync(CancellationToken cancellationToken)
    {
        if (this.onlineArchive is { } open)
            return open;

        await this.onlineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.onlineArchive is { } raced)
                return raced;

            // Offline, every tile would try the header again; once a minute is enough to notice the network is back.
            if (DateTimeOffset.UtcNow < this.onlineRetryAt)
                return null;

            try
            {
                this.onlineArchive = await PmTilesArchive
                    .OpenAsync(new HttpRangeSource(this.http, new Uri(this.options.OnlineTiles!), this.options.ConfigureRequest), cancellationToken)
                    .ConfigureAwait(false);
                return this.onlineArchive;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                this.onlineRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
                throw;
            }
        }
        finally
        {
            this.onlineGate.Release();
        }
    }

    static bool IsGzip(byte[] data) => data is [0x1F, 0x8B, ..];

    string CachedTilePath(int z, int x, int y) => Path.Combine(this.Storage.TileCache, "tiles", $"{z}", $"{x}", $"{y}");

    MapTile? ReadCachedTile(int z, int x, int y)
    {
        if (this.options.TileCacheBytes <= 0)
            return null;

        var path = this.CachedTilePath(z, x, y);
        try
        {
            if (!File.Exists(path))
                return null;

            var data = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new MapTile(data, IsGzip(data) ? PmTilesCompression.Gzip : PmTilesCompression.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    void WriteCachedTile(int z, int x, int y, MapTile tile)
    {
        if (this.options.TileCacheBytes <= 0)
            return;

        try
        {
            var path = this.CachedTilePath(z, x, y);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Stored as it came, compressed or not; the first two bytes say which when it is read back.
            MapStorage.WriteAtomically(path, tile.Data);

            lock (this.tileCacheGate)
            {
                if (this.tileCacheBytes < 0)
                    this.tileCacheBytes = this.MeasureTileCache().Sum(x => x.Length);

                this.tileCacheBytes += tile.Data.Length;
                if (this.tileCacheBytes > this.options.TileCacheBytes)
                    this.TrimTileCache();
            }
        }
        catch (IOException ex)
        {
            this.logger.LogDebug(ex, "Tile {Z}/{X}/{Y} not cached", z, x, y);
        }
    }

    IEnumerable<FileInfo> MeasureTileCache()
    {
        var root = new DirectoryInfo(Path.Combine(this.Storage.TileCache, "tiles"));
        return root.Exists ? root.EnumerateFiles("*", SearchOption.AllDirectories) : [];
    }

    /// <summary>Drops the least recently used tiles until the cache is three quarters of its limit, so trimming is rare.</summary>
    void TrimTileCache()
    {
        var files = this.MeasureTileCache().OrderBy(x => x.LastWriteTimeUtc).ToList();
        var total = files.Sum(x => x.Length);
        var target = this.options.TileCacheBytes * 3 / 4;

        foreach (var file in files)
        {
            if (total <= target)
                break;

            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch (IOException)
            {
            }
        }

        this.tileCacheBytes = total;
    }

    // ---------------------------------------------------------------- glyphs and sprites

    /// <summary>A glyph range or sprite file, relative to an asset directory: <c>fonts/Noto Sans Regular/0-255.pbf</c>, <c>sprites/v4/light.json</c>.</summary>
    public async Task<byte[]?> GetAssetAsync(string relativePath, CancellationToken cancellationToken)
    {
        foreach (var root in new[] { this.Storage.Assets, this.Storage.FetchedAssets })
        {
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (this.options.OnlineAssets is not { } online)
            return null;

        try
        {
            // '@' is legal in a path, and sprite names have one (light@2x.png); escaping it relies on the server decoding it.
            var url = new Uri(online, string.Join('/', relativePath.Split('/').Select(x => Uri.EscapeDataString(x).Replace("%40", "@", StringComparison.Ordinal))));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            this.options.ConfigureRequest?.Invoke(request);

            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Kept for good: a glyph range seen once is needed again the next time the map draws, online or not.
            var saved = Path.Combine(this.Storage.FetchedAssets, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            MapStorage.WriteAtomically(saved, data);
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            this.logger.LogDebug(ex, "Asset {Asset} unavailable", relativePath);
            return null;
        }
    }

    // ---------------------------------------------------------------- catalog

    /// <summary>The catalog, fetched when stale or asked, the last copy otherwise. Parts whose signature does not check out are dropped.</summary>
    public async Task<(MapPackCatalog? Catalog, bool Reachable)> GetCatalogAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (this.options.Catalog is null || this.catalogKey is null)
            return (null, false);

        if (!refresh && this.catalog is { } fresh && DateTimeOffset.UtcNow - fresh.FetchedAt < this.options.CatalogLifetime)
            return (fresh.Catalog, true);

        await this.catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, this.options.Catalog);
                this.options.ConfigureRequest?.Invoke(request);
                using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize(bytes, MapPackJsonContext.Default.MapPackCatalog)
                             ?? throw new InvalidDataException("The catalog is empty.");

                var verified = this.Verify(parsed);
                this.catalog = (verified, DateTimeOffset.UtcNow);
                MapStorage.WriteAtomically(this.Storage.CatalogFile, bytes);
                return (verified, true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                this.logger.LogInformation(ex, "Map catalog {Catalog} unavailable; using the last copy", this.options.Catalog);
            }

            if (this.catalog is { } last)
                return (last.Catalog, false);

            try
            {
                if (File.Exists(this.Storage.CatalogFile)
                    && JsonSerializer.Deserialize(File.ReadAllBytes(this.Storage.CatalogFile), MapPackJsonContext.Default.MapPackCatalog) is { } saved)
                {
                    var verified = this.Verify(saved);
                    this.catalog = (verified, DateTimeOffset.MinValue);
                    return (verified, false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }

            return (null, false);
        }
        finally
        {
            this.catalogGate.Release();
        }
    }

    MapPackCatalog Verify(MapPackCatalog catalog)
    {
        var key = this.catalogKey!;
        var regions = new List<MapPackRegion>();

        foreach (var region in catalog.Regions)
        {
            if (!MapPackSignature.IsValidRegionId(region.Id) || !MapPackSignature.IsValidBounds(region.Bounds))
                continue;

            if (!MapPackSignature.Verify(region.Id, region.Bounds, region.Map, key) || region.Map.Kind != MapPackPartKind.Map)
            {
                this.logger.LogWarning("Map region {Region} is not signed by the catalog key; ignored", region.Id);
                continue;
            }

            var directions = region.Directions is { Kind: MapPackPartKind.Directions } d && MapPackSignature.Verify(region.Id, region.Bounds, d, key) ? d : null;
            regions.Add(region with { Directions = directions });
        }

        var assets = catalog.Assets is { Kind: MapPackPartKind.Assets } a && MapPackSignature.Verify(MapPackSignature.AssetsRegionId, null, a, key) ? a : null;
        return catalog with { Regions = regions, Assets = assets };
    }

    // ---------------------------------------------------------------- downloads

    public MapPackDownload? GetDownload(string regionId)
        => this.downloads.TryGetValue(regionId, out var job) ? job.Snapshot() : null;

    /// <summary>Starts a region's download. Null when the region is not in the catalog.</summary>
    public async Task<(MapPackDownload? Download, bool AlreadyRunning)> InstallAsync(string regionId, bool withDirections, CancellationToken cancellationToken)
    {
        var (catalog, _) = await this.GetCatalogAsync(false, cancellationToken).ConfigureAwait(false);
        if (catalog?.Regions.FirstOrDefault(x => x.Id == regionId) is not { } region)
            return (null, false);

        if (this.downloads.TryGetValue(regionId, out var running))
            return (running.Snapshot(), true);

        var current = this.Installed.FirstOrDefault(x => x.Id == regionId);
        var keepDirections = withDirections || current?.DirectionsVersion is not null;
        var parts = new List<MapPackPart>();

        if (current?.MapVersion != region.Map.Version)
            parts.Add(region.Map);

        if (keepDirections && region.Directions is { } directions && current?.DirectionsVersion != directions.Version)
            parts.Add(directions);

        if (catalog.Assets is { } assets && this.Storage.ReadAssets()?.Version != assets.Version)
            parts.Add(assets);

        var job = new DownloadJob(region, withDirections && region.Directions is not null, parts);
        if (!this.downloads.TryAdd(regionId, job))
            return (this.downloads[regionId].Snapshot(), true);

        _ = Task.Run(() => this.RunAsync(job));
        return (job.Snapshot(), false);
    }

    public bool CancelDownload(string regionId)
    {
        if (!this.downloads.TryGetValue(regionId, out var job))
            return false;

        job.Cancellation.Cancel();
        return true;
    }

    async Task RunAsync(DownloadJob job)
    {
        var token = job.Cancellation.Token;
        try
        {
            this.Publish(job, MapPackDownloadState.Downloading);

            foreach (var part in job.Parts)
            {
                var file = await this.DownloadPartAsync(job, part, token).ConfigureAwait(false);

                this.Publish(job, MapPackDownloadState.Verifying);
                await VerifyAsync(file, part, token).ConfigureAwait(false);
                this.InstallPart(job.Region, part, file);
                job.Completed += part.Size;
                job.Current = 0;
            }

            // A region with nothing new still has its record brought up to date with the catalog's name and bounds.
            this.RecordRegion(job.Region, null);
            this.Publish(job, MapPackDownloadState.Installed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            this.Publish(job, MapPackDownloadState.Cancelled);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Map region {Region} failed to download", job.Region.Id);
            this.Publish(job, MapPackDownloadState.Failed, ex is HttpRequestException or IOException or InvalidDataException or TimeoutException ? ex.Message : "The download failed.");
        }
        finally
        {
            this.downloads.TryRemove(job.Region.Id, out _);
            job.Cancellation.Dispose();
        }
    }

    async Task<string> DownloadPartAsync(DownloadJob job, MapPackPart part, CancellationToken cancellationToken)
    {
        // Named by content, so a partial file is only ever resumed into the same bytes it started as.
        var file = Path.Combine(this.Storage.Downloads, $"{job.Region.Id}.{part.Kind.ToString().ToLowerInvariant()}.{part.Sha256[..16]}");

        // Counts attempts in a row that got nowhere: a connection that keeps dropping but moves forward each time finishes.
        var fruitless = 0;
        while (true)
        {
            var before = File.Exists(file) ? new FileInfo(file).Length : 0;
            try
            {
                await this.DownloadRemainderAsync(job, part, file, cancellationToken).ConfigureAwait(false);
                return file;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is TimeoutException or HttpRequestException or IOException or ObjectDisposedException)
            {
                fruitless = (File.Exists(file) ? new FileInfo(file).Length : 0) > before ? 0 : fruitless + 1;
                if (fruitless >= this.options.DownloadAttempts)
                    throw new IOException($"The {part.Kind} download made no progress in {fruitless} attempts: {ex.Message}", ex);

                // A stalled or dropped connection: pick up from what reached the disk. The file is only complete once
                // its size and signed hash check out, so a resume can never smuggle in bytes from elsewhere.
                this.logger.LogInformation(ex, "Region {Region}'s {Part} download was interrupted; resuming", job.Region.Id, part.Kind);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 + fruitless * 2)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    async Task DownloadRemainderAsync(DownloadJob job, MapPackPart part, string file, CancellationToken cancellationToken)
    {
        var existing = File.Exists(file) ? new FileInfo(file).Length : 0;
        if (existing == part.Size)
            return;

        if (existing > part.Size)
        {
            File.Delete(file);
            existing = 0;
        }

        var url = new Uri(this.options.Catalog!, part.Url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);
        this.options.ConfigureRequest?.Invoke(request);

        HttpResponseMessage response;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // A server that accepts the connection and never answers is as stuck as one that stops mid-file.
            headers.CancelAfter(this.options.DownloadStallTimeout);
            try
            {
                response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer for {this.options.DownloadStallTimeout.TotalSeconds:0} seconds.");
            }
        }

        using var _ = response;
        response.EnsureSuccessStatusCode();

        // A server that ignores the Range header sends it all again from the start.
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
            existing = 0;

        job.Current = existing;
        await using var output = new FileStream(file, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81920];
        var lastReport = DateTimeOffset.MinValue;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Some handlers' streams — Android's, reading through Java — ignore the token while a read blocks. Disposing the
        // response closes the connection under the read, which ends it on every platform.
        using var abort = stall.Token.Register(response.Dispose);

        while (true)
        {
            // A connection that goes quiet — a phone between networks, a proxy that stopped forwarding — never ends on its
            // own. No bytes for this long, and it is given up on and resumed.
            stall.CancelAfter(this.options.DownloadStallTimeout);

            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                                       && ex is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
            {
                throw new TimeoutException($"No data for {this.options.DownloadStallTimeout.TotalSeconds:0} seconds.");
            }

            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            job.Current += read;

            if (job.Current > part.Size)
                throw new InvalidDataException($"The {part.Kind} download is larger than the catalog says.");

            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(500))
            {
                lastReport = DateTimeOffset.UtcNow;
                this.Publish(job, MapPackDownloadState.Downloading);
            }
        }

        if (job.Current < part.Size)
            throw new IOException($"The {part.Kind} download ended {part.Size - job.Current} bytes short.");
    }

    static async Task VerifyAsync(string file, MapPackPart part, CancellationToken cancellationToken)
    {
        var length = new FileInfo(file).Length;
        if (length != part.Size)
        {
            File.Delete(file);
            throw new InvalidDataException($"The {part.Kind} download is {length} bytes; the catalog says {part.Size}.");
        }

        string hash;
        await using (var stream = File.OpenRead(file))
            hash = await WebAppReleaseSignature.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);

        if (!String.Equals(hash, part.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidDataException($"The {part.Kind} download does not match the catalog's signed hash.");
        }
    }

    void InstallPart(MapPackRegion region, MapPackPart part, string file)
    {
        switch (part.Kind)
        {
            case MapPackPartKind.Map:
                this.CloseArchive(region.Id);
                Directory.CreateDirectory(this.Storage.RegionDirectory(region.Id));
                File.Move(file, this.Storage.MapFile(region.Id), true);
                MapStorage.ExcludeFromBackup(this.Storage.MapFile(region.Id));
                this.RecordRegion(region, r => r with { MapVersion = part.Version, MapSize = part.Size, MaxZoom = region.MaxZoom });
                break;

            case MapPackPartKind.Directions:
                this.CloseRouter(region.Id);
                Directory.CreateDirectory(this.Storage.RegionDirectory(region.Id));
                File.Move(file, this.Storage.DirectionsFile(region.Id), true);
                MapStorage.ExcludeFromBackup(this.Storage.DirectionsFile(region.Id));
                this.RecordRegion(region, r => r with { DirectionsVersion = part.Version, DirectionsSize = part.Size });
                break;

            case MapPackPartKind.Assets:
                this.InstallAssets(part, file);
                break;
        }
    }

    void InstallAssets(MapPackPart part, string file)
    {
        var staging = this.Storage.Assets + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);

        // ExtractToDirectory refuses entries that would land outside the directory.
        ZipFile.ExtractToDirectory(file, staging);
        File.Delete(file);

        var old = this.Storage.Assets + ".old";
        if (Directory.Exists(old))
            Directory.Delete(old, true);
        if (Directory.Exists(this.Storage.Assets))
            Directory.Move(this.Storage.Assets, old);

        Directory.Move(staging, this.Storage.Assets);
        this.Storage.WriteAssets(new InstalledAssets(part.Version, part.Size));

        if (Directory.Exists(old))
            Directory.Delete(old, true);
    }

    void RecordRegion(MapPackRegion region, Func<InstalledRegion, InstalledRegion>? change)
    {
        lock (this.gate)
        {
            var current = this.Storage.ReadRegion(region.Id)
                          ?? new InstalledRegion { Id = region.Id, Name = region.Name, Bounds = region.Bounds, MaxZoom = region.MaxZoom };

            current = current with { Name = region.Name, Bounds = region.Bounds };
            if (change is not null)
                current = change(current);

            if (current.MapVersion is not null || current.DirectionsVersion is not null)
                this.Storage.WriteRegion(current);

            this.installed = null;
        }
    }

    void Publish(DownloadJob job, MapPackDownloadState state, string? error = null)
    {
        job.State = state;
        job.Error = error;
        this.downloadEvents.Publish(job.Snapshot());
    }

    // ---------------------------------------------------------------- removal

    public void Remove(string regionId)
    {
        this.CancelDownload(regionId);
        this.CloseArchive(regionId);
        this.CloseRouter(regionId);

        lock (this.gate)
        {
            var directory = this.Storage.RegionDirectory(regionId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);

            this.installed = null;
        }
    }

    public bool RemoveDirections(string regionId)
    {
        lock (this.gate)
        {
            if (this.Storage.ReadRegion(regionId) is not { DirectionsVersion: not null } region)
                return false;

            this.CloseRouter(regionId);
            File.Delete(this.Storage.DirectionsFile(regionId));

            region = region with { DirectionsVersion = null, DirectionsSize = 0 };
            if (region.MapVersion is null)
                Directory.Delete(this.Storage.RegionDirectory(regionId), true);
            else
                this.Storage.WriteRegion(region);

            this.installed = null;
            return true;
        }
    }

    void CloseArchive(string regionId)
    {
        lock (this.gate)
        {
            if (this.archives.Remove(regionId, out var archive))
                archive.Dispose();
        }
    }

    void CloseRouter(string regionId)
    {
        lock (this.gate)
        {
            if (this.routers.Remove(regionId, out var router))
                router.Dispose();
        }
    }

    // ---------------------------------------------------------------- directions

    /// <summary>An on-device router for an installed region whose bounds hold every stop, or null.</summary>
    public IValhallaRouter? FindOnDeviceRouter(IReadOnlyList<RouteStop> stops)
    {
        if (this.onDevice is null)
            return null;

        foreach (var region in this.Installed)
        {
            if (region.DirectionsVersion is null || !stops.All(s => Contains(region.Bounds, s)))
                continue;

            lock (this.gate)
            {
                if (!this.routers.TryGetValue(region.Id, out var router))
                {
                    router = this.onDevice.Open(this.Storage.DirectionsFile(region.Id));
                    this.routers[region.Id] = router;
                }

                return router;
            }
        }

        return null;
    }

    static bool Contains(double[] bounds, RouteStop stop)
        => stop.Longitude >= bounds[0] && stop.Latitude >= bounds[1] && stop.Longitude <= bounds[2] && stop.Latitude <= bounds[3];

    public long InstalledBytes => this.Installed.Sum(x => x.MapSize + x.DirectionsSize);

    /// <summary>
    /// A live traffic tile from <see cref="MapsOptions.Traffic"/>, or null: outside the layer's zooms, where the provider has
    /// nothing, or when it cannot be reached. Failures are not kept, so the next request tries again.
    /// </summary>
    public async Task<TrafficTile?> GetTrafficTileAsync(int z, int x, int y, CancellationToken cancellationToken)
    {
        if (this.options.Traffic is not { } provider)
            return null;

        var layer = provider.Layer;
        if (z < layer.MinZoom || z > layer.MaxZoom)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (this.trafficTiles.TryGetValue((z, x, y), out var cached) && cached.Expires > now)
            return cached.Tile;

        TrafficTile? tile;
        try
        {
            tile = await provider.GetTileAsync(z, x, y, this.http, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.logger.LogDebug(ex, "Traffic tile {Z}/{X}/{Y} unavailable", z, x, y);
            return null;
        }

        if (this.trafficTiles.Count >= TrafficCacheLimit)
        {
            foreach (var (key, entry) in this.trafficTiles)
            {
                if (entry.Expires <= now)
                    this.trafficTiles.TryRemove(key, out _);
            }

            // Still full of live tiles: a map panned across a continent. Start again rather than track use.
            if (this.trafficTiles.Count >= TrafficCacheLimit)
                this.trafficTiles.Clear();
        }

        this.trafficTiles[(z, x, y)] = (tile, now + layer.Refresh);
        return tile;
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            foreach (var archive in this.archives.Values)
                archive.Dispose();
            foreach (var router in this.routers.Values)
                router.Dispose();

            this.archives.Clear();
            this.routers.Clear();
        }

        foreach (var job in this.downloads.Values)
            job.Cancellation.Cancel();

        this.onlineArchive?.Dispose();
        this.catalogKey?.Dispose();
        this.http.Dispose();
    }

    sealed class DownloadJob(MapPackRegion region, bool directions, List<MapPackPart> parts)
    {
        public MapPackRegion Region { get; } = region;
        public bool Directions { get; } = directions;
        public List<MapPackPart> Parts { get; } = parts;
        public CancellationTokenSource Cancellation { get; } = new();
        public MapPackDownloadState State { get; set; } = MapPackDownloadState.Queued;
        public string? Error { get; set; }

        /// <summary>Bytes of finished parts, and of the part downloading now.</summary>
        public long Completed { get; set; }
        public long Current { get; set; }

        public MapPackDownload Snapshot() => new(
            this.Region.Id,
            this.Directions,
            this.State,
            Math.Min(this.Completed + (this.State == MapPackDownloadState.Installed ? 0 : this.Current), this.Total),
            this.Total,
            this.Error
        );

        long Total => this.Parts.Sum(x => x.Size);
    }
}
