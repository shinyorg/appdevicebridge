using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.BluetoothLE.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.BluetoothLE;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using static Shiny.AppDeviceBridge.BluetoothLE.BleContractMapping;

namespace Shiny.AppDeviceBridge.BluetoothLE;

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
/// A scan needs a <c>ble.scan</c> listener and characteristic notifications a <c>ble.notification</c> one; when the
/// last listener of either leaves, the scan or every notification subscription stops. <c>ble.status</c> watches the
/// peripherals connected through the bridge only while a page listens to it. Peripherals stay connected either way.
/// </summary>
public sealed class BleBridge : IWebAppBridge, IDisposable
{
    const string CharacteristicRoute = "/peripherals/{uuid}/services/{service}/characteristics/{characteristic}";

    readonly IBleManager? ble;
    readonly Lock gate = new();
    readonly WebAppEventSource<BleScanResult> scanResults = new();
    readonly WebAppEventSource<BleNotification> notificationValues = new();
    readonly WebAppEventSource<BleError> errors = new();
    readonly Dictionary<string, IPeripheral> connected = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IDisposable> notifications = new(StringComparer.OrdinalIgnoreCase);
    Action<IPeripheral>? peripheralConnecting;
    IDisposable? scan;

    public BleBridge(IServiceProvider services)
        => this.ble = services.GetOptionalService<IBleManager>();

    public string Name => "ble";

    public bool IsSupported => this.ble is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapPost("/scan", this.StartScanAsync)
        .MapDelete("/scan", this.StopScanAsync)
        .MapGet("/peripherals", this.ConnectedAsync)
        .MapGet("/peripherals/{uuid}", ctx => this.WithPeripheral(ctx, p => this.Json(ctx, ToContract(p), BleJsonContext.Default.BlePeripheral)))
        .MapPost("/peripherals/{uuid}/connection", ctx => this.WithPeripheral(ctx, p => this.ConnectAsync(ctx, p)))
        .MapDelete("/peripherals/{uuid}/connection", ctx => this.WithPeripheral(ctx, p => this.DisconnectAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/rssi", ctx => this.WithPeripheral(ctx, p => this.RssiAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/services", ctx => this.WithPeripheral(ctx, p => this.ServicesAsync(ctx, p)))
        .MapGet("/peripherals/{uuid}/services/{service}/characteristics", ctx => this.WithPeripheral(ctx, p => this.CharacteristicsAsync(ctx, p)))
        .MapGet(CharacteristicRoute, ctx => this.WithPeripheral(ctx, p => this.ReadAsync(ctx, p)))
        .MapPut(CharacteristicRoute, ctx => this.WithPeripheral(ctx, p => this.WriteAsync(ctx, p)))
        .MapPost(CharacteristicRoute + "/notifications", ctx => this.WithPeripheral(ctx, p => this.StartNotificationsAsync(ctx, p)))
        .MapDelete(CharacteristicRoute + "/notifications", ctx => this.WithPeripheral(ctx, p => this.StopNotificationsAsync(ctx, p)))
        .MapEvent("ble.scan", ct => this.scanResults.ListenAsync(this.OnScanListenerStopped, ct), BleJsonContext.Default.BleScanResult)
        .MapEvent("ble.notification", ct => this.notificationValues.ListenAsync(this.OnNotificationListenerStopped, ct), BleJsonContext.Default.BleNotification)
        .MapEvent("ble.status", this.StatusChanges, BleJsonContext.Default.BlePeripheralStatus)
        .MapEvent("ble.error", ct => this.errors.ListenAsync(ct), BleJsonContext.Default.BleError);

    ValueTask StatusAsync(HttpContext context)
        => this.ble is { } b
            ? this.Json(context, new BleStatus(Convert(b.CurrentAccess), b.IsScanning), BleJsonContext.Default.BleStatus)
            : WebAppBridgeResults.NotSupported(context, "Bluetooth LE");

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE");
            return;
        }

