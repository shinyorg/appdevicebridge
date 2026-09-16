using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// Web platform features the page may use through the WebView's own APIs rather than a bridge. Nothing is
/// allowed unless the app says so with <see cref="WebAppHostExtensions.AllowWebPermissions"/>.
/// </summary>
[Flags]
public enum WebAppWebPermissions
{
    None = 0,

    /// <summary><c>getUserMedia({ video })</c>, and on Android <c>&lt;input type="file" capture&gt;</c> opening the camera.</summary>
    Camera = 1,

    /// <summary><c>getUserMedia({ audio })</c>.</summary>
    Microphone = 2,

    /// <summary><c>navigator.geolocation</c> on Android and Windows. Apple platforms have no hook to decide it; see the readme.</summary>
    Geolocation = 4
}

/// <summary>Decides WebView permission requests: allowed by the app, and asked for by the host's own origin.</summary>
sealed class WebAppWebPermissionPolicy(WebAppWebPermissions allowed, IServiceProvider services)
{
    public WebAppWebPermissions Allowed => allowed;

    public bool Allows(WebAppWebPermissions permission)
        => permission != WebAppWebPermissions.None && (allowed & permission) == permission;

    public bool IsHostOrigin(string? origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && this.IsHostOrigin(uri.Scheme, uri.Host, uri.Port);

    /// <summary>
    /// The loopback origin the host is serving on right now. Anything else — a page the user navigated to, a
    /// third-party iframe — is denied, since the permission the app granted was for its own web app.
    /// </summary>
    public bool IsHostOrigin(string? scheme, string? host, int port)
    {
        if (this.TryGetOrigin() is not { } current)
            return false;

        return String.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && port == current.Port
               && (String.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                   || String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    Uri? TryGetOrigin()
    {
        try
        {
            return services.GetService<WebAppHost>()?.Origin;
        }
        catch (Exception)
        {
            // Resolving the host constructs every bridge; one that throws is reported by WebAppHostView, not here.
            return null;
        }
    }
}

/// <summary>Installs the platform's permission handling on the host's WebView, and on no other.</summary>
static partial class WebAppWebViewPermissions
{
    public static void Attach(Microsoft.Maui.Controls.WebView view)
    {
        if (view.Handler?.MauiContext?.Services is not { } services)
            return;

        var policy = services.GetService<WebAppWebPermissionPolicy>() ?? new WebAppWebPermissionPolicy(WebAppWebPermissions.None, services);
        AttachPlatform(view, policy);
    }

    // Implemented per platform; the GTK4 head (plain net10.0) has none.
    static partial void AttachPlatform(Microsoft.Maui.Controls.WebView view, WebAppWebPermissionPolicy policy);
}
