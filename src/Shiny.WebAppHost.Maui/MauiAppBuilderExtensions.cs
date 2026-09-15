using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.WebAppHost.Maui;

public static class WebAppHostMauiExtensions
{
    /// <summary>
    /// Registers the web app host. The host version defaults to the app's display version. Bridges come
    /// from their own packages, one extension each:
    /// <code>
    /// builder
    ///     .UseWebAppHost(o =>
    ///     {
    ///         o.AppId = "field-app";
    ///         o.UpdateServer = new Uri("https://api.example.com/webapps");
    ///         o.PublicKey = WebAppKeys.Public;
    ///         o.UseBaseline(typeof(App).Assembly, "webapp.zip", "1.0.0");
    ///     })
    ///     .AddAppSupportBridge()
    ///     .AddLocationBridges()
    ///     .AddBluetoothLEBridge();
    /// </code>
    /// <para>
    /// Platform setup the library cannot do for you:
    /// <list type="bullet">
    /// <item>Android — cleartext to loopback is blocked from API 28. Add a network security config
    /// permitting <c>127.0.0.1</c>, or <c>android:usesCleartextTraffic="true"</c>.</item>
    /// <item>iOS / Mac Catalyst — <c>NSAppTransportSecurity</c> → <c>NSAllowsLocalNetworking</c>.</item>
    /// <item>Mac Catalyst and sandboxed macOS — the <c>com.apple.security.network.server</c> entitlement.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static MauiAppBuilder UseWebAppHost(this MauiAppBuilder builder, Action<WebAppHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddWebAppHost(o =>
        {
            if (TryGetAppVersion() is { } version)
                o.HostVersion = version;

            configure(o);
        });

        return builder;
    }

    /// <summary>
    /// Lets the web app use the camera, microphone or location through the WebView's own APIs —
    /// <c>getUserMedia</c>, <c>navigator.geolocation</c>, <c>&lt;input type="file" capture&gt;</c>. Without this
    /// call every such request is denied. Calling it again replaces the earlier set.
    /// <code>
    /// builder
    ///     .UseWebAppHost(o => { … })
    ///     .AllowWebPermissions(WebAppWebPermissions.Camera | WebAppWebPermissions.Microphone);
    /// </code>
    /// <para>
    /// Requests are granted only to the host's own loopback origin, never to a page the user navigated to or an
    /// embedded third-party frame. The OS permission is requested when the page first asks, so the app still
    /// declares it: <c>CAMERA</c>, <c>RECORD_AUDIO</c> and <c>MODIFY_AUDIO_SETTINGS</c>, and the location permissions,
    /// on Android; <c>NSCameraUsageDescription</c>, <c>NSMicrophoneUsageDescription</c> and
    /// <c>NSLocationWhenInUseUsageDescription</c> on Apple platforms, plus the camera and audio-input entitlements
    /// when sandboxed.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AllowWebPermissions(this MauiAppBuilder builder, WebAppWebPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.RemoveAll<WebAppWebPermissionPolicy>();
        builder.Services.AddSingleton(sp => new WebAppWebPermissionPolicy(permissions, sp));
        return builder;
    }

    static string? TryGetAppVersion()
    {
        try
        {
            if (WebAppVersion.TryParse(AppInfo.Current.VersionString, out _))
                return AppInfo.Current.VersionString;
        }
        catch (Exception)
        {
            // Essentials is not implemented on every backend; the entry assembly is the fallback.
        }

        return Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"
            : null;
    }
}
