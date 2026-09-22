using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;
using Shiny.Net.HttpServer;
using static Shiny.AppDeviceBridge.Tests.Maps.MapPackCatalogTests;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>/_bridge/maps</c> on the app's real server: tiles from installed regions, online sources and the tile cache; glyphs
/// and sprites; and regions downloaded from a real release server's signed catalog.
/// </summary>
public class MapsBridgeTests
{
    const string TileSha256 = "804ebda264e45d8593e6d74690790c55292d8b90fdb18b2a71f875083641cdf9";
    static readonly double[] Boulder = [-105.285, 40.005, -105.265, 40.02];

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Hands_the_page_urls_under_the_bridge_prefix_and_no_secrets()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.OnlineTiles = "https://tiles.example.com/{z}/{x}/{y}.mvt?key=secret";
            o.OnlineMaxZoom = 14;
        });

        var info = await fixture.Maps.GetInfoAsync();

        Assert.Equal("/_bridge/maps/tiles/{z}/{x}/{y}", info.TilesUrl);
        Assert.Equal("/_bridge/maps/glyphs/{fontstack}/{range}.pbf", info.GlyphsUrl);
        Assert.Equal("/_bridge/maps/sprites", info.SpritesUrl);
        Assert.Equal(14, info.MaxZoom);
        Assert.True(info.Online);
        Assert.False(info.Catalog);
        Assert.DoesNotContain("secret", await fixture.WebView.GetStringAsync("/_bridge/maps"));
    }

    [Fact]
    public async Task Serves_a_tile_from_an_installed_region_as_stored()
    {
        await using var fixture = await MapsFixture.StartAsync();
        fixture.InstallDirectly("boulder", Boulder, PmTilesTests.Fixture("boulder.pmtiles"));

        using var response = await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/1700/3100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.mapbox-vector-tile", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(TileSha256, Sha256(await response.Content.ReadAsByteArrayAsync()));
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task A_tile_no_source_has_is_204_so_the_map_draws_a_gap()
    {
        await using var fixture = await MapsFixture.StartAsync();
        fixture.InstallDirectly("boulder", Boulder, PmTilesTests.Fixture("boulder.pmtiles"));

        using var outside = await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/100/100");
        using var invalid = await fixture.WebView.GetAsync("/_bridge/maps/tiles/3/9/9");
        using var garbage = await fixture.WebView.GetAsync("/_bridge/maps/tiles/a/b/c");

        Assert.Equal(HttpStatusCode.NoContent, outside.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
    }

    [Fact]
    public async Task Online_tiles_carry_the_apps_headers_and_are_kept_for_offline()
    {
        var tile = System.Text.Encoding.UTF8.GetBytes("a vector tile");
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.OnlineTiles = "https://tiles.example.com/v4/{z}/{x}/{y}.mvt";
            o.ConfigureRequest = r => r.Headers.Add("X-Api-Key", "secret");
        });
        fixture.Network.Stub("tiles.example.com", r => r.RequestUri!.AbsolutePath == "/v4/12/850/1550.mvt"
            ? Network.Bytes(tile)
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(tile, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/tiles/12/850/1550"));
        Assert.Equal("secret", fixture.Network.Requests.Single().Headers.GetValues("X-Api-Key").Single());

        fixture.Network.Offline = true;
        Assert.Equal(tile, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/tiles/12/850/1550"));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/tiles/12/851/1550")).StatusCode);
    }

    [Fact]
    public async Task An_online_pmtiles_archive_is_read_a_range_at_a_time()
    {
        var bytes = await File.ReadAllBytesAsync(PmTilesTests.Fixture("boulder.pmtiles"));
        await using var fixture = await MapsFixture.StartAsync(o => o.OnlineTiles = "https://cdn.example.com/planet.pmtiles");
        var server = new PmTilesTests.RangeHandler(bytes);
        fixture.Network.Route("cdn.example.com", server);

        using var response = await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/1700/3100");

        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(TileSha256, Sha256(await response.Content.ReadAsByteArrayAsync()));
        Assert.All(server.Requests, r => Assert.NotNull(r.Headers.Range));
    }

    [Fact]
    public async Task Online_tiles_past_the_online_zoom_are_not_fetched()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.OnlineTiles = "https://tiles.example.com/{z}/{x}/{y}";
            o.OnlineMaxZoom = 14;
        });

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/tiles/15/6800/12400")).StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Glyphs_and_sprites_are_fetched_online_once_and_kept()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.OnlineAssets = new Uri("https://assets.example.com/basemaps/"));
        fixture.Network.Stub("assets.example.com", r => r.RequestUri!.AbsolutePath switch
        {
            "/basemaps/fonts/Noto%20Sans%20Regular/0-255.pbf" => Network.Bytes("glyphs"u8.ToArray()),
            "/basemaps/sprites/v4/light@2x.png" => Network.Bytes("png"u8.ToArray()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        // The first font of a stack that has none falls through to the next.
        using var glyphs = await fixture.WebView.GetAsync("/_bridge/maps/glyphs/Missing%20Font,Noto%20Sans%20Regular/0-255.pbf");
        Assert.Equal(HttpStatusCode.OK, glyphs.StatusCode);
        Assert.Equal("glyphs", await glyphs.Content.ReadAsStringAsync());

        using var sprite = await fixture.WebView.GetAsync("/_bridge/maps/sprites/light@2x.png");
        Assert.Equal("image/png", sprite.Content.Headers.ContentType?.MediaType);

        fixture.Network.Offline = true;
        Assert.Equal("glyphs", await fixture.WebView.GetStringAsync("/_bridge/maps/glyphs/Noto%20Sans%20Regular/0-255.pbf"));
        Assert.Equal("png", await fixture.WebView.GetStringAsync("/_bridge/maps/sprites/light@2x.png"));
    }

    [Theory]
    [InlineData("/_bridge/maps/glyphs/..%2F..%2Fsecret/0-255.pbf")]
    [InlineData("/_bridge/maps/glyphs/Noto%20Sans%20Regular/..%2F..%2Fx.pbf")]
    [InlineData("/_bridge/maps/sprites/..%2Fregion.json")]
    [InlineData("/_bridge/maps/sprites/light.svg")]
    public async Task Asset_names_that_could_leave_the_asset_directory_are_refused(string path)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.OnlineAssets = new Uri("https://assets.example.com/"));
        fixture.Network.Stub("assets.example.com", _ => Network.Bytes("x"u8.ToArray()));

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.WebView.GetAsync(path)).StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Installs_a_region_from_the_signed_catalog_and_draws_it_offline()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());

        var catalog = await fixture.Maps.GetRegionsAsync();
        var region = Assert.Single(catalog.Regions);
        Assert.True(catalog.CatalogReachable);
        Assert.False(region.MapInstalled);
        Assert.NotNull(region.DirectionsSize);

        var events = await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()));

        Assert.Equal(MapPackDownloadState.Installed, events[^1].State);
        Assert.Contains(events, e => e.State == MapPackDownloadState.Verifying);
        Assert.False(events[^1].Directions);

        catalog = await fixture.Maps.GetRegionsAsync();
        region = Assert.Single(catalog.Regions);
        Assert.True(region.MapInstalled);
        Assert.False(region.DirectionsInstalled);
        Assert.False(region.UpdateAvailable);
        Assert.Null(region.Download);
        Assert.Equal(region.MapSize, catalog.InstalledBytes);

        // Offline, the region draws, and so do its labels and icons from the asset pack installed with it.
        fixture.Network.Offline = true;
        Assert.Equal(TileSha256, Sha256(await fixture.WebView.GetByteArrayAsync("/_bridge/maps/tiles/13/1700/3100")));
        Assert.Equal("glyphs 0-255", await fixture.WebView.GetStringAsync("/_bridge/maps/glyphs/Noto%20Sans%20Regular/0-255.pbf"));
        Assert.Equal("""{"icon":{}}""", await fixture.WebView.GetStringAsync("/_bridge/maps/sprites/light.json"));

        var offline = await fixture.Maps.GetRegionsAsync(refresh: true);
        Assert.False(offline.CatalogReachable);
        Assert.True(Assert.Single(offline.Regions).MapInstalled);
    }

    [Fact]
    public async Task Directions_download_only_when_asked_and_are_removed_on_their_own()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());

        var events = await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest(Directions: true)));

        Assert.True(events[^1].Directions);
        Assert.Equal(MapPackDownloadState.Installed, events[^1].State);
        Assert.Equal(events[^1].TotalBytes, events[^1].BytesDownloaded);
        Assert.Equal("valhalla tiles", await File.ReadAllTextAsync(fixture.Service.Storage.DirectionsFile("boulder")));
        Assert.Equal(["boulder"], (await fixture.Directions.GetInfoAsync()).OfflineRegions);

        await fixture.Maps.RemoveDirectionsAsync("boulder");

        var region = Assert.Single((await fixture.Maps.GetRegionsAsync()).Regions);
        Assert.True(region.MapInstalled);
        Assert.False(region.DirectionsInstalled);
        Assert.False(File.Exists(fixture.Service.Storage.DirectionsFile("boulder")));

        await fixture.Maps.RemoveAsync("boulder");

        Assert.False(Assert.Single((await fixture.Maps.GetRegionsAsync()).Regions).MapInstalled);
        Assert.False(Directory.Exists(fixture.Service.Storage.RegionDirectory("boulder")));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/1700/3100")).StatusCode);
    }

    [Fact]
    public async Task A_changed_catalog_version_is_offered_as_an_update()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());
        await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()));

        // A rebuilt region: the store computes a new hash, so a new version.
        await File.AppendAllTextAsync(Path.Combine(packs.Path, "boulder.pmtiles"), "rebuilt");

        Assert.True(Assert.Single((await fixture.Maps.GetRegionsAsync(refresh: true)).Regions).UpdateAvailable);
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_signed_hash_is_discarded()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());
        await fixture.Maps.GetRegionsAsync();

        // Same length, different bytes, after the catalog was signed: what a tampering CDN would serve.
        var path = Path.Combine(packs.Path, "boulder.pmtiles");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var events = await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()));

        Assert.Equal(MapPackDownloadState.Failed, events[^1].State);
        Assert.Contains("hash", events[^1].Error);
        Assert.False(File.Exists(fixture.Service.Storage.MapFile("boulder")));
        Assert.Empty(Directory.GetFiles(fixture.Service.Storage.Downloads));
    }

    [Fact]
    public async Task A_catalog_signed_with_another_key_offers_nothing()
    {
        using var packs = PackDirectory.Create();
        var (_, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        var (otherPublicKey, _) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = otherPublicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());

        Assert.Empty((await fixture.Maps.GetRegionsAsync()).Regions);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()))).StatusCode);
    }

    [Fact]
    public async Task A_partial_download_is_resumed_where_it_stopped()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
        });
        fixture.Network.Route("packs.example.com", server.CreateHandler());

        // What an app killed mid-download leaves behind: the first half of the file, named for its hash.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(packs.Path, "boulder.pmtiles"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Service.Storage.Downloads, $"boulder.map.{packs.MapSha256[..16]}"), bytes[..(bytes.Length / 2)]);

        var events = await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()));

        Assert.Equal(MapPackDownloadState.Installed, events[^1].State);
        var request = fixture.Network.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("boulder.pmtiles"));
        Assert.Equal(bytes.Length / 2, request.Headers.Range!.Ranges.Single().From);
        Assert.Equal(TileSha256, Sha256(await fixture.WebView.GetByteArrayAsync("/_bridge/maps/tiles/13/1700/3100")));
    }

    [Fact]
    public async Task A_download_that_goes_quiet_is_resumed_where_it_stopped()
    {
        using var packs = PackDirectory.Create();
        var (publicKey, privateKey) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privateKey);
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Catalog = new Uri("http://packs.example.com/maps/catalog");
            o.CatalogPublicKey = publicKey;
            o.DownloadStallTimeout = TimeSpan.FromMilliseconds(300);
        });
        var stalling = new StallingHandler(server.CreateHandler(), "boulder.pmtiles", stallAfter: 50_000);
        fixture.Network.Route("packs.example.com", stalling);

        var events = await fixture.WaitForDownloadAsync("boulder", () => fixture.Maps.InstallAsync("boulder", new MapPackInstallRequest()));

        Assert.Equal(MapPackDownloadState.Installed, events[^1].State);
        var requests = fixture.Network.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("boulder.pmtiles")).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[0].Headers.Range);
        Assert.Equal(50_000, requests[1].Headers.Range!.Ranges.Single().From);
        Assert.Equal(TileSha256, Sha256(await fixture.WebView.GetByteArrayAsync("/_bridge/maps/tiles/13/1700/3100")));
    }

    /// <summary>Passes requests on, but the first answer for one file stops sending after some bytes and never ends.</summary>
    sealed class StallingHandler(HttpMessageHandler inner, string file, int stallAfter) : DelegatingHandler(inner)
    {
        int stalled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!request.RequestUri!.AbsolutePath.EndsWith(file) || Interlocked.Exchange(ref this.stalled, 1) == 1)
                return response;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new StreamContent(new StallingStream(bytes[..stallAfter]));
            response.Content = content;
            return response;
        }

        sealed class StallingStream(byte[] prefix) : Stream
        {
            int position;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (this.position < prefix.Length)
                {
                    var n = Math.Min(buffer.Length, prefix.Length - this.position);
                    prefix.AsMemory(this.position, n).CopyTo(buffer);
                    this.position += n;
                    return n;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Region_calls_fail_the_way_the_page_can_tell_apart()
    {
        await using var noCatalog = await MapsFixture.StartAsync();
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => noCatalog.Maps.InstallAsync("boulder", new MapPackInstallRequest()))).IsNotSupported);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => noCatalog.Maps.CancelDownloadAsync("boulder"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => noCatalog.Maps.RemoveDirectionsAsync("boulder"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await noCatalog.WebView.DeleteAsync("/_bridge/maps/regions/..%2F..")).StatusCode);

        var (catalog, reachable) = await noCatalog.Service.GetCatalogAsync(true, CancellationToken.None);
        Assert.Null(catalog);
        Assert.False(reachable);
    }

    [Fact]
    public async Task A_catalog_needs_its_public_key()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddMapsBridge(o => o.Catalog = new Uri("https://example.com/maps/catalog"))),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<AppDeviceBridgeServer>().Http);
    }

    [Fact]
    public async Task Every_call_configures_the_same_options_and_registers_the_bridges_once()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddMapsBridge(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"))
                .AddMapsBridge(o => o.OnlineMaxZoom = 12)),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        Assert.Single(server.Bridges, x => x is MapsBridge);
        Assert.Single(server.Bridges, x => x is DirectionsBridge { IsSupported: true });
        var options = provider.GetRequiredService<MapsOptions>();
        Assert.Equal(12, options.OnlineMaxZoom);
        Assert.NotNull(options.Directions.OnlineRouteUrl);
    }
}
