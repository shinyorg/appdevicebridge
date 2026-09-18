using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.WebView;

public static class WebAppHostExtensions
{
    /// <summary>
    /// Registers the bridge server with a web app on it: <paramref name="bridge"/> configures the bridge server the page
    /// calls (the app id, who may call the bridges) and adds its device bridges, one extension each, and
    /// <paramref name="webApp"/> the web app — served from the bridge server and shown in <see cref="WebAppHostView"/>. The
    /// server's own settings (the port) are on <c>AddShinyHttpServer</c>. With no web app, the overload taking only
    /// <paramref name="bridge"/> (<c>Shiny.AppDeviceBridge.Maui</c>) registers the bridges alone; the two can't be confused,
    /// because this one needs both delegates.
    /// <code>
    /// builder.UseAppDeviceBridge(
    ///     bridge => bridge
    ///         .Configure(o => o.AppId = "field-app")
    ///         .AddAppSupportBridge()
    ///         .AddLocationBridges(),
    ///     webApp =>
    ///     {
    ///         webApp.UpdateServer = new Uri("https://api.example.com/webapps");
    ///         webApp.PublicKey = WebAppKeys.Public;
    ///         webApp.UseBaseline(typeof(App).Assembly, "webapp.zip", "1.0.0");
    ///     }
    /// );
    /// </code>
    /// </summary>
    public static MauiAppBuilder UseAppDeviceBridge(this MauiAppBuilder builder, Action<MauiAppDeviceBridgeBuilder> bridge, Action<WebAppHostOptions> webApp)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(webApp);

        AppDeviceBridgeMauiExtensions.UseAppDeviceBridge(builder, bridge);
        AddWebAppHost(new ShinyHttpServerBuilder(builder.Services), webApp);
        return builder;
    }

    /// <summary>
    /// Registers the bridge server with a web app on it on the app's server, without MAUI's startup — for tests, or a host of
    /// your own. <paramref name="bridge"/> configures the bridge server and adds bridges, and <paramref name="webApp"/>
    /// configures <see cref="WebAppHost"/>. Every call configures the same options, which are validated when the host is
    /// created. With no web app, the overload taking only <paramref name="bridge"/> registers the bridges alone.
    /// <para>
    /// The WebView's launch session is added to the server as an authentication scheme, and
    /// <see cref="WebAppPolicies.Session"/> as a policy, so the app's own endpoints can recognise the page — through the
    /// app's <c>UseAuthentication</c> and <c>UseAuthorization</c>, as for any other scheme.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddAppDeviceBridge(this ShinyHttpServerBuilder http, Action<AppDeviceBridgeBuilder> bridge, Action<WebAppHostOptions> webApp)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(webApp);

        AppDeviceBridgeHttpServerBuilderExtensions.AddAppDeviceBridge(http, bridge);
        AddWebAppHost(http, webApp);
        return http;
    }

    static void AddWebAppHost(ShinyHttpServerBuilder http, Action<WebAppHostOptions> webApp)
    {
        var services = http.Services;
        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppHostOptions))?.ImplementationInstance is WebAppHostOptions existing)
        {
            webApp(existing);

            // An instance registered up front still needs everything below, once.
            if (services.Any(x => x.ServiceType == typeof(WebAppHost)))
                return;
        }
        else
        {
            existing = new WebAppHostOptions();
            webApp(existing);
            services.AddSingleton(existing);
        }

        var options = existing;

        services.TryAddSingleton<WebAppSession>();
        services.TryAddSingleton(sp => new WebAppHost(
            options,
            sp.GetRequiredService<AppDeviceBridgeServer>(),
            sp.GetRequiredService<WebAppSession>(),
            sp.GetService<ILoggerFactory>()
        ));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppDeviceBridgeServerExtension, WebAppHost>(sp => sp.GetRequiredService<WebAppHost>()));
        services.TryAddSingleton<IWebAppBackgroundInvoker>(sp => new WebAppScriptEngine(
            options,
            () => sp.GetRequiredService<WebAppHost>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<WebAppScriptEngine>()
        ));

        http.AddAuthentication().AddScheme(sp => new WebAppSessionAuthenticationHandler(sp.GetRequiredService<WebAppSession>()));
        http.AddAuthorization(o => o.AddPolicy(WebAppPolicies.Session, p => p.RequireClaim(WebAppSessionAuthenticationHandler.SessionClaim)));
    }

    /// <summary>
    /// Lets the web app use the camera, microphone or location through the WebView's own APIs —
    /// <c>getUserMedia</c>, <c>navigator.geolocation</c>, <c>&lt;input type="file" capture&gt;</c>. Without this
    /// call every such request is denied. Calling it again replaces the earlier set.
    /// <code>
    /// builder
    ///     .UseAppDeviceBridge(bridge => { … }, webApp => { … })
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
}
