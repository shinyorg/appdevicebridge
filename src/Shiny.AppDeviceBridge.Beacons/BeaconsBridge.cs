using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Beacons.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.Beacons;
using static Shiny.AppDeviceBridge.Beacons.BeaconContractMapping;

namespace Shiny.AppDeviceBridge.Beacons;

/// <summary>
/// <c>/_bridge/beacons</c> over Shiny.Beacons' <see cref="Native.IBeaconRangingManager"/>,
/// <see cref="Native.IBeaconMonitoringManager"/>, <see cref="Native.IEddystoneScanner"/> and
/// <see cref="Native.IBeaconBroadcaster"/>. Each part answers 501 when its service is not registered.
/// <code>
/// GET    /_bridge/beacons/status
/// POST   /_bridge/beacons/access                   { "feature": "Ranging" }
/// GET    /_bridge/beacons/ranging
/// POST   /_bridge/beacons/ranging                  { "identifier": "lobby", "uuid": "…", "major": 1 }   readings arrive as beacons.ranged
/// DELETE /_bridge/beacons/ranging
/// DELETE /_bridge/beacons/ranging/{identifier}
/// GET    /_bridge/beacons/regions
/// POST   /_bridge/beacons/regions                  { "identifier": "lobby", "uuid": "…" }
/// DELETE /_bridge/beacons/regions
/// DELETE /_bridge/beacons/regions/{identifier}
/// GET    /_bridge/beacons/regions/{identifier}/state
/// POST   /_bridge/beacons/eddystone                frames arrive as beacons.eddystone
/// DELETE /_bridge/beacons/eddystone
/// GET    /_bridge/beacons/broadcast
/// POST   /_bridge/beacons/broadcast/ibeacon        { "uuid": "…", "major": 1, "minor": 2 }
/// POST   /_bridge/beacons/broadcast/eddystone-uid  { "namespace": "hex", "instance": "hex" }
/// POST   /_bridge/beacons/broadcast/eddystone-url  { "url": "https://…" }
/// DELETE /_bridge/beacons/broadcast
///
/// events:   beacons.ranged, beacons.region, beacons.eddystone
/// handlers: beacon
/// </code>
/// Ranging needs a <c>beacons.ranged</c> listener and an Eddystone scan a <c>beacons.eddystone</c> one; when the last
/// listener of either leaves, every ranged region or the scan stops. Monitoring carries on without a page:
/// <see cref="WebAppBeaconMonitorDelegate"/> hands its transitions to background.js.
/// </summary>
public sealed class BeaconsBridge : IWebAppBridge, IDisposable
{
    readonly Native.IBeaconRangingManager? ranging;
    readonly Native.IBeaconMonitoringManager? monitoring;
    readonly Native.IEddystoneScanner? eddystone;
    readonly Native.IBeaconBroadcaster? broadcaster;
    readonly Lock gate = new();
    readonly WebAppEventSource<Contracts.BeaconReading> readings = new();
    readonly WebAppEventSource<Contracts.EddystoneFrame> frames = new();
    readonly Dictionary<string, (Contracts.BeaconRegion Region, IDisposable Subscription)> ranged = new(StringComparer.Ordinal);
    IDisposable? eddystoneScan;

    public BeaconsBridge(IServiceProvider services)
    {
        this.ranging = services.GetOptionalService<Native.IBeaconRangingManager>();
        this.monitoring = services.GetOptionalService<Native.IBeaconMonitoringManager>();
        this.eddystone = services.GetOptionalService<Native.IEddystoneScanner>();
        this.broadcaster = services.GetOptionalService<Native.IBeaconBroadcaster>();
    }

    public string Name => "beacons";

