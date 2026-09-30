using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Geofencing.Client;
using static Shiny.AppDeviceBridge.Geofencing.GeofenceContractMapping;

namespace Shiny.AppDeviceBridge.Geofencing;

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
        routes.Events.Source("geofence.status", Contracts.GeofencingJsonContext.Default.GeofenceStatus);
    }

    ValueTask StatusAsync(HttpContext context)
        => this.geofences is { } g
            ? WebAppBridgeResults.Json(context, ToContract(g.CurrentStatus), Contracts.GeofencingJsonContext.Default.GeofenceAccessResult)
            : WebAppBridgeResults.NotSupported(context, "Geofencing");

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        await WebAppBridgeResults.Json(context, ToContract(await g.RequestAccess()), Contracts.GeofencingJsonContext.Default.GeofenceAccessResult);
    }

    ValueTask ListAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
            return WebAppBridgeResults.NotSupported(context, "Geofencing");

        IReadOnlyList<Contracts.GeofenceRegion> regions = [.. g.GetMonitorRegions().Select(ToContract)];
        return WebAppBridgeResults.Json(context, regions, Contracts.GeofencingJsonContext.Default.IReadOnlyListGeofenceRegion);
    }

    async ValueTask StartMonitoringAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofencing");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.GeofencingJsonContext.Default.GeofenceRegion);
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
            Contracts.GeofencingJsonContext.Default.GeofenceStatus
        );
    }
}

/// <summary>
/// Forwards geofence transitions to the page as <c>geofence.status</c> events.
/// <see cref="GeofenceBridgeExtensions.AddGeofenceBridge"/> registers it; subclass it to do more with
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
            Contracts.GeofencingJsonContext.Default.GeofenceStatus
        );
    }

    public static void Publish(WebAppEventHub events, GeofenceState state, GeofenceRegion region)
        => events
            .Source("geofence.status", Contracts.GeofencingJsonContext.Default.GeofenceStatus)
            .Publish(ToContract(region, state));
}

public static class GeofenceBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/geofences</c> and registers Shiny's geofencing with
    /// <see cref="WebAppGeofenceDelegate"/>, so transitions reach the page as <c>geofence.status</c>.
    /// </summary>
    public static TBuilder AddGeofenceBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        bridge.Services.AddGeofencing<WebAppGeofenceDelegate>();
#endif

        bridge.AddBridge<GeofenceBridge>();
        return bridge;
    }
}

static class GeofenceContractMapping
{
    public static Contracts.GeofenceAccessResult ToContract(AccessState access)
        => new(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access));

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
}
