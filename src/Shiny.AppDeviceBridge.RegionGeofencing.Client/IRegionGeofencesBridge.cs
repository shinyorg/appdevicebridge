using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.RegionGeofencing.Client;

/// <summary>
/// Region geofencing: GPS-driven monitoring of polygon, proximity and reverse-geocoded regions, with no platform limit
/// on how many. The app registers the region sets in C#; the page starts and stops monitoring, reads where the device
/// is, and hears changes. Android, iOS and Mac Catalyst; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("regiongeofences", typeof(RegionGeofencingJsonContext))]
public interface IRegionGeofencesBridge
{
    /// <summary>Whether monitoring is running, and the region sets the app registered.</summary>
    [BridgeGet("status")]
    Task<RegionGeofenceStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the background location permission monitoring needs.</summary>
    [BridgePost("access")]
    Task<RegionGeofenceAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts monitoring; a no-op while it runs. Fails with 409 without permission.</summary>
    [BridgePost("start")]
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring and forgets which regions the device was in.</summary>
    [BridgePost("stop")]
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The region the device is in for each set, from the last GPS reading, without raising changes. Empty when there
    /// is no reading.
    /// </summary>
    [BridgeGet("current")]
    Task<IReadOnlyList<CurrentRegion>> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>The device entered or left a region, while the page is open.</summary>
    [BridgeEvent("regiongeofence.change")]
    Task<IAsyncDisposable> OnChangeAsync(Func<RegionChange, Task> handler);
}
