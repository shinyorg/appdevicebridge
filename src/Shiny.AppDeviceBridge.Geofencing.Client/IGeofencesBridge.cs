using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Geofencing.Client;

/// <summary>
/// Geofences: monitor circular regions and hear when the device enters or leaves one. Android, iOS, Mac Catalyst and
/// Windows; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("geofences", typeof(GeofencingJsonContext))]
public interface IGeofencesBridge
{
    /// <summary>The permission state, without prompting.</summary>
    [BridgeGet("status")]
    Task<GeofenceAccessResult> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the background location permission geofencing needs.</summary>
    [BridgePost("access")]
    Task<GeofenceAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>The regions being monitored.</summary>
    [BridgeGet("regions")]
    Task<IReadOnlyList<GeofenceRegion>> GetRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts monitoring a region. Fails with 409 without permission.</summary>
    [BridgePost("regions")]
    Task StartMonitoringAsync(GeofenceRegion region, CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring every region.</summary>
    [BridgeDelete("regions")]
    Task StopAllMonitoringAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring one region.</summary>
    [BridgeDelete("regions/{identifier}")]
    Task StopMonitoringAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>Whether the device is inside a monitored region now. Fails with 404 for a region not monitored.</summary>
    [BridgeGet("regions/{identifier}/state")]
    Task<GeofenceStatus> GetStateAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>The device entered or left a region, while the page is open.</summary>
    [BridgeEvent("geofence.status")]
    Task<IAsyncDisposable> OnStatusAsync(Func<GeofenceStatus, Task> handler);
}
