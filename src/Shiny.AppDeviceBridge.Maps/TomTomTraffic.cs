using System.Globalization;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// TomTom's Traffic Flow vector tiles: every road TomTom covers, with its current speed relative to free flow and whether it is
/// closed. Needs a TomTom API key with the Traffic API; the key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Traffic = new TomTomTrafficProvider(tomTomKey));
/// </code>
/// <para>
/// Each tile is a request against the key's quota, and every tile on screen is fetched again after <see cref="Refresh"/>.
/// <see cref="MinZoom"/> keeps the continent-wide zooms, where traffic is unreadable anyway, from spending it.
/// </para>
/// </summary>
public sealed class TomTomTrafficProvider(string apiKey) : ITrafficProvider
{
    readonly string apiKey = TomTom.RequireKey(apiKey);

    /// <summary>TomTom's API host. Change it only for a regional endpoint or a test server.</summary>
    public Uri BaseAddress { get; set; } = TomTom.DefaultBaseAddress;

    /// <summary>The lowest zoom tiles are fetched at. 6 by default: regions and the roads between cities.</summary>
    public int MinZoom { get; set; } = 6;

    /// <summary>TomTom's flow data changes about once a minute. Two minutes by default, to halve the quota spent.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromMinutes(2);

    public TrafficLayer Layer => new(TrafficTileFormat.Vector, this.MinZoom, 22, this.Refresh, TomTom.Attribution)
    {
        SourceLayer = "Traffic flow",
        SpeedRatioProperty = "traffic_level",
        ClosedProperty = "road_closure"
    };

    // The "relative" style: traffic_level is current speed over free-flow speed, which is what the layer colours by.
    public Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
        => TrafficTiles.GetAsync(http, TomTom.TileUri(this.BaseAddress, "flow/relative", z, x, y, this.apiKey), TrafficTiles.VectorTile, null, cancellationToken);
}

/// <summary>
/// TomTom's Traffic Incident vector tiles: accidents, jams, roadworks, closures and weather, as the stretch of road each one
/// affects and a point where it is, with a description and the delay it causes. The same key as
/// <see cref="TomTomTrafficProvider"/>; each tile is a request against its quota.
/// <code>
/// bridge.AddMapsBridge(o => o.TrafficIncidents = new TomTomIncidentProvider(tomTomKey));
/// </code>
/// </summary>
public sealed class TomTomIncidentProvider(string apiKey) : ITrafficIncidentProvider
{
    // TomTom's icon_category values. 13 is a cluster of several, which the map counts instead of naming.
    static readonly IReadOnlyDictionary<string, TrafficIncidentKind> Kinds = new Dictionary<string, TrafficIncidentKind>
    {
        ["1"] = TrafficIncidentKind.Accident,
        ["2"] = TrafficIncidentKind.Weather,
        ["3"] = TrafficIncidentKind.Hazard,
        ["4"] = TrafficIncidentKind.Weather,
        ["5"] = TrafficIncidentKind.Weather,
        ["6"] = TrafficIncidentKind.Congestion,
        ["7"] = TrafficIncidentKind.LaneClosed,
        ["8"] = TrafficIncidentKind.RoadClosed,
        ["9"] = TrafficIncidentKind.RoadWorks,
        ["10"] = TrafficIncidentKind.Weather,
        ["11"] = TrafficIncidentKind.Weather,
        ["14"] = TrafficIncidentKind.BrokenDownVehicle
    };

    readonly string apiKey = TomTom.RequireKey(apiKey);

    /// <summary>TomTom's API host. Change it only for a regional endpoint or a test server.</summary>
    public Uri BaseAddress { get; set; } = TomTom.DefaultBaseAddress;

    /// <summary>The lowest zoom tiles are fetched at. 8 by default: incidents are too dense to read further out.</summary>
    public int MinZoom { get; set; } = 8;

    /// <summary>Incidents change less often than flow. Two minutes by default.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The language descriptions are written in, as TomTom names it: <c>en-GB</c> by default, <c>de-DE</c>, <c>fr-FR</c>, ….</summary>
    public string Language { get; set; } = "en-GB";

    public TrafficIncidentLayer Layer => new(this.MinZoom, 22, this.Refresh, TomTom.Attribution, "icon_category", Kinds)
    {
        LineSourceLayer = "Traffic incident flow",
        PointSourceLayer = "Traffic incident POI",
        DescriptionProperty = "description",
        DelayProperty = "delay",
        ClusterSizeProperty = "cluster_size"
    };

    public Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        var uri = TomTom.TileUri(this.BaseAddress, "incidents", z, x, y, this.apiKey);
        return TrafficTiles.GetAsync(http, new Uri($"{uri}&language={Uri.EscapeDataString(this.Language)}"), TrafficTiles.VectorTile, null, cancellationToken);
    }
}

static class TomTom
{
    public static readonly Uri DefaultBaseAddress = new("https://api.tomtom.com/");

    public const string Attribution = "<a href=\"https://www.tomtom.com/\">© TomTom</a>";

    public static string RequireKey(string apiKey) => String.IsNullOrWhiteSpace(apiKey)
        ? throw new ArgumentException("A TomTom API key is required.", nameof(apiKey))
        : apiKey;

    public static Uri TileUri(Uri baseAddress, string kind, int z, int x, int y, string apiKey)
        => new(baseAddress, String.Create(CultureInfo.InvariantCulture, $"traffic/map/4/tile/{kind}/{z}/{x}/{y}.pbf?key={Uri.EscapeDataString(apiKey)}"));
}