    public bool IsSupported => this.ranging is not null || this.monitoring is not null || this.eddystone is not null || this.broadcaster is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("/status", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapGet("/ranging", this.RangedAsync)
            .MapPost("/ranging", this.StartRangingAsync)
            .MapDelete("/ranging", this.StopAllRangingAsync)
            .MapDelete("/ranging/{identifier}", this.StopRangingAsync)
            .MapGet("/regions", this.MonitoredAsync)
            .MapPost("/regions", this.StartMonitoringAsync)
            .MapDelete("/regions", this.StopAllMonitoringAsync)
            .MapDelete("/regions/{identifier}", this.StopMonitoringAsync)
            .MapGet("/regions/{identifier}/state", this.RegionStateAsync)
            .MapPost("/eddystone", this.StartEddystoneAsync)
            .MapDelete("/eddystone", this.StopEddystoneAsync)
            .MapGet("/broadcast", this.BroadcastAsync)
            .MapPost("/broadcast/ibeacon", this.BroadcastIBeaconAsync)
            .MapPost("/broadcast/eddystone-uid", this.BroadcastEddystoneUidAsync)
            .MapPost("/broadcast/eddystone-url", this.BroadcastEddystoneUrlAsync)
            .MapDelete("/broadcast", this.StopBroadcastAsync)
            .MapEvent("beacons.ranged", ct => this.readings.ListenAsync(this.OnRangingListenerStopped, ct), Contracts.BeaconsJsonContext.Default.BeaconReading)
            .MapEvent("beacons.eddystone", ct => this.frames.ListenAsync(this.OnEddystoneListenerStopped, ct), Contracts.BeaconsJsonContext.Default.EddystoneFrame);

        // Published by WebAppBeaconMonitorDelegate when the OS reports a transition.
        routes.Events.Source("beacons.region", Contracts.BeaconsJsonContext.Default.BeaconRegionStatus);
    }

