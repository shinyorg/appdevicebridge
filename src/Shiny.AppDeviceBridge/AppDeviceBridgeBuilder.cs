using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Where the bridges are registered: <see cref="Configure"/> for the bridge server's options, <see cref="AddBridge{TBridge}"/>
/// for a bridge of your own, and one extension per bridge package. Creating one puts the bridge server on the app's
/// Shiny.Net.HttpServer, with the built-in host, settings, files and native-call bridges.
/// <code>
/// // A MAUI app: UseAppDeviceBridge hands out the builder, with or without a web app.
/// builder.UseAppDeviceBridge(
///     bridge => bridge
///         .Configure(o => o.AppId = "field-app")
///         .AddAppSupportBridge()
///         .AddLocationBridges(),
///     webApp => webApp.UseBaseline(typeof(App).Assembly, "webapp.zip")
/// );
///
/// // Anywhere else — a headless device, a test — on Shiny.Net.HttpServer's own builder.
/// services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge
///     .Configure(o => o.AppId = "greenhouse")
///     .AddRpiCameraBridge()
///     .AddBridge&lt;ClipboardBridge&gt;()));
/// </code>
/// <para>
/// No MAUI in it: a MAUI app gets a <c>MauiAppDeviceBridgeBuilder</c> (<c>Shiny.AppDeviceBridge.Maui</c>), which builds on this
/// one for the bridges that need the app's <c>MauiAppBuilder</c>. Bridges that need no MAUI extend this type generically and
/// return the builder they were given, so they chain with either.
/// </para>
/// </summary>
public class AppDeviceBridgeBuilder
{
    public AppDeviceBridgeBuilder(ShinyHttpServerBuilder http)
    {
        ArgumentNullException.ThrowIfNull(http);

        this.Http = http;
        this.Options = AppDeviceBridgeHttpServerBuilderExtensions.Register(http);
    }

    /// <summary>The server the bridges are on: its address, port, TLS, authentication and endpoints of your own.</summary>
    public ShinyHttpServerBuilder Http { get; }

    public IServiceCollection Services => this.Http.Services;

    /// <summary>The one options instance every builder configures. Validated when the server is built.</summary>
    public AppDeviceBridgeOptions Options { get; }

    /// <summary>Configures the bridge server: the app id, mount points, allowed hosts and who may call the bridges.</summary>
    public AppDeviceBridgeBuilder Configure(Action<AppDeviceBridgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(this.Options);
        return this;
    }

    /// <summary>
    /// Registers a bridge of your own. Its routes are mapped when the server is built, so the order relative to the others
    /// does not matter, and adding the same bridge twice is harmless.
    /// </summary>
    public AppDeviceBridgeBuilder AddBridge<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBridge>()
        where TBridge : class, IWebAppBridge
    {
        this.Services.AddWebAppBridge<TBridge>();
        return this;
    }
}
