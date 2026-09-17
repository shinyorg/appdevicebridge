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
    /// Serves a web app from the bridge server and shows it in <see cref="WebAppHostView"/>. Registers the bridge server
    /// too, if <see cref="AppDeviceBridgeMauiExtensions.UseAppDeviceBridge"/> has not — its options (the app id, who may call
    /// the bridges) are set there, and the server's own (the port) on <c>AddShinyHttpServer</c>.
    /// <code>
    /// builder
    ///     .UseAppDeviceBridge(o => o.AppId = "field-app")
    ///     .UseWebAppHost(o =>
    ///     {
    ///         o.UpdateServer = new Uri("https://api.example.com/webapps");
    ///         o.PublicKey = WebAppKeys.Public;
    ///         o.UseBaseline(typeof(App).Assembly, "webapp.zip", "1.0.0");
    ///     });
    /// </code>
    /// </summary>
    public static MauiAppBuilder UseWebAppHost(this MauiAppBuilder builder, Action<WebAppHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.UseAppDeviceBridge();
        new ShinyHttpServerBuilder(builder.Services).AddWebAppHost(configure);
        return builder;
    }

    /// <summary>
    /// Registers <see cref="WebAppHost"/> on the app's server, without MAUI's startup — for tests, or a host of your own. Adds
    /// the bridges if they are not there yet. Every call configures the same options, which are validated when the host is
    /// created.
    /// <para>
    /// The WebView's launch session is added to the server as an authentication scheme, and
    /// <see cref="WebAppPolicies.Session"/> as a policy, so the app's own endpoints can recognise the page — through the
    /// app's <c>UseAuthentication</c> and <c>UseAuthorization</c>, as for any other scheme.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddWebAppHost(this ShinyHttpServerBuilder http, Action<WebAppHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(configure);

        http.AddAppDeviceBridge();

        var services = http.Services;
        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppHostOptions))?.ImplementationInstance is WebAppHostOptions existing)
        {
            configure(existing);

            // An instance registered up front still needs everything below, once.
            if (services.Any(x => x.ServiceType == typeof(WebAppHost)))
                return http;
        }
        else
        {
            existing = new WebAppHostOptions();
            configure(existing);
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

        return http;
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
}
