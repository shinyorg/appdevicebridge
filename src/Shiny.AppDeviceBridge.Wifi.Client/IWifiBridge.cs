using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Wifi.Client;

/// <summary>
/// Wi-Fi: scanning, the current network, joining and forgetting networks, the radio, and a hotspot. What works varies
/// more by platform than for any other bridge — iOS cannot scan, Android cannot toggle the radio — so check
/// <see cref="WifiStatus.Capabilities"/>; a call the platform lacks fails with 501.
/// </summary>
[BridgeClient("wifi", typeof(WifiJsonContext))]
public interface IWifiBridge
{
    /// <summary>What this platform can do, the current network, and whether a hotspot is available.</summary>
    [BridgeGet]
    Task<WifiStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the permission Wi-Fi needs here — location on Android.</summary>
    [BridgePost("access")]
    Task<WifiAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Scans for networks in range.</summary>
    [BridgeGet("networks")]
    Task<IReadOnlyList<WifiScanResult>> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>The network the device is on. Null when it is not on Wi-Fi.</summary>
    [BridgeGet("current")]
    Task<WifiNetworkInfo?> GetCurrentNetworkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins a network by SSID, or a saved one by id. iOS asks the user first. Fails with 409 when the connection
    /// fails, 403 when permission is missing.
    /// </summary>
    [BridgePost("connection")]
    Task<WifiNetworkInfo> ConnectAsync(WifiConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects from the current network.</summary>
    [BridgeDelete("connection")]
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>The networks saved on the device that this app can see.</summary>
    [BridgeGet("known")]
    Task<IReadOnlyList<KnownWifiNetwork>> GetKnownNetworksAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets a saved network.</summary>
    [BridgeDelete("known")]
    Task ForgetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Whether the Wi-Fi radio is on.</summary>
    [BridgeGet("radio")]
    Task<WifiRadioState> GetRadioAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the Wi-Fi radio on or off.</summary>
    [BridgePut("radio")]
    Task SetRadioAsync(WifiRadioState state, CancellationToken cancellationToken = default);

    /// <summary>Whether a hotspot is supported and running.</summary>
    [BridgeGet("hotspot")]
    Task<HotspotStatus> GetHotspotAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a hotspot. Every field of the request is optional; the platform picks what is not given.</summary>
    [BridgePost("hotspot")]
    Task<HotspotInfo> StartHotspotAsync(HotspotStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops the hotspot.</summary>
    [BridgeDelete("hotspot")]
    Task StopHotspotAsync(CancellationToken cancellationToken = default);

    /// <summary>The devices connected to a hotspot this app started. Fails with 409 when it is not running.</summary>
    [BridgeGet("hotspot/clients")]
    Task<IReadOnlyList<HotspotClient>> GetHotspotClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>The device joined, left or changed networks.</summary>
    [BridgeEvent("wifi.changed")]
    Task<IAsyncDisposable> OnChangedAsync(Func<WifiChangedEvent, Task> handler);

    /// <summary>The hotspot started, stopped or changed.</summary>
    [BridgeEvent("wifi.hotspot")]
    Task<IAsyncDisposable> OnHotspotChangedAsync(Func<HotspotChangedEvent, Task> handler);
}
