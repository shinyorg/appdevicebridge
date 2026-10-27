using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Vpn.Client;

/// <summary>The state of the app's VPN tunnel.</summary>
public enum VpnConnectionState
{
    /// <summary>The platform cannot report the state — see <see cref="VpnCapabilityInfo.Status"/>.</summary>
    Unknown,

    /// <summary>No profile has been saved, or it was removed, including by the user from Settings.</summary>
    NotConfigured,

    /// <summary>A profile is saved and the tunnel is down.</summary>
    Disconnected,

    /// <summary>The tunnel is being negotiated.</summary>
    Connecting,

    /// <summary>The tunnel is up and traffic is flowing through it.</summary>
    Connected,

    /// <summary>The tunnel was up and is being re-established, after a network change for example.</summary>
    Reconnecting,

    /// <summary>The tunnel is being torn down.</summary>
    Disconnecting
}

/// <summary>How a profile authenticates with the server.</summary>
public enum VpnAuthenticationType
{
    /// <summary>IKEv2 with a pre-shared key, in <see cref="VpnProfileRequest.Secret"/>.</summary>
    SharedSecret,

    /// <summary>IKEv2 EAP (EAP-MSCHAPv2), with <see cref="VpnProfileRequest.Username"/> and <see cref="VpnProfileRequest.Password"/>.</summary>
    UsernamePassword,

    /// <summary>
    /// IKEv2 with a client certificate and its private key, as the PKCS#12 bundle in <see cref="VpnProfileRequest.Pkcs12"/>,
    /// with its password in <see cref="VpnProfileRequest.Password"/>.
    /// </summary>
    Certificate
}

/// <summary>What this platform's VPN client can do.</summary>
/// <param name="Profile">A profile can be saved and removed.</param>
/// <param name="Connect">The tunnel can be brought up.</param>
/// <param name="Disconnect">The tunnel can be taken down.</param>
/// <param name="Status">
/// The tunnel's state is reported. Without it (Android 11 and 12) <see cref="IVpnBridge.ConnectAsync"/> returns once the
/// OS accepts the request, and <c>vpn.status</c> never fires.
/// </param>
/// <param name="SharedSecret">A <see cref="VpnAuthenticationType.SharedSecret"/> profile can be saved.</param>
/// <param name="UsernamePassword">A <see cref="VpnAuthenticationType.UsernamePassword"/> profile can be saved.</param>
/// <param name="Certificate">A <see cref="VpnAuthenticationType.Certificate"/> profile can be saved (not on Linux).</param>
/// <param name="ServerRootCertificate"><see cref="VpnProfileRequest.ServerRootCertificate"/> is honoured (not on Apple).</param>
/// <param name="RemoteIdentifier"><see cref="VpnProfileRequest.RemoteIdentifier"/> can differ from the server address (not on Android).</param>
public sealed record VpnCapabilityInfo(
    bool Profile,
    bool Connect,
    bool Disconnect,
    bool Status,
    bool SharedSecret,
    bool UsernamePassword,
    bool Certificate,
    bool ServerRootCertificate,
    bool RemoteIdentifier
);

/// <param name="State">The tunnel's state, or <see cref="VpnConnectionState.Unknown"/> without <see cref="VpnCapabilityInfo.Status"/>.</param>
/// <param name="Capabilities">What this platform can do.</param>
public sealed record VpnStatus(VpnConnectionState State, VpnCapabilityInfo Capabilities);

/// <summary>
/// The IKEv2 profile to install. The app owns exactly one, so saving again replaces it. Secrets go to the OS's keychain or
/// keystore and are never read back.
/// </summary>
/// <param name="Name">The name shown in the system VPN settings (Android lists the profile under the app's name).</param>
/// <param name="ServerAddress">The VPN server's host name or IP address.</param>
/// <param name="Authentication">How the device proves who it is.</param>
/// <param name="Secret">The pre-shared key, for <see cref="VpnAuthenticationType.SharedSecret"/>.</param>
/// <param name="Username">The EAP username, for <see cref="VpnAuthenticationType.UsernamePassword"/>.</param>
/// <param name="Password">The EAP password, or the PKCS#12 bundle's password for <see cref="VpnAuthenticationType.Certificate"/>.</param>
/// <param name="Pkcs12">The client certificate and private key (.p12 / .pfx), base64 in JSON, for <see cref="VpnAuthenticationType.Certificate"/>.</param>
/// <param name="RemoteIdentifier">The server's IKE ID; the server address when null.</param>
/// <param name="LocalIdentifier">
/// The device's IKE ID. Shared secret servers usually look the secret up by it, and Android needs it for shared secret and
/// certificate profiles.
/// </param>
/// <param name="ServerRootCertificate">The CA certificate that issued the server's, DER or PEM; the system CA store when null.</param>
public sealed record VpnProfileRequest(
    string Name,
    string ServerAddress,
    VpnAuthenticationType Authentication,
    string? Secret = null,
    string? Username = null,
    string? Password = null,
    byte[]? Pkcs12 = null,
    string? RemoteIdentifier = null,
    string? LocalIdentifier = null,
    byte[]? ServerRootCertificate = null
)
{
    // A record prints every member, and the first log line that formatted one would carry the secret.
    public override string ToString() => $"VpnProfileRequest {{ Name = {this.Name}, ServerAddress = {this.ServerAddress}, Authentication = {this.Authentication} }}";
}

/// <param name="Access"><see cref="AccessState.Available"/> once saved, <see cref="AccessState.Denied"/> when the user declined.</param>
public sealed record VpnProfileResult(AccessState Access);

/// <param name="TimeoutSeconds">How long to wait for the tunnel, 1 to 300 seconds; 30 when null.</param>
public sealed record VpnConnectRequest(int? TimeoutSeconds = null);

/// <param name="State">The tunnel's new state.</param>
public sealed record VpnStatusChanged(VpnConnectionState State);

/// <summary>Serialization for every VPN contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VpnStatus))]
[JsonSerializable(typeof(VpnProfileRequest))]
[JsonSerializable(typeof(VpnProfileResult))]
[JsonSerializable(typeof(VpnConnectRequest))]
[JsonSerializable(typeof(VpnStatusChanged))]
public partial class VpnJsonContext : JsonSerializerContext;
