using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost.Bridge.BluetoothLE;

/// <summary>
/// <c>/_bridge/ble</c> over <see cref="IBleManager"/>. Peripheral UUIDs go in the path URL-encoded —
/// on Android they are MAC addresses, colons and all. Byte arrays travel as base64.
/// <code>
/// GET    /_bridge/ble/status
/// POST   /_bridge/ble/access
/// POST   /_bridge/ble/scan                         { "serviceUuids": ["180d"] }   results arrive as ble.scan
/// DELETE /_bridge/ble/scan
/// GET    /_bridge/ble/peripherals                  connected peripherals
/// GET    /_bridge/ble/peripherals/{uuid}
/// POST   /_bridge/ble/peripherals/{uuid}/connection     { "autoConnect": true, "timeoutMs": 30000 }
/// DELETE /_bridge/ble/peripherals/{uuid}/connection
/// GET    /_bridge/ble/peripherals/{uuid}/rssi
/// GET    /_bridge/ble/peripherals/{uuid}/services
/// GET    /_bridge/ble/peripherals/{uuid}/services/{service}/characteristics
/// GET    /_bridge/ble/peripherals/{uuid}/services/{service}/characteristics/{characteristic}          read
/// PUT    /_bridge/ble/peripherals/{uuid}/services/{service}/characteristics/{characteristic}          { "data": "base64", "withResponse": true }
/// POST   /_bridge/ble/peripherals/{uuid}/services/{service}/characteristics/{characteristic}/notifications
/// DELETE /_bridge/ble/peripherals/{uuid}/services/{service}/characteristics/{characteristic}/notifications
///
/// events: ble.scan, ble.status, ble.notification, ble.error
/// </code>
/// </summary>
public sealed class BleBridge : IWebAppBridge, IDisposable
{
    const string CharacteristicRoute = "/peripherals/{uuid}/services/{service}/characteristics/{characteristic}";

    readonly IBleManager? ble;
    readonly WebAppEventHub events;
    readonly Lock gate = new();
    readonly Dictionary<string, IDisposable> statusWatches = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IDisposable> notifications = new(StringComparer.OrdinalIgnoreCase);
    IDisposable? scan;

