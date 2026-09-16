using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.BluetoothLE.Client;

/// <summary>
/// Bluetooth LE central: scan, connect, discover services and characteristics, read, write and subscribe to
/// notifications. Peripheral ids are GUIDs on Apple platforms and MAC addresses on Android. On Linux every call fails
/// with 501. A peripheral must have been seen in a scan first; otherwise calls on it fail with 404.
/// </summary>
[BridgeClient("ble", typeof(BleJsonContext))]
public interface IBluetoothLEBridge
{
    /// <summary>Whether Bluetooth is available to the app, and whether a scan is running.</summary>
    [BridgeGet("status")]
    Task<BleStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests Bluetooth access.</summary>
    [BridgePost("access")]
    Task<BleStatus> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts scanning; results arrive through <see cref="OnScanResultAsync"/>. Starting again replaces the running scan.
    /// </summary>
    [BridgePost("scan")]
    Task StartScanAsync(BleScanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops scanning.</summary>
    [BridgeDelete("scan")]
    Task StopScanAsync(CancellationToken cancellationToken = default);

    /// <summary>The peripherals connected now.</summary>
    [BridgeGet("peripherals")]
    Task<IReadOnlyList<BlePeripheral>> GetConnectedPeripheralsAsync(CancellationToken cancellationToken = default);

    /// <summary>A peripheral seen in a scan.</summary>
    [BridgeGet("peripherals/{uuid}")]
    Task<BlePeripheral> GetPeripheralAsync(string uuid, CancellationToken cancellationToken = default);

    /// <summary>Connects. Fails with 504 on timeout, 409 when the connection fails.</summary>
    [BridgePost("peripherals/{uuid}/connection")]
    Task<BlePeripheral> ConnectAsync(string uuid, BleConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects, ending its notifications.</summary>
    [BridgeDelete("peripherals/{uuid}/connection")]
    Task DisconnectAsync(string uuid, CancellationToken cancellationToken = default);

    /// <summary>The connected peripheral's signal strength.</summary>
    [BridgeGet("peripherals/{uuid}/rssi")]
    Task<BleRssi> ReadRssiAsync(string uuid, CancellationToken cancellationToken = default);

    /// <summary>The connected peripheral's services.</summary>
    [BridgeGet("peripherals/{uuid}/services")]
    Task<IReadOnlyList<BleService>> GetServicesAsync(string uuid, CancellationToken cancellationToken = default);

    /// <summary>A service's characteristics.</summary>
    [BridgeGet("peripherals/{uuid}/services/{service}/characteristics")]
    Task<IReadOnlyList<BleCharacteristic>> GetCharacteristicsAsync(string uuid, string service, CancellationToken cancellationToken = default);

    /// <summary>Reads a characteristic.</summary>
    [BridgeGet("peripherals/{uuid}/services/{service}/characteristics/{characteristic}")]
    Task<BleCharacteristicValue> ReadAsync(string uuid, string service, string characteristic, CancellationToken cancellationToken = default);

    /// <summary>Writes a characteristic.</summary>
    [BridgePut("peripherals/{uuid}/services/{service}/characteristics/{characteristic}")]
    Task WriteAsync(string uuid, string service, string characteristic, BleWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to a characteristic; values arrive through <see cref="OnNotificationAsync"/>.</summary>
    [BridgePost("peripherals/{uuid}/services/{service}/characteristics/{characteristic}/notifications")]
    Task StartNotificationsAsync(string uuid, string service, string characteristic, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribes from a characteristic.</summary>
    [BridgeDelete("peripherals/{uuid}/services/{service}/characteristics/{characteristic}/notifications")]
    Task StopNotificationsAsync(string uuid, string service, string characteristic, CancellationToken cancellationToken = default);

    /// <summary>A peripheral seen by the running scan. Raised for every advertisement.</summary>
    [BridgeEvent("ble.scan")]
    Task<IAsyncDisposable> OnScanResultAsync(Func<BleScanResult, Task> handler);

    /// <summary>A peripheral this page connected to changed connection state.</summary>
    [BridgeEvent("ble.status")]
    Task<IAsyncDisposable> OnStatusAsync(Func<BlePeripheralStatus, Task> handler);

    /// <summary>A subscribed characteristic changed.</summary>
    [BridgeEvent("ble.notification")]
    Task<IAsyncDisposable> OnNotificationAsync(Func<BleNotification, Task> handler);

    /// <summary>A scan or a notification subscription failed and has ended.</summary>
    [BridgeEvent("ble.error")]
    Task<IAsyncDisposable> OnErrorAsync(Func<BleError, Task> handler);
}
