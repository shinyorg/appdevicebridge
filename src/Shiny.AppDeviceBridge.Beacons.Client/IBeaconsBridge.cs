using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Beacons.Client;

/// <summary>
/// Beacons: range iBeacons in the foreground, monitor iBeacon regions in the background, scan for Eddystone frames, and
/// advertise the device as a beacon. Android, iOS, Mac Catalyst, macOS and Windows; a feature the app did not register,
/// or a platform without it, fails with 501.
/// </summary>
[BridgeClient("beacons", typeof(BeaconsJsonContext))]
public interface IBeaconsBridge
{
    /// <summary>The permission state of each feature, and whether the device is broadcasting.</summary>
    [BridgeGet("status")]
    Task<BeaconsStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the permission a feature needs — background location for monitoring on Apple platforms.</summary>
    [BridgePost("access")]
    Task<BeaconAccessResult> RequestAccessAsync(BeaconAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>The regions being ranged.</summary>
    [BridgeGet("ranging")]
    Task<IReadOnlyList<BeaconRegion>> GetRangedRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts ranging a region; readings arrive through <see cref="OnBeaconAsync"/>, so listen first: without a listener
    /// it fails with 409. Several regions can be ranged at once. Ranging is a foreground activity, and it stops once
    /// nothing listens to it.
    /// </summary>
    [BridgePost("ranging")]
    Task StartRangingAsync(BeaconRegion region, CancellationToken cancellationToken = default);

    /// <summary>Stops ranging every region.</summary>
    [BridgeDelete("ranging")]
    Task StopAllRangingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops ranging one region.</summary>
    [BridgeDelete("ranging/{identifier}")]
    Task StopRangingAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>The regions being monitored. Monitoring survives the app restarting.</summary>
    [BridgeGet("regions")]
    Task<IReadOnlyList<BeaconRegion>> GetMonitoredRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts monitoring a region; transitions arrive through <see cref="OnRegionAsync"/> while a page is open, and to
    /// the web app's <c>beacon</c> handler otherwise. Fails with 409 without permission.
    /// </summary>
    [BridgePost("regions")]
    Task StartMonitoringAsync(BeaconRegion region, CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring every region.</summary>
    [BridgeDelete("regions")]
    Task StopAllMonitoringAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring one region.</summary>
    [BridgeDelete("regions/{identifier}")]
    Task StopMonitoringAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>Whether the device is inside a monitored region now. Fails with 404 for a region not monitored.</summary>
    [BridgeGet("regions/{identifier}/state")]
    Task<BeaconRegionStatus> GetRegionStateAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts scanning for Eddystone frames; they arrive through <see cref="OnEddystoneAsync"/>, so listen first: without
    /// a listener it fails with 409. The scan stops once nothing listens to it.
    /// </summary>
    [BridgePost("eddystone")]
    Task StartEddystoneScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops scanning for Eddystone frames.</summary>
    [BridgeDelete("eddystone")]
    Task StopEddystoneScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the device is advertising as a beacon.</summary>
    [BridgeGet("broadcast")]
    Task<BeaconBroadcastStatus> GetBroadcastAsync(CancellationToken cancellationToken = default);

    /// <summary>Advertises as an iBeacon, replacing any running advertisement. Apple platforms stop it in the background.</summary>
    [BridgePost("broadcast/ibeacon")]
    Task BroadcastIBeaconAsync(BeaconBroadcast broadcast, CancellationToken cancellationToken = default);

    /// <summary>Advertises an Eddystone-UID frame, replacing any running advertisement.</summary>
    [BridgePost("broadcast/eddystone-uid")]
    Task BroadcastEddystoneUidAsync(EddystoneUidBroadcast broadcast, CancellationToken cancellationToken = default);

    /// <summary>Advertises an Eddystone-URL frame, replacing any running advertisement. Fails with 400 for a URL too long to encode.</summary>
    [BridgePost("broadcast/eddystone-url")]
    Task BroadcastEddystoneUrlAsync(EddystoneUrlBroadcast broadcast, CancellationToken cancellationToken = default);

    /// <summary>Stops advertising.</summary>
    [BridgeDelete("broadcast")]
    Task StopBroadcastAsync(CancellationToken cancellationToken = default);

    /// <summary>An iBeacon seen while ranging.</summary>
    [BridgeEvent("beacons.ranged")]
    Task<IAsyncDisposable> OnBeaconAsync(Func<BeaconReading, Task> handler);

    /// <summary>The device entered or left a monitored region, while the page is open.</summary>
    [BridgeEvent("beacons.region")]
    Task<IAsyncDisposable> OnRegionAsync(Func<BeaconRegionStatus, Task> handler);

    /// <summary>An Eddystone frame seen while scanning.</summary>
    [BridgeEvent("beacons.eddystone")]
    Task<IAsyncDisposable> OnEddystoneAsync(Func<EddystoneFrame, Task> handler);
}
