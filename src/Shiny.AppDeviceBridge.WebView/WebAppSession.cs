using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.WebView;

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
/// The host adds it to the default bridge policy, on top of the server's own checks that the caller is on this
/// device, reached it by a loopback name (which defeats DNS rebinding) and, for a browser, from a loopback
/// <c>Origin</c> (so a page on another origin cannot drive the bridges even inside the same WebView).
/// </para>
/// </summary>
public sealed class WebAppSession
{
    public const string CookieName = "__appdevicebridge";

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

/// <summary>Policy names the WebView host defines, for <c>RequireAuthorization(...)</c> on your own endpoints.</summary>
public static class WebAppPolicies
{
    /// <summary>
    /// Only this device's own WebView — the caller holding the launch cookie. Every other scheme is refused,
    /// however valid its credentials, which makes this the policy for an endpoint the page uses that is not
    /// meant for anything else.
    /// </summary>
    public const string Session = "appdevicebridge:session";
}

/// <summary>
/// The WebView as an authentication scheme: the launch cookie becomes a principal, so your own endpoints treat
/// the page the same way they treat a caller presenting an API key or a token.
/// <para>
/// Only ever from this device. The cookie is the device's secret; one arriving over the network — or through a tunnel,
/// which delivers from loopback — proves nothing about who sent it, so such a request never authenticates through this
/// scheme.
/// </para>
/// </summary>
public sealed class WebAppSessionAuthenticationHandler(WebAppSession session) : IAuthenticationHandler
{
    public const string SchemeName = "WebAppSession";

    /// <summary>Carried by the WebView's principal; <see cref="WebAppPolicies.Session"/> requires it.</summary>
    public const string SessionClaim = "appdevicebridge:session";

    public string Scheme => SchemeName;

    public ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!BridgeCallers.IsLocalConnection(context) || !session.IsValid(context.Request.Cookies[WebAppSession.CookieName]))
            return ValueTask.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "webview"),
                new Claim(SessionClaim, "true")
            ],
            SchemeName
        );

        return ValueTask.FromResult(AuthenticateResult.Success(new ClaimsPrincipal(identity)));
    }
}
