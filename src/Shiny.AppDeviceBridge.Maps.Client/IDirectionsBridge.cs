using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Maps.Client;

/// <summary>
/// Turn-by-turn directions. A route is computed on the device when the user downloaded the road network of a region
/// covering every stop and the platform can run the router, and by the app's online router otherwise. The online
/// router's address and key stay in the native app; the page never sees them.
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
}
