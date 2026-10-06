using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Where the map comes from online — <see cref="ProtomapsBasemapProvider"/> for OpenStreetMap, <see cref="AzureMapsBasemapProvider"/>,
/// <see cref="GoogleMapsBasemapProvider"/>, or a class of the app's own. Set it on <see cref="MapsOptions.Basemap"/>.
/// <para>
/// The provider describes its tiles once, through <see cref="Layer"/>, and fetches them one at a time. A
/// <see cref="TileFormat.Vector"/> provider serves tiles in the Protomaps basemap schema — the schema downloaded regions use — and
/// they are served with the regions' at <c>/_bridge/maps/tiles/{z}/{x}/{y}</c>. A <see cref="TileFormat.Raster"/> provider serves
/// images at <c>/_bridge/maps/basemap/{z}/{x}/{y}</c>, which the page lays over the regions. The page never learns the provider's
/// address or key. A null or an exception answers 204, so offline the map shows what is downloaded rather than an error.
/// </para>
/// </summary>
public interface IBasemapProvider
{
    BasemapLayer Layer { get; }

    /// <summary>One tile, or null when the provider has nothing there.</summary>
    /// <param name="http">The maps bridge's client, built from <see cref="MapsOptions.HttpMessageHandlerFactory"/>.</param>
    Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken);
}

/// <summary>What a basemap provider's tiles are. See <see cref="BasemapInfo"/>, which the page receives.</summary>
/// <param name="Provider">Who draws the map, for showing the user: <c>Protomaps</c>, <c>Azure Maps</c>, <c>Google Maps</c>.</param>
/// <param name="Attribution">The credit the provider's terms require, as HTML.</param>
public sealed record BasemapLayer(string Provider, TileFormat Format, int MinZoom, int MaxZoom, string Attribution)
{
    /// <summary>Raster: the size each tile is drawn at, in CSS pixels.</summary>
    public int TileSize { get; init; } = 256;

    /// <summary>
    /// Whether the provider's terms let its tiles be kept on the device, in the tile cache sized by
    /// <see cref="MapsOptions.TileCacheBytes"/>, so an area seen online still draws offline. Only vector tiles are kept.
    /// </summary>
    public bool Cacheable { get; init; }
}

/// <summary>
/// OpenStreetMap, as Protomaps tiles it: vector tiles in the Protomaps basemap schema, the schema downloaded regions use, so
/// the two draw as one map. Read from a PMTiles archive on a server that answers Range requests —
/// <c>https://cdn.example.com/planet.pmtiles</c>, read a directory and a tile at a time — or from a tile URL template with
/// <c>{z}</c>, <c>{x}</c> and <c>{y}</c>. Tiles seen online are kept for offline use.
/// <code>
/// bridge.AddMapsBridge(o => o.Basemap = new ProtomapsBasemapProvider("https://maps.example.com/planet.pmtiles"));
/// </code>
/// </summary>
/// <param name="source">The archive's URL, or a template. A key in it stays in the app.</param>
public sealed class ProtomapsBasemapProvider(string source) : IBasemapProvider, IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    PmTilesArchive? archive;
    DateTimeOffset retryAt;

    public string Source { get; } = String.IsNullOrWhiteSpace(source)
        ? throw new ArgumentException("A PMTiles URL or a tile URL template is required.", nameof(source))
        : source;

    /// <summary>The highest zoom the source has. The renderer scales past it. 15 by default, as Protomaps builds go.</summary>
    public int MaxZoom { get; set; } = 15;

    /// <summary>The credit OpenStreetMap's licence requires, as HTML.</summary>
    public string Attribution { get; set; } = "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    /// <summary>Adds headers to every tile request — an API key, say.</summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    public BasemapLayer Layer => new("Protomaps", TileFormat.Vector, 0, this.MaxZoom, this.Attribution) { Cacheable = true };

    bool IsTemplate => this.Source.Contains("{z}", StringComparison.Ordinal);

    public async Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (this.IsTemplate)
        {
            var url = this.Source
                .Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            return await ProviderTiles.GetAsync(http, new Uri(url), ProviderTiles.VectorTile, this.ConfigureRequest, cancellationToken).ConfigureAwait(false);
        }

        var archive = await this.GetArchiveAsync(http, cancellationToken).ConfigureAwait(false);
        if (archive is null || await archive.GetTileAsync(z, x, y, cancellationToken).ConfigureAwait(false) is not { } tile)
            return null;

        return new ProviderTile(tile.Data, ProviderTiles.VectorTile, tile.Compression switch
        {
            PmTilesCompression.Gzip => "gzip",
            PmTilesCompression.Brotli => "br",
            _ => null
        });
    }

    async Task<PmTilesArchive?> GetArchiveAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (this.archive is { } open)
            return open;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.archive is { } raced)
                return raced;

            // Offline, every tile would try the header again; once a minute is enough to notice the network is back.
            if (DateTimeOffset.UtcNow < this.retryAt)
                return null;

            try
            {
                this.archive = await PmTilesArchive
                    .OpenAsync(new HttpRangeSource(http, new Uri(this.Source), this.ConfigureRequest), cancellationToken)
                    .ConfigureAwait(false);
                return this.archive;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                this.retryAt = DateTimeOffset.UtcNow.AddMinutes(1);
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    public void Dispose() => this.archive?.Dispose();
}

