using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Locations.Client;
using static Shiny.AppDeviceBridge.Locations.LocationContractMapping;

namespace Shiny.AppDeviceBridge.Locations;

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
        .MapEvent("gps.reading", this.Readings, Contracts.LocationsJsonContext.Default.GpsReading);

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

        return WebAppBridgeResults.Json(context, ToContract(g.GetCurrentStatus(mode)), Contracts.LocationsJsonContext.Default.LocationAccessResult);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.LocationsJsonContext.Default.GpsListenerSettings) ?? new Contracts.GpsListenerSettings();
        var access = await g.RequestAccess(ToRequest(body));

        await WebAppBridgeResults.Json(context, ToContract(access), Contracts.LocationsJsonContext.Default.LocationAccessResult);
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
            ? WebAppBridgeResults.Json(context, ToContract(listener), Contracts.LocationsJsonContext.Default.GpsListenerSettings)
            : WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StartListenerAsync(HttpContext context)
    {
        if (this.gps is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "GPS");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.LocationsJsonContext.Default.GpsListenerSettings) ?? new Contracts.GpsListenerSettings();

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
            : WebAppBridgeResults.Json(context, ToContract(reading), Contracts.LocationsJsonContext.Default.GpsReading);

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
/// <c>/_bridge/geofences</c> over <see cref="IGeofenceManager"/>.
/// <code>
/// GET    /_bridge/geofences/status
/// POST   /_bridge/geofences/access
/// GET    /_bridge/geofences/regions
/// POST   /_bridge/geofences/regions                 { "identifier": "home", "latitude": …, "longitude": …, "radiusMeters": 200 }
/// DELETE /_bridge/geofences/regions
/// DELETE /_bridge/geofences/regions/{identifier}
/// GET    /_bridge/geofences/regions/{identifier}/state
///
/// events: geofence.status
/// </code>
/// </summary>
public sealed class GeofenceBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IGeofenceManager? geofences = services.GetOptionalService<IGeofenceManager>();

    public string Name => "geofences";

    public bool IsSupported => this.geofences is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("/status", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapGet("/regions", this.ListAsync)
            .MapPost("/regions", this.StartMonitoringAsync)
            .MapDelete("/regions", this.StopAllAsync)
            .MapDelete("/regions/{identifier}", this.StopMonitoringAsync)
            .MapGet("/regions/{identifier}/state", this.StateAsync);

        // Published by WebAppGeofenceDelegate when the OS reports a transition.
        routes.Events.Source("geofence.status", Contracts.LocationsJsonContext.Default.GeofenceStatus);
    }

    ValueTask StatusAsync(HttpContext context)
        => this.geofences is { } g
            ? WebAppBridgeResults.Json(context, ToContract(g.CurrentStatus), Contracts.LocationsJsonContext.Default.LocationAccessResult)
            : WebAppBridgeResults.NotSupported(context, "Geofencing");

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        await WebAppBridgeResults.Json(context, ToContract(await g.RequestAccess()), Contracts.LocationsJsonContext.Default.LocationAccessResult);
    }

    ValueTask ListAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
            return WebAppBridgeResults.NotSupported(context, "Geofencing");

        IReadOnlyList<Contracts.GeofenceRegion> regions = [.. g.GetMonitorRegions().Select(ToContract)];
        return WebAppBridgeResults.Json(context, regions, Contracts.LocationsJsonContext.Default.IReadOnlyListGeofenceRegion);
    }

    async ValueTask StartMonitoringAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.LocationsJsonContext.Default.GeofenceRegion);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a region.");
            return;
        }

        if (!TryToRegion(body, out var region, out var error))
        {
            await WebAppBridgeResults.BadRequest(context, error!);
            return;
        }

        try
        {
            await g.StartMonitoring(region!);
        }
        catch (InvalidOperationException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "geofence_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopAllAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        await g.StopAllMonitoring();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopMonitoringAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        await g.StopMonitoring(context.Request.RouteValues["identifier"] ?? String.Empty);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StateAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        var identifier = context.Request.RouteValues["identifier"];
        var region = g.GetMonitorRegions().FirstOrDefault(x => x.Identifier == identifier);

        if (region is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No monitored region '{identifier}'.");
            return;
        }

        var state = await g.RequestState(region, context.RequestAborted);
        await WebAppBridgeResults.Json(
            context,
            ToContract(region, state),
            Contracts.LocationsJsonContext.Default.GeofenceStatus
        );
    }
}

