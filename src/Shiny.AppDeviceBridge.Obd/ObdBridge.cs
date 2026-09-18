using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Obd.Client;
using Shiny.BluetoothLE;
using Shiny.Net.HttpServer;
using Shiny.Obd;
using Shiny.Obd.Ble;
using Shiny.Obd.Commands;
using Shiny.Obd.Wifi;

namespace Shiny.AppDeviceBridge.Obd;

/// <summary>
/// <c>/_bridge/obd</c> over Shiny.Obd — one ELM327 or OBDLink adapter at a time, over Bluetooth LE or Wi-Fi.
/// Every exchange with the adapter is serialized: ELM327 is half-duplex, and a second command sent before
/// the prompt returns corrupts both replies.
/// <code>
/// GET    /_bridge/obd/status
/// GET    /_bridge/obd/commands                    the names POST command and monitor accept
/// POST   /_bridge/obd/scan         { "transport": "ble", "scanMs": 5000, "nameFilter": "OBD" }   the adapters seen in the window
/// GET    /_bridge/obd/adapters                    the last scan's adapters
/// POST   /_bridge/obd/connection   { "transport": "ble", "peripheralUuid": "…" }
///                                  { "transport": "wifi", "host": "192.168.0.10", "port": 35000 }   omit host to probe
/// DELETE /_bridge/obd/connection
/// POST   /_bridge/obd/command      { "command": "engineRpm" }
/// POST   /_bridge/obd/raw          { "raw": "010C" }
/// GET    /_bridge/obd/vin
/// GET    /_bridge/obd/dtc                         stored, pending and permanent trouble codes
/// DELETE /_bridge/obd/dtc?confirm=true            clears codes and resets readiness monitors
/// POST   /_bridge/obd/monitor      { "commands": ["engineRpm", "vehicleSpeed"], "intervalMs": 1000 }
/// DELETE /_bridge/obd/monitor
///
/// events: obd.reading, obd.disconnected
/// </code>
/// <para>
/// The monitor is for whoever listens for <c>obd.reading</c>: starting it needs such a listener, and it stops once
/// the last of them is gone. The adapter stays connected.
/// </para>
/// </summary>
public sealed partial class ObdBridge : IWebAppBridge, IDisposable
{
    const int DefaultScanMs = 5_000;
    const int MaxScanMs = 30_000;
    const int DefaultConnectTimeoutMs = 30_000;
    const int MaxConnectTimeoutMs = 60_000;
    const int DefaultMonitorIntervalMs = 1_000;
    const int MinMonitorIntervalMs = 250;
    const int MaxMonitorIntervalMs = 60_000;
    const int MaxMonitorCommands = 10;
    const int MaxNameFilterLength = 64;
    const int UnresponsiveRounds = 3;

    readonly IBleManager? ble;
    readonly WebAppEventSource<ObdMonitorReading> readings = new();
    readonly WebAppEventSource<ObdDisconnected> disconnected = new();
    readonly ILoggerFactory? loggerFactory;
    readonly SemaphoreSlim exchange = new(1, 1);
    readonly Lock gate = new();
    readonly Dictionary<string, ScannedAdapter> adapters = new(StringComparer.OrdinalIgnoreCase);

    ActiveConnection? active;
    ActiveMonitor? monitor;
    bool scanning;

    public ObdBridge(IServiceProvider services)
    {
        this.ble = services.GetOptionalService<IBleManager>();
        this.loggerFactory = services.GetOptionalService<ILoggerFactory>();
    }

    public string Name => "obd";

