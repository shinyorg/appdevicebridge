using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Beacons.Client;

/// <summary>A part of the beacons bridge, each with its own permission and registration.</summary>
public enum BeaconFeature { Ranging, Monitoring, Eddystone, Broadcasting }

/// <summary>A distance bucket derived from the estimated distance.</summary>
public enum BeaconProximity { Unknown, Immediate, Near, Far }

public enum BeaconRegionState { Unknown, Entered, Exited }

public enum EddystoneFrameType { Uid, Url, Tlm, Eid }

/// <summary>The permission state of each feature, without prompting. A feature the app did not register is <c>NotSupported</c>.</summary>
/// <param name="BroadcastingSupported">Whether the app registered broadcasting and the platform has it.</param>
/// <param name="Broadcasting">Whether the device is advertising as a beacon now.</param>
public sealed record BeaconsStatus(
    AccessState Ranging,
    AccessState Monitoring,
    AccessState Eddystone,
    bool BroadcastingSupported,
    bool Broadcasting
);

public sealed record BeaconAccessRequest(BeaconFeature Feature);

public sealed record BeaconAccessResult(AccessState Access);

/// <summary>A set of iBeacons, by UUID and optionally narrowed by major and then minor.</summary>
/// <param name="Identifier">Names the region; ranging or monitoring one with an identifier already in use replaces it.</param>
/// <param name="Minor">Needs <paramref name="Major"/>.</param>
/// <param name="NotifyOnEntry">Monitoring only.</param>
/// <param name="NotifyOnExit">Monitoring only.</param>
public sealed record BeaconRegion(
    string Identifier,
    Guid Uuid,
    ushort? Major = null,
    ushort? Minor = null,
    bool NotifyOnEntry = true,
    bool NotifyOnExit = true
);

/// <summary>One observation of an iBeacon while ranging.</summary>
/// <param name="Region">The identifier of the ranged region it belongs to.</param>
/// <param name="Rssi">dBm.</param>
/// <param name="Distance">Estimated meters.</param>
/// <param name="TxPower">The beacon's calibrated power at one meter. Null on Apple platforms, where CoreLocation hides it.</param>
public sealed record BeaconReading(
    string Region,
    Guid Uuid,
    ushort Major,
    ushort Minor,
    int Rssi,
    BeaconProximity Proximity,
    double Distance,
    sbyte? TxPower,
    DateTimeOffset Timestamp
);

public sealed record BeaconRegionStatus(string Identifier, BeaconRegionState State);

/// <summary>
/// An Eddystone frame. Which fields are set depends on <paramref name="FrameType"/>: <c>Uid</c> has
/// <paramref name="Namespace"/> and <paramref name="Instance"/>, <c>Url</c> has <paramref name="Url"/>, <c>Eid</c> has
/// <paramref name="EphemeralId"/>, and those three have <paramref name="TxPower"/>, <paramref name="Distance"/> and
/// <paramref name="Proximity"/>. <c>Tlm</c> is the beacon's telemetry.
/// </summary>
/// <param name="PeripheralId">The advertising device: a GUID on Apple platforms, a MAC address on Android.</param>
/// <param name="Namespace">10 bytes, hex.</param>
/// <param name="Instance">6 bytes, hex.</param>
/// <param name="EphemeralId">Hex.</param>
/// <param name="Uptime">Telemetry: time since the beacon powered up.</param>
public sealed record EddystoneFrame(
    EddystoneFrameType FrameType,
    string PeripheralId,
    int Rssi,
    DateTimeOffset Timestamp,
    string? Namespace = null,
    string? Instance = null,
    string? Url = null,
    string? EphemeralId = null,
    sbyte? TxPower = null,
    double? Distance = null,
    BeaconProximity? Proximity = null,
    double? BatteryVolts = null,
    double? TemperatureCelsius = null,
    uint? AdvertisementCount = null,
    TimeSpan? Uptime = null,
    bool? Encrypted = null
);

/// <param name="TxPower">The power a receiver sees at one meter, dBm; -59 when null. It calibrates distance and does not change the radio.</param>
/// <summary>Advertising as an iBeacon.</summary>
public sealed record BeaconBroadcast(Guid Uuid, ushort Major, ushort Minor, sbyte? TxPower = null);

/// <param name="Namespace">10 bytes, hex.</param>
/// <param name="Instance">6 bytes, hex.</param>
/// <param name="TxPower">The power at zero meters, dBm; -18 when null.</param>
public sealed record EddystoneUidBroadcast(string Namespace, string Instance, sbyte? TxPower = null);

/// <param name="Url">Must compress to 17 bytes or fewer under Eddystone's URL encoding.</param>
/// <param name="TxPower">The power at zero meters, dBm; -18 when null.</param>
public sealed record EddystoneUrlBroadcast(string Url, sbyte? TxPower = null);

public sealed record BeaconBroadcastStatus(bool Broadcasting);

/// <summary>Serialization for every beacon contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BeaconsStatus))]
[JsonSerializable(typeof(BeaconAccessRequest))]
[JsonSerializable(typeof(BeaconAccessResult))]
[JsonSerializable(typeof(BeaconRegion))]
[JsonSerializable(typeof(IReadOnlyList<BeaconRegion>))]
[JsonSerializable(typeof(BeaconReading))]
[JsonSerializable(typeof(BeaconRegionStatus))]
[JsonSerializable(typeof(EddystoneFrame))]
[JsonSerializable(typeof(BeaconBroadcast))]
[JsonSerializable(typeof(EddystoneUidBroadcast))]
[JsonSerializable(typeof(EddystoneUrlBroadcast))]
[JsonSerializable(typeof(BeaconBroadcastStatus))]
public partial class BeaconsJsonContext : JsonSerializerContext;
