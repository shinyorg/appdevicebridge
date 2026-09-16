using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace Shiny.AppDeviceBridge.Maui;

public static class AppDeviceBridgeMauiExtensions
{
    /// <summary>
    /// Registers the bridge server. The host version defaults to the app's display version. Call it as often as you like;
    /// every call configures the same options. Bridges come from their own packages, one extension each, and a WebView
    /// host from <c>Shiny.AppDeviceBridge.WebView</c>:
    /// <code>
    /// builder
    ///     .UseAppDeviceBridge(o =>
    ///     {
    ///         o.AppId = "field-app";
    ///         o.Server.Port = 5780;
    ///     })
    ///     .UseWebAppHost(o => o.UseBaseline(typeof(App).Assembly, "webapp.zip"))
    ///     .AddLocationBridges();
    /// </code>
    /// <para>
    /// The server starts with the app, and restarts when the app returns to the foreground if the OS took its socket away
    /// while it was suspended — iOS does. Pass <paramref name="startWithApp"/> false to start
    /// <see cref="AppDeviceBridgeServer"/> yourself.
    /// </para>
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
    public static MauiAppBuilder UseAppDeviceBridge(this MauiAppBuilder builder, Action<AppDeviceBridgeOptions>? configure = null, bool startWithApp = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var first = !builder.Services.Any(x => x.ServiceType == typeof(AppDeviceBridgeServer));

        builder.Services.AddAppDeviceBridge(o =>
        {
            if (first && TryGetAppVersion() is { } version)
                o.HostVersion = version;

            configure?.Invoke(o);
        });

        if (first && startWithApp)
        {
            builder.Services.AddSingleton<IMauiInitializeService, AppDeviceBridgeStartup>();
            builder.ConfigureLifecycleEvents(events =>
            {
#if IOS || MACCATALYST
                events.AddiOS(ios => ios.WillEnterForeground(_ => AppDeviceBridgeStartup.Resume()));
#elif ANDROID
                events.AddAndroid(android => android.OnResume(_ => AppDeviceBridgeStartup.Resume()));
#endif
            });
        }

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

/// <summary>Starts the server once the app's container is built, and checks it is still answering on resume.</summary>
sealed class AppDeviceBridgeStartup : IMauiInitializeService
{
    static AppDeviceBridgeServer? server;
    static ILogger? logger;

    public void Initialize(IServiceProvider services)
    {
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<AppDeviceBridgeServer>();

        try
        {
            server = services.GetRequiredService<AppDeviceBridgeServer>();
        }
        catch (Exception ex)
        {
            // A bridge that cannot be constructed would otherwise take the app down at launch.
            logger?.LogError(ex, "The bridge server could not be created");
            return;
        }

        _ = Run(s => s.StartAsync());
    }

    public static void Resume()
    {
        if (server?.Origin is not null)
            _ = Run(s => s.EnsureRunningAsync());
    }

    static async Task Run(Func<AppDeviceBridgeServer, Task> action)
    {
        try
        {
            await action(server!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The bridge server failed to start");
        }
    }
}