    // Wi-Fi adapters are plain TCP, so there is always a transport; status reports whether BLE is one of them.
    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("obd.reading", ct => this.readings.ListenAsync(this.OnReadingListenerStopped, ct), ObdJsonContext.Default.ObdMonitorReading)
        .MapEvent("obd.disconnected", this.disconnected.ListenAsync, ObdJsonContext.Default.ObdDisconnected)
        .MapGet("/status", this.StatusAsync)
        .MapGet("/commands", CommandsAsync)
        .MapPost("/scan", ctx => this.Guarded(ctx, () => this.ScanAsync(ctx)))
        .MapGet("/adapters", this.AdaptersAsync)
        .MapPost("/connection", this.ConnectAsync)
        .MapDelete("/connection", this.DisconnectAsync)
        .MapPost("/command", this.CommandAsync)
        .MapPost("/raw", this.RawAsync)
        .MapGet("/vin", ctx => this.WithConnection(ctx, async (c, ct) =>
            await WebAppBridgeResults.Json(ctx, new ObdVin(await c.Execute(StandardCommands.Vin, ct)), ObdJsonContext.Default.ObdVin)))
        .MapGet("/dtc", this.ReadCodesAsync)
        .MapDelete("/dtc", this.ClearCodesAsync)
        .MapPost("/monitor", this.StartMonitorAsync)
        .MapDelete("/monitor", this.StopMonitorAsync);

    ValueTask StatusAsync(HttpContext context)
    {
        var a = this.active;
        var m = this.monitor;
        bool isScanning;

        lock (this.gate)
            isScanning = this.scanning;

        return WebAppBridgeResults.Json(
            context,
            new ObdStatus(
                a is not null && a.Connection.IsConnected,
                a?.Transport,
                a?.AdapterId,
                a?.AdapterName,
                a?.Connection.DetectedAdapter?.Type is { } type ? BridgeEnum.Convert<Shiny.Obd.ObdAdapterType, Client.ObdAdapterType>(type) : null,
                a?.Connection.DetectedAdapter?.RawIdentifier,
                a?.Connection.NegotiatedProtocol,
                this.ble is not null,
                this.ble is { } b ? BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(b.CurrentAccess) : null,
                isScanning,
                m is null ? null : new ObdMonitor(m.Commands, m.IntervalMs)
            ),
            ObdJsonContext.Default.ObdStatus
        );
    }

    static ValueTask CommandsAsync(HttpContext context)
    {
        IReadOnlyList<ObdCommandInfo> commands = [.. ObdCommands.All.Select(x => new ObdCommandInfo(x.Name, x.RawCommand, x.Unit))];
        return WebAppBridgeResults.Json(context, commands, ObdJsonContext.Default.IReadOnlyListObdCommandInfo);
    }