/// <summary>The Azure Maps basemaps <see cref="AzureMapsBasemapProvider"/> draws.</summary>
public enum AzureMapsBasemapStyle
{
    /// <summary>The road map. <c>microsoft.base.road</c>.</summary>
    Road,

    /// <summary>The road map in dark grey, for a dark page. <c>microsoft.base.darkgrey</c>.</summary>
    DarkGrey,

    /// <summary>Satellite and aerial imagery, without labels. <c>microsoft.imagery</c>.</summary>
    Imagery
}

/// <summary>
/// Azure Maps, from the Render service's <c>Get Map Tile</c>: raster images the page lays over downloaded regions.
/// Authenticates with an <see cref="AzureMapsCredential"/>, which stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Basemap = new AzureMapsBasemapProvider(azureMapsKey));
/// </code>
/// <para>
/// Azure Maps' terms do not allow its tiles to be stored for offline use, so they are not kept in the tile cache; offline the
/// map shows downloaded regions. Its attribution is reported with the layer and the map shows it.
/// </para>
/// </summary>
public sealed class AzureMapsBasemapProvider : IBasemapProvider
{
    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsBasemapProvider(string subscriptionKey) : this(new AzureMapsCredential(subscriptionKey))
    {
    }

    /// <summary>Authenticates with a shared key or Microsoft Entra ID, as <paramref name="credential"/> says.</summary>
    public AzureMapsBasemapProvider(AzureMapsCredential credential)
        => this.Credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public AzureMapsCredential Credential { get; }

    /// <summary><c>https://atlas.microsoft.com/</c>, or a geography's own host such as <c>https://us.atlas.microsoft.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://atlas.microsoft.com/");

    public AzureMapsBasemapStyle Style { get; set; } = AzureMapsBasemapStyle.Road;

    /// <summary>The language labels are drawn in, as an IETF tag such as <c>fr-FR</c>. Azure Maps' default when null.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Which country's view of disputed borders and labels to draw, as an ISO 3166-1 alpha-2 code — or <c>Auto</c>, Azure Maps'
    /// default, which picks by where the request comes from.
    /// </summary>
    public string? View { get; set; }

    /// <summary>The highest zoom Azure Maps is asked for; the map scales the images past it. 22 by default.</summary>
    public int MaxZoom { get; set; } = 22;

    /// <summary>Image size, 256 or 512 pixels. 512 by default, which draws sharply on high-density screens.</summary>
    public int TileSize { get; set; } = 512;

    public BasemapLayer Layer => new("Azure Maps", TileFormat.Raster, 0, this.MaxZoom, "© Microsoft Azure Maps, © TomTom")
    {
        TileSize = this.TileSize
    };

    string TilesetId => this.Style switch
    {
        AzureMapsBasemapStyle.DarkGrey => "microsoft.base.darkgrey",
        AzureMapsBasemapStyle.Imagery => "microsoft.imagery",
        _ => "microsoft.base.road"
    };

