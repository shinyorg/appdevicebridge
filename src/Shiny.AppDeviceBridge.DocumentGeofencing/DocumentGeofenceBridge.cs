using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Client;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Geofencing;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.DocumentGeofencing.Client;

namespace Shiny.AppDeviceBridge.DocumentGeofencing;

/// <summary>
/// <c>/_bridge/documentgeofences</c> over <see cref="IDocumentGeofenceManager"/>.
/// <code>
/// GET  /_bridge/documentgeofences/status
/// POST /_bridge/documentgeofences/access
/// POST /_bridge/documentgeofences/start
/// POST /_bridge/documentgeofences/stop
/// GET  /_bridge/documentgeofences/current
///
/// events: documentgeofence.change
/// </code>
/// The region sets are registered in C# — they are typed selectors and filters over the app's own documents — so the
/// page controls monitoring but never defines what is monitored.
/// </summary>
public sealed class DocumentGeofenceBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IDocumentGeofenceManager? geofences = services.GetOptionalService<IDocumentGeofenceManager>();
    readonly DocumentGeofenceConfig? config = services.GetOptionalService<DocumentGeofenceConfig>();
    readonly DocumentRegionSerializer serializer = new(services);

    public string Name => "documentgeofences";

    public bool IsSupported => this.geofences is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("/status", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapPost("/start", this.StartAsync)
            .MapPost("/stop", this.StopAsync)
            .MapGet("/current", this.CurrentAsync);

        // Published by WebAppDocumentGeofenceDelegate when a GPS reading crosses a region boundary.
        routes.Events.Source(WebAppDocumentGeofenceDelegate.EventName, Contracts.DocumentGeofencingJsonContext.Default.DocumentRegionChange);
    }

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
            return WebAppBridgeResults.NotSupported(context, "Document geofencing");

        IReadOnlyList<Contracts.DocumentRegionSetInfo> sets = [.. (this.config?.RegionSets ?? []).Select(x => new Contracts.DocumentRegionSetInfo(x.Name, x.WithinMeters))];
        return WebAppBridgeResults.Json(context, new Contracts.DocumentGeofenceStatus(g.IsStarted, sets), Contracts.DocumentGeofencingJsonContext.Default.DocumentGeofenceStatus);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Document geofencing");
            return;
        }

        var access = await g.RequestAccess();
        await WebAppBridgeResults.Json(
            context,
            new Contracts.DocumentGeofenceAccessResult(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access)),
            Contracts.DocumentGeofencingJsonContext.Default.DocumentGeofenceAccessResult
        );
    }

    async ValueTask StartAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Document geofencing");
            return;
        }

        try
        {
            await g.Start();
        }
        catch (NotSupportedException ex)
        {
            // The store's provider cannot run spatial queries (LiteDB, IndexedDB, …).
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "spatial_not_supported", ex.Message);
            return;
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
            await WebAppBridgeResults.NotSupported(context, "Document geofencing");
            return;
        }

        await g.Stop();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CurrentAsync(HttpContext context)
    {
        if (this.geofences is not { } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Document geofencing");
            return;
        }

        var current = await g.GetCurrent(context.RequestAborted);
        IReadOnlyList<Contracts.DocumentCurrentRegion> regions = [.. current.Select(x => new Contracts.DocumentCurrentRegion(
            x.RegionSet,
            x.RegionId,
            x.RegionName,
            x.DistanceMeters,
            this.serializer.Serialize(x.Region)
        ))];

        await WebAppBridgeResults.Json(context, regions, Contracts.DocumentGeofencingJsonContext.Default.IReadOnlyListDocumentCurrentRegion);
    }
}

/// <summary>
/// Forwards region changes to the page as <c>documentgeofence.change</c> events, and calls the web app's
/// <c>documentgeofence</c> handler with the same payload — the page if it is listening, background.js otherwise — which
/// is how a change that wakes the app in the background reaches web code.
/// <para>
/// <see cref="DocumentGeofenceBridgeExtensions.AddDocumentGeofenceBridge"/> registers it as the app's
/// <see cref="IDocumentGeofenceDelegate"/>, and Shiny.DocumentDb.Geofencing has room for one: to handle a change in C#
/// as well, subclass it, call the base <see cref="OnRegionChanged"/>, and register the subclass with
/// <see cref="WebAppDocumentGeofenceOptions.UseDelegate{TDelegate}"/>.
/// </para>
/// </summary>
public class WebAppDocumentGeofenceDelegate(IServiceProvider services, WebAppEventHub events, WebAppInvoker invoker) : IDocumentGeofenceDelegate
{
    public const string EventName = "documentgeofence.change";
    public const string HandlerName = "documentgeofence";

    readonly DocumentRegionSerializer serializer = new(services);

