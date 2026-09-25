using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Maps.Client;

/// <summary>
/// Turn-by-turn directions. A route is computed on the device when the user downloaded the road network of a region
/// covering every stop and the platform can run the router, and by the app's online router otherwise. Addresses become stops
/// through the app's geocoder. The router's and the geocoder's addresses and keys stay in the native app; the page never
/// sees them.
/// </summary>
[BridgeClient("directions", typeof(DirectionsJsonContext))]
public interface IDirectionsBridge
{
    /// <summary>Which sources can answer.</summary>
    [BridgeGet]
    Task<DirectionsInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a route through the stops. Fails with 400 for fewer than two stops or a stop off the map, 404 when no
    /// route connects them, 503 (<c>offline_unavailable</c>) when the device cannot compute it and the online router cannot
    /// be reached, 501 when neither is available on this platform and app.
    /// </summary>
    [BridgePost("route")]
    Task<DirectionsRoute> RouteAsync(DirectionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds places matching an address or a name — "1701 Wynkoop St, Denver", "Red Rocks Amphitheatre" — best first, for
    /// turning what the user typed into a <see cref="RouteStop"/>. Always online. Fails with 400 for an empty query or one
    /// over 200 characters, 503 (<c>geocoder_unavailable</c>) when the geocoder cannot be reached, 501 when the app
    /// configured no geocoder.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="limit">How many places, 1 to 20.</param>
    /// <param name="language">An IETF language tag for the names, such as <c>en-US</c>. The geocoder's default when null.</param>
    [BridgeGet("geocode")]
    Task<GeocodeResult> GeocodeAsync(string query, int limit = 5, string? language = null, CancellationToken cancellationToken = default);
}