    public BleBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.ble = services.GetOptionalService<IBleManager>();
        this.events = events;
    }

    public string Name => "ble";

    public bool IsSupported => this.ble is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapPost("/scan", this.StartScanAsync)
        .MapDelete("/scan", this.StopScanAsync)
        .MapGet("/peripherals", this.ConnectedAsync)
        .MapGet("/peripherals/{uuid}", ctx => this.WithPeripheral(ctx, p => this.Json(ctx, PeripheralResponse.From(p), BleBridgeJsonContext.Default.PeripheralResponse)))
        .MapPost("/peripherals/{uuid}/connection", ctx => this.WithPeripheral(ctx, p => this.ConnectAsync(ctx, p)))
        .MapDelete("/peripherals/{uuid}/connection", ctx => this.WithPeripheral(ctx, p => this.DisconnectAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/rssi", ctx => this.WithPeripheral(ctx, p => this.RssiAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/services", ctx => this.WithPeripheral(ctx, p => this.ServicesAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/services/{service}/characteristics", ctx => this.WithPeripheral(ctx, p => this.CharacteristicsAsync(ctx, p)))
        .MapGet(CharacteristicRoute, ctx => this.WithPeripheral(ctx, p => this.ReadAsync(ctx, p)))
        .MapPut(CharacteristicRoute, ctx => this.WithPeripheral(ctx, p => this.WriteAsync(ctx, p)))
        .MapPost(CharacteristicRoute + "/notifications", ctx => this.WithPeripheral(ctx, p => this.StartNotificationsAsync(ctx, p)))
        .MapDelete(CharacteristicRoute + "/notifications", ctx => this.WithPeripheral(ctx, p => this.StopNotificationsAsync(ctx, p)));

    ValueTask StatusAsync(HttpContext context)
        => this.ble is { } b
            ? this.Json(context, new BleStatusResponse(b.CurrentAccess, b.IsScanning), BleBridgeJsonContext.Default.BleStatusResponse)
            : WebAppBridgeResults.NotSupported(context, "Bluetooth LE");

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE");
            return;
        }

        var access = await b.RequestAccessAsync();
        await this.Json(context, new BleStatusResponse(access, b.IsScanning), BleBridgeJsonContext.Default.BleStatusResponse);
    }

    async ValueTask StartScanAsync(HttpContext context)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleBridgeJsonContext.Default.ScanRequest) ?? new ScanRequest();
        var config = body.ServiceUuids is { Length: > 0 } uuids ? new ScanConfig(uuids) : null;

        lock (this.gate)
        {
            // One scan at a time; starting again replaces the filter rather than stacking scans.
            this.scan?.Dispose();
            this.scan = b.Scan(config).Subscribe(
                result =>
                {
                    if (this.events.HasSubscribers)
                        this.events.Publish("ble.scan", ScanResultEvent.From(result), BleBridgeJsonContext.Default.ScanResultEvent);
                },
                ex => this.PublishError("scan", null, ex)
            );
        }

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask StopScanAsync(HttpContext context)
    {
        if (this.ble is null)
            return WebAppBridgeResults.NotSupported(context, "Bluetooth LE");

        lock (this.gate)
        {
            this.scan?.Dispose();
            this.scan = null;
        }

        return WebAppBridgeResults.NoContent(context);
    }

    ValueTask ConnectedAsync(HttpContext context)
    {
        if (this.ble is not { } b)
            return WebAppBridgeResults.NotSupported(context, "Bluetooth LE");

        List<PeripheralResponse> connected = [.. b.GetConnectedPeripherals().Select(PeripheralResponse.From)];
        return this.Json(context, connected, BleBridgeJsonContext.Default.ListPeripheralResponse);
    }

    async ValueTask ConnectAsync(HttpContext context, IPeripheral peripheral)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleBridgeJsonContext.Default.ConnectRequest) ?? new ConnectRequest();

        this.WatchStatus(peripheral);

        await peripheral.ConnectAsync(
            new ConnectionConfig(body.AutoConnect),
            context.RequestAborted,
            TimeSpan.FromMilliseconds(body.TimeoutMs is > 0 and var ms ? ms : 30_000)
        );

        await this.Json(context, PeripheralResponse.From(peripheral), BleBridgeJsonContext.Default.PeripheralResponse);
    }

    async ValueTask DisconnectAsync(HttpContext context, IPeripheral peripheral)
    {
        lock (this.gate)
        {
            foreach (var key in this.notifications.Keys.Where(x => x.StartsWith(peripheral.Uuid + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                this.notifications[key].Dispose();
                this.notifications.Remove(key);
            }
        }

        await peripheral.DisconnectAsync(context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask RssiAsync(HttpContext context, IPeripheral peripheral)
        => await this.Json(context, new RssiResponse(await peripheral.ReadRssiAsync(context.RequestAborted)), BleBridgeJsonContext.Default.RssiResponse);

    async ValueTask ServicesAsync(HttpContext context, IPeripheral peripheral)
    {
        var services = await peripheral.GetServicesAsync(context.RequestAborted);
        List<ServiceResponse> response = [.. services.Select(x => new ServiceResponse(x.Uuid))];

        await this.Json(context, response, BleBridgeJsonContext.Default.ListServiceResponse);
    }

    async ValueTask CharacteristicsAsync(HttpContext context, IPeripheral peripheral)
    {
        var characteristics = await peripheral.GetCharacteristicsAsync(Route(context, "service"), context.RequestAborted);
        List<CharacteristicResponse> response = [.. characteristics.Select(CharacteristicResponse.From)];

        await this.Json(context, response, BleBridgeJsonContext.Default.ListCharacteristicResponse);
    }

    async ValueTask ReadAsync(HttpContext context, IPeripheral peripheral)
    {
        var service = Route(context, "service");
        var characteristic = Route(context, "characteristic");
        var result = await peripheral.ReadCharacteristicAsync(service, characteristic, context.RequestAborted);

        await this.Json(
            context,
            new CharacteristicValueResponse(service, characteristic, result.Data),
            BleBridgeJsonContext.Default.CharacteristicValueResponse
        );
    }

    async ValueTask WriteAsync(HttpContext context, IPeripheral peripheral)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleBridgeJsonContext.Default.WriteRequest);
        if (body?.Data is not { } data)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"data\": \"<base64>\" }.");
            return;
        }

        await peripheral.WriteCharacteristicAsync(Route(context, "service"), Route(context, "characteristic"), data, body.WithResponse, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask StartNotificationsAsync(HttpContext context, IPeripheral peripheral)
    {
        var service = Route(context, "service");
        var characteristic = Route(context, "characteristic");
        var key = NotificationKey(peripheral, service, characteristic);

        lock (this.gate)
        {
            if (!this.notifications.ContainsKey(key))
            {
                this.notifications[key] = peripheral.NotifyCharacteristic(service, characteristic).Subscribe(
                    result => this.events.Publish(
                        "ble.notification",
                        new NotificationEvent(peripheral.Uuid, service, characteristic, result.Data),
                        BleBridgeJsonContext.Default.NotificationEvent
                    ),
                    ex =>
                    {
                        lock (this.gate)
                            this.notifications.Remove(key);

                        this.PublishError("notify", peripheral.Uuid, ex);
                    }
                );
            }
        }

        return WebAppBridgeResults.NoContent(context);
    }

    ValueTask StopNotificationsAsync(HttpContext context, IPeripheral peripheral)
    {
        var key = NotificationKey(peripheral, Route(context, "service"), Route(context, "characteristic"));

        lock (this.gate)
        {
            if (this.notifications.Remove(key, out var subscription))
                subscription.Dispose();
        }

        return WebAppBridgeResults.NoContent(context);
    }

    /// <summary>Resolves the peripheral in the route and maps Shiny's failures to responses the page can act on.</summary>
    async ValueTask WithPeripheral(HttpContext context, Func<IPeripheral, ValueTask> action)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE");
            return;
        }

        var uuid = Route(context, "uuid");
        IPeripheral? peripheral;

        try
        {
            peripheral = b.GetKnownPeripheral(uuid)
                ?? b.GetConnectedPeripherals().FirstOrDefault(x => String.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // iOS identifiers are GUIDs; anything else is simply not a peripheral.
            peripheral = null;
        }

        if (peripheral is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No known peripheral '{uuid}'. Scan for it first.");
            return;
        }

        try
        {
            await action(peripheral);
        }
        catch (TimeoutException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status504GatewayTimeout, "ble_timeout", ex.Message);
        }
        catch (BleException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "ble_failed", ex.Message);
        }
    }

    void WatchStatus(IPeripheral peripheral)
    {
        lock (this.gate)
        {
            if (this.statusWatches.ContainsKey(peripheral.Uuid))
                return;

            this.statusWatches[peripheral.Uuid] = peripheral.WhenStatusChanged().Subscribe(status =>
                this.events.Publish(
                    "ble.status",
                    new PeripheralStatusEvent(peripheral.Uuid, status),
                    BleBridgeJsonContext.Default.PeripheralStatusEvent
                )
            );
        }
    }

    void PublishError(string operation, string? peripheralUuid, Exception ex)
        => this.events.Publish("ble.error", new BleErrorEvent(operation, peripheralUuid, ex.Message), BleBridgeJsonContext.Default.BleErrorEvent);

    ValueTask Json<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => WebAppBridgeResults.Json(context, value, typeInfo);

    static string Route(HttpContext context, string name) => context.Request.RouteValues[name] ?? String.Empty;

    static string NotificationKey(IPeripheral peripheral, string service, string characteristic)
        => $"{peripheral.Uuid}|{service}|{characteristic}";

    public void Dispose()
    {
        lock (this.gate)
        {
            this.scan?.Dispose();
            this.scan = null;

            foreach (var subscription in this.notifications.Values.Concat(this.statusWatches.Values))
                subscription.Dispose();

            this.notifications.Clear();
            this.statusWatches.Clear();
        }
    }
}

