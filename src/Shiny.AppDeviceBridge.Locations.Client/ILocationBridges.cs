using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Locations.Client;

/// <summary>
/// GPS: permission, the last and current position, and a listener whose readings arrive as events. Android, iOS, Mac
/// Catalyst and Windows; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("gps", typeof(LocationsJsonContext))]
public interface IGpsBridge
{
    /// <summary>The permission state for a kind of GPS use, without prompting.</summary>
    [BridgeGet("status")]
    Task<LocationAccessResult> GetStatusAsync(GpsAccessMode mode = GpsAccessMode.Foreground, CancellationToken cancellationToken = default);

    /// <summary>Requests the permission the listener settings need — background access for a background mode.</summary>
    [BridgePost("access")]
    Task<LocationAccessResult> RequestAccessAsync(GpsListenerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The last known position, or null when there is none.</summary>
    [BridgeGet("last")]
    Task<GpsReading?> GetLastReadingAsync(CancellationToken cancellationToken = default);

    /// <summary>A fresh position, or null when none could be had.</summary>
    [BridgeGet("current")]
    Task<GpsReading?> GetCurrentPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>The running listener's settings, or null when not listening.</summary>
    [BridgeGet("listener")]
    Task<GpsListenerSettings?> GetListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts listening; readings arrive through <see cref="OnReadingAsync"/>, and in the background to the web app's
    /// <c>gps</c> handler. Fails with 409 without permission or while another listener runs.
    /// </summary>
    [BridgePost("listener")]
    Task StartListenerAsync(GpsListenerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Stops listening.</summary>
    [BridgeDelete("listener")]
    Task StopListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>A reading from the listener, while the page is open.</summary>
    [BridgeEvent("gps.reading")]
    Task<IAsyncDisposable> OnReadingAsync(Func<GpsReading, Task> handler);
}

/// <summary>
/// Geofences: monitor circular regions and hear when the device enters or leaves one. Android, iOS, Mac Catalyst and
/// Windows; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("geofences", typeof(LocationsJsonContext))]
public interface IGeofencesBridge
{
    /// <summary>The permission state, without prompting.</summary>
    [BridgeGet("status")]
    Task<LocationAccessResult> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the background location permission geofencing needs.</summary>
    [BridgePost("access")]
    Task<LocationAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

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

/// <summary>
/// Motion activity: walking, running, cycling, driving or stationary, as the OS's activity recognition reports it.
/// Android, iOS and Mac Catalyst; elsewhere every call fails with 501. There is no history, only the latest reading and
/// live ones.
/// </summary>
[BridgeClient("motion", typeof(LocationsJsonContext))]
public interface IMotionBridge
{
    /// <summary>The permission state, without prompting.</summary>
    [BridgeGet("status")]
    Task<LocationAccessResult> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests motion activity permission.</summary>
    [BridgePost("access")]
    Task<LocationAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>The latest reading, or null when there is none.</summary>
    [BridgeGet("current")]
    Task<MotionActivity?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the listener is running.</summary>
    [BridgeGet("listener")]
    Task<MotionListener> GetListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts listening; readings arrive through <see cref="OnActivityAsync"/>, and in the background to the web app's
    /// <c>motion</c> handler. Fails with 409 without permission.
    /// </summary>
    [BridgePost("listener")]
    Task StartListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening.</summary>
    [BridgeDelete("listener")]
    Task StopListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>A reading from the listener, while the page is open.</summary>
    [BridgeEvent("motion.activity")]
    Task<IAsyncDisposable> OnActivityAsync(Func<MotionActivity, Task> handler);
}
