using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maui;

public static class AppDeviceBridgeMauiExtensions
{
    /// <summary>
    /// Registers Shiny.Net.HttpServer with the bridges on it — <c>AddShinyHttpServer</c> and <c>AddAppDeviceBridge</c> in one
    /// call. The host version defaults to the app's display version. Call it as often as you like; every call configures the
    /// same options. Bridges come from their own packages, one extension each, and a WebView host from
    /// <c>Shiny.AppDeviceBridge.WebView</c>:
    /// <code>
    /// builder
    ///     .UseAppDeviceBridge(o => o.AppId = "field-app")
    ///     .UseWebAppHost(o => o.UseBaseline(typeof(App).Assembly, "webapp.zip"))
    ///     .AddLocationBridges();
    ///
    /// // The server itself, and anything else on it, on the same builder — before or after:
    /// builder.Services.AddShinyHttpServer(http =>
    /// {
    ///     http.Options.Address = IPAddress.Any;
    ///     http.AddAuthentication().AddCookie(...);
    ///     http.Configure(server => server.MapMyApi());
    /// }, autoStart: false);
    /// </code>
    /// <para>
    /// Loopback on port <see cref="DefaultPort"/>, unless the app gives the server a port of its own — before this call or
    /// after it. The port is fixed on purpose: a page's origin includes it, and web storage belongs to the origin.
    /// </para>
    /// <para>
    /// The server starts with the app, and restarts when the app returns to the foreground if the OS took its socket away
    /// while it was suspended — iOS does. Pass <paramref name="startWithApp"/> false to start
    /// <see cref="AppDeviceBridgeServer"/> yourself — an app with a switch for sharing over the network.
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

        // Not started by a hosted service: MAUI does not run them. AppDeviceBridgeStartup starts it instead.
        builder.Services.AddShinyHttpServer(
            http =>
            {
                // Decided by the port, not by whether the server was registered before: a bridge or a tunnel registering
                // on the builder first says nothing about the port, and would otherwise leave the server on
                // Shiny.Net.HttpServer's 5000 — a different origin from every earlier launch, and a WebView whose storage
                // is suddenly empty. A port the app set later still wins, because its configure runs later.
                if (http.Options.Port == LibraryDefaultPort)
                    http.Options.Port = DefaultPort;

                http.AddAppDeviceBridge(o =>
                {
                    if (first && TryGetAppVersion() is { } version)
                        o.HostVersion = version;

                    configure?.Invoke(o);
                });
            },
            autoStart: false
        );

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

    /// <summary>The port a MAUI app's server listens on unless the app chooses one.</summary>
    public const int DefaultPort = 5780;

    static readonly int LibraryDefaultPort = new HttpServerOptions().Port;

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
