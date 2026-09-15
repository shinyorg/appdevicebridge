using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.WebAppHost;

/// <summary>
/// The secret that makes the loopback server answer only the app's own WebView.
/// <para>
/// Binding to loopback keeps other machines out, not other apps: on Android any installed app can
/// open a socket to <c>127.0.0.1</c>, and on a desktop any process can. Without this, the bridge
/// endpoints — location, Bluetooth, whatever the app exposes — would be available to all of them.
/// </para>
/// <para>
/// The token is 256 random bits, new every launch, and never written to disk. The host hands it to
/// the WebView once, in the start URL; <c>/_host/start</c> exchanges it for an HttpOnly,
/// SameSite=Strict cookie and redirects, so the token leaves the address bar and script on the page
/// cannot read it. Every later request needs the cookie.
/// </para>
/// <para>
/// Two further checks close what a cookie alone leaves open. The <c>Host</c> header must be the
/// loopback address, which defeats DNS rebinding — a hostile site resolving its own name to
/// 127.0.0.1 arrives with its own name. And a bridge call carrying an <c>Origin</c> must carry this
/// server's, so a page on another origin cannot drive the bridge even inside the same WebView.
/// </para>
/// </summary>
public sealed class WebAppSession
{
    public const string CookieName = "__webapphost";
    public const string StartPath = "/_host/start";
    public const string PingPath = "/_host/ping";
    public const string BridgePrefix = "/_bridge";

    readonly byte[] tokenBytes;

    public WebAppSession()
    {
        this.Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        this.tokenBytes = Encoding.ASCII.GetBytes(this.Token);
    }

    /// <summary>The launch token. Put it in nothing but the start URL.</summary>
    public string Token { get; }

    /// <summary>Constant-time comparison, so response timing says nothing about how much of a guess was right.</summary>
    public bool IsValid(string? candidate)
        => candidate is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), this.tokenBytes);
}