public static class BleBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/ble</c> and registers Shiny's Bluetooth LE service for the platform — there is
    /// nothing else to call. On Linux the endpoints answer 501.
    /// </summary>
    public static MauiAppBuilder AddBluetoothLEBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#elif MACOS
        builder.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        builder.Services.AddBluetoothLE();
#endif

        builder.Services.AddWebAppBridge<BleBridge>();
        return builder;
    }
}

public sealed record BleStatusResponse(AccessState Access, bool IsScanning);
public sealed record ScanRequest(string[]? ServiceUuids = null);

public sealed record ScanResultEvent(
    string Uuid,
    string? Name,
    int Rssi,
    string? LocalName,
    bool? IsConnectable,
    string[]? ServiceUuids,
    int? TxPower,
    ushort? ManufacturerId,
    byte[]? ManufacturerData
)
{
    internal static ScanResultEvent From(ScanResult result) => new(
        result.Peripheral.Uuid,
        result.Peripheral.Name,
        result.Rssi,
        result.AdvertisementData?.LocalName,
        result.AdvertisementData?.IsConnectable,
        result.AdvertisementData?.ServiceUuids,
        result.AdvertisementData?.TxPower,
        result.AdvertisementData?.ManufacturerData?.CompanyId,
        result.AdvertisementData?.ManufacturerData?.Data
    );
}

