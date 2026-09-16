using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Obd.Client;

/// <summary>
/// OBD-II through one ELM327 or OBDLink adapter at a time, over Bluetooth LE or Wi-Fi: find and connect to an adapter,
/// read values, the VIN and trouble codes, and monitor readings live. Calls that need an adapter fail with 409 when none
/// is connected.
/// </summary>
[BridgeClient("obd", typeof(ObdJsonContext))]
public interface IObdBridge
{
    /// <summary>The connection, the adapter, Bluetooth availability, and the running monitor.</summary>
    [BridgeGet("status")]
    Task<ObdStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The named commands <see cref="ReadAsync"/> and <see cref="StartMonitorAsync"/> accept.</summary>
    [BridgeGet("commands")]
    Task<IReadOnlyList<ObdCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>The adapters seen during the scan window.</summary>
    [BridgePost("scan")]
    Task<IReadOnlyList<ObdAdapter>> ScanAsync(ObdScanRequest request, CancellationToken cancellationToken = default);

    /// <summary>The last scan's adapters.</summary>
    [BridgeGet("adapters")]
    Task<IReadOnlyList<ObdAdapter>> GetAdaptersAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects to an adapter — a Bluetooth peripheral, or a Wi-Fi host, probed when none is given.</summary>
    [BridgePost("connection")]
    Task<ObdStatus> ConnectAsync(ObdConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects, stopping the monitor.</summary>
    [BridgeDelete("connection")]
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one named value. Fails with 404 for a command not in <see cref="GetCommandsAsync"/>.</summary>
    [BridgePost("command")]
    Task<ObdReading> ReadAsync(ObdReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a raw request and returns the adapter's reply. Only read-only requests are accepted: OBD modes 01, 02, 03, 05,
    /// 06, 07, 09, 0A or 22 in hex, or a harmless AT command.
    /// </summary>
    [BridgePost("raw")]
    Task<ObdRawResult> SendRawAsync(ObdRawRequest request, CancellationToken cancellationToken = default);

    /// <summary>The vehicle identification number.</summary>
    [BridgeGet("vin")]
    Task<ObdVin> GetVinAsync(CancellationToken cancellationToken = default);

    /// <summary>Stored, pending and permanent trouble codes.</summary>
    [BridgeGet("dtc")]
    Task<ObdTroubleCodes> GetTroubleCodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears trouble codes. This also resets the emissions readiness monitors, which take several drive cycles to
    /// complete again, so <paramref name="confirm"/> must be true.
    /// </summary>
    [BridgeDelete("dtc")]
    Task<ObdClearResult> ClearTroubleCodesAsync(bool confirm, CancellationToken cancellationToken = default);

    /// <summary>Reads up to 10 named values on an interval; readings arrive through <see cref="OnReadingAsync"/>.</summary>
    [BridgePost("monitor")]
    Task<ObdMonitor> StartMonitorAsync(ObdMonitorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops the monitor.</summary>
    [BridgeDelete("monitor")]
    Task StopMonitorAsync(CancellationToken cancellationToken = default);

    /// <summary>A monitored reading, or the error reading it.</summary>
    [BridgeEvent("obd.reading")]
    Task<IAsyncDisposable> OnReadingAsync(Func<ObdMonitorReading, Task> handler);

    /// <summary>The adapter went away or stopped answering.</summary>
    [BridgeEvent("obd.disconnected")]
    Task<IAsyncDisposable> OnDisconnectedAsync(Func<ObdDisconnected, Task> handler);
}
