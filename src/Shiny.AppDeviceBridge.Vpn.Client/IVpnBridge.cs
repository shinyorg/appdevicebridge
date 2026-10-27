using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Vpn.Client;

/// <summary>
/// The app's one profile in the operating system's built-in IKEv2 VPN client. The OS owns the tunnel: it keeps running
/// after the app closes and the user can switch it off or delete it in the system VPN settings. Check
/// <see cref="VpnStatus.Capabilities"/> before offering an option; one the platform lacks answers 501. iOS, Mac Catalyst,
/// macOS, Android 11+ and Linux (NetworkManager with the strongSwan plugin); Windows answers 501.
/// </summary>
[BridgeClient("vpn", typeof(VpnJsonContext))]
public interface IVpnBridge
{
    /// <summary>The tunnel's state and what this platform can do.</summary>
    [BridgeGet]
    Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs or replaces the app's VPN profile. The first save shows the OS consent prompt, and a user who declines
    /// answers <see cref="AccessState.Denied"/> with nothing saved. Saving does not connect. Fails with 400 when the
    /// profile is incomplete, and 501 when the platform cannot save this kind of profile.
    /// </summary>
    [BridgePut("profile")]
    Task<VpnProfileResult> SaveProfileAsync(VpnProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects and deletes the app's VPN profile and its saved secrets. Does nothing when there is none.</summary>
    [BridgeDelete("profile")]
    Task RemoveProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the tunnel up and waits until it is connected. Fails with <c>403 vpn_permission</c> when no profile is saved
    /// or the user withdrew consent, and <c>502 vpn_connection_failed</c> with the OS's reason when the tunnel does not come
    /// up in time.
    /// </summary>
    [BridgePost("connect")]
    Task<VpnStatus> ConnectAsync(VpnConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes the tunnel down. The profile stays saved.</summary>
    [BridgePost("disconnect")]
    Task<VpnStatus> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The tunnel's state changed, including changes the user makes in system settings and the OS reconnecting on its own.
    /// Never fires without <see cref="VpnCapabilityInfo.Status"/>.
    /// </summary>
    [BridgeEvent("vpn.status")]
    Task<IAsyncDisposable> OnStatusChangedAsync(Func<VpnStatusChanged, Task> handler);
}