    public async Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"map/tile?api-version=2024-04-01&tilesetId={this.TilesetId}&zoom={z}&x={x}&y={y}&tileSize={this.TileSize}"
        );
        if (!String.IsNullOrWhiteSpace(this.Language))
            path += "&language=" + Uri.EscapeDataString(this.Language);
        if (!String.IsNullOrWhiteSpace(this.View))
            path += "&view=" + Uri.EscapeDataString(this.View);

        var authorize = await this.Credential.AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        var type = this.Style == AzureMapsBasemapStyle.Imagery ? "image/jpeg" : "image/png";
        return await ProviderTiles.GetAsync(http, new Uri(this.BaseAddress, path), type, authorize, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The Google Maps basemaps <see cref="GoogleMapsBasemapProvider"/> draws.</summary>
public enum GoogleMapsBasemapStyle
{
    /// <summary>The standard road map.</summary>
    Roadmap,

    /// <summary>Satellite imagery.</summary>
    Satellite,

    /// <summary>Terrain: relief shading with roads and labels.</summary>
    Terrain
}

/// <summary>
/// Google Maps, from the Map Tiles API's 2D tiles: raster images the page lays over downloaded regions. The provider creates
/// the API's session itself and renews it before it expires; the key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Basemap = new GoogleMapsBasemapProvider(googleMapsKey));
/// </code>
/// <para>
/// The key needs the Map Tiles API enabled. Google's terms do not allow its tiles to be stored for offline use, so they are not
/// kept in the tile cache; offline the map shows downloaded regions. They also require Google's attribution, which the layer
/// reports and the map shows, and — for routes and places from other Google APIs — that those are shown on a Google map.
/// </para>
/// </summary>
public sealed class GoogleMapsBasemapProvider : IBasemapProvider
{
    readonly SemaphoreSlim gate = new(1, 1);
    (string Id, DateTimeOffset Expires)? session;

    public GoogleMapsBasemapProvider(string apiKey)
        => this.ApiKey = String.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("A Google Maps API key is required.", nameof(apiKey)) : apiKey;

    public string ApiKey { get; }

    /// <summary><c>https://tile.googleapis.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://tile.googleapis.com/");

    public GoogleMapsBasemapStyle Style { get; set; } = GoogleMapsBasemapStyle.Roadmap;

    /// <summary>The language labels are drawn in, as an IETF tag such as <c>en-US</c>. Read when a session is created.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>The region, as a CLDR code such as <c>US</c>, whose borders and labels the map follows. Read when a session is created.</summary>
    public string Region { get; set; } = "US";

    /// <summary>Asks for images at twice the pixels, which draw sharply on high-density screens. True by default.</summary>
    public bool HighDpi { get; set; } = true;

    /// <summary>The highest zoom Google is asked for; the map scales the images past it. 22 by default.</summary>
    public int MaxZoom { get; set; } = 22;

    public BasemapLayer Layer => new("Google Maps", TileFormat.Raster, 0, this.MaxZoom, "Map data © Google");

    public async Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var session = await this.GetSessionAsync(http, false, cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.FetchAsync(http, session, z, x, y, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // A session Google ended early answers as unauthorized: start another and try once more.
            session = await this.GetSessionAsync(http, true, cancellationToken).ConfigureAwait(false);
            return await this.FetchAsync(http, session, z, x, y, cancellationToken).ConfigureAwait(false);
        }
    }

    Task<ProviderTile?> FetchAsync(HttpClient http, string session, int z, int x, int y, CancellationToken cancellationToken)
    {
        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"v1/2dtiles/{z}/{x}/{y}?session={Uri.EscapeDataString(session)}&key={Uri.EscapeDataString(this.ApiKey)}"
        );
        return ProviderTiles.GetAsync(http, new Uri(this.BaseAddress, path), "image/png", null, cancellationToken);
    }

    async Task<string> GetSessionAsync(HttpClient http, bool renew, CancellationToken cancellationToken)
    {
        if (!renew && this.session is { } current && current.Expires > DateTimeOffset.UtcNow)
            return current.Id;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!renew && this.session is { } raced && raced.Expires > DateTimeOffset.UtcNow)
                return raced.Id;

            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["mapType"] = this.Style switch
                {
                    GoogleMapsBasemapStyle.Satellite => "satellite",
                    GoogleMapsBasemapStyle.Terrain => "terrain",
                    _ => "roadmap"
                },
                ["language"] = this.Language,
                ["region"] = this.Region
            };
            if (this.HighDpi)
            {
                body["scale"] = "scaleFactor2x";
                body["highDpi"] = true;
            }

            // Terrain is relief shading only; Google draws roads and labels over it when asked for the roadmap layer.
            if (this.Style == GoogleMapsBasemapStyle.Terrain)
                body["layerTypes"] = new System.Text.Json.Nodes.JsonArray("layerRoadmap");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(this.BaseAddress, $"v1/createSession?key={Uri.EscapeDataString(this.ApiKey)}"))
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var id = (string?)json?["session"] ?? throw new InvalidDataException("Google answered without a session.");

            // expiry is seconds since the epoch, as a string. Renewed an hour early, so no tile is asked for with a dead one.
            var expires = Int64.TryParse((string?)json?["expiry"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).AddHours(-1)
                : DateTimeOffset.UtcNow.AddHours(12);

            this.session = (id, expires);
            return id;
        }
        finally
        {
            this.gate.Release();
        }
    }
}
