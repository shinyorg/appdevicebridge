using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Maps.Client;

/// <summary>
/// Everything a map renderer needs to draw from the bridge. The URLs are templates MapLibre (or any vector-tile renderer)
/// fills in itself, relative to the page's origin and already under the bridge's prefix.
/// </summary>
/// <param name="TilesUrl">
/// The vector tiles, in the Protomaps basemap schema: <c>…/maps/tiles/{z}/{x}/{y}</c>. Served from a downloaded region when one
/// covers the tile, from tiles already seen online, and from the online basemap when it is a vector one.
/// </param>
/// <param name="GlyphsUrl">The label fonts: <c>…/maps/glyphs/{fontstack}/{range}.pbf</c>.</param>
/// <param name="SpritesUrl">The icon sheets. Append a flavor — <c>light</c>, <c>dark</c>, <c>white</c>, <c>grayscale</c>, <c>black</c> — for MapLibre's <c>sprite</c>.</param>
/// <param name="MaxZoom">The highest zoom <paramref name="TilesUrl"/> carries. The renderer draws closer zooms by scaling these, which vector tiles do cleanly.</param>
/// <param name="Catalog">Whether the app configured a region catalog to download from.</param>
/// <param name="Attribution">The credit the vector map data's licence requires, as HTML.</param>
/// <param name="Basemap">The online basemap, when the app configured one. Null for downloaded regions only.</param>
/// <param name="Traffic">The live traffic layer, when the app configured a traffic provider. Null otherwise.</param>
/// <param name="Incidents">Live traffic incidents — accidents, roadworks, closures — when the app configured an incident provider. Null otherwise.</param>
public sealed record MapsInfo(
    string TilesUrl,
    string GlyphsUrl,
    string SpritesUrl,
    int MaxZoom,
    bool Catalog,
    string Attribution,
    BasemapInfo? Basemap = null,
    TrafficInfo? Traffic = null,
    TrafficIncidentInfo? Incidents = null
);

/// <summary>How a provider's tiles are drawn.</summary>
public enum TileFormat
{
    /// <summary>Vector tiles the renderer styles itself.</summary>
    Vector,

    /// <summary>Images already drawn by the provider, laid over the map.</summary>
    Raster
}

/// <summary>
/// The online basemap, whatever provider is behind it — OpenStreetMap through Protomaps, Azure Maps, Google Maps or the app's
/// own. Tiles come through the bridge, which holds the provider's key.
/// <para>
/// <see cref="TileFormat.Vector"/>: tiles in the Protomaps schema, served through <see cref="MapsInfo.TilesUrl"/> together with
/// downloaded regions, so <see cref="TilesUrl"/> is that same URL and the page draws one source.
/// </para>
/// <para>
/// <see cref="TileFormat.Raster"/>: images served at <see cref="TilesUrl"/>, which the page lays over the vector tiles. Offline
/// the bridge answers 204 for them, so downloaded regions still show through where the images are missing.
/// </para>
/// </summary>
/// <param name="Provider">Who draws the map — <c>Protomaps</c>, <c>Azure Maps</c>, <c>Google Maps</c> — for showing the user.</param>
/// <param name="Format">Whether the page styles the tiles itself or lays the provider's images over the map.</param>
/// <param name="TilesUrl">The tiles: <c>…/maps/tiles/{z}/{x}/{y}</c> for vector, <c>…/maps/basemap/{z}/{x}/{y}</c> for raster.</param>
/// <param name="MinZoom">Below this the provider has no tiles, and none are asked for.</param>
/// <param name="MaxZoom">The highest zoom the provider has. The renderer scales past it.</param>
/// <param name="TileSize">Raster: the size each tile is drawn at, in CSS pixels. Images may carry more pixels than this, for sharp drawing on high-density screens.</param>
/// <param name="Attribution">The credit the provider's terms require, as HTML.</param>
public sealed record BasemapInfo(
    string Provider,
    TileFormat Format,
    string TilesUrl,
    int MinZoom,
    int MaxZoom,
    int TileSize,
    string Attribution
);

/// <summary>
/// A live traffic layer, whatever provider is behind it. Tiles come through the bridge, which holds the provider's key, and
/// only while online: offline the layer is simply empty.
/// </summary>
/// <param name="TilesUrl">The traffic tiles: <c>…/maps/traffic/{z}/{x}/{y}</c>.</param>
/// <param name="MinZoom">Below this the layer draws nothing, and no tiles are asked for.</param>
/// <param name="MaxZoom">The highest zoom the provider has. The renderer scales past it.</param>
/// <param name="RefreshSeconds">How often the provider's data changes, so how often the page should fetch the tiles again.</param>
/// <param name="SourceLayer">Vector: the layer inside each tile holding the road segments.</param>
/// <param name="SpeedRatioProperty">
/// Vector: the property holding current speed over free-flow speed, from 0 (stopped) to 1 (free flow).
/// </param>
/// <param name="ClosedProperty">Vector: the property that is true on a closed road. Null when the provider does not mark closures.</param>
/// <param name="TileSize">Raster: the images' size in pixels.</param>
/// <param name="Attribution">The credit the provider's terms require, as HTML.</param>
public sealed record TrafficInfo(
    string TilesUrl,
    TileFormat Format,
    int MinZoom,
    int MaxZoom,
    int RefreshSeconds,
    string? SourceLayer,
    string? SpeedRatioProperty,
    string? ClosedProperty,
    int TileSize,
    string Attribution
);

