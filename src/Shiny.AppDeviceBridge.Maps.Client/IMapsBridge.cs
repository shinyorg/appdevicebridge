using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Maps.Client;

/// <summary>
/// Vector maps the page draws with MapLibre or any other vector-tile renderer. Tiles come from a region the user
/// downloaded when one covers them, and from the app's online source otherwise, so the page asks for one set of URLs and
/// never has to know which. Regions are listed from the app's signed catalog and downloaded on request.
/// </summary>
[BridgeClient("maps", typeof(MapsJsonContext))]
public interface IMapsBridge
{
    /// <summary>The URLs to hand the renderer, and what the app configured.</summary>
    [BridgeGet]
    Task<MapsInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The downloadable regions and what is installed. The catalog is fetched again when <paramref name="refresh"/> is true
    /// or the last copy is old; offline, the last copy is used.
    /// </summary>
    [BridgeGet("regions")]
    Task<MapCatalog> GetRegionsAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts downloading a region — its map, and its road network when asked — or updates an installed one to the
    /// catalog's build. Returns at once; progress arrives as <c>maps.download</c>. Fails with 404 for a region the catalog
    /// does not list, 409 when one is already downloading, 501 when the app configured no catalog.
    /// </summary>
    [BridgePost("regions/{id}")]
    Task<MapPackDownload> InstallAsync(string id, MapPackInstallRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a region — map and road network — from the device, cancelling its download if one is running.</summary>
    [BridgeDelete("regions/{id}")]
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes only a region's road network: the map still draws offline, directions go online.</summary>
    [BridgeDelete("regions/{id}/directions")]
    Task RemoveDirectionsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Cancels a region's download. What was already installed stays.</summary>
    [BridgeDelete("regions/{id}/download")]
    Task CancelDownloadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// One vector tile, as MapLibre asks for it through <see cref="MapsInfo.TilesUrl"/>. 204 when no source has it — outside
    /// every downloaded region while offline.
    /// </summary>
    [BridgeGet("tiles/{z}/{x}/{y}")]
    Task<Stream> GetTileAsync(int z, int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// One live traffic tile, as the renderer asks for it through <see cref="TrafficInfo.TilesUrl"/>: a vector tile or an
    /// image, as <see cref="TrafficInfo.Format"/> says. 204 when the provider has nothing there or cannot be reached; 501 when
    /// the app configured no traffic provider.
    /// </summary>
    [BridgeGet("traffic/{z}/{x}/{y}")]
    Task<Stream> GetTrafficTileAsync(int z, int x, int y, CancellationToken cancellationToken = default);

    /// <summary>A range of label glyphs, as MapLibre asks for it through <see cref="MapsInfo.GlyphsUrl"/>: <c>0-255.pbf</c>.</summary>
    [BridgeGet("glyphs/{fontstack}/{range}")]
    Task<Stream> GetGlyphsAsync(string fontstack, string range, CancellationToken cancellationToken = default);

    /// <summary>A sprite sheet or its index, as MapLibre asks for it: <c>light.json</c>, <c>light@2x.png</c>.</summary>
    [BridgeGet("sprites/{name}")]
    Task<Stream> GetSpriteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A region's download moved on, finished, failed or was cancelled.</summary>
    [BridgeEvent("maps.download")]
    Task<IAsyncDisposable> OnDownloadAsync(Func<MapPackDownload, Task> handler);
}
