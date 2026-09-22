using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Maps.Client;

/// <summary>
/// Everything a map renderer needs to draw from the bridge. The URLs are templates MapLibre (or any vector-tile renderer)
/// fills in itself, relative to the page's origin and already under the bridge's prefix.
/// </summary>
/// <param name="TilesUrl">The vector tiles: <c>…/maps/tiles/{z}/{x}/{y}</c>. Served from a downloaded region when one covers the tile, from the online source otherwise.</param>
/// <param name="GlyphsUrl">The label fonts: <c>…/maps/glyphs/{fontstack}/{range}.pbf</c>.</param>
/// <param name="SpritesUrl">The icon sheets. Append a flavor — <c>light</c>, <c>dark</c>, <c>white</c>, <c>grayscale</c>, <c>black</c> — for MapLibre's <c>sprite</c>.</param>
/// <param name="MaxZoom">The highest zoom the tiles carry. The renderer draws closer zooms by scaling these, which vector tiles do cleanly.</param>
/// <param name="Online">Whether the app configured an online tile source. Without one, only downloaded regions draw.</param>
/// <param name="Catalog">Whether the app configured a region catalog to download from.</param>
/// <param name="Attribution">The credit the map data's licence requires, as HTML.</param>
public sealed record MapsInfo(
    string TilesUrl,
    string GlyphsUrl,
    string SpritesUrl,
    int MaxZoom,
    bool Online,
    bool Catalog,
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

/// <summary>Serialization for every map contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MapsInfo))]
[JsonSerializable(typeof(MapRegion))]
[JsonSerializable(typeof(MapCatalog))]
[JsonSerializable(typeof(MapPackInstallRequest))]
[JsonSerializable(typeof(MapPackDownload))]
public partial class MapsJsonContext : JsonSerializerContext;