    async ValueTask ScanAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, ObdJsonContext.Default.ObdScanRequest) ?? new ObdScanRequest();

        if (body.NameFilter is { Length: > MaxNameFilterLength })
        {
            await WebAppBridgeResults.BadRequest(context, $"nameFilter is at most {MaxNameFilterLength} characters.");
            return;
        }

        IObdDeviceScanner scanner;

        if (body.Transport == ObdTransport.Ble)
        {
            if (this.ble is not { } b)
            {
                await WebAppBridgeResults.NotSupported(context, "Bluetooth LE OBD adapters");
                return;
            }

            if (b.CurrentAccess != AccessState.Available && await b.RequestAccessAsync() != AccessState.Available)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", "Bluetooth access was not granted.");
                return;
            }

            var filter = String.IsNullOrWhiteSpace(body.NameFilter) ? null : body.NameFilter.Trim();
            scanner = new BleObdDeviceScanner(b, new BleObdConfiguration { DeviceNameFilter = filter }, this.Logger<BleObdDeviceScanner>());
        }
        else
        {
            // Probes only the library's own candidates — the default gateway and the addresses these adapters ship
            // with — so the page cannot aim it at other hosts.
            scanner = new WifiObdDeviceScanner(new WifiObdConfiguration(), this.Logger<WifiObdDeviceScanner>());
        }

        var started = false;
        lock (this.gate)
        {
            if (!this.scanning)
                this.scanning = started = true;
        }

        if (!started)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "scan_in_progress", "A scan is already running.");
            return;
        }

        var found = new List<ObdAdapter>();
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            window.CancelAfter(Math.Clamp(body.ScanMs ?? DefaultScanMs, 1_000, MaxScanMs));

            await scanner.Scan(device =>
            {
                var adapter = new ScannedAdapter(device, body.Transport);
                lock (this.gate)
                {
                    this.adapters[device.Id] = adapter;
                    found.Add(adapter.ToResponse());
                }
            }, window.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The window closing is how a scan ends.
        }
        finally
        {
            lock (this.gate)
                this.scanning = false;
        }

        IReadOnlyList<ObdAdapter> response;
        lock (this.gate)
            response = [.. found];

        await WebAppBridgeResults.Json(context, response, ObdJsonContext.Default.IReadOnlyListObdAdapter);
    }

    ValueTask AdaptersAsync(HttpContext context)
    {
        IReadOnlyList<ObdAdapter> response;
        lock (this.gate)
            response = [.. this.adapters.Values.Select(x => x.ToResponse())];

        return WebAppBridgeResults.Json(context, response, ObdJsonContext.Default.IReadOnlyListObdAdapter);
    }

    async ValueTask ConnectAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, ObdJsonContext.Default.ObdConnectRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"transport\": \"ble\", \"peripheralUuid\": \"…\" } or { \"transport\": \"wifi\", \"host\": \"192.168.0.10\", \"port\": 35000 }.");
            return;
        }

        bool isScanning;
        lock (this.gate)
            isScanning = this.scanning;

        if (isScanning)
        {
            // Most adapters take one client; a probe still holding it would make the connection look dead.
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "scan_in_progress", "Wait for the scan to finish before connecting.");
            return;
        }

        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is { } existing && this.EnsureAlive(existing))
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "already_connected", $"Already connected to {existing.AdapterId}. DELETE /_bridge/obd/connection first.");
                return;
            }

            await this.Guarded(context, () => body.Transport == ObdTransport.Ble
                ? this.ConnectBleAsync(context, body)
                : this.ConnectWifiAsync(context, body));
        }
        finally
        {
            this.exchange.Release();
        }
    }

    async ValueTask ConnectBleAsync(HttpContext context, ObdConnectRequest body)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE OBD adapters");
            return;
        }

        if (String.IsNullOrWhiteSpace(body.PeripheralUuid))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"transport\": \"ble\", \"peripheralUuid\": \"…\" }.");
            return;
        }

        if (!IsBleUuid(body.ServiceUuid) || !IsBleUuid(body.ReadCharacteristicUuid) || !IsBleUuid(body.WriteCharacteristicUuid))
        {
            await WebAppBridgeResults.BadRequest(context, "serviceUuid, readCharacteristicUuid and writeCharacteristicUuid must be 4-digit short UUIDs or full GUIDs.");
            return;
        }

        ScannedAdapter? scanned;
        lock (this.gate)
            this.adapters.TryGetValue(body.PeripheralUuid, out scanned);

        var peripheral = scanned?.Device.NativeDevice as IPeripheral ?? FindPeripheral(b, body.PeripheralUuid);
        if (peripheral is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No known adapter '{body.PeripheralUuid}'. Scan for it first.");
            return;
        }

        var timeout = ConnectTimeout(body);
        var config = new BleObdConfiguration { ConnectTimeout = timeout };
        if (body.ServiceUuid is { } service)
            config.ServiceUuid = service;
        if (body.ReadCharacteristicUuid is { } read)
            config.ReadCharacteristicUuid = read;
        if (body.WriteCharacteristicUuid is { } write)
            config.WriteCharacteristicUuid = write;

        var connection = new ObdConnection(new BleObdTransport(peripheral, config));
        await this.OpenAsync(context, connection, timeout, ObdTransport.Ble, peripheral.Uuid, peripheral.Name ?? scanned?.Device.Name);
    }

    async ValueTask ConnectWifiAsync(HttpContext context, ObdConnectRequest body)
    {
        WifiObdConfiguration config;
        string adapterId;

        if (body.Host is null)
        {
            config = new WifiObdConfiguration();
            adapterId = "wifi";
        }
        else
        {
            var port = body.Port ?? 35_000;

            if (!IPAddress.TryParse(body.Host, out var address) || !IsLocalNetwork(address) || port is < 1 or > 65_535)
            {
                await WebAppBridgeResults.BadRequest(context, "host must be an IP address on the local network (loopback, private or link-local), and port 1-65535.");
                return;
            }

            config = new WifiObdConfiguration { Host = address.ToString(), Port = port, AutoDetectEndpoint = false };
            adapterId = new WifiObdEndpoint(config.Host, port).ToString();
        }

        var transport = new WifiObdTransport(config, this.Logger<WifiObdTransport>());
        var connection = new ObdConnection(transport);

        if (await this.OpenAsync(context, connection, ConnectTimeout(body), ObdTransport.Wifi, adapterId, null) is { } opened)
        {
            // A probe only knows where the adapter was once it answered.
            opened.AdapterId = transport.ConnectedEndpoint?.ToString() ?? adapterId;
            opened.AdapterName = transport.DetectedIdentifier;
        }
    }

    /// <summary>Connects, runs the adapter's initialization, and becomes the active connection. Runs holding the exchange.</summary>
    async ValueTask<ActiveConnection?> OpenAsync(HttpContext context, ObdConnection connection, TimeSpan timeout, ObdTransport transport, string adapterId, string? adapterName)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timeout);

        try
        {
            await connection.Connect(deadline.Token);
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(connection);

            if (ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status504GatewayTimeout, "obd_timeout", $"The adapter did not finish connecting within {timeout.TotalSeconds:0} seconds.");
                return null;
            }

            throw;
        }

        var opened = new ActiveConnection(connection, transport, adapterId, adapterName);
        this.active = opened;

        await this.StatusAsync(context);
        return opened;
    }

    async ValueTask DisconnectAsync(HttpContext context)
    {
        this.CancelMonitor();

        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is { } a)
            {
                this.active = null;

                try
                {
                    await a.Connection.Disconnect();
                }
                catch (Exception)
                {
                    // Already gone; disposing below is all that is left to do.
                }

                await DisposeQuietlyAsync(a.Connection);
                this.PublishDisconnected(a, "requested");
            }
        }
        finally
        {
            this.exchange.Release();
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CommandAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, ObdJsonContext.Default.ObdReadRequest);
        if (body?.Command is not { Length: > 0 } name)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"command\": \"engineRpm\" }.");
            return;
        }

        if (!ObdCommands.TryGet(name, out var entry))
        {
            await WebAppBridgeResults.NotFound(context, $"Unknown command '{name}'. GET /_bridge/obd/commands lists them.");
            return;
        }

        await this.WithConnection(context, async (c, ct) =>
        {
            var value = await entry.Read(c, ct);
            await WebAppBridgeResults.Json(
                context,
                new ObdReading(entry.Name, value.Value, value.Text, entry.Unit),
                ObdJsonContext.Default.ObdReading
            );
        });
    }

    async ValueTask RawAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, ObdJsonContext.Default.ObdRawRequest);
        if (body?.Raw is not { } requested || !TryNormalizeRaw(requested, out var raw))
        {
            await WebAppBridgeResults.BadRequest(
                context,
                "raw must be a read-only OBD request in hex (modes 01, 02, 03, 05, 06, 07, 09, 0A or 22, at most 8 bytes) or one of AT I, @1, DP, DPN, RV, CS, AR, SP, TP, ST, AT, SH, CRA."
            );
            return;
        }

        await this.WithConnection(context, async (c, ct) =>
            await WebAppBridgeResults.Json(context, new ObdRawResult(raw, await c.SendRaw(raw, ct)), ObdJsonContext.Default.ObdRawResult));
    }

    ValueTask ReadCodesAsync(HttpContext context) => this.WithConnection(context, async (c, ct) =>
    {
        var stored = await ReadCodesAsync(c, DtcReadCommand.Stored, ct);
        var pending = await ReadCodesAsync(c, DtcReadCommand.Pending, ct);
        var permanent = await ReadCodesAsync(c, DtcReadCommand.Permanent, ct);

        await WebAppBridgeResults.Json(context, new ObdTroubleCodes(stored, pending, permanent), ObdJsonContext.Default.ObdTroubleCodes);
    });

    async ValueTask ClearCodesAsync(HttpContext context)
    {
        if (!String.Equals(context.Request.Query["confirm"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            await WebAppBridgeResults.BadRequest(
                context,
                "Clearing trouble codes also resets the emissions readiness monitors, which take several drive cycles to complete again. Confirm with ?confirm=true."
            );
            return;
        }

        await this.WithConnection(context, async (c, ct) =>
            await WebAppBridgeResults.Json(context, new ObdClearResult(await c.Execute(ClearDtcCommand.Instance, ct)), ObdJsonContext.Default.ObdClearResult));
    }

    async ValueTask StartMonitorAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, ObdJsonContext.Default.ObdMonitorRequest);
        if (body?.Commands is not { Count: > 0 and <= MaxMonitorCommands } names)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"commands\": [\"engineRpm\"], \"intervalMs\": 1000 }} with 1-{MaxMonitorCommands} commands.");
            return;
        }

        var entries = new List<ObdCommandEntry>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ObdCommands.TryGet(name, out var entry))
            {
                await WebAppBridgeResults.NotFound(context, $"Unknown command '{name}'. GET /_bridge/obd/commands lists them.");
                return;
            }

            entries.Add(entry);
        }

        if (!this.readings.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen for obd.reading first: readings arrive as events.");
            return;
        }

        if (this.active is null)
        {
            await NotConnected(context);
            return;
        }

        var started = new ActiveMonitor(
            new CancellationTokenSource(),
            [.. entries.Select(x => x.Name)],
            Math.Clamp(body.IntervalMs ?? DefaultMonitorIntervalMs, MinMonitorIntervalMs, MaxMonitorIntervalMs)
        );

        ActiveMonitor? previous;
        lock (this.gate)
        {
            previous = this.monitor;
            this.monitor = started;
        }

        previous?.Cancellation.Cancel();
        _ = Task.Run(() => this.RunMonitorAsync(started, entries));

        // The last reading listener may have gone since the check above, and then nothing would ever stop this monitor.
        if (!this.readings.HasListeners)
            this.CancelMonitor();

        await WebAppBridgeResults.Json(context, new ObdMonitor(started.Commands, started.IntervalMs), ObdJsonContext.Default.ObdMonitor);
    }

    ValueTask StopMonitorAsync(HttpContext context)
    {
        this.CancelMonitor();
        return WebAppBridgeResults.NoContent(context);
    }

    async Task RunMonitorAsync(ActiveMonitor m, IReadOnlyList<ObdCommandEntry> entries)
    {
        var ct = m.Cancellation.Token;
        var silentRounds = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var timeouts = 0;

                foreach (var entry in entries)
                {
                    // Taken per command rather than per round, so a page's own request slots in between readings.
                    await this.exchange.WaitAsync(ct);
                    try
                    {
                        if (this.active is not { } a || !this.EnsureAlive(a))
                            return;

                        try
                        {
                            var value = await entry.Read(a.Connection, ct);
                            this.PublishReading(entry, value, null);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (ex is ObdTimeoutException or TimeoutException)
                                timeouts++;

                            this.PublishReading(entry, default, ex.Message);
                        }
                    }
                    finally
                    {
                        this.exchange.Release();
                    }
                }

                // A BLE adapter reports connected long after it stops answering; rounds of nothing but timeouts are the tell.
                silentRounds = timeouts == entries.Count ? silentRounds + 1 : 0;
                if (silentRounds >= UnresponsiveRounds)
                {
                    await this.exchange.WaitAsync(ct);
                    try
                    {
                        if (this.active is { } a)
                            this.Drop(a, "unresponsive");
                    }
                    finally
                    {
                        this.exchange.Release();
                    }

                    return;
                }

                await Task.Delay(m.IntervalMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (this.gate)
            {
                if (ReferenceEquals(this.monitor, m))
                    this.monitor = null;
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> against the live connection, holding the exchange for its duration.</summary>
    async ValueTask WithConnection(HttpContext context, Func<ObdConnection, CancellationToken, Task> action)
    {
        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is not { } a || !this.EnsureAlive(a))
            {
                await NotConnected(context);
                return;
            }

            await this.Guarded(context, async () => await action(a.Connection, context.RequestAborted));
        }
        finally
        {
            this.exchange.Release();
        }
    }

    /// <summary>Maps adapter and radio failures to responses the page can switch on.</summary>
    async ValueTask Guarded(HttpContext context, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (ObdTimeoutException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status504GatewayTimeout, "obd_timeout", ex.Message);
        }
        catch (TimeoutException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status504GatewayTimeout, "obd_timeout", ex.Message);
        }
        catch (ObdException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "obd_failed", ex.Message);
        }
        catch (BleException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "ble_failed", ex.Message);
        }
        catch (SocketException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "connection_failed", ex.Message);
        }
    }

    /// <summary>False, having dropped it, when the transport has lost the adapter. Call holding the exchange.</summary>
    bool EnsureAlive(ActiveConnection a)
    {
        if (a.Connection.IsConnected)
            return true;

        this.Drop(a, "lost");
        return false;
    }

    /// <summary>
    /// Forgets a connection that can no longer be used. Reconnecting the transport alone would leave the adapter
    /// with its defaults (echo on, headers off) and every reply unparseable, so the page has to connect again,
    /// which re-runs the initialization. Call holding the exchange.
    /// </summary>
    void Drop(ActiveConnection a, string reason)
    {
        if (!ReferenceEquals(this.active, a))
            return;

        this.active = null;
        this.CancelMonitor();
        _ = DisposeQuietlyAsync(a.Connection);
        this.PublishDisconnected(a, reason);
    }

    void CancelMonitor()
    {
        ActiveMonitor? m;
        lock (this.gate)
        {
            m = this.monitor;
            this.monitor = null;
        }

        m?.Cancellation.Cancel();
    }

    void OnReadingListenerStopped(int remaining)
    {
        // Readings nobody can receive would only keep the adapter busy. The connection itself stays up.
        if (remaining == 0)
            this.CancelMonitor();
    }

    void PublishReading(ObdCommandEntry entry, ObdValue value, string? error)
        => this.readings.Publish(new ObdMonitorReading(entry.Name, value.Value, value.Text, entry.Unit, error, DateTimeOffset.UtcNow));

    void PublishDisconnected(ActiveConnection a, string reason)
        => this.disconnected.Publish(new ObdDisconnected(reason, a.Transport, a.AdapterId));

    ILogger<T> Logger<T>() => this.loggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;

    static ValueTask NotConnected(HttpContext context)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_connected", "No OBD adapter is connected. POST /_bridge/obd/connection first.");

    static async Task<string[]?> ReadCodesAsync(ObdConnection connection, DtcReadCommand command, CancellationToken ct)
    {
        try
        {
            return [.. await connection.Execute(command, ct)];
        }
        catch (ObdException ex) when (ex is not ObdTimeoutException)
        {
            // NO DATA here means the vehicle does not support the mode — permanent codes predate many vehicles.
            // That is an absence, not a clean bill of health, so it is null rather than empty.
            return null;
        }
    }

    static TimeSpan ConnectTimeout(ObdConnectRequest body)
        => TimeSpan.FromMilliseconds(Math.Clamp(body.TimeoutMs ?? DefaultConnectTimeoutMs, 1_000, MaxConnectTimeoutMs));

    static IPeripheral? FindPeripheral(IBleManager ble, string uuid)
    {
        try
        {
            return ble.GetKnownPeripheral(uuid)
                ?? ble.GetConnectedPeripherals().FirstOrDefault(x => String.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // iOS identifiers are GUIDs; anything else is simply not a peripheral.
            return null;
        }
    }

    /// <summary>
    /// Wi-Fi adapters run their own access point on a private address. Holding the page to local ranges keeps it
    /// from using the device to open TCP connections to arbitrary hosts, and IP literals rule out a name that
    /// resolves somewhere else.
    /// </summary>
    static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// Raw requests are limited to what only reads. Clearing codes (mode 04), controlling systems (08), resetting
    /// or reprogramming an ECU (UDS 10, 11, 27, 2E, 31, 34…) and the adapter's persistent settings (AT PP, AT WS)
    /// are not reachable from here, and echo, linefeeds and headers stay as the connection's parser expects them.
    /// </summary>
    static bool TryNormalizeRaw(string value, out string raw)
    {
        raw = value.Replace(" ", String.Empty).ToUpperInvariant();
        return raw.Length <= 16 && (HexRequest().IsMatch(raw) || AtRequest().IsMatch(raw));
    }

    static bool IsBleUuid(string? value) => value is null || BleUuid().IsMatch(value);

    static async Task DisposeQuietlyAsync(ObdConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception)
        {
            // The transport is being thrown away; a failure to close it cleanly changes nothing.
        }
    }

    [GeneratedRegex("^(01|02|03|05|06|07|09|0A|22)([0-9A-F]{2})*$")]
    private static partial Regex HexRequest();

    [GeneratedRegex("^AT(I|@1|DPN?|RV|CS|AR|SP[0-9A-C]|TP[0-9A-C]|ST[0-9A-F]{2}|AT[0-2]|SH([0-9A-F]{3}|[0-9A-F]{6})|CRA([0-9A-F]{3}|[0-9A-F]{8})?)$")]
    private static partial Regex AtRequest();

    [GeneratedRegex("^([0-9A-Fa-f]{4}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$")]
    private static partial Regex BleUuid();

    public void Dispose()
    {
        this.CancelMonitor();

        if (Interlocked.Exchange(ref this.active, null) is { } a)
        {
            try
            {
                a.Connection.Dispose();
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }
    }

    sealed class ActiveConnection(ObdConnection connection, ObdTransport transport, string adapterId, string? adapterName)
    {
        public ObdConnection Connection { get; } = connection;
        public ObdTransport Transport { get; } = transport;
        public string AdapterId { get; set; } = adapterId;
        public string? AdapterName { get; set; } = adapterName;
    }

    sealed record ActiveMonitor(CancellationTokenSource Cancellation, string[] Commands, int IntervalMs);

    sealed record ScannedAdapter(ObdDiscoveredDevice Device, ObdTransport Transport)
    {
        public ObdAdapter ToResponse() => this.Device.NativeDevice is WifiObdEndpoint endpoint
            ? new ObdAdapter(this.Device.Id, this.Device.Name, this.Transport, endpoint.Host, endpoint.Port)
            : new ObdAdapter(this.Device.Id, this.Device.Name, this.Transport, null, null);
    }
}

