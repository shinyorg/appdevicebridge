namespace Shiny.AppDeviceBridge;

/// <summary>
/// A public address the bridge server is reachable through that is not one of its own names — a tunnel. Registered in the
/// container, it is how the server knows which <c>Host</c> a tunneled request may carry. <c>Shiny.AppDeviceBridge.Tunnel</c>
/// implements it; implement it yourself to put the server behind a tunnel of your own.
/// <para>
/// Everything that arrives through a tunnel is a caller from the network, whatever address it appears to come from: it is
/// never <see cref="BridgeCallers.IsOnDevice"/>, never holds the WebView's session, and is not let in by
/// <see cref="AppDeviceBridgeOptions.AllowAnyCallerInDebug"/>. What it can reach is what the bridge policy and the app's own
/// endpoint policies grant a remote caller.
/// </para>
/// </summary>
public interface IAppDeviceBridgeTunnel
{
    /// <summary>The address the tunnel answers at while it is open; null while it is not. It may change on a reconnect.</summary>
    Uri? PublicUrl { get; }
}
