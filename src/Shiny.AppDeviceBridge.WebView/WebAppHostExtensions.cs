using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Maui;

namespace Shiny.AppDeviceBridge.WebView;

public static class WebAppHostExtensions
{
    /// <summary>
    /// Serves a web app from the bridge server and shows it in <see cref="WebAppHostView"/>. Registers the bridge server
    /// too, if <see cref="AppDeviceBridgeMauiExtensions.UseAppDeviceBridge"/> has not — its options (the app id, the
    /// port, who may call the bridges) are set there.
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
        builder.Services.AddWebAppHost(configure);
        return builder;
    }

    /// <summary>
    /// Registers <see cref="WebAppHost"/> on the bridge server, without MAUI's startup — for tests, or a host of your own.
    /// Every call configures the same options, which are validated when the host is created.
    /// </summary>
    public static IServiceCollection AddWebAppHost(this IServiceCollection services, Action<WebAppHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAppDeviceBridge();

        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppHostOptions))?.ImplementationInstance is WebAppHostOptions existing)
        {
            configure(existing);
            return services;
        }

        var options = new WebAppHostOptions();
        configure(options);
        services.AddSingleton(options);

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

        return services;
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