/// <summary>The commands the page can name. Each reads one standard PID and answers a number in its unit, or text.</summary>
static class ObdCommands
{
    static readonly ObdCommandEntry[] all =
    [
        Number("vehicleSpeed", StandardCommands.VehicleSpeed, "km/h", x => x),
        Number("engineRpm", StandardCommands.EngineRpm, "rpm", x => x),
        Number("coolantTemperature", StandardCommands.CoolantTemperature, "°C", x => x),
        Number("engineOilTemperature", StandardCommands.EngineOilTemperature, "°C", x => x),
        Number("intakeAirTemperature", StandardCommands.IntakeAirTemperature, "°C", x => x),
        Number("ambientAirTemperature", StandardCommands.AmbientAirTemperature, "°C", x => x),
        Number("relativeThrottlePosition", StandardCommands.RelativeThrottlePosition, "%", x => x),
        Number("throttlePosition", StandardCommands.ThrottlePosition, "%", x => x),
        Number("relativeAcceleratorPedalPosition", StandardCommands.RelativeAcceleratorPedalPosition, "%", x => x),
        Number("calculatedEngineLoad", StandardCommands.CalculatedEngineLoad, "%", x => x),
        Number("fuelLevel", StandardCommands.FuelLevel, "%", x => x),
        Number("engineFuelRate", StandardCommands.EngineFuelRate, "L/h", x => x),
        Number("massAirFlow", StandardCommands.MassAirFlow, "g/s", x => x),
        Number("intakeManifoldPressure", StandardCommands.IntakeManifoldPressure, "kPa", x => x),
        Number("barometricPressure", StandardCommands.BarometricPressure, "kPa", x => x),
        Number("timingAdvance", StandardCommands.TimingAdvance, "°", x => x),
        Number("controlModuleVoltage", StandardCommands.ControlModuleVoltage, "V", x => x),
        Number("hybridBatteryLife", StandardCommands.HybridBatteryLife, "%", x => x),
        Number("odometer", StandardCommands.Odometer, "km", x => x),
        Number("distanceSinceCodesCleared", StandardCommands.DistanceSinceCodesCleared, "km", x => x),
        Number("distanceWithMilOn", StandardCommands.DistanceWithMilOn, "km", x => x),
        Number("warmUpsSinceCodesCleared", StandardCommands.WarmUpsSinceCodesCleared, "count", x => x),
        Number("runtimeSinceStart", StandardCommands.RuntimeSinceStart, "s", x => x.TotalSeconds),
        Number("timeSinceCodesCleared", StandardCommands.TimeSinceCodesCleared, "min", x => x.TotalMinutes),
        Text("fuelType", StandardCommands.FuelType, FuelTypes.Describe),
        Text("ecuName", StandardCommands.EcuName, x => x),
        Text("vin", StandardCommands.Vin, x => x)
    ];

