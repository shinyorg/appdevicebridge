using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.Wifi;
using Contracts = Shiny.AppDeviceBridge.Wifi.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using static Shiny.AppDeviceBridge.Wifi.WifiContractMapping;

namespace Shiny.AppDeviceBridge.Wifi;

public static class WifiBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/wifi</c> and registers Shiny's Wi-Fi service for the platform — there is nothing else
    /// to call. Pass <paramref name="hotspot"/> to register the hotspot service and its endpoints too.
    /// <para>
    /// What works varies more by platform than for any other bridge — iOS cannot scan, Android cannot toggle
    /// the radio, and so on. <c>GET /_bridge/wifi</c> reports the platform's capabilities, and a call it lacks
    /// answers 501. Permissions are Shiny.Net.Wifi's: location on Android, the Access Wi-Fi Information and
    /// Hotspot Configuration entitlements on iOS.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddWifiBridge(this MauiAppBuilder builder, bool hotspot = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#endif

        // Called as static methods: the net10.0 build references both Shiny.Net.Wifi and Shiny.Net.Wifi.Linux,
        // which each define AddWifi, so extension syntax would be ambiguous there.
#if WIFI_LINUX
        if (OperatingSystem.IsLinux())
        {
            global::Shiny.LinuxWifiServiceCollectionExtensions.AddWifi(builder.Services);

            if (hotspot)
                global::Shiny.LinuxWifiServiceCollectionExtensions.AddWifiHotspot(builder.Services);
        }
        else
#endif
        {
            global::Shiny.WifiServiceCollectionExtensions.AddWifi(builder.Services);

            if (hotspot)
                global::Shiny.WifiServiceCollectionExtensions.AddWifiHotspot(builder.Services);
        }

        builder.Services.AddWebAppBridge<WifiBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/wifi</c> over <see cref="IWifiManager"/> and, when registered, <see cref="IWifiHotspot"/>.
/// <code>
/// GET    /_bridge/wifi                     { "capabilities": ["Scan", "Connect", …], "current": {…}, "hotspotSupported": false }
/// POST   /_bridge/wifi/access
/// GET    /_bridge/wifi/networks            scans
/// GET    /_bridge/wifi/current             204 when not on Wi-Fi
/// POST   /_bridge/wifi/connection          { "ssid": "…", "passphrase": "…" }  or  { "knownNetworkId": "…" }
/// DELETE /_bridge/wifi/connection
/// GET    /_bridge/wifi/known
/// DELETE /_bridge/wifi/known?id=…          forgets a saved network
/// GET    /_bridge/wifi/radio               { "enabled": true }
/// PUT    /_bridge/wifi/radio               { "enabled": false }
/// GET    /_bridge/wifi/hotspot
/// POST   /_bridge/wifi/hotspot             { "ssid": "…", "passphrase": "…" }   every field optional
/// DELETE /_bridge/wifi/hotspot
/// GET    /_bridge/wifi/hotspot/clients
///
/// events: wifi.changed, wifi.hotspot
/// </code>
/// Each event hooks its Shiny <c>Changed</c> event only while a page listens to it — hooking <c>wifi.changed</c>
/// starts a platform watcher. Where the service is not registered, the event ends as soon as it is listened to.
/// </summary>
public sealed class WifiBridge : IWebAppBridge
{
    readonly IWifiManager? wifi;
    readonly IWifiHotspot? hotspot;
    readonly Lock gate = new();
    IHotspotSession? session;

    public WifiBridge(IServiceProvider services)
    {
        this.wifi = services.GetOptionalService<IWifiManager>();
        this.hotspot = services.GetOptionalService<IWifiHotspot>();
    }

    public string Name => "wifi";

