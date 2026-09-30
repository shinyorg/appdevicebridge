using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Gps.Client;
using static Shiny.AppDeviceBridge.Gps.GpsContractMapping;

namespace Shiny.AppDeviceBridge.Gps;

/// <summary>
/// <c>/_bridge/gps</c> over <see cref="IGpsManager"/>.
/// <code>
/// GET    /_bridge/gps/status?mode=foreground|background|realtime
/// POST   /_bridge/gps/access      { "backgroundMode": "None", "requestPreciseAccuracy": false }
/// GET    /_bridge/gps/last        204 when there is none
/// GET    /_bridge/gps/current     starts, reads and stops a foreground listener
/// GET    /_bridge/gps/listener    204 when not listening
/// POST   /_bridge/gps/listener    { "backgroundMode": "Standard" }
/// DELETE /_bridge/gps/listener
///
/// events: gps.reading
/// </code>
/// <c>gps.reading</c> hooks the listener's readings only while a page listens to it. Leaving does not stop the
/// listener: <see cref="WebAppGpsDelegate"/> still hands its readings to background.js.
/// </summary>
public sealed class GpsBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IGpsManager? gps = services.GetOptionalService<IGpsManager>();

    public string Name => "gps";

    public bool IsSupported => this.gps is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/last", this.LastAsync)
        .MapGet("/current", this.CurrentAsync)
        .MapGet("/listener", this.ListenerAsync)
        .MapPost("/listener", this.StartListenerAsync)
        .MapDelete("/listener", this.StopListenerAsync)
        .MapEvent("gps.reading", this.Readings, Contracts.GpsJsonContext.Default.GpsReading);

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.gps is not { } g)
            return WebAppBridgeResults.NotSupported(context, "GPS");

        var mode = context.Request.Query["mode"].ToString().ToLowerInvariant() switch
        {
            "background" => GpsRequest.Background,
            "realtime" => GpsRequest.Realtime(false),
            _ => GpsRequest.Foreground
        };

        return WebAppBridgeResults.Json(context, ToGpsAccess(g.GetCurrentStatus(mode)), Contracts.GpsJsonContext.Default.GpsAccessResult);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.GpsJsonContext.Default.GpsListenerSettings) ?? new Contracts.GpsListenerSettings();
        var access = await g.RequestAccess(ToRequest(body));

        await WebAppBridgeResults.Json(context, ToGpsAccess(access), Contracts.GpsJsonContext.Default.GpsAccessResult);
    }

    async ValueTask LastAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        await Reading(context, await g.GetLastReading());
    }

    async ValueTask CurrentAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        await Reading(context, await g.GetCurrentPosition(context.RequestAborted));
    }

    ValueTask ListenerAsync(HttpContext context)
    {
        if (this.gps is not { } g)
            return WebAppBridgeResults.NotSupported(context, "GPS");

        return g.CurrentListener is { } listener
            ? WebAppBridgeResults.Json(context, ToContract(listener), Contracts.GpsJsonContext.Default.GpsListenerSettings)
            : WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StartListenerAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.GpsJsonContext.Default.GpsListenerSettings) ?? new Contracts.GpsListenerSettings();

        try
        {
            await g.StartListener(ToRequest(body));
        }
        catch (InvalidOperationException ex)
        {
            // Shiny refuses to start without permission, or while another listener runs.
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "gps_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopListenerAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        await g.StopListener();
        await WebAppBridgeResults.NoContent(context);
    }

    static ValueTask Reading(HttpContext context, GpsReading? reading)
        => reading is null
            ? WebAppBridgeResults.NoContent(context)
            : WebAppBridgeResults.Json(context, ToContract(reading), Contracts.GpsJsonContext.Default.GpsReading);

    IAsyncEnumerable<Contracts.GpsReading> Readings(CancellationToken cancellationToken)
    {
        if (this.gps is not { } g)
            return AsyncEnumerable.Empty<Contracts.GpsReading>();

        return WebAppEventStream.FromEvent<Contracts.GpsReading>(emit =>
        {
            EventHandler<GpsReading> handler = (_, reading) => emit(ToContract(reading));
            g.GpsReadingReceived += handler;
            return () => g.GpsReadingReceived -= handler;
        }, cancellationToken);
    }
}

/// <summary>
/// Hands readings from Shiny's GPS delegate — what runs when the OS delivers them in the background — to
/// the web app's <c>gps</c> handler: the page if it is listening, background.js otherwise. The live
/// <c>gps.reading</c> event is separate and unchanged. Registered by
/// <see cref="GpsBridgeExtensions.AddGpsBridge"/>.
/// </summary>
public class WebAppGpsDelegate(WebAppInvoker invoker) : IGpsDelegate
{
    public virtual Task OnReading(GpsReading reading)
        => invoker.InvokeAsync("gps", ToContract(reading), Contracts.GpsJsonContext.Default.GpsReading);
}

public static class GpsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/gps</c> and registers Shiny's GPS service for the platform — there is nothing
    /// else to call. Where the platform has no GPS support the endpoints answer 501.
    /// </summary>
    public static TBuilder AddGpsBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        // The delegate is how a reading reaches the web app while it is in the background. Shiny runs every
        // registered delegate, so the app's own keep working alongside it.
        bridge.Services.AddGps<WebAppGpsDelegate>();
#endif

        bridge.AddBridge<GpsBridge>();
        return bridge;
    }
}

static class GpsContractMapping
{
    public static Contracts.GpsAccessResult ToGpsAccess(AccessState access)
        => new(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access));

    public static GpsRequest ToRequest(Contracts.GpsListenerSettings settings)
        => new(BridgeEnum.Convert<Contracts.GpsBackgroundMode, GpsBackgroundMode>(settings.BackgroundMode), settings.RequestPreciseAccuracy, settings.AutoRestart);

    public static Contracts.GpsListenerSettings ToContract(GpsRequest request)
        => new(BridgeEnum.Convert<GpsBackgroundMode, Contracts.GpsBackgroundMode>(request.BackgroundMode), request.RequestPreciseAccuracy, request.AutoRestart);

    public static Contracts.GpsReading ToContract(GpsReading r) => new(
        r.Position.Latitude,
        r.Position.Longitude,
        r.PositionAccuracy,
        r.Timestamp,
        r.Heading,
        r.HeadingAccuracy,
        r.Altitude,
        r.Speed,
        r.SpeedAccuracy,
        r.Floor,
        r.IsStationary
    );

    public static Contracts.MotionActivity ToContract(MotionActivityReading reading) => new(
        BridgeEnum.Convert<MotionActivityType, Contracts.MotionActivityType>(reading.Activity),
        BridgeEnum.Convert<MotionActivityConfidence, Contracts.MotionActivityConfidence>(reading.Confidence),
        reading.Timestamp
    );

    public static Contracts.MotionAccessResult ToMotionAccess(AccessState access)
        => new(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access));
}