        var access = await b.RequestAccessAsync();
        await this.Json(context, new BleStatus(Convert(access), b.IsScanning), BleJsonContext.Default.BleStatus);
    }

    async ValueTask StartScanAsync(HttpContext context)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleJsonContext.Default.BleScanRequest) ?? new BleScanRequest();
        var config = body.ServiceUuids is { Count: > 0 } uuids ? new ScanConfig([.. uuids]) : null;

        bool listening;
        lock (this.gate)
        {
            // Checked under the lock the stopped callback takes, so a scan never outlives its last listener.
            listening = this.scanResults.HasListeners;
            if (listening)
            {
                // One scan at a time; starting again replaces the filter rather than stacking scans.
                this.scan?.Dispose();
                this.scan = b.Scan(config).Subscribe(
                    result => this.scanResults.Publish(ToContract(result)),
                    ex => this.PublishError("scan", null, ex)
                );
            }
        }

        await (listening
            ? WebAppBridgeResults.NoContent(context)
            : NotListening(context, "ble.scan", "scan results"));
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

        IReadOnlyList<BlePeripheral> connected = [.. b.GetConnectedPeripherals().Select(ToContract)];
        return this.Json(context, connected, BleJsonContext.Default.IReadOnlyListBlePeripheral);
    }

    async ValueTask ConnectAsync(HttpContext context, IPeripheral peripheral)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleJsonContext.Default.BleConnectRequest) ?? new BleConnectRequest();

        this.Track(peripheral);

        await peripheral.ConnectAsync(
            new ConnectionConfig(body.AutoConnect),
            context.RequestAborted,
            TimeSpan.FromMilliseconds(body.TimeoutMs is > 0 and var ms ? ms : 30_000)
        );

        await this.Json(context, ToContract(peripheral), BleJsonContext.Default.BlePeripheral);
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
        => await this.Json(context, new BleRssi(await peripheral.ReadRssiAsync(context.RequestAborted)), BleJsonContext.Default.BleRssi);

    async ValueTask ServicesAsync(HttpContext context, IPeripheral peripheral)
    {
        var services = await peripheral.GetServicesAsync(context.RequestAborted);
        IReadOnlyList<BleService> response = [.. services.Select(x => new BleService(x.Uuid))];

        await this.Json(context, response, BleJsonContext.Default.IReadOnlyListBleService);
    }

    async ValueTask CharacteristicsAsync(HttpContext context, IPeripheral peripheral)
    {
        var characteristics = await peripheral.GetCharacteristicsAsync(Route(context, "service"), context.RequestAborted);
        IReadOnlyList<BleCharacteristic> response = [.. characteristics.Select(ToContract)];

        await this.Json(context, response, BleJsonContext.Default.IReadOnlyListBleCharacteristic);
    }

    async ValueTask ReadAsync(HttpContext context, IPeripheral peripheral)
    {
        var service = Route(context, "service");
        var characteristic = Route(context, "characteristic");
        var result = await peripheral.ReadCharacteristicAsync(service, characteristic, context.RequestAborted);

        await this.Json(
            context,
            new BleCharacteristicValue(service, characteristic, result.Data),
            BleJsonContext.Default.BleCharacteristicValue
        );
    }

    async ValueTask WriteAsync(HttpContext context, IPeripheral peripheral)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, BleJsonContext.Default.BleWriteRequest);
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
            if (!this.notificationValues.HasListeners)
                return NotListening(context, "ble.notification", "characteristic values");

            if (!this.notifications.ContainsKey(key))
            {
                this.notifications[key] = peripheral.NotifyCharacteristic(service, characteristic).Subscribe(
                    result => this.notificationValues.Publish(new BleNotification(peripheral.Uuid, service, characteristic, result.Data)),
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

    /// <summary>Remembers a peripheral the page connects to, and has every <c>ble.status</c> listener watch it.</summary>
    void Track(IPeripheral peripheral)
    {
        lock (this.gate)
        {
            if (this.connected.TryAdd(peripheral.Uuid, peripheral))
                this.peripheralConnecting?.Invoke(peripheral);
        }
    }

    /// <summary>One listener's watch on every tracked peripheral, and on each one connected while it listens.</summary>
    IAsyncEnumerable<BlePeripheralStatus> StatusChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<BlePeripheralStatus>(emit =>
        {
            var watches = new List<IDisposable>();

            void Watch(IPeripheral peripheral)
                => watches.Add(peripheral.WhenStatusChanged().Subscribe(status => emit(new BlePeripheralStatus(peripheral.Uuid, Convert(status)))));

            // Under the gate, so a peripheral tracked meanwhile is watched exactly once.
            lock (this.gate)
            {
                foreach (var peripheral in this.connected.Values)
                    Watch(peripheral);

                this.peripheralConnecting += Watch;
            }

            return () =>
            {
                lock (this.gate)
                {
                    this.peripheralConnecting -= Watch;

                    foreach (var watch in watches)
                        watch.Dispose();
                }
            };
        }, cancellationToken);

    /// <summary>Nobody sees the results any more, so the radio stops scanning.</summary>
    void OnScanListenerStopped(int remaining)
    {
        lock (this.gate)
        {
            if (this.scanResults.HasListeners)
                return;

            this.scan?.Dispose();
            this.scan = null;
        }
    }

    /// <summary>Nobody sees the values any more, so every characteristic subscription ends.</summary>
    void OnNotificationListenerStopped(int remaining)
    {
        lock (this.gate)
        {
            if (this.notificationValues.HasListeners)
                return;

            foreach (var subscription in this.notifications.Values)
                subscription.Dispose();

            this.notifications.Clear();
        }
    }

    void PublishError(string operation, string? peripheralUuid, Exception ex)
        => this.errors.Publish(new BleError(operation, peripheralUuid, ex.Message));

    static ValueTask NotListening(HttpContext context, string eventName, string what)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", $"Listen for {eventName} first: {what} arrive as events.");

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

            foreach (var subscription in this.notifications.Values)
                subscription.Dispose();

            this.notifications.Clear();
            this.connected.Clear();
        }
    }
}

public static class BleBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/ble</c> and registers Shiny's Bluetooth LE service for the platform — there is
    /// nothing else to call. On Linux the endpoints answer 501.
    /// </summary>
    public static TBuilder AddBluetoothLEBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if MACOS
        bridge.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        bridge.Services.AddBluetoothLE();
#endif

        bridge.AddBridge<BleBridge>();
        return bridge;
    }
}

static class BleContractMapping
{
    public static ContractAccess Convert(AccessState access) => BridgeEnum.Convert<AccessState, ContractAccess>(access);

    public static BleConnectionState Convert(ConnectionState state) => BridgeEnum.Convert<ConnectionState, BleConnectionState>(state);

    public static BleScanResult ToContract(ScanResult result) => new(
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

    public static BlePeripheral ToContract(IPeripheral peripheral) => new(peripheral.Uuid, peripheral.Name, Convert(peripheral.Status), peripheral.Mtu);

    public static BleCharacteristic ToContract(BleCharacteristicInfo info) => new(
        info.Service.Uuid,
        info.Uuid,
        info.IsNotifying,
        [
            .. Enum.GetValues<CharacteristicProperties>()
                .Where(x => info.Properties.HasFlag(x))
                .Select(x => BridgeEnum.Convert<CharacteristicProperties, BleCharacteristicProperty>(x))
        ]
    );
}
