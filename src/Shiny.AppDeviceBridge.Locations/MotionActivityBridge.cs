using Microsoft.Extensions.DependencyInjection;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Locations.Client;
using static Shiny.AppDeviceBridge.Locations.LocationContractMapping;

namespace Shiny.AppDeviceBridge.Locations;

/// <summary>
/// <c>/_bridge/motion</c> over <see cref="IMotionActivityManager"/> — walking, running, cycling, driving or
/// stationary, as the OS's activity recognition reports it. Shiny.Locations has no history query, so only the
/// latest reading and live ones are available.
/// <code>
/// GET    /_bridge/motion/status
/// POST   /_bridge/motion/access
/// GET    /_bridge/motion/current     the latest reading; 204 when there is none
/// GET    /_bridge/motion/listener    { "isListening": true }
/// POST   /_bridge/motion/listener
/// DELETE /_bridge/motion/listener
///
/// events:   motion.activity
/// handlers: motion
/// </code>
/// </summary>
public sealed class MotionActivityBridge : IWebAppBridge, IDisposable
{
    readonly IMotionActivityManager? motion;
    readonly WebAppEventHub events;

    public MotionActivityBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.motion = services.GetOptionalService<IMotionActivityManager>();
        this.events = events;

        if (this.motion is not null)
            this.motion.MotionActivityReadingReceived += this.OnReading;
    }

    public string Name => "motion";

    public bool IsSupported => this.motion is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/current", this.CurrentAsync)
        .MapGet("/listener", this.ListenerAsync)
        .MapPost("/listener", this.StartListenerAsync)
        .MapDelete("/listener", this.StopListenerAsync);

    async ValueTask StatusAsync(HttpContext context)
    {
        if (this.motion is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Motion activity");
            return;
        }

        await WebAppBridgeResults.Json(context, ToContract(m.GetCurrentStatus()), Contracts.LocationsJsonContext.Default.LocationAccessResult);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.motion is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Motion activity");
            return;
        }

        // The permission prompt is UI.
        var access = Application.Current?.Dispatcher is { } dispatcher
            ? await dispatcher.DispatchAsync(m.RequestAccess)
            : await m.RequestAccess();

        await WebAppBridgeResults.Json(context, ToContract(access), Contracts.LocationsJsonContext.Default.LocationAccessResult);
    }

    async ValueTask CurrentAsync(HttpContext context)
    {
        if (this.motion is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Motion activity");
            return;
        }

        await (await m.GetLastReading() is { } reading
            ? WebAppBridgeResults.Json(context, ToContract(reading), Contracts.LocationsJsonContext.Default.MotionActivity)
            : WebAppBridgeResults.NoContent(context));
    }

    ValueTask ListenerAsync(HttpContext context)
        => this.motion is { } m
            ? WebAppBridgeResults.Json(context, new Contracts.MotionListener(m.IsListening), Contracts.LocationsJsonContext.Default.MotionListener)
            : WebAppBridgeResults.NotSupported(context, "Motion activity");

    async ValueTask StartListenerAsync(HttpContext context)
    {
        if (this.motion is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Motion activity");
            return;
        }

        try
        {
            await m.StartListener();
        }
        catch (InvalidOperationException ex)
        {
            // Shiny refuses to start without permission.
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "motion_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopListenerAsync(HttpContext context)
    {
        if (this.motion is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Motion activity");
            return;
        }

        await m.StopListener();
        await WebAppBridgeResults.NoContent(context);
    }

    void OnReading(object? sender, MotionActivityReading reading)
    {
        if (this.events.HasSubscribers)
            this.events.Publish("motion.activity", ToContract(reading), Contracts.LocationsJsonContext.Default.MotionActivity);
    }

    public void Dispose()
    {
        if (this.motion is not null)
            this.motion.MotionActivityReadingReceived -= this.OnReading;
    }
}

/// <summary>
/// Hands readings from Shiny's motion activity delegate — what runs when the OS delivers them in the background —
/// to the web app's <c>motion</c> handler: the page if it is listening, background.js otherwise. Registered by
/// <see cref="MotionActivityBridgeExtensions.AddMotionActivityBridge"/>.
/// </summary>
public class WebAppMotionActivityDelegate(WebAppInvoker invoker) : IMotionActivityDelegate
{
    public virtual Task OnReading(MotionActivityReading reading)
        => invoker.InvokeAsync("motion", ToContract(reading), Contracts.LocationsJsonContext.Default.MotionActivity);
}

public static class MotionActivityBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/motion</c> and registers Shiny's motion activity recognition with
    /// <see cref="WebAppMotionActivityDelegate"/>. Not part of <see cref="LocationBridgeExtensions.AddLocationBridges"/>:
    /// it needs its own platform setup — <c>NSMotionUsageDescription</c> on iOS, without which the permission
    /// request terminates the app, and <c>ACTIVITY_RECOGNITION</c> with Google Play Services on Android.
    /// Other platforms answer 501.
    /// </summary>
    public static MauiAppBuilder AddMotionActivityBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST
        builder.EnsureShiny();
        builder.Services.AddMotionActivity<WebAppMotionActivityDelegate>();
#endif

        builder.Services.AddWebAppBridge<MotionActivityBridge>();
        return builder;
    }
}
