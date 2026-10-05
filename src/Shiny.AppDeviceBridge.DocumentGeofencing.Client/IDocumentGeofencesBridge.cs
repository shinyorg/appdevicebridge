using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.DocumentGeofencing.Client;

/// <summary>
/// Document geofencing: GPS-driven monitoring of polygon and proximity regions stored in Shiny.DocumentDb, with no
/// platform limit on how many. The app registers the region sets in C#; the page starts and stops monitoring, reads
/// where the device is, and hears changes. Android, iOS and Mac Catalyst; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("documentgeofences", typeof(DocumentGeofencingJsonContext))]
public interface IDocumentGeofencesBridge
{
    /// <summary>Whether monitoring is running, and the region sets the app registered.</summary>
    [BridgeGet("status")]
    Task<DocumentGeofenceStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the background location permission monitoring needs.</summary>
    [BridgePost("access")]
    Task<DocumentGeofenceAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts monitoring; a no-op while it runs. Fails with 409 without permission and with 501 when the store's
    /// provider cannot run spatial queries.
    /// </summary>
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
    Task<IReadOnlyList<DocumentCurrentRegion>> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>The device entered or left a region, while the page is open.</summary>
    [BridgeEvent("documentgeofence.change")]
    Task<IAsyncDisposable> OnChangeAsync(Func<DocumentRegionChange, Task> handler);
}