public sealed record PeripheralResponse(string Uuid, string? Name, ConnectionState Status, int Mtu)
{
    internal static PeripheralResponse From(IPeripheral peripheral) => new(peripheral.Uuid, peripheral.Name, peripheral.Status, peripheral.Mtu);
}

public sealed record ConnectRequest(bool AutoConnect = true, int? TimeoutMs = null);
public sealed record ServiceResponse(string Uuid);

public sealed record CharacteristicResponse(string ServiceUuid, string Uuid, bool IsNotifying, CharacteristicProperties Properties)
{
    internal static CharacteristicResponse From(BleCharacteristicInfo info) => new(info.Service.Uuid, info.Uuid, info.IsNotifying, info.Properties);
}

public sealed record CharacteristicValueResponse(string ServiceUuid, string CharacteristicUuid, byte[]? Data);
public sealed record WriteRequest(byte[]? Data, bool WithResponse = true);
public sealed record RssiResponse(int Rssi);
public sealed record PeripheralStatusEvent(string Uuid, ConnectionState Status);
public sealed record NotificationEvent(string PeripheralUuid, string ServiceUuid, string CharacteristicUuid, byte[]? Data);
public sealed record BleErrorEvent(string Operation, string? PeripheralUuid, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BleStatusResponse))]
[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(ScanResultEvent))]
[JsonSerializable(typeof(PeripheralResponse))]
[JsonSerializable(typeof(List<PeripheralResponse>))]
[JsonSerializable(typeof(ConnectRequest))]
[JsonSerializable(typeof(List<ServiceResponse>))]
[JsonSerializable(typeof(List<CharacteristicResponse>))]
[JsonSerializable(typeof(CharacteristicValueResponse))]
[JsonSerializable(typeof(WriteRequest))]
[JsonSerializable(typeof(RssiResponse))]
[JsonSerializable(typeof(PeripheralStatusEvent))]
[JsonSerializable(typeof(NotificationEvent))]
[JsonSerializable(typeof(BleErrorEvent))]
partial class BleBridgeJsonContext : JsonSerializerContext;
