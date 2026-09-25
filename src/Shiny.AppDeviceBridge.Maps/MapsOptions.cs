using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AppDeviceBridge.Maps;

public sealed class MapsOptions
{
    /// <summary>
    /// Where tiles come from when no downloaded region covers them. Either a PMTiles archive answering Range requests —
    /// <c>https://cdn.example.com/planet.pmtiles</c>, read a directory and a tile at a time — or a tile URL template with
    /// <c>{z}</c>, <c>{x}</c> and <c>{y}</c>. Null for downloaded regions only. The page never sees this address, so a key
    /// in it stays in the app.
    /// </summary>
    public string? OnlineTiles { get; set; }

    /// <summary>The highest zoom the online source has. The renderer scales past it.</summary>
    public int OnlineMaxZoom { get; set; } = 15;

    /// <summary>
    /// The signed catalog of downloadable regions, as the release server's <c>MapMapPacks</c> serves it. Null when the app
    /// offers no downloads.
    /// </summary>
    public Uri? Catalog { get; set; }

    /// <summary>
    /// The public key the catalog is signed with — PEM or base64, the same form as the web app's update key, and usually
    /// the same key. Required with <see cref="Catalog"/>: a region is only installed when its signature checks out.
    /// </summary>
    public string? CatalogPublicKey { get; set; }

    /// <summary>How long a fetched catalog is used before it is fetched again.</summary>
    public TimeSpan CatalogLifetime { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Where glyphs and sprites come from until the catalog's asset pack is installed: a directory laid out as
    /// <c>fonts/{fontstack}/{range}.pbf</c> and <c>sprites/v4/{name}</c>. What is fetched is kept, so labels keep drawing
    /// offline. Null to use installed assets only.
    /// </summary>
    public Uri? OnlineAssets { get; set; } = new("https://protomaps.github.io/basemaps-assets/");

    /// <summary>
    /// Disk space for online tiles already seen, so an area viewed online still draws offline. Oldest first to go.
    /// Zero turns the cache off.
    /// </summary>
    public long TileCacheBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// How long a region download may go without receiving a byte before the connection is dropped and the download resumed
    /// from where it stopped.
    /// </summary>
    public TimeSpan DownloadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many attempts in a row may make no progress before a region's download fails. An attempt that receives anything
    /// starts the count again, so a connection that keeps dropping but moves forward each time still finishes.
    /// </summary>
    public int DownloadAttempts { get; set; } = 5;

    /// <summary>The credit the map data's licence requires, as HTML.</summary>
    public string Attribution { get; set; } = "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    /// <summary>Where regions and assets are kept. <c>{DataDirectory}/maps</c> when null.</summary>
    public string? Directory { get; set; }

    /// <summary>Where the online tile cache is kept. A temporary directory for the app when null.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Adds headers to every request for tiles, assets and the catalog — an API key, say.</summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    /// <summary>
    /// The handler outgoing requests use; <see cref="HttpClientHandler"/> when null, which on iOS and Mac Catalyst is
    /// NSURLSession. On macOS pass <c>() =&gt; new NSUrlSessionHandler()</c>: HttpClient's own TLS there stops at 1.2, and
    /// servers that only take TLS 1.3 refuse it.
    /// </summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    public DirectionsOptions Directions { get; } = new();

    /// <summary>
    /// Live traffic for the map: <see cref="TomTomTrafficProvider"/>, or an <see cref="ITrafficProvider"/> of the app's own.
    /// Null for no traffic layer. Its tiles are fetched with the same client as everything else here.
    /// </summary>
    public ITrafficProvider? Traffic { get; set; }
}

public sealed class DirectionsOptions
{
    /// <summary>
    /// A Valhalla <c>route</c> endpoint: <c>https://valhalla.example.com/route</c> for your own, or
    /// <c>https://api.stadiamaps.com/route/v1</c> for Stadia Maps. Null for on-device directions only.
    /// </summary>
    public Uri? OnlineRouteUrl { get; set; }

    /// <summary>Sent as the <c>api_key</c> query parameter, which is how hosted Valhalla services take it. Never reaches the page.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Adds headers to every online route request.</summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    /// <summary>How long an online route may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public static class MapsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/maps</c> and <c>/_bridge/directions</c>. Call it as often as you like — every call configures the same
    /// <see cref="MapsOptions"/> — so a shared project can set up the sources and one head add what only it needs.
    /// <code>
    /// bridge.AddMapsBridge(o =>
    /// {
    ///     o.OnlineTiles = "https://maps.example.com/planet.pmtiles";
    ///     o.Catalog = new Uri("https://releases.example.com/maps/catalog");
    ///     o.CatalogPublicKey = publicKey;
    ///     o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route");
    ///     o.Traffic = new TomTomTrafficProvider(tomTomKey);
    /// });
    ///
    /// // macOS: HttpClient's own TLS there stops at 1.2; NSURLSession speaks 1.3.
    /// bridge.AddMapsBridge(o => o.HttpMessageHandlerFactory = () => new NSUrlSessionHandler());
    /// </code>
    /// On-device directions need <c>Shiny.AppDeviceBridge.Maps.Valhalla</c> and <c>bridge.AddOnDeviceDirections()</c> as well.
    /// </summary>
    public static TBuilder AddMapsBridge<TBuilder>(this TBuilder bridge, Action<MapsOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (bridge.Services.FirstOrDefault(x => x.ServiceType == typeof(MapsOptions))?.ImplementationInstance is not MapsOptions options)
        {
            options = new MapsOptions();
            bridge.Services.AddSingleton(options);
            bridge.Services.TryAddSingleton<MapsService>();
            bridge.AddBridge<MapsBridge>();
            bridge.AddBridge<DirectionsBridge>();
        }

        configure?.Invoke(options);
        return bridge;
    }
}