    static readonly FrozenDictionary<string, ObdCommandEntry> byName = all.ToFrozenDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ObdCommandEntry> All => all;

    public static bool TryGet(string name, [NotNullWhen(true)] out ObdCommandEntry? entry) => byName.TryGetValue(name, out entry);

    static ObdCommandEntry Number<T>(string name, IObdCommand<T> command, string unit, Func<T, double> value)
        => new(name, command.RawCommand, unit, async (c, ct) => new ObdValue(value(await c.Execute(command, ct)), null));

    static ObdCommandEntry Text<T>(string name, IObdCommand<T> command, Func<T, string?> text)
        => new(name, command.RawCommand, null, async (c, ct) => new ObdValue(null, text(await c.Execute(command, ct))));
}

sealed record ObdCommandEntry(string Name, string RawCommand, string? Unit, Func<ObdConnection, CancellationToken, Task<ObdValue>> Read);

readonly record struct ObdValue(double? Value, string? Text);

public static class ObdBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/obd</c> and registers Shiny's Bluetooth LE service for the platform — there is nothing
    /// else to call, and it sits happily beside <c>AddBluetoothLEBridge()</c>. Wi-Fi adapters work on every
    /// platform; Bluetooth LE adapters wherever Shiny.BluetoothLE does, which excludes Linux.
    /// </summary>
    public static MauiAppBuilder AddObdBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#elif MACOS
        builder.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        // Registers once however many bridges call it: Shiny checks for an existing BleManager first.
        builder.Services.AddBluetoothLE();
#endif

        builder.Services.AddWebAppBridge<ObdBridge>();
        return builder;
    }
}
