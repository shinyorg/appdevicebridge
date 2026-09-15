using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.Wifi;

namespace Shiny.WebAppHost.Bridge.Wifi;

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
/// </summary>
public sealed class WifiBridge : IWebAppBridge, IDisposable
{
    readonly IWifiManager? wifi;
    readonly IWifiHotspot? hotspot;
    readonly WebAppEventHub events;
    readonly Lock gate = new();
    IHotspotSession? session;
    bool watching;

    public WifiBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.wifi = services.GetOptionalService<IWifiManager>();
        this.hotspot = services.GetOptionalService<IWifiHotspot>();
        this.events = events;

        // Subscribing to Changed starts a platform watcher, so it runs only while a page is listening.
        events.SubscribersChanged += this.UpdateWatchers;
    }

    public string Name => "wifi";

    public bool IsSupported => this.wifi is { Capabilities: not WifiCapabilities.None };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", ctx => this.WithWifi(ctx, w => this.StatusAsync(ctx, w)))
        .MapPost("/access", ctx => this.WithWifi(ctx, async w =>
        {
            var access = await OnMainThread(() => w.RequestAccess(ctx.RequestAborted));
            await Json(ctx, new WifiAccessResponse(access), WifiBridgeJsonContext.Default.WifiAccessResponse);
        }))
        .MapGet("/networks", ctx => this.WithWifi(ctx, async w =>
        {
            var networks = await w.Scan(ctx.RequestAborted);
            List<WifiScanResult> results = [.. networks.Select(WifiScanResult.From)];
            await Json(ctx, results, WifiBridgeJsonContext.Default.ListWifiScanResult);
        }))
        .MapGet("/current", ctx => this.WithWifi(ctx, async w =>
        {
            var current = await w.GetCurrentNetwork(ctx.RequestAborted);
            await (current is null
                ? WebAppBridgeResults.NoContent(ctx)
                : Json(ctx, WifiNetworkInfoResponse.From(current), WifiBridgeJsonContext.Default.WifiNetworkInfoResponse));
        }))
        .MapPost("/connection", ctx => this.WithWifi(ctx, w => ConnectAsync(ctx, w)))
        .MapDelete("/connection", ctx => this.WithWifi(ctx, async w =>
        {
            await w.Disconnect(ctx.RequestAborted);
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/known", ctx => this.WithWifi(ctx, async w =>
        {
            List<KnownWifiNetwork> known = [.. await w.GetKnownNetworks(ctx.RequestAborted)];
            await Json(ctx, known, WifiBridgeJsonContext.Default.ListKnownWifiNetwork);
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
            await Json(ctx, new WifiRadioState(await w.GetRadioEnabled(ctx.RequestAborted)), WifiBridgeJsonContext.Default.WifiRadioState)))
        .MapPut("/radio", ctx => this.WithWifi(ctx, async w =>
        {
            if (await WebAppBridgeResults.ReadBodyAsync(ctx, WifiBridgeJsonContext.Default.WifiRadioState) is not { } body)
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
        .MapGet("/hotspot/clients", ctx => this.WithHotspot(ctx, h => this.HotspotClientsAsync(ctx)));

    async ValueTask StatusAsync(HttpContext context, IWifiManager wifi)
    {
        var capabilities = wifi.Capabilities;
        var current = capabilities.HasFlag(WifiCapabilities.CurrentNetwork)
            ? await wifi.GetCurrentNetwork(context.RequestAborted)
            : null;

        await Json(
            context,
            new WifiStatusResponse(
                [.. Enum.GetValues<WifiCapabilities>().Where(x => x != WifiCapabilities.None && capabilities.HasFlag(x)).Select(x => x.ToString())],
                current is null ? null : WifiNetworkInfoResponse.From(current),
                this.hotspot?.IsSupported == true
            ),
            WifiBridgeJsonContext.Default.WifiStatusResponse
        );
    }

    static async ValueTask ConnectAsync(HttpContext context, IWifiManager wifi)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, WifiBridgeJsonContext.Default.WifiConnectRequest);

        Func<Task<WifiNetworkInfo>>? connect = body switch
        {
            { KnownNetworkId: { Length: > 0 } id } => () => wifi.Connect(id, context.RequestAborted),
            { Ssid: { Length: > 0 } ssid } => () => wifi.Connect(
                new WifiConnectionRequest(ssid)
                {
                    Passphrase = body.Passphrase,
                    Security = body.Security,
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
        await Json(context, WifiNetworkInfoResponse.From(joined), WifiBridgeJsonContext.Default.WifiNetworkInfoResponse);
    }

    ValueTask HotspotStatusAsync(HttpContext context)
    {
        IHotspotSession? active;
        lock (this.gate)
            active = this.session;

        var current = this.hotspot?.Current;

        return Json(
            context,
            new HotspotStatusResponse(
                this.hotspot?.IsSupported == true,
                active?.IsRunning == true,
                current is null ? null : HotspotResponse.From(current)
            ),
            WifiBridgeJsonContext.Default.HotspotStatusResponse
        );
    }

    async ValueTask StartHotspotAsync(HttpContext context, IWifiHotspot hotspot)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, WifiBridgeJsonContext.Default.HotspotStartRequest) ?? new HotspotStartRequest();

        var started = await OnMainThread(() => hotspot.Start(
            new HotspotConfiguration
            {
                Ssid = body.Ssid,
                Passphrase = body.Passphrase,
                Band = body.Band,
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

        await Json(context, HotspotResponse.From(started.Info), WifiBridgeJsonContext.Default.HotspotResponse);
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

        List<HotspotClientResponse> clients = [.. (await active.GetClients(context.RequestAborted)).Select(HotspotClientResponse.From)];
        await Json(context, clients, WifiBridgeJsonContext.Default.ListHotspotClientResponse);
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

    void UpdateWatchers()
    {
        lock (this.gate)
        {
            var listening = this.events.HasSubscribers;
            if (listening == this.watching)
                return;

            this.watching = listening;

            if (listening)
            {
                if (this.wifi is not null)
                    this.wifi.Changed += this.OnWifiChanged;

                if (this.hotspot is not null)
                    this.hotspot.Changed += this.OnHotspotChanged;
            }
            else
            {
                if (this.wifi is not null)
                    this.wifi.Changed -= this.OnWifiChanged;

                if (this.hotspot is not null)
                    this.hotspot.Changed -= this.OnHotspotChanged;
            }
        }
    }

    void OnWifiChanged(object? sender, WifiNetworkInfo? network)
        => this.events.Publish(
            "wifi.changed",
            new WifiChangedEvent(network is null ? null : WifiNetworkInfoResponse.From(network)),
            WifiBridgeJsonContext.Default.WifiChangedEvent
        );

    void OnHotspotChanged(object? sender, HotspotInfo? info)
        => this.events.Publish(
            "wifi.hotspot",
            new HotspotChangedEvent(info is null ? null : HotspotResponse.From(info)),
            WifiBridgeJsonContext.Default.HotspotChangedEvent
        );

    static ValueTask Json<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => WebAppBridgeResults.Json(context, value, typeInfo);

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    public void Dispose()
    {
        this.events.SubscribersChanged -= this.UpdateWatchers;

        lock (this.gate)
        {
            if (this.watching)
            {
                if (this.wifi is not null)
                    this.wifi.Changed -= this.OnWifiChanged;

                if (this.hotspot is not null)
                    this.hotspot.Changed -= this.OnHotspotChanged;

                this.watching = false;
            }
        }
    }
}

public sealed record WifiAccessResponse(AccessState Access);

public sealed record WifiStatusResponse(IReadOnlyList<string> Capabilities, WifiNetworkInfoResponse? Current, bool HotspotSupported);

public sealed record WifiNetworkInfoResponse(
    string InterfaceName,
    string? Ssid,
    string? Bssid,
    WifiSecurity Security,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsAddresses,
    string? Gateway,
    string? SubnetMask,
    int? SignalStrengthDbm,
    int? SignalStrengthPercent,
    int? FrequencyMhz,
    WifiBand Band,
    int? Channel
)
{
    internal static WifiNetworkInfoResponse From(WifiNetworkInfo info) => new(
        info.InterfaceName,
        info.Ssid,
        info.Bssid,
        info.Security,
        [.. info.IpAddresses.Select(Text)],
        [.. info.DnsAddresses.Select(Text)],
        info.Gateway?.ToString(),
        info.SubnetMask?.ToString(),
        info.SignalStrengthDbm,
        info.SignalStrengthPercent,
        info.FrequencyMhz,
        info.Band,
        info.Channel
    );

    static string Text(IPAddress address) => address.ToString();
}

public sealed record WifiScanResult(
    string Ssid,
    string? Bssid,
    WifiSecurity Security,
    int? SignalStrengthDbm,
    int SignalStrengthPercent,
    int? FrequencyMhz,
    WifiBand Band,
    int? Channel,
    bool IsHidden,
    bool IsOpen
)
{
    internal static WifiScanResult From(WifiNetwork network) => new(
        network.Ssid,
        network.Bssid,
        network.Security,
        network.SignalStrengthDbm,
        network.SignalStrengthPercent,
        network.FrequencyMhz,
        network.Band,
        network.Channel,
        network.IsHidden,
        network.IsOpen
    );
}

public sealed record WifiConnectRequest(
    string? Ssid = null,
    string? Passphrase = null,
    WifiSecurity Security = WifiSecurity.Unknown,
    string? Bssid = null,
    bool IsHidden = false,
    bool Remember = true,
    int? TimeoutMs = null,
    string? KnownNetworkId = null
);

public sealed record WifiRadioState(bool Enabled);

public sealed record HotspotStartRequest(string? Ssid = null, string? Passphrase = null, WifiBand Band = WifiBand.Unknown, bool IsHidden = false);

public sealed record HotspotResponse(string Ssid, string? Passphrase, WifiSecurity Security, WifiBand Band, string? Address)
{
    internal static HotspotResponse From(HotspotInfo info) => new(info.Ssid, info.Passphrase, info.Security, info.Band, info.Address?.ToString());
}

public sealed record HotspotStatusResponse(bool Supported, bool Running, HotspotResponse? Current);

public sealed record HotspotClientResponse(string MacAddress, string? IpAddress, string? HostName)
{
    internal static HotspotClientResponse From(HotspotClient client) => new(client.MacAddress, client.IpAddress?.ToString(), client.HostName);
}

public sealed record WifiChangedEvent(WifiNetworkInfoResponse? Current);
public sealed record HotspotChangedEvent(HotspotResponse? Current);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WifiAccessResponse))]
[JsonSerializable(typeof(WifiStatusResponse))]
[JsonSerializable(typeof(WifiNetworkInfoResponse))]
[JsonSerializable(typeof(List<WifiScanResult>))]
[JsonSerializable(typeof(WifiConnectRequest))]
[JsonSerializable(typeof(List<KnownWifiNetwork>))]
[JsonSerializable(typeof(WifiRadioState))]
[JsonSerializable(typeof(HotspotStartRequest))]
[JsonSerializable(typeof(HotspotResponse))]
[JsonSerializable(typeof(HotspotStatusResponse))]
[JsonSerializable(typeof(List<HotspotClientResponse>))]
[JsonSerializable(typeof(WifiChangedEvent))]
[JsonSerializable(typeof(HotspotChangedEvent))]
partial class WifiBridgeJsonContext : JsonSerializerContext;