    public bool IsSupported => this.wifi is { Capabilities: not WifiCapabilities.None };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", ctx => this.WithWifi(ctx, w => this.StatusAsync(ctx, w)))
        .MapPost("/access", ctx => this.WithWifi(ctx, async w =>
        {
            var access = await OnMainThread(() => w.RequestAccess(ctx.RequestAborted));
            await Json(ctx, new Contracts.WifiAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)), Contracts.WifiJsonContext.Default.WifiAccessResult);
        }))
        .MapGet("/networks", ctx => this.WithWifi(ctx, async w =>
        {
            var networks = await w.Scan(ctx.RequestAborted);
            IReadOnlyList<Contracts.WifiScanResult> results = [.. networks.Select(ToContract)];
            await Json(ctx, results, Contracts.WifiJsonContext.Default.IReadOnlyListWifiScanResult);
        }))
        .MapGet("/current", ctx => this.WithWifi(ctx, async w =>
        {
            var current = await w.GetCurrentNetwork(ctx.RequestAborted);
            await (current is null
                ? WebAppBridgeResults.NoContent(ctx)
                : Json(ctx, ToContract(current), Contracts.WifiJsonContext.Default.WifiNetworkInfo));
        }))
        .MapPost("/connection", ctx => this.WithWifi(ctx, w => ConnectAsync(ctx, w)))
        .MapDelete("/connection", ctx => this.WithWifi(ctx, async w =>
        {
            await w.Disconnect(ctx.RequestAborted);
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/known", ctx => this.WithWifi(ctx, async w =>
        {
            IReadOnlyList<Contracts.KnownWifiNetwork> known = [.. (await w.GetKnownNetworks(ctx.RequestAborted)).Select(ToContract)];
            await Json(ctx, known, Contracts.WifiJsonContext.Default.IReadOnlyListKnownWifiNetwork);
        }))
        .MapDelete("/known", ctx => this.WithWifi(ctx, async w =>
        {
            // In the query rather than the path: ids are an int on Android, a UUID on Linux and the SSID itself
            // on Apple platforms, spaces and all.
            var id = ctx.Request.Query["id"].ToString();
            if (id.Length == 0)
            {
                await WebAppBridgeResults.BadRequest(ctx, "Pass the known network's id as ?id=.");
                return;
            }

            await w.Forget(id, ctx.RequestAborted);
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/radio", ctx => this.WithWifi(ctx, async w =>
            await Json(ctx, new Contracts.WifiRadioState(await w.GetRadioEnabled(ctx.RequestAborted)), Contracts.WifiJsonContext.Default.WifiRadioState)))
        .MapPut("/radio", ctx => this.WithWifi(ctx, async w =>
        {
            if (await WebAppBridgeResults.ReadBodyAsync(ctx, Contracts.WifiJsonContext.Default.WifiRadioState) is not { } body)
            {
                await WebAppBridgeResults.BadRequest(ctx, "Expected { \"enabled\": true | false }.");
                return;
            }

            await w.SetRadioEnabled(body.Enabled, ctx.RequestAborted);
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/hotspot", ctx => this.HotspotStatusAsync(ctx))
        .MapPost("/hotspot", ctx => this.WithHotspot(ctx, h => this.StartHotspotAsync(ctx, h)))
        .MapDelete("/hotspot", ctx => this.WithHotspot(ctx, h => this.StopHotspotAsync(ctx, h)))
        .MapGet("/hotspot/clients", ctx => this.WithHotspot(ctx, h => this.HotspotClientsAsync(ctx)))
        .MapEvent("wifi.changed", this.WifiChanges, Contracts.WifiJsonContext.Default.WifiChangedEvent)
        .MapEvent("wifi.hotspot", this.HotspotChanges, Contracts.WifiJsonContext.Default.HotspotChangedEvent);

    async ValueTask StatusAsync(HttpContext context, IWifiManager wifi)
    {
        var capabilities = wifi.Capabilities;
        var current = capabilities.HasFlag(WifiCapabilities.CurrentNetwork)
            ? await wifi.GetCurrentNetwork(context.RequestAborted)
            : null;

        await Json(
            context,
            new Contracts.WifiStatus(
                [
                    .. Enum.GetValues<WifiCapabilities>()
                        .Where(x => x != WifiCapabilities.None && capabilities.HasFlag(x))
                        .Select(x => BridgeEnum.Convert<WifiCapabilities, Contracts.WifiCapability>(x))
                ],
                current is null ? null : ToContract(current),
                this.hotspot?.IsSupported == true
            ),
            Contracts.WifiJsonContext.Default.WifiStatus
        );
    }

    static async ValueTask ConnectAsync(HttpContext context, IWifiManager wifi)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.WifiJsonContext.Default.WifiConnectRequest);

        Func<Task<WifiNetworkInfo>>? connect = body switch
        {
            { KnownNetworkId: { Length: > 0 } id } => () => wifi.Connect(id, context.RequestAborted),
            { Ssid: { Length: > 0 } ssid } => () => wifi.Connect(
                new WifiConnectionRequest(ssid)
                {
                    Passphrase = body.Passphrase,
                    Security = BridgeEnum.Convert<Contracts.WifiSecurity, WifiSecurity>(body.Security),
                    Bssid = body.Bssid,
                    IsHidden = body.IsHidden,
                    Remember = body.Remember,
                    Timeout = body.TimeoutMs is > 0 and var ms ? TimeSpan.FromMilliseconds(ms) : null
                },
                context.RequestAborted
            ),
            _ => null
        };

        if (connect is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"ssid\": \"…\", \"passphrase\": \"…\" } or { \"knownNetworkId\": \"…\" }.");
            return;
        }

        // iOS asks the user before joining a network.
        var joined = await OnMainThread(connect);
        await Json(context, ToContract(joined), Contracts.WifiJsonContext.Default.WifiNetworkInfo);
    }

    ValueTask HotspotStatusAsync(HttpContext context)
    {
        IHotspotSession? active;
        lock (this.gate)
            active = this.session;

        var current = this.hotspot?.Current;

        return Json(
            context,
            new Contracts.HotspotStatus(
                this.hotspot?.IsSupported == true,
                active?.IsRunning == true,
                current is null ? null : ToContract(current)
            ),
            Contracts.WifiJsonContext.Default.HotspotStatus
        );
    }

    async ValueTask StartHotspotAsync(HttpContext context, IWifiHotspot hotspot)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.WifiJsonContext.Default.HotspotStartRequest) ?? new Contracts.HotspotStartRequest();

        var started = await OnMainThread(() => hotspot.Start(
            new HotspotConfiguration
            {
                Ssid = body.Ssid,
                Passphrase = body.Passphrase,
                Band = BridgeEnum.Convert<Contracts.WifiBand, WifiBand>(body.Band),
                IsHidden = body.IsHidden
            },
            context.RequestAborted
        ));

        IHotspotSession? previous;
        lock (this.gate)
        {
            previous = this.session;
            this.session = started;
        }

        if (previous is not null && !ReferenceEquals(previous, started))
            await previous.DisposeAsync();

        await Json(context, ToContract(started.Info), Contracts.WifiJsonContext.Default.HotspotInfo);
    }

    async ValueTask StopHotspotAsync(HttpContext context, IWifiHotspot hotspot)
    {
        IHotspotSession? active;
        lock (this.gate)
        {
            active = this.session;
            this.session = null;
        }

        if (active is not null)
            await active.Stop(context.RequestAborted);
        else
            await hotspot.Stop(context.RequestAborted);

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask HotspotClientsAsync(HttpContext context)
    {
        IHotspotSession? active;
        lock (this.gate)
            active = this.session;

        if (active is not { IsRunning: true })
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "hotspot_not_running", "Start the hotspot from this app first.");
            return;
        }

        IReadOnlyList<Contracts.HotspotClient> clients = [.. (await active.GetClients(context.RequestAborted)).Select(ToContract)];
        await Json(context, clients, Contracts.WifiJsonContext.Default.IReadOnlyListHotspotClient);
    }

    async ValueTask WithWifi(HttpContext context, Func<IWifiManager, ValueTask> action)
    {
        if (this.wifi is not { } w)
        {
            await WebAppBridgeResults.NotSupported(context, "Wi-Fi");
            return;
        }

        await Mapped(context, () => action(w));
    }

    async ValueTask WithHotspot(HttpContext context, Func<IWifiHotspot, ValueTask> action)
    {
        if (this.hotspot is not { IsSupported: true } h)
        {
            await WebAppBridgeResults.NotSupported(context, "A Wi-Fi hotspot");
            return;
        }

        await Mapped(context, () => action(h));
    }

    /// <summary>Shiny.Net.Wifi says why a call failed through its exception type; the page gets the same distinction as a status.</summary>
    static async ValueTask Mapped(HttpContext context, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (WifiException ex) when (!context.Response.HasStarted)
        {
            await (ex switch
            {
                WifiNotSupportedException => WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message),
                WifiPermissionException => WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", ex.Message),
                WifiConnectionException => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "connection_failed", ex.Message),
                _ => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "wifi_failed", ex.Message)
            });
        }
    }

    IAsyncEnumerable<Contracts.WifiChangedEvent> WifiChanges(CancellationToken cancellationToken)
    {
        if (this.wifi is not { } w)
            return AsyncEnumerable.Empty<Contracts.WifiChangedEvent>();

        return WebAppEventStream.FromEvent<Contracts.WifiChangedEvent>(emit =>
        {
            EventHandler<WifiNetworkInfo?> handler = (_, network) => emit(new(network is null ? null : ToContract(network)));
            w.Changed += handler;
            return () => w.Changed -= handler;
        }, cancellationToken);
    }

    IAsyncEnumerable<Contracts.HotspotChangedEvent> HotspotChanges(CancellationToken cancellationToken)
    {
        if (this.hotspot is not { } h)
            return AsyncEnumerable.Empty<Contracts.HotspotChangedEvent>();

        return WebAppEventStream.FromEvent<Contracts.HotspotChangedEvent>(emit =>
        {
            EventHandler<HotspotInfo?> handler = (_, info) => emit(new(info is null ? null : ToContract(info)));
            h.Changed += handler;
            return () => h.Changed -= handler;
        }, cancellationToken);
    }

    static ValueTask Json<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => WebAppBridgeResults.Json(context, value, typeInfo);

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();
}

