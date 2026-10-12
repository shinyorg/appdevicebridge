using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.RegionGeofencing.Client;

namespace Shiny.AppDeviceBridge.RegionGeofencing;

/// <summary>
/// <c>/_bridge/regiongeofences</c> over <see cref="IRegionGeofenceManager"/>.
/// <code>
/// GET  /_bridge/regiongeofences/status
/// POST /_bridge/regiongeofences/access
/// POST /_bridge/regiongeofences/start
/// POST /_bridge/regiongeofences/stop
/// GET  /_bridge/regiongeofences/current
///
/// events: regiongeofence.change
/// </code>
/// The region sets are registered in C# — they are region sources over the app's own documents, the geocoder or
/// anything else — so the page controls monitoring but never defines what is monitored.
/// </summary>
public sealed class RegionGeofenceBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IRegionGeofenceManager? geofences = services.GetOptionalService<IRegionGeofenceManager>();
    readonly RegionGeofenceConfig? config = services.GetOptionalService<RegionGeofenceConfig>();
    readonly RegionSerializer serializer = new(services);

    public string Name => "regiongeofences";

    public bool IsSupported => this.geofences is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("/status", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapPost("/start", this.StartAsync)
            .MapPost("/stop", this.StopAsync)
            .MapGet("/current", this.CurrentAsync);

        // Published by WebAppRegionGeofenceDelegate when a GPS reading crosses a region boundary.
        routes.Events.Source(WebAppRegionGeofenceDelegate.EventName, Contracts.RegionGeofencingJsonContext.Default.RegionChange);
    }

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
            return WebAppBridgeResults.NotSupported(context, "Region geofencing");

        IReadOnlyList<Contracts.RegionSetInfo> sets = [.. (this.config?.RegionSets ?? []).Select(x => new Contracts.RegionSetInfo(x.Name))];
        return WebAppBridgeResults.Json(context, new Contracts.RegionGeofenceStatus(g.IsStarted, sets), Contracts.RegionGeofencingJsonContext.Default.RegionGeofenceStatus);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Region geofencing");
            return;
        }

        var access = await g.RequestAccess();
        await WebAppBridgeResults.Json(
            context,
            new Contracts.RegionGeofenceAccessResult(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access)),
            Contracts.RegionGeofencingJsonContext.Default.RegionGeofenceAccessResult
        );
    }

    async ValueTask StartAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Region geofencing");
            return;
        }

        try
        {
            await g.Start();
        }
        catch (InvalidOperationException ex)
        {
            // Shiny's GPS refuses to start without permission.
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "geofence_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Region geofencing");
            return;
        }

        await g.Stop();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CurrentAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Region geofencing");
            return;
        }

        var current = await g.GetCurrent(context.RequestAborted);
        IReadOnlyList<Contracts.CurrentRegion> regions = [.. current.Select(x => new Contracts.CurrentRegion(
            x.RegionSet,
            x.RegionId,
            x.RegionName,
            x.DistanceMeters,
            this.serializer.Serialize(x.Region)
        ))];

        await WebAppBridgeResults.Json(context, regions, Contracts.RegionGeofencingJsonContext.Default.IReadOnlyListCurrentRegion);
    }
}

/// <summary>
/// Forwards region changes to the page as <c>regiongeofence.change</c> events, and calls the web app's
/// <c>regiongeofence</c> handler with the same payload — the page if it is listening, background.js otherwise — which
/// is how a change that wakes the app in the background reaches web code.
/// <para>
/// <see cref="RegionGeofenceBridgeExtensions.AddRegionGeofenceBridge"/> registers it as the app's
/// <see cref="IRegionGeofenceDelegate"/>, and Shiny.Geofencing.Regions has room for one: to handle a change in C# as
/// well, subclass it, call the base <see cref="OnRegionChanged"/>, and register the subclass with
/// <see cref="WebAppRegionGeofenceOptions.UseDelegate{TDelegate}"/>.
/// </para>
/// </summary>
public class WebAppRegionGeofenceDelegate(IServiceProvider services, WebAppEventHub events, WebAppInvoker invoker) : IRegionGeofenceDelegate
{
    public const string EventName = "regiongeofence.change";
    public const string HandlerName = "regiongeofence";

    readonly RegionSerializer serializer = new(services);

