using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Wifi.Client;

/// <summary>Something a platform's Wi-Fi can do.</summary>
public enum WifiCapability
{
    Scan,
    Connect,
    Disconnect,
    CurrentNetwork,
    RadioState,
    RadioToggle,
    Hotspot,
    HotspotCustomConfiguration,
    HotspotClients,
    KnownNetworks,
    ForgetNetwork,
    ConnectKnownNetwork
}

public enum WifiSecurity
{
    Unknown,
    Open,
    Wep,
    WpaPsk,
    Wpa2Psk,
    Wpa3Psk,
    Enterprise,
    Owe,

    /// <summary>Some WPA personal variant, where the platform does not say which.</summary>
    Psk
}

public enum WifiBand
{
    Unknown,
    TwoPointFourGhz,
    FiveGhz,
    SixGhz
}

/// <summary>Wi-Fi on this device.</summary>
/// <param name="Capabilities">What this platform can do. Calls outside it fail with 501.</param>
/// <param name="Current">The network the device is on, where the platform can tell.</param>
/// <param name="HotspotSupported">Whether the app registered the hotspot and the platform supports one.</param>
public sealed record WifiStatus(IReadOnlyList<WifiCapability> Capabilities, WifiNetworkInfo? Current, bool HotspotSupported);

public sealed record WifiAccessResult(AccessState Access);

/// <summary>The network the device is on.</summary>
public sealed record WifiNetworkInfo(
    string InterfaceName,
    string? Ssid,
    string? Bssid,
    WifiSecurity Security,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsAddresses,
    string? Gateway,
    string? SubnetMask,
    int? SignalStrengthDbm,
    int? SignalStrengthPercent,
    int? FrequencyMhz,
    WifiBand Band,
    int? Channel
);

/// <summary>A network in range.</summary>
public sealed record WifiScanResult(
    string Ssid,
    string? Bssid,
    WifiSecurity Security,
    int? SignalStrengthDbm,
    int SignalStrengthPercent,
    int? FrequencyMhz,
    WifiBand Band,
    int? Channel,
    bool IsHidden,
    bool IsOpen
);

/// <summary>A network to join: by <see cref="Ssid"/>, or a saved one by <see cref="KnownNetworkId"/>.</summary>
public sealed class WifiConnectRequest
{
    public string? Ssid { get; init; }

    public string? Passphrase { get; init; }

    public WifiSecurity Security { get; init; } = WifiSecurity.Unknown;

    /// <summary>Join this access point in particular.</summary>
    public string? Bssid { get; init; }

    public bool IsHidden { get; init; }

    /// <summary>Save the network on the device.</summary>
    public bool Remember { get; init; } = true;

    /// <summary>How long to wait for the connection, in milliseconds; the platform's default when null.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>A saved network's <see cref="KnownWifiNetwork.Id"/>, instead of an SSID.</summary>
    public string? KnownNetworkId { get; init; }
}

/// <summary>A network saved on the device.</summary>
/// <param name="Id">An int on Android, a UUID on Linux, the SSID itself on Apple platforms.</param>
public sealed record KnownWifiNetwork(string Id, string Ssid, WifiSecurity Security, bool IsHidden, bool AddedByThisApp);

public sealed record WifiRadioState(bool Enabled);

/// <summary>A hotspot to start. Every field is optional.</summary>
public sealed class HotspotStartRequest
{
    public string? Ssid { get; init; }

    public string? Passphrase { get; init; }

    public WifiBand Band { get; init; } = WifiBand.Unknown;

    public bool IsHidden { get; init; }
}

/// <summary>A running hotspot.</summary>
public sealed record HotspotInfo(string Ssid, string? Passphrase, WifiSecurity Security, WifiBand Band, string? Address);

/// <param name="Supported">Whether the platform supports a hotspot and the app registered it.</param>
/// <param name="Running">Whether one this app started is running.</param>
/// <param name="Current">The hotspot, when one is running.</param>
public sealed record HotspotStatus(bool Supported, bool Running, HotspotInfo? Current);

/// <summary>A device connected to the hotspot.</summary>
public sealed record HotspotClient(string MacAddress, string? IpAddress, string? HostName);

/// <param name="Current">The network now; null when the device left Wi-Fi.</param>
public sealed record WifiChangedEvent(WifiNetworkInfo? Current);

/// <param name="Current">The hotspot now; null when it stopped.</param>
public sealed record HotspotChangedEvent(HotspotInfo? Current);

/// <summary>Serialization for every Wi-Fi contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WifiStatus))]
[JsonSerializable(typeof(WifiAccessResult))]
[JsonSerializable(typeof(WifiNetworkInfo))]
[JsonSerializable(typeof(IReadOnlyList<WifiScanResult>))]
[JsonSerializable(typeof(WifiConnectRequest))]
[JsonSerializable(typeof(IReadOnlyList<KnownWifiNetwork>))]
[JsonSerializable(typeof(WifiRadioState))]
[JsonSerializable(typeof(HotspotStartRequest))]
[JsonSerializable(typeof(HotspotInfo))]
[JsonSerializable(typeof(HotspotStatus))]
[JsonSerializable(typeof(IReadOnlyList<HotspotClient>))]
[JsonSerializable(typeof(WifiChangedEvent))]
[JsonSerializable(typeof(HotspotChangedEvent))]
public partial class WifiJsonContext : JsonSerializerContext;