static class WifiContractMapping
{
    public static Contracts.WifiNetworkInfo ToContract(WifiNetworkInfo info) => new(
        info.InterfaceName,
        info.Ssid,
        info.Bssid,
        Convert(info.Security),
        [.. info.IpAddresses.Select(Text)],
        [.. info.DnsAddresses.Select(Text)],
        info.Gateway?.ToString(),
        info.SubnetMask?.ToString(),
        info.SignalStrengthDbm,
        info.SignalStrengthPercent,
        info.FrequencyMhz,
        Convert(info.Band),
        info.Channel
    );

    public static Contracts.WifiScanResult ToContract(WifiNetwork network) => new(
        network.Ssid,
        network.Bssid,
        Convert(network.Security),
        network.SignalStrengthDbm,
        network.SignalStrengthPercent,
        network.FrequencyMhz,
        Convert(network.Band),
        network.Channel,
        network.IsHidden,
        network.IsOpen
    );

    public static Contracts.KnownWifiNetwork ToContract(KnownWifiNetwork known)
        => new(known.Id, known.Ssid, Convert(known.Security), known.IsHidden, known.AddedByThisApp);

    public static Contracts.HotspotInfo ToContract(HotspotInfo info)
        => new(info.Ssid, info.Passphrase, Convert(info.Security), Convert(info.Band), info.Address?.ToString());

    public static Contracts.HotspotClient ToContract(HotspotClient client)
        => new(client.MacAddress, client.IpAddress?.ToString(), client.HostName);

    static Contracts.WifiSecurity Convert(WifiSecurity security) => BridgeEnum.Convert<WifiSecurity, Contracts.WifiSecurity>(security);

    static Contracts.WifiBand Convert(WifiBand band) => BridgeEnum.Convert<WifiBand, Contracts.WifiBand>(band);

    static string Text(IPAddress address) => address.ToString();
}
