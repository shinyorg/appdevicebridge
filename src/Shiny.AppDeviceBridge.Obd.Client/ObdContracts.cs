using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Obd.Client;

public enum ObdTransport { Ble, Wifi }

public enum ObdAdapterType { Unknown, Elm327, ObdLink }

/// <param name="AdapterIdentifier">What the adapter reports about itself.</param>
/// <param name="Protocol">The vehicle protocol the adapter settled on.</param>
/// <param name="BluetoothSupported">Whether Bluetooth LE adapters can be used here.</param>
/// <param name="Monitor">The running monitor.</param>
public sealed record ObdStatus(
    bool Connected,
    ObdTransport? Transport,
    string? AdapterId,
    string? AdapterName,
    ObdAdapterType? AdapterType,
    string? AdapterIdentifier,
    string? Protocol,
    bool BluetoothSupported,
    AccessState? BluetoothAccess,
    bool Scanning,
    ObdMonitor? Monitor
);

/// <param name="Name">Such as <c>engineRpm</c>.</param>
/// <param name="Pid">The raw request it sends.</param>
public sealed record ObdCommandInfo(string Name, string Pid, string? Unit);

/// <param name="ScanMs">Up to 30,000 ms; 5 seconds when null.</param>
/// <param name="NameFilter">Only adapters whose name contains this; at most 64 characters.</param>
public sealed record ObdScanRequest(ObdTransport Transport = ObdTransport.Ble, int? ScanMs = null, string? NameFilter = null);

/// <param name="Id">A Bluetooth peripheral id, or a Wi-Fi adapter's host and port.</param>
public sealed record ObdAdapter(string Id, string Name, ObdTransport Transport, string? Host, int? Port);

/// <summary>An adapter to connect to: <see cref="PeripheralUuid"/> for Bluetooth, <see cref="Host"/> and <see cref="Port"/> for Wi-Fi.</summary>
/// <param name="TimeoutMs">Up to 60,000 ms; 30 seconds when null.</param>
/// <param name="ServiceUuid">For an adapter that does not use the usual GATT service.</param>
public sealed record ObdConnectRequest(
    ObdTransport Transport = ObdTransport.Ble,
    string? PeripheralUuid = null,
    string? Host = null,
    int? Port = null,
    int? TimeoutMs = null,
    string? ServiceUuid = null,
    string? ReadCharacteristicUuid = null,
    string? WriteCharacteristicUuid = null
);

/// <param name="Command">A name from the command list, such as <c>engineRpm</c>.</param>
public sealed record ObdReadRequest(string Command);

/// <param name="Raw">Such as <c>010C</c> or <c>AT RV</c>.</param>
public sealed record ObdRawRequest(string Raw);

/// <param name="Value">A numeric reading.</param>
/// <param name="Text">A text reading.</param>
public sealed record ObdReading(string Command, double? Value, string? Text, string? Unit);

public sealed record ObdRawResult(string Command, string Response);

public sealed record ObdVin(string Vin);

/// <summary>Each list is null where the vehicle did not answer that request.</summary>
public sealed record ObdTroubleCodes(IReadOnlyList<string>? Stored, IReadOnlyList<string>? Pending, IReadOnlyList<string>? Permanent);

public sealed record ObdClearResult(bool Cleared);

/// <param name="IntervalMs">250–60,000 ms; one second when null.</param>
public sealed record ObdMonitorRequest(IReadOnlyList<string> Commands, int? IntervalMs = null);

public sealed record ObdMonitor(IReadOnlyList<string> Commands, int IntervalMs);

/// <param name="Error">Why the reading failed; the value is null then.</param>
public sealed record ObdMonitorReading(string Command, double? Value, string? Text, string? Unit, string? Error, DateTimeOffset Timestamp);

public sealed record ObdDisconnected(string Reason, ObdTransport Transport, string AdapterId);

/// <summary>Serialization for every OBD contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ObdStatus))]
[JsonSerializable(typeof(IReadOnlyList<ObdCommandInfo>))]
[JsonSerializable(typeof(ObdScanRequest))]
[JsonSerializable(typeof(IReadOnlyList<ObdAdapter>))]
[JsonSerializable(typeof(ObdConnectRequest))]
[JsonSerializable(typeof(ObdReadRequest))]
[JsonSerializable(typeof(ObdRawRequest))]
[JsonSerializable(typeof(ObdReading))]
[JsonSerializable(typeof(ObdRawResult))]
[JsonSerializable(typeof(ObdVin))]
[JsonSerializable(typeof(ObdTroubleCodes))]
[JsonSerializable(typeof(ObdClearResult))]
[JsonSerializable(typeof(ObdMonitorRequest))]
[JsonSerializable(typeof(ObdMonitor))]
[JsonSerializable(typeof(ObdMonitorReading))]
[JsonSerializable(typeof(ObdDisconnected))]
public partial class ObdJsonContext : JsonSerializerContext;
