using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AppDeviceBridge.Maps;

public sealed class MapsOptions
{
    /// <summary>
    /// Where the map comes from online: <see cref="ProtomapsBasemapProvider"/> for OpenStreetMap, <see cref="AzureMapsBasemapProvider"/>,
    /// <see cref="GoogleMapsBasemapProvider"/>, or an <see cref="IBasemapProvider"/> of the app's own. Downloaded regions draw
    /// wherever they cover the map, whatever the provider — under a raster provider's images, which leave them showing through
    /// offline. Null for downloaded regions only. Its address and key never reach the page. Can be changed while the app runs
    /// and takes effect from the next request; the page reads <c>GET /_bridge/maps</c> again to see it.
    /// </summary>
    public IBasemapProvider? Basemap { get; set; }

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
    /// Disk space for online tiles already seen, so an area viewed online still draws offline. Oldest first to go. Only a
    /// basemap whose terms allow it is kept (<see cref="BasemapLayer.Cacheable"/>). Zero turns the cache off.
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

    /// <summary>The credit downloaded regions' map data requires, as HTML. OpenStreetMap's by default, as regions are built from it.</summary>
    public string Attribution { get; set; } = "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    /// <summary>Where regions and assets are kept. <c>{DataDirectory}/maps</c> when null.</summary>
    public string? Directory { get; set; }

    /// <summary>Where the online tile cache is kept. A temporary directory for the app when null.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Adds headers to every request for the catalog, region downloads, glyphs and sprites — an API key, say. Each provider
    /// configures its own requests.
    /// </summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    /// <summary>
    /// The handler outgoing requests use; <see cref="HttpClientHandler"/> when null, which on iOS and Mac Catalyst is
    /// NSURLSession. On macOS pass <c>() =&gt; new NSUrlSessionHandler()</c>: HttpClient's own TLS there stops at 1.2, and
    /// servers that only take TLS 1.3 refuse it.
    /// </summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    public DirectionsOptions Directions { get; } = new();

    /// <summary>
    /// Live traffic flow for the map: <see cref="TomTomTrafficProvider"/>, <see cref="HereTrafficProvider"/>,
    /// <see cref="AzureMapsTrafficProvider"/>, or an <see cref="ITrafficProvider"/> of the app's own. Null for no traffic layer.
    /// Its tiles are fetched with the same client as everything else here. Can be changed while the app runs — to let the user
    /// pick a provider, say — and takes effect from the next request; the page reads <c>GET /_bridge/maps</c> again to see it.
    /// </summary>
    public ITrafficProvider? Traffic { get; set; }

    /// <summary>
    /// Live traffic incidents — accidents, roadworks, closures — for the map: <see cref="TomTomIncidentProvider"/>, or an
    /// <see cref="ITrafficIncidentProvider"/> of the app's own. Independent of <see cref="Traffic"/>, and like it can be changed
    /// while the app runs. Null for none.
    /// </summary>
    public ITrafficIncidentProvider? TrafficIncidents { get; set; }
}

public sealed class DirectionsOptions
{
    /// <summary>
    /// Computes routes online, wherever no downloaded road network covers every stop: <see cref="ValhallaRouteProvider"/>,
    /// <see cref="AzureMapsRouteProvider"/>, <see cref="GoogleMapsRouteProvider"/>, or an <see cref="IRouteProvider"/> of the
    /// app's own. Null for on-device directions only. It can be replaced while the app runs; the next route uses the new one.
    /// </summary>
    public IRouteProvider? Router { get; set; }

    /// <summary>How long an online route, or a geocoder search, may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Turns addresses into stops for <c>/_bridge/directions/geocode</c>: <see cref="NominatimGeocoder"/>,
    /// <see cref="AzureMapsGeocoder"/>, <see cref="GoogleMapsGeocoder"/>, or a class of the app's own. Null answers 501. It can
    /// be replaced while the app runs; the next search uses the new one.
    /// </summary>
    public IGeocoder? Geocoder { get; set; }
}

public static class MapsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/maps</c> and <c>/_bridge/directions</c>. Call it as often as you like — every call configures the same
    /// <see cref="MapsOptions"/> — so a shared project can set up the sources and one head add what only it needs.
    /// <code>
    /// bridge.AddMapsBridge(o =>
    /// {
    ///     o.Basemap = new ProtomapsBasemapProvider("https://maps.example.com/planet.pmtiles");
    ///     o.Catalog = new Uri("https://releases.example.com/maps/catalog");
    ///     o.CatalogPublicKey = publicKey;
    ///     o.Directions.Router = new ValhallaRouteProvider(new Uri("https://valhalla.example.com/route"));
    ///     o.Directions.Geocoder = new NominatimGeocoder("MyApp/1.0 (support@example.com)");
    ///     o.Traffic = new TomTomTrafficProvider(tomTomKey);
    ///     o.TrafficIncidents = new TomTomIncidentProvider(tomTomKey);
    /// });
    ///
    /// // Or Azure Maps, or Google Maps, for any of the three:
    /// var azure = new AzureMapsCredential(azureMapsKey);
    /// bridge.AddMapsBridge(o =>
    /// {
    ///     o.Basemap = new AzureMapsBasemapProvider(azure);
    ///     o.Directions.Router = new AzureMapsRouteProvider(azure);
    ///     o.Directions.Geocoder = new AzureMapsGeocoder(azure);
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