    ValueTask StatusAsync(HttpContext context)
        => WebAppBridgeResults.Json(
            context,
            new Contracts.BeaconsStatus(
                this.ranging is { } r ? Convert(r.CurrentStatus) : ContractAccess.NotSupported,
                this.monitoring is { } m ? Convert(m.CurrentStatus) : ContractAccess.NotSupported,
                this.eddystone is { } e ? Convert(e.CurrentStatus) : ContractAccess.NotSupported,
                this.broadcaster is not null,
                this.broadcaster?.IsBroadcasting ?? false
            ),
            Contracts.BeaconsJsonContext.Default.BeaconsStatus
        );

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.BeaconsJsonContext.Default.BeaconAccessRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a feature.");
            return;
        }

        Func<Task<AccessState>>? request = body.Feature switch
        {
            Contracts.BeaconFeature.Ranging => this.ranging is { } r ? r.RequestAccess : null,
            Contracts.BeaconFeature.Monitoring => this.monitoring is { } m ? m.RequestAccess : null,
            Contracts.BeaconFeature.Eddystone => this.eddystone is { } e ? e.RequestAccess : null,
            Contracts.BeaconFeature.Broadcasting => this.broadcaster is { } b ? b.RequestAccess : null,
            _ => null
        };

        if (request is null)
        {
            await WebAppBridgeResults.NotSupported(context, $"Beacon {body.Feature.ToString().ToLowerInvariant()}");
            return;
        }

        await WebAppBridgeResults.Json(context, new Contracts.BeaconAccessResult(Convert(await request())), Contracts.BeaconsJsonContext.Default.BeaconAccessResult);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ranging
    // ---------------------------------------------------------------------------------------------------------------

    ValueTask RangedAsync(HttpContext context)
    {
        if (this.ranging is null)
            return WebAppBridgeResults.NotSupported(context, "Beacon ranging");

        IReadOnlyList<Contracts.BeaconRegion> regions;
        lock (this.gate)
            regions = [.. this.ranged.Values.Select(x => x.Region)];

        return WebAppBridgeResults.Json(context, regions, Contracts.BeaconsJsonContext.Default.IReadOnlyListBeaconRegion);
    }

    async ValueTask StartRangingAsync(HttpContext context)
    {
        // A manager that is registered but reports NotSupported — iBeacon ranging on macOS — is a 501 like a missing one.
        if (this.ranging is not { CurrentStatus: not AccessState.NotSupported } r)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon ranging");
            return;
        }

        if (await ReadRegionAsync(context) is not { } region)
            return;

        if (r.CurrentStatus is not (AccessState.Available or AccessState.Restricted))
        {
            await Refused(context, "ranging", r.CurrentStatus);
            return;
        }

        bool listening;
        lock (this.gate)
        {
            // Checked under the lock the stopped callback takes, so ranging never outlives its last listener.
            listening = this.readings.HasListeners;
            if (listening)
            {
                var identifier = region.Contract.Identifier;
                if (this.ranged.Remove(identifier, out var previous))
                    previous.Subscription.Dispose();

                // A failed ranging (permission revoked, radio off) drops its region — unless a newer one replaced it.
                var subscription = new SingleAssignment();
                subscription.Set(r.WhenBeaconRanged(region.Native).Subscribe(
                    beacon => this.readings.Publish(ToContract(identifier, beacon)),
                    _ => this.EndRanging(identifier, subscription)
                ));
                this.ranged[identifier] = (region.Contract, subscription);
            }
        }

        await (listening
            ? WebAppBridgeResults.NoContent(context)
            : NotListening(context, "beacons.ranged", "readings"));
    }

    ValueTask StopAllRangingAsync(HttpContext context)
    {
        if (this.ranging is null)
            return WebAppBridgeResults.NotSupported(context, "Beacon ranging");

        this.EndAllRanging();
        return WebAppBridgeResults.NoContent(context);
    }

    ValueTask StopRangingAsync(HttpContext context)
    {
        if (this.ranging is null)
            return WebAppBridgeResults.NotSupported(context, "Beacon ranging");

        this.EndRanging(Route(context));
        return WebAppBridgeResults.NoContent(context);
    }

    void EndRanging(string identifier, IDisposable? only = null)
    {
        lock (this.gate)
        {
            if (!this.ranged.TryGetValue(identifier, out var entry) || (only is not null && !ReferenceEquals(entry.Subscription, only)))
                return;

            this.ranged.Remove(identifier);
            entry.Subscription.Dispose();
        }
    }

    void EndAllRanging()
    {
        lock (this.gate)
        {
            foreach (var entry in this.ranged.Values)
                entry.Subscription.Dispose();

            this.ranged.Clear();
        }
    }

    /// <summary>Nobody sees the readings any more, so every region stops ranging.</summary>
    void OnRangingListenerStopped(int remaining)
    {
        lock (this.gate)
        {
            if (!this.readings.HasListeners)
                this.EndAllRanging();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Monitoring
    // ---------------------------------------------------------------------------------------------------------------

    ValueTask MonitoredAsync(HttpContext context)
    {
        if (this.monitoring is not { } m)
            return WebAppBridgeResults.NotSupported(context, "Beacon monitoring");

        IReadOnlyList<Contracts.BeaconRegion> regions = [.. m.GetMonitoredRegions().Select(ToContract)];
        return WebAppBridgeResults.Json(context, regions, Contracts.BeaconsJsonContext.Default.IReadOnlyListBeaconRegion);
    }

    async ValueTask StartMonitoringAsync(HttpContext context)
    {
        if (this.monitoring is not { CurrentStatus: not AccessState.NotSupported } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon monitoring");
            return;
        }

        if (await ReadRegionAsync(context) is not { } region)
            return;

        try
        {
            await m.StartMonitoring(region.Native);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PermissionException)
        {
            // Shiny refuses to monitor without permission.
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "beacons_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopAllMonitoringAsync(HttpContext context)
    {
        if (this.monitoring is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon monitoring");
            return;
        }

        await m.StopAllMonitoring();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopMonitoringAsync(HttpContext context)
    {
        if (this.monitoring is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon monitoring");
            return;
        }

        await m.StopMonitoring(Route(context));
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask RegionStateAsync(HttpContext context)
    {
        if (this.monitoring is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon monitoring");
            return;
        }

        var identifier = Route(context);
        var region = m.GetMonitoredRegions().FirstOrDefault(x => x.Identifier == identifier);

        if (region is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No monitored region '{identifier}'.");
            return;
        }

        var state = await m.RequestState(region, context.RequestAborted);
        await WebAppBridgeResults.Json(context, ToContract(region, state), Contracts.BeaconsJsonContext.Default.BeaconRegionStatus);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Eddystone
    // ---------------------------------------------------------------------------------------------------------------

    async ValueTask StartEddystoneAsync(HttpContext context)
    {
        if (this.eddystone is not { CurrentStatus: not AccessState.NotSupported } e)
        {
            await WebAppBridgeResults.NotSupported(context, "Eddystone scanning");
            return;
        }

        if (e.CurrentStatus is not (AccessState.Available or AccessState.Restricted))
        {
            await Refused(context, "Eddystone scanning", e.CurrentStatus);
            return;
        }

        bool listening;
        lock (this.gate)
        {
            listening = this.frames.HasListeners;
            if (listening)
            {
                // One scan at a time; starting again restarts it rather than stacking scans.
                this.eddystoneScan?.Dispose();
                this.eddystoneScan = e.WhenFrameReceived().Subscribe(
                    frame => this.frames.Publish(ToContract(frame)),
                    _ => this.EndEddystone()
                );
            }
        }

        await (listening
            ? WebAppBridgeResults.NoContent(context)
            : NotListening(context, "beacons.eddystone", "frames"));
    }

    ValueTask StopEddystoneAsync(HttpContext context)
    {
        if (this.eddystone is null)
            return WebAppBridgeResults.NotSupported(context, "Eddystone scanning");

        this.EndEddystone();
        return WebAppBridgeResults.NoContent(context);
    }

    void EndEddystone()
    {
        lock (this.gate)
        {
            this.eddystoneScan?.Dispose();
            this.eddystoneScan = null;
        }
    }

    /// <summary>Nobody sees the frames any more, so the scan stops.</summary>
    void OnEddystoneListenerStopped(int remaining)
    {
        lock (this.gate)
        {
            if (!this.frames.HasListeners)
                this.EndEddystone();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Broadcasting
    // ---------------------------------------------------------------------------------------------------------------

    ValueTask BroadcastAsync(HttpContext context)
        => this.broadcaster is { } b
            ? WebAppBridgeResults.Json(context, new Contracts.BeaconBroadcastStatus(b.IsBroadcasting), Contracts.BeaconsJsonContext.Default.BeaconBroadcastStatus)
            : WebAppBridgeResults.NotSupported(context, "Beacon broadcasting");

    async ValueTask BroadcastIBeaconAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.BeaconsJsonContext.Default.BeaconBroadcast);
        await this.StartBroadcastAsync(
            context,
            body is null ? null : body.Uuid == Guid.Empty ? "uuid is required." : null,
            b => b.StartIBeacon(body!.Uuid, body.Major, body.Minor, body.TxPower),
            body is null
        );
    }

    async ValueTask BroadcastEddystoneUidAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.BeaconsJsonContext.Default.EddystoneUidBroadcast);
        Native.EddystoneUid uid = default;
        string? error = null;

        if (body is not null)
        {
            try
            {
                uid = Native.EddystoneUid.Parse(body.Namespace, body.Instance);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                error = "namespace must be 10 bytes and instance 6 bytes, in hex.";
            }
        }

        await this.StartBroadcastAsync(context, error, b => b.StartEddystoneUid(uid, body!.TxPower), body is null);
    }

    async ValueTask BroadcastEddystoneUrlAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.BeaconsJsonContext.Default.EddystoneUrlBroadcast);
        await this.StartBroadcastAsync(
            context,
            body is null ? null : String.IsNullOrWhiteSpace(body.Url) ? "url is required." : null,
            b => b.StartEddystoneUrl(body!.Url, body.TxPower),
            body is null
        );
    }

    async ValueTask StartBroadcastAsync(HttpContext context, string? error, Func<Native.IBeaconBroadcaster, Task> start, bool missingBody = false)
    {
        if (this.broadcaster is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Beacon broadcasting");
            return;
        }

        if (missingBody || error is not null)
        {
            await WebAppBridgeResults.BadRequest(context, error ?? "Expected a broadcast.");
            return;
        }

        try
        {
            await start(b);
        }
        catch (ArgumentException ex)
        {
            // A URL too long to encode, or a value out of range.
            await WebAppBridgeResults.BadRequest(context, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PermissionException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "beacons_refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask StopBroadcastAsync(HttpContext context)
    {
        if (this.broadcaster is not { } b)
            return WebAppBridgeResults.NotSupported(context, "Beacon broadcasting");

        b.Stop();
        return WebAppBridgeResults.NoContent(context);
    }

    // ---------------------------------------------------------------------------------------------------------------

    static async ValueTask<(Contracts.BeaconRegion Contract, Native.BeaconRegion Native)?> ReadRegionAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.BeaconsJsonContext.Default.BeaconRegion);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a region.");
            return null;
        }

        if (!TryToRegion(body, out var region, out var error))
        {
            await WebAppBridgeResults.BadRequest(context, error!);
            return null;
        }

        return (body, region!);
    }

    static ValueTask Refused(HttpContext context, string what, AccessState access)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "beacons_refused", $"Beacon {what} needs permission ({access}); request access first.");

    static ValueTask NotListening(HttpContext context, string eventName, string what)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", $"Listen for {eventName} first: {what} arrive as events.");

    static string Route(HttpContext context) => context.Request.RouteValues["identifier"] ?? String.Empty;

    public void Dispose()
    {
        this.EndAllRanging();
        this.EndEddystone();
    }
    /// <summary>Holds a subscription that its own callbacks need to recognise, before Subscribe has returned it.</summary>
    sealed class SingleAssignment : IDisposable
    {
        IDisposable? inner;
        bool disposed;

        public void Set(IDisposable subscription)
        {
            lock (this)
            {
                if (!this.disposed)
                {
                    this.inner = subscription;
                    return;
                }
            }

            subscription.Dispose();
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (this)
            {
                this.disposed = true;
                inner = this.inner;
                this.inner = null;
            }

            inner?.Dispose();
        }
    }
}

/// <summary>
/// Forwards beacon region transitions to the page as <c>beacons.region</c> events, and calls the web app's
/// <c>beacon</c> handler with <c>{ identifier, state }</c> — the page if it is listening, background.js otherwise — which
/// is how a transition that wakes the app in the background reaches web code. <see cref="BeaconsBridgeExtensions.AddBeaconsBridge"/>
/// registers it; subclass it to do more with a transition, or call <see cref="Publish"/> from a delegate of your own.
/// </summary>
public class WebAppBeaconMonitorDelegate(WebAppEventHub events, WebAppInvoker invoker) : Native.IBeaconMonitorDelegate
{
    public virtual async Task OnStatusChanged(Native.BeaconRegionState newStatus, Native.BeaconRegion region)
    {
        Publish(events, newStatus, region);
        await invoker.InvokeAsync("beacon", ToContract(region, newStatus), Contracts.BeaconsJsonContext.Default.BeaconRegionStatus);
    }

    public static void Publish(WebAppEventHub events, Native.BeaconRegionState state, Native.BeaconRegion region)
        => events
            .Source("beacons.region", Contracts.BeaconsJsonContext.Default.BeaconRegionStatus)
            .Publish(ToContract(region, state));
}

/// <summary>The parts of the beacons bridge <see cref="BeaconsBridgeExtensions.AddBeaconsBridge"/> registers.</summary>
[Flags]
public enum BeaconFeatures
{
    None = 0,

    /// <summary>iBeacons in range, with distances, while the app is in the foreground.</summary>
    Ranging = 1,

    /// <summary>Enter and exit transitions for iBeacon regions, in the background and across restarts.</summary>
    Monitoring = 2,

    /// <summary>Eddystone UID, URL, TLM and EID frames.</summary>
    Eddystone = 4,

    /// <summary>The device advertising itself as an iBeacon or an Eddystone beacon.</summary>
    Broadcasting = 8,

    All = Ranging | Monitoring | Eddystone | Broadcasting
}

public static class BeaconsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/beacons</c> and registers Shiny.Beacons' services for the features asked for, with
    /// <see cref="WebAppBeaconMonitorDelegate"/> for monitoring — there is nothing else to call. A feature left out
    /// answers 501, as does every feature on Linux.
    /// <para>
    /// Platform setup is Shiny.Beacons': on iOS and Mac Catalyst, <c>NSLocationWhenInUseUsageDescription</c> for ranging,
    /// <c>NSLocationAlwaysAndWhenInUseUsageDescription</c> for monitoring and <c>NSBluetoothAlwaysUsageDescription</c> for
    /// Eddystone and broadcasting; on Android, <c>BLUETOOTH_SCAN</c> and <c>BLUETOOTH_CONNECT</c> (with location before
    /// Android 12), <c>BLUETOOTH_ADVERTISE</c> to broadcast, and <c>FOREGROUND_SERVICE_CONNECTED_DEVICE</c> with
    /// <c>POST_NOTIFICATIONS</c> to monitor.
    /// </para>
    /// </summary>
    /// <param name="options">How advertisements become distances and region transitions; Shiny.Beacons' defaults when null.</param>
    public static TBuilder AddBeaconsBridge<TBuilder>(this TBuilder bridge, BeaconFeatures features = BeaconFeatures.All, Native.BeaconRangingOptions? options = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if MACOS
        bridge.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        if (features.HasFlag(BeaconFeatures.Ranging))
            bridge.Services.AddBeaconRanging(options);

        if (features.HasFlag(BeaconFeatures.Monitoring))
            bridge.Services.AddBeaconMonitoring<WebAppBeaconMonitorDelegate>(options);

        if (features.HasFlag(BeaconFeatures.Eddystone))
            bridge.Services.AddEddystoneScanning(options);

        if (features.HasFlag(BeaconFeatures.Broadcasting))
            bridge.Services.AddBeaconBroadcasting();
#endif

        bridge.AddBridge<BeaconsBridge>();
        return bridge;
    }
}

