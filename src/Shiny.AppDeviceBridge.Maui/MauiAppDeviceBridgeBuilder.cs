using System.Diagnostics.CodeAnalysis;
using Microsoft.Maui.Hosting;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maui;

/// <summary>
/// The bridge builder a MAUI app gets from <see cref="AppDeviceBridgeMauiExtensions.UseAppDeviceBridge"/>, with or without a web app:
/// an <see cref="AppDeviceBridgeBuilder"/> with the app's <see cref="MauiAppBuilder"/> on it. The host work every MAUI app
/// needs — Shiny's host, the UI thread for permission prompts, starting the server — is already done, so bridges that need
/// no MAUI never see this type. Bridges that do need MAUI (a control, lifecycle events, Essentials) extend it and use
/// <see cref="Maui"/>.
/// <code>
/// builder.UseAppDeviceBridge(
///     bridge => bridge
///         .Configure(o => o.AppId = "field-app")
///         .AddLocationBridges()      // no MAUI
///         .AddCameraBridge(),        // MAUI: registers the camera control itself
///     webApp => webApp.UseBaseline(typeof(App).Assembly, "webapp.zip")
/// );
/// </code>
/// </summary>
public sealed class MauiAppDeviceBridgeBuilder : AppDeviceBridgeBuilder
{
    public MauiAppDeviceBridgeBuilder(MauiAppBuilder maui, ShinyHttpServerBuilder http) : base(http)
    {
        ArgumentNullException.ThrowIfNull(maui);
        this.Maui = maui;
    }

    /// <summary>The app's builder, for the MAUI services a bridge registers.</summary>
    public MauiAppBuilder Maui { get; }

    /// <inheritdoc cref="AppDeviceBridgeBuilder.Configure"/>
    public new MauiAppDeviceBridgeBuilder Configure(Action<AppDeviceBridgeOptions> configure)
    {
        base.Configure(configure);
        return this;
    }

    /// <inheritdoc cref="AppDeviceBridgeBuilder.AddBridge{TBridge}"/>
    public new MauiAppDeviceBridgeBuilder AddBridge<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBridge>()
        where TBridge : class, IWebAppBridge
    {
        base.AddBridge<TBridge>();
        return this;
    }
}