/// <summary>A region the page can download, merged with what is on the device.</summary>
/// <param name="Bounds"><c>[west, south, east, north]</c> in degrees.</param>
/// <param name="MapSize">Bytes to download for the map tiles.</param>
/// <param name="DirectionsSize">Bytes to download for on-device directions, when the region offers them.</param>
/// <param name="MapInstalled">Whether the region's map is on the device, so it draws offline.</param>
/// <param name="DirectionsInstalled">Whether the region's road network is on the device, so directions inside it work offline.</param>
/// <param name="UpdateAvailable">Whether the catalog has a newer build of an installed part.</param>
/// <param name="Download">The download in progress for this region, if any.</param>
public sealed record MapRegion(
    string Id,
    string Name,
    double[] Bounds,
    long MapSize,
    long? DirectionsSize,
    bool MapInstalled,
    bool DirectionsInstalled,
    bool UpdateAvailable,
    MapPackDownload? Download = null
);

/// <param name="Regions">The catalog's regions, and any installed region the catalog no longer lists.</param>
/// <param name="CatalogReachable">False when the catalog could not be fetched — offline, say. The regions are then the ones last seen and the ones installed.</param>
/// <param name="InstalledBytes">Disk space the installed regions take.</param>
public sealed record MapCatalog(IReadOnlyList<MapRegion> Regions, bool CatalogReachable, long InstalledBytes);

/// <param name="Directions">Also download the road network, so directions inside the region work offline. Ignored when the region does not offer it.</param>
public sealed record MapPackInstallRequest(bool Directions = false);

public enum MapPackDownloadState
{
    Queued,
    Downloading,

    /// <summary>Checking the download against the catalog's signed hash before it is used.</summary>
    Verifying,

    Installed,
    Failed,
    Cancelled
}

/// <summary>A region's download. Raised as <c>maps.download</c> while it runs and once more when it ends.</summary>
/// <param name="Directions">Whether the download includes the road network.</param>
/// <param name="Error">Why it failed.</param>
public sealed record MapPackDownload(
    string RegionId,
    bool Directions,
    MapPackDownloadState State,
    long BytesDownloaded,
    long TotalBytes,
    string? Error = null
);

/// <summary>What a traffic incident is, whatever each provider calls it. The map colours incidents by it.</summary>
public enum TrafficIncidentKind
{
    Other,
    Accident,
    Congestion,
    RoadWorks,
    RoadClosed,
    LaneClosed,
    Weather,
    Hazard,
    BrokenDownVehicle
}

/// <summary>
/// A live layer of traffic incidents, whatever provider is behind it: vector tiles holding the stretch of road an incident
/// affects as lines, where it is as points, or both. Tiles come through the bridge, which holds the provider's key, and only
/// while online.
/// </summary>
/// <param name="TilesUrl">The incident tiles: <c>…/maps/incidents/{z}/{x}/{y}</c>.</param>
/// <param name="MinZoom">Below this the layer draws nothing, and no tiles are asked for.</param>
/// <param name="MaxZoom">The highest zoom the provider has. The renderer scales past it.</param>
/// <param name="RefreshSeconds">How often the page should fetch the tiles again.</param>
/// <param name="LineSourceLayer">The layer inside each tile holding the affected stretches of road. Null when the provider has none.</param>
/// <param name="PointSourceLayer">The layer inside each tile holding incident locations. Null when the provider has none.</param>
/// <param name="KindProperty">The property holding the provider's incident type.</param>
/// <param name="Kinds">The provider's values of <paramref name="KindProperty"/>, as text, and what each is. Anything not listed is <see cref="TrafficIncidentKind.Other"/>.</param>
/// <param name="DescriptionProperty">The property holding a description to show for an incident. Null when there is none.</param>
/// <param name="DelayProperty">The property holding the delay the incident causes, in seconds. Null when there is none.</param>
/// <param name="ClusterSizeProperty">The property holding how many incidents a point stands for, where the provider groups them. Null when it does not.</param>
/// <param name="Attribution">The credit the provider's terms require, as HTML.</param>
public sealed record TrafficIncidentInfo(
    string TilesUrl,
    int MinZoom,
    int MaxZoom,
    int RefreshSeconds,
    string? LineSourceLayer,
    string? PointSourceLayer,
    string KindProperty,
    IReadOnlyDictionary<string, TrafficIncidentKind> Kinds,
    string? DescriptionProperty,
    string? DelayProperty,
    string? ClusterSizeProperty,
    string Attribution
);

/// <summary>Serialization for every map contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MapsInfo))]
[JsonSerializable(typeof(BasemapInfo))]
[JsonSerializable(typeof(TrafficInfo))]
[JsonSerializable(typeof(TrafficIncidentInfo))]
[JsonSerializable(typeof(MapRegion))]
[JsonSerializable(typeof(MapCatalog))]
[JsonSerializable(typeof(MapPackInstallRequest))]
[JsonSerializable(typeof(MapPackDownload))]
public partial class MapsJsonContext : JsonSerializerContext;
