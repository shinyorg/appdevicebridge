using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.BluetoothLE.Client;

public enum BleConnectionState { Disconnected, Disconnecting, Connected, Connecting }

/// <summary>What a characteristic supports.</summary>
public enum BleCharacteristicProperty
{
    Broadcast,
    Read,
    WriteWithoutResponse,
    Write,
    Notify,
    Indicate,
    AuthenticatedSignedWrites,
    ExtendedProperties,
    NotifyEncryptionRequired,
    IndicateEncryptionRequired
}

public sealed record BleStatus(AccessState Access, bool IsScanning);

/// <param name="ServiceUuids">Only report peripherals advertising one of these services; every peripheral when empty.</param>
public sealed record BleScanRequest(IReadOnlyList<string>? ServiceUuids = null);

/// <summary>An advertisement.</summary>
/// <param name="ManufacturerData">Base64 in JSON.</param>
public sealed record BleScanResult(
    string Uuid,
    string? Name,
    int Rssi,
    string? LocalName,
    bool? IsConnectable,
    IReadOnlyList<string>? ServiceUuids,
    int? TxPower,
    ushort? ManufacturerId,
    byte[]? ManufacturerData
);

public sealed record BlePeripheral(string Uuid, string? Name, BleConnectionState Status, int Mtu);

/// <param name="AutoConnect">Reconnect when the peripheral comes back in range.</param>
/// <param name="TimeoutMs">How long to wait for the connection; 30 seconds when null.</param>
public sealed record BleConnectRequest(bool AutoConnect = true, int? TimeoutMs = null);

public sealed record BleService(string Uuid);

public sealed record BleCharacteristic(string ServiceUuid, string Uuid, bool IsNotifying, IReadOnlyList<BleCharacteristicProperty> Properties);

/// <param name="Data">Base64 in JSON.</param>
public sealed record BleCharacteristicValue(string ServiceUuid, string CharacteristicUuid, byte[]? Data);

/// <param name="Data">Base64 in JSON.</param>
/// <param name="WithResponse">Wait for the peripheral to acknowledge the write.</param>
public sealed record BleWriteRequest(byte[] Data, bool WithResponse = true);

public sealed record BleRssi(int Rssi);

public sealed record BlePeripheralStatus(string Uuid, BleConnectionState Status);

/// <param name="Data">Base64 in JSON.</param>
public sealed record BleNotification(string PeripheralUuid, string ServiceUuid, string CharacteristicUuid, byte[]? Data);

/// <param name="Operation"><c>scan</c> or <c>notify</c>.</param>
public sealed record BleError(string Operation, string? PeripheralUuid, string Message);

/// <summary>Serialization for every Bluetooth LE contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BleStatus))]
[JsonSerializable(typeof(BleScanRequest))]
[JsonSerializable(typeof(BleScanResult))]
[JsonSerializable(typeof(BlePeripheral))]
[JsonSerializable(typeof(IReadOnlyList<BlePeripheral>))]
[JsonSerializable(typeof(BleConnectRequest))]
[JsonSerializable(typeof(IReadOnlyList<BleService>))]
[JsonSerializable(typeof(IReadOnlyList<BleCharacteristic>))]
[JsonSerializable(typeof(BleCharacteristicValue))]
[JsonSerializable(typeof(BleWriteRequest))]
[JsonSerializable(typeof(BleRssi))]
[JsonSerializable(typeof(BlePeripheralStatus))]
[JsonSerializable(typeof(BleNotification))]
[JsonSerializable(typeof(BleError))]
public partial class BleJsonContext : JsonSerializerContext;