    public virtual async Task OnRegionChanged(DocumentRegionChange change)
    {
        var contract = new Contracts.DocumentRegionChange(
            change.RegionSet,
            change.RegionId,
            change.RegionName,
            change.Entered,
            change.Position.Latitude,
            change.Position.Longitude,
            this.serializer.Serialize(change.Region)
        );

        events
            .Source(EventName, Contracts.DocumentGeofencingJsonContext.Default.DocumentRegionChange)
            .Publish(contract);

        await invoker.InvokeAsync(HandlerName, contract, Contracts.DocumentGeofencingJsonContext.Default.DocumentRegionChange);
    }
}

public sealed class WebAppDocumentGeofenceOptions
{
    /// <summary>
    /// How region documents are written into <c>region</c> on a change and on <c>current</c>. Defaults to the unkeyed
    /// store's <c>DocumentStoreOptions.JsonSerializerOptions</c>; set it when the region sets live in a keyed store or
    /// the store's options lack the region types' metadata. A type with no metadata here is left out, never reflected
    /// over.
    /// </summary>
    public JsonSerializerOptions? RegionSerializerOptions { get; set; }

    /// <summary>
    /// Your own delegate in place of <see cref="WebAppDocumentGeofenceDelegate"/>, to handle changes in C# as well.
    /// <code>
    /// bridge.AddDocumentGeofenceBridge(cfg => cfg.AddRegionSet&lt;Zone&gt;(…), o => o.UseDelegate&lt;MyGeofenceDelegate&gt;());
    /// </code>
    /// </summary>
    public WebAppDocumentGeofenceOptions UseDelegate<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] TDelegate
    >() where TDelegate : WebAppDocumentGeofenceDelegate
    {
#if ANDROID || IOS || MACCATALYST
        this.Register = (services, configure) => services.AddDocumentGeofencing<TDelegate>(configure);
#endif
        return this;
    }

#if ANDROID || IOS || MACCATALYST
    internal Action<IServiceCollection, Action<DocumentGeofenceConfig>> Register { get; private set; }
        = (services, configure) => services.AddDocumentGeofencing<WebAppDocumentGeofenceDelegate>(configure);
#endif
}

public static class DocumentGeofenceBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/documentgeofences</c> and registers Shiny.DocumentDb.Geofencing with
    /// <see cref="WebAppDocumentGeofenceDelegate"/>, so changes reach the page as <c>documentgeofence.change</c>.
    /// The app registers its document store itself — with a spatial provider and each region type's geometry mapped.
    /// <code>
    /// bridge.AddDocumentGeofenceBridge(cfg => cfg
    ///     .AddRegionSet&lt;Zone&gt;("zones", z => z.Id, z => z.Name, filter: z => z.Active)
    ///     .AddRegionSet&lt;GeoCity&gt;("cities", c => c.Id, c => c.Name, withinMeters: 25_000));
    /// </code>
    /// <para>
    /// Monitoring runs Shiny.Gps' one listener. The GPS bridge drives that same listener, so a page that stops it
    /// through <c>/_bridge/gps/listener</c> stops geofencing too.
    /// </para>
    /// </summary>
    /// <param name="configure">Registers the region sets with <c>AddRegionSet&lt;T&gt;</c> and tunes the GPS reading filters.</param>
    /// <param name="options">The delegate, and how region documents are serialized.</param>
    public static TBuilder AddDocumentGeofenceBridge<TBuilder>(
        this TBuilder bridge,
        Action<DocumentGeofenceConfig> configure,
        Action<WebAppDocumentGeofenceOptions>? options = null
    ) where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = new WebAppDocumentGeofenceOptions();
        options?.Invoke(opts);
        bridge.Services.TryAddSingleton(opts);

#if ANDROID || IOS || MACCATALYST
        opts.Register(bridge.Services, configure);
#endif

        bridge.AddBridge<DocumentGeofenceBridge>();
        return bridge;
    }
}

/// <summary>
/// Writes a region document as the store serializes it — only through <see cref="JsonSerializerOptions"/> metadata, so
/// trimmed and AOT apps stay safe: a type with no metadata is left out rather than reflected over.
/// </summary>
sealed class DocumentRegionSerializer(IServiceProvider services)
{
    readonly JsonSerializerOptions? configured = services.GetOptionalService<WebAppDocumentGeofenceOptions>()?.RegionSerializerOptions;
    readonly DocumentStoreOptions? store = services.GetOptionalService<DocumentStoreOptions>();

    public JsonElement? Serialize(object? region)
    {
        // Read per call: the store fills in its default options only when it is first built.
        var options = this.configured ?? this.store?.JsonSerializerOptions;
        if (region is null || options is null)
            return null;

        try
        {
            return options.TryGetTypeInfo(region.GetType(), out var typeInfo)
                ? JsonSerializer.SerializeToElement(region, typeInfo)
                : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
