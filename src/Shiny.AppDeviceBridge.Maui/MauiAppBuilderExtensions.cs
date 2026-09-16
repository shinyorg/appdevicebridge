using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Maui;

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

    /// <summary>
    /// Adds your own endpoints beside the web app and the bridges, with Shiny.Net.HttpServer's own API — raw
    /// routes, a source-generated <c>[Route]</c> class, or a module. Map them from the root; the host moves them
    /// under <see cref="WebAppHostOptions.BasePath"/>.
    /// <code>
    /// builder
    ///     .UseWebAppHost(o => { … })
    ///     .AddWebAppEndpoints(server =>
    ///     {
    ///         server.MapOrderEndpoints();                                  // [Route("/api/orders")]
    ///         server.MapGet("/api/health", ctx => …).AllowAnonymous();
    ///     })
    ///     .AddWebAppAuthentication(auth => auth.AddApiKey(o => o.AddKey(key, "kiosk")));
    /// </code>
    /// <para>
    /// An endpoint needs an authenticated caller unless it opts out with <c>AllowAnonymous()</c>: the WebView's
    /// session counts, and so does any scheme from <see cref="AddWebAppAuthentication"/>. Bridges keep their own
    /// rules and are unaffected. With <see cref="WebAppHostOptions.RemoteAccess"/> on, these endpoints are reachable
    /// from the network — no allowlist, their authorization is the gate.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddWebAppEndpoints(this MauiAppBuilder builder, Action<HttpServer> map)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddWebAppEndpoints(map);
        return builder;
    }

    /// <summary>Adds an <see cref="IEndpointModule"/>, resolved from the container so it can take dependencies.</summary>
    public static MauiAppBuilder AddWebAppEndpoints<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>(
        this MauiAppBuilder builder
    ) where TModule : class, IEndpointModule
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddWebAppEndpoints<TModule>();
        return builder;
    }

    /// <summary>Adds authentication schemes for your endpoints, alongside the WebView's own session.</summary>
    public static MauiAppBuilder AddWebAppAuthentication(this MauiAppBuilder builder, Action<AuthenticationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddWebAppAuthentication(configure);
        return builder;
    }

    /// <summary>Adds authorization policies for your endpoints. Every call applies.</summary>
    public static MauiAppBuilder AddWebAppAuthorization(this MauiAppBuilder builder, Action<AuthorizationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddWebAppAuthorization(configure);
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