static class BeaconContractMapping
{
    public static ContractAccess Convert(AccessState access) => BridgeEnum.Convert<AccessState, ContractAccess>(access);

    public static Contracts.BeaconRegion ToContract(Native.BeaconRegion r)
        => new(r.Identifier, r.Uuid, r.Major, r.Minor, r.NotifyOnEntry, r.NotifyOnExit);

    public static Contracts.BeaconRegionStatus ToContract(Native.BeaconRegion region, Native.BeaconRegionState state)
        => new(region.Identifier, BridgeEnum.Convert<Native.BeaconRegionState, Contracts.BeaconRegionState>(state));

    public static Contracts.BeaconReading ToContract(string region, Native.Beacon b) => new(
        region,
        b.Uuid,
        b.Major,
        b.Minor,
        b.Rssi,
        BridgeEnum.Convert<Native.Proximity, Contracts.BeaconProximity>(b.Proximity),
        b.Distance,
        b.TxPower,
        b.Timestamp
    );

    public static Contracts.EddystoneFrame ToContract(Native.EddystoneFrame frame) => frame switch
    {
        Native.EddystoneUidFrame uid => new(
            Contracts.EddystoneFrameType.Uid, uid.PeripheralId, uid.Rssi, uid.Timestamp,
            Namespace: uid.Uid.Namespace, Instance: uid.Uid.Instance,
            TxPower: uid.TxPower, Distance: uid.Distance, Proximity: Convert(uid.Proximity)
        ),
        Native.EddystoneUrlFrame url => new(
            Contracts.EddystoneFrameType.Url, url.PeripheralId, url.Rssi, url.Timestamp,
            Url: url.Url,
            TxPower: url.TxPower, Distance: url.Distance, Proximity: Convert(url.Proximity)
        ),
        Native.EddystoneEidFrame eid => new(
            Contracts.EddystoneFrameType.Eid, eid.PeripheralId, eid.Rssi, eid.Timestamp,
            EphemeralId: eid.EphemeralIdHex,
            TxPower: eid.TxPower, Distance: eid.Distance, Proximity: Convert(eid.Proximity)
        ),
        Native.EddystoneTlmFrame tlm => new(
            Contracts.EddystoneFrameType.Tlm, tlm.PeripheralId, tlm.Rssi, tlm.Timestamp,
            BatteryVolts: tlm.BatteryVolts, TemperatureCelsius: tlm.TemperatureCelsius,
            AdvertisementCount: tlm.AdvertisementCount, Uptime: tlm.Uptime, Encrypted: tlm.IsEncrypted
        ),
        _ => new(BridgeEnum.Convert<Native.EddystoneFrameType, Contracts.EddystoneFrameType>(frame.FrameType), frame.PeripheralId, frame.Rssi, frame.Timestamp)
    };

    static Contracts.BeaconProximity Convert(Native.Proximity proximity)
        => BridgeEnum.Convert<Native.Proximity, Contracts.BeaconProximity>(proximity);

    public static bool TryToRegion(Contracts.BeaconRegion request, out Native.BeaconRegion? region, out string? error)
    {
        region = null;
        error = null;

        if (String.IsNullOrWhiteSpace(request.Identifier))
            error = "identifier is required.";
        else if (request.Uuid == Guid.Empty)
            error = "uuid is required.";
        else if (request.Minor is not null && request.Major is null)
            error = "minor needs major.";

        if (error is not null)
            return false;

        region = new Native.BeaconRegion(request.Identifier, request.Uuid, request.Major, request.Minor, request.NotifyOnEntry, request.NotifyOnExit);
        return true;
    }
}