/// <summary>
/// Forwards geofence transitions to the page as <c>geofence.status</c> events.
/// <see cref="LocationBridgeExtensions.AddGeofenceBridge"/> registers it; subclass it to do more with
/// a transition, or call <see cref="Publish"/> from a delegate of your own.
/// <para>
/// It also calls the web app's <c>geofence</c> handler with <c>{ identifier, state }</c> — the page if it
/// is listening, background.js otherwise — which is how a transition that wakes the app in the background
/// reaches web code.
/// </para>
/// </summary>
public class WebAppGeofenceDelegate(WebAppEventHub events, WebAppInvoker invoker) : IGeofenceDelegate
{
    public virtual async Task OnStatusChanged(GeofenceState newStatus, GeofenceRegion region)
    {
        Publish(events, newStatus, region);

        await invoker.InvokeAsync(
            "geofence",
            ToContract(region, newStatus),
            Contracts.LocationsJsonContext.Default.GeofenceStatus
        );
    }

    public static void Publish(WebAppEventHub events, GeofenceState state, GeofenceRegion region)
        => events
            .Source("geofence.status", Contracts.LocationsJsonContext.Default.GeofenceStatus)
            .Publish(ToContract(region, state));
}

public static class LocationBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/gps</c> and registers Shiny's GPS service for the platform — there is nothing
    /// else to call. Where the platform has no GPS support the endpoints answer 501.
    /// </summary>
    public static MauiAppBuilder AddGpsBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        // The delegate is how a reading reaches the web app while it is in the background. Shiny runs every
        // registered delegate, so the app's own keep working alongside it.
        builder.EnsureShiny();
        builder.Services.AddGps<WebAppGpsDelegate>();
#endif

        builder.Services.AddWebAppBridge<GpsBridge>();
        return builder;
    }

    /// <summary>
    /// Adds <c>/_bridge/geofences</c> and registers Shiny's geofencing with
    /// <see cref="WebAppGeofenceDelegate"/>, so transitions reach the page as <c>geofence.status</c>.
    /// </summary>
    public static MauiAppBuilder AddGeofenceBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
        builder.Services.AddGeofencing<WebAppGeofenceDelegate>();
#endif

        builder.Services.AddWebAppBridge<GeofenceBridge>();
        return builder;
    }

    /// <summary><see cref="AddGpsBridge"/> and <see cref="AddGeofenceBridge"/>.</summary>
    public static MauiAppBuilder AddLocationBridges(this MauiAppBuilder builder)
        => builder.AddGpsBridge().AddGeofenceBridge();
}

/// <summary>
/// Hands readings from Shiny's GPS delegate — what runs when the OS delivers them in the background — to
/// the web app's <c>gps</c> handler: the page if it is listening, background.js otherwise. The live
/// <c>gps.reading</c> event is separate and unchanged. Registered by
/// <see cref="LocationBridgeExtensions.AddGpsBridge"/>.
/// </summary>
public class WebAppGpsDelegate(WebAppInvoker invoker) : IGpsDelegate
{
    public virtual Task OnReading(GpsReading reading)
        => invoker.InvokeAsync("gps", ToContract(reading), Contracts.LocationsJsonContext.Default.GpsReading);
}

static class LocationContractMapping
{
    public static Contracts.LocationAccessResult ToContract(AccessState access)
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

    public static Contracts.GeofenceRegion ToContract(GeofenceRegion r) => new(
        r.Identifier,
        r.Center.Latitude,
        r.Center.Longitude,
        r.Radius.TotalMeters,
        r.SingleUse,
        r.NotifyOnEntry,
        r.NotifyOnExit
    );

    public static Contracts.GeofenceStatus ToContract(GeofenceRegion region, GeofenceState state)
        => new(region.Identifier, BridgeEnum.Convert<GeofenceState, Contracts.GeofenceState>(state));

    public static bool TryToRegion(Contracts.GeofenceRegion request, out GeofenceRegion? region, out string? error)
    {
        region = null;
        error = null;

        if (String.IsNullOrWhiteSpace(request.Identifier))
            error = "identifier is required.";
        else if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            error = "latitude or longitude is out of range.";
        else if (request.RadiusMeters <= 0)
            error = "radiusMeters must be positive.";

        if (error is not null)
            return false;

        region = new GeofenceRegion(
            request.Identifier,
            new Position(request.Latitude, request.Longitude),
            Distance.FromMeters(request.RadiusMeters),
            request.SingleUse,
            request.NotifyOnEntry,
            request.NotifyOnExit
        );

        return true;
    }

    public static Contracts.MotionActivity ToContract(MotionActivityReading reading) => new(
        BridgeEnum.Convert<MotionActivityType, Contracts.MotionActivityType>(reading.Activity),
        BridgeEnum.Convert<MotionActivityConfidence, Contracts.MotionActivityConfidence>(reading.Confidence),
        reading.Timestamp
    );
}