    public virtual async Task OnRegionChanged(RegionChange change)
    {
        var contract = new Contracts.RegionChange(
            change.RegionSet,
            change.RegionId,
            change.RegionName,
            change.Entered,
            change.Position.Latitude,
            change.Position.Longitude,
            this.serializer.Serialize(change.Region)
        );

        events
            .Source(EventName, Contracts.RegionGeofencingJsonContext.Default.RegionChange)
            .Publish(contract);

        await invoker.InvokeAsync(HandlerName, contract, Contracts.RegionGeofencingJsonContext.Default.RegionChange);
    }
}

public sealed class WebAppRegionGeofenceOptions
{
    /// <summary>
    /// How region objects are written into <c>region</c> on a change and on <c>current</c> — for document sets, the
    /// document store's own <c>JsonSerializerOptions</c> is the usual choice. A geocoder set's placemark is written
    /// without it. A type with no metadata here is left out, never reflected over.
    /// </summary>
    public JsonSerializerOptions? RegionSerializerOptions { get; set; }

    /// <summary>
    /// Your own delegate in place of <see cref="WebAppRegionGeofenceDelegate"/>, to handle changes in C# as well.
    /// <code>
    /// bridge.AddRegionGeofenceBridge(cfg => cfg.AddNorthAmericanRegions(), o => o.UseDelegate&lt;MyGeofenceDelegate&gt;());
    /// </code>
    /// </summary>
    public WebAppRegionGeofenceOptions UseDelegate<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TDelegate
    >() where TDelegate : WebAppRegionGeofenceDelegate
    {
#if ANDROID || IOS || MACCATALYST
        this.Register = (services, configure) => services.AddRegionGeofencing<TDelegate>(configure);
#endif
        return this;
    }

#if ANDROID || IOS || MACCATALYST
    internal Action<IServiceCollection, Action<RegionGeofenceConfig>> Register { get; private set; }
        = (services, configure) => services.AddRegionGeofencing<WebAppRegionGeofenceDelegate>(configure);
#endif
}

public static class RegionGeofenceBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/regiongeofences</c> and registers Shiny.Geofencing.Regions with
    /// <see cref="WebAppRegionGeofenceDelegate"/>, so changes reach the page as <c>regiongeofence.change</c>.
    /// <code>
    /// bridge.AddRegionGeofenceBridge(cfg => cfg
    ///     .AddGeocoderRegionSet("countries", GeocoderRegionLevel.Country)
    ///     .AddDocumentRegionSet&lt;Zone&gt;("zones", z => z.Id, z => z.Name, filter: z => z.Active));
    /// </code>
    /// <para>
    /// Document sets come from Shiny.Geofencing.Regions.DocumentDb, and the app registers their SQLite document store
    /// itself, with each region type's geometry mapped. Monitoring runs Shiny.Gps' one listener. The GPS bridge drives
    /// that same listener, so a page that stops it through <c>/_bridge/gps/listener</c> stops geofencing too.
    /// </para>
    /// </summary>
    /// <param name="configure">Registers the region sets and tunes the GPS reading filters.</param>
    /// <param name="options">The delegate, and how region objects are serialized.</param>
    public static TBuilder AddRegionGeofenceBridge<TBuilder>(
        this TBuilder bridge,
        Action<RegionGeofenceConfig> configure,
        Action<WebAppRegionGeofenceOptions>? options = null
    ) where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = new WebAppRegionGeofenceOptions();
        options?.Invoke(opts);
        bridge.Services.TryAddSingleton(opts);

#if ANDROID || IOS || MACCATALYST
        opts.Register(bridge.Services, configure);
#endif

        bridge.AddBridge<RegionGeofenceBridge>();
        return bridge;
    }
}

/// <summary>
/// Writes a region object only through <see cref="JsonSerializerOptions"/> metadata, so trimmed and AOT apps stay safe:
/// a type with no metadata is left out rather than reflected over.
/// </summary>
sealed class RegionSerializer(IServiceProvider services)
{
    readonly JsonSerializerOptions? configured = services.GetOptionalService<WebAppRegionGeofenceOptions>()?.RegionSerializerOptions;

    public JsonElement? Serialize(object? region)
    {
        if (region is null)
            return null;

        if (region is Placemark placemark)
            return JsonSerializer.SerializeToElement(placemark, RegionJsonContext.Default.Placemark);

        try
        {
            return this.configured is { } options && options.TryGetTypeInfo(region.GetType(), out var typeInfo)
                ? JsonSerializer.SerializeToElement(region, typeInfo)
                : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

// A geocoder set's region.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Placemark))]
partial class RegionJsonContext : JsonSerializerContext;
