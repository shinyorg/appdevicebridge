using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Printers.Client;
using Shiny.BluetoothLE;
using Shiny.Net.HttpServer;
using Shiny.Printers;
using Shiny.Printers.BluetoothLE;
using Shiny.Printers.Network;
using Shiny.Printers.Protocols.EscPos;
using Shiny.Printers.Protocols.Tspl;
using Shiny.Printing.Rendering;
using Native = Shiny.Printers;
using PrinterCapabilities = Shiny.AppDeviceBridge.Printers.Client.PrinterCapabilities;

namespace Shiny.AppDeviceBridge.Printers;

/// <summary>
/// <c>/_bridge/printers</c> over Shiny.Printers — one ESC/POS or TSPL printer at a time, over Bluetooth LE or raw TCP.
/// Prints to it are serialized: a second receipt streamed while the first is still going would interleave both.
/// <code>
/// GET    /_bridge/printers/status
/// POST   /_bridge/printers/scan         { "transport": "Ble", "scanMs": 5000 }   the printers seen in the window
/// POST   /_bridge/printers/connection   { "id": "…" }                            a printer the last scan found
///                                       { "transport": "Network", "host": "192.168.1.50", "port": 9100, "paper": "Paper80mm" }
///                                       { "transport": "Ble", "peripheralUuid": "…", "serviceUuid": "…", "writeCharacteristicUuid": "…" }
/// DELETE /_bridge/printers/connection
/// POST   /_bridge/printers/print        { "elements": [{ "type": "Line", "text": "Hello" }, { "type": "Cut" }] }
/// POST   /_bridge/printers/render       { "elements": […], "pageWidth": 595 }      application/pdf
///
/// events: printers.disconnected
/// </code>
/// </summary>
public sealed partial class PrintersBridge : IWebAppBridge, IDisposable
{
    const int DefaultScanMs = 5_000;
    const int MaxScanMs = 30_000;
    const int DefaultConnectTimeoutMs = 15_000;
    const int MaxConnectTimeoutMs = 60_000;

    readonly IBleManager? ble;
    readonly IPrinterScanner? bleScanner;
    readonly INetworkPrinterScanner? networkScanner;
    readonly IPrintDocumentRenderer? renderer;
    readonly PrintersBridgeOptions options;
    readonly NetworkPrinterManager network = new();
    readonly WebAppEventSource<PrinterDisconnected> disconnected = new();
    readonly SemaphoreSlim exchange = new(1, 1);
    readonly Lock gate = new();
    readonly Dictionary<string, Scanned> found = new(StringComparer.OrdinalIgnoreCase);

    ActivePrinter? active;
    bool scanning;

    public PrintersBridge(IServiceProvider services)
    {
        this.ble = services.GetOptionalService<IBleManager>();

        // Shiny's own registrations when the app made them; otherwise built over what is there, so a Linux app that
        // registers Bluetooth after the bridge still gets Bluetooth printers.
        this.bleScanner = services.GetOptionalService<IPrinterScanner>() ?? (this.ble is { } b ? new BlePrinterScanner(b) : null);
        this.networkScanner = services.GetOptionalService<INetworkPrinterScanner>();
        this.renderer = services.GetOptionalService<IPrintDocumentRenderer>();
        this.options = services.GetOptionalService<PrintersBridgeOptions>() ?? new();
    }

    public string Name => "printers";

    // Network printers are plain TCP, so there is always a transport; status says which others there are.
    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("printers.disconnected", this.disconnected.ListenAsync, PrintersJsonContext.Default.PrinterDisconnected)
        .MapGet("/status", this.StatusAsync)
        .MapPost("/scan", this.ScanAsync)
        .MapPost("/connection", this.ConnectAsync)
        .MapDelete("/connection", this.DisconnectAsync)
        .MapPost("/print", this.PrintAsync)
        .MapPost("/render", this.RenderAsync);

    ValueTask StatusAsync(HttpContext context)
    {
        bool isScanning;
        lock (this.gate)
            isScanning = this.scanning;

        var a = this.active;
        return WebAppBridgeResults.Json(
            context,
            new PrintersStatus(
                a is not null && a.Printer.IsConnected ? a.Info : null,
                this.bleScanner is not null,
                this.ble is { } b ? BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(b.CurrentAccess) : null,
                this.networkScanner is not null,
                this.renderer is not null,
                isScanning
            ),
            PrintersJsonContext.Default.PrintersStatus
        );
    }

    async ValueTask ScanAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, PrintersJsonContext.Default.PrinterScanRequest) ?? new PrinterScanRequest();

        IObservable<Scanned> source;
        if (body.Transport == PrinterTransport.Ble)
        {
            if (this.bleScanner is not { } scanner || this.ble is not { } b)
            {
                await WebAppBridgeResults.NotSupported(context, "Bluetooth LE printers");
                return;
            }

            if (b.CurrentAccess != AccessState.Available && await b.RequestAccessAsync() != AccessState.Available)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", "Bluetooth access was not granted.");
                return;
            }

            source = new Select<Native.BluetoothLE.DiscoveredPrinter, Scanned>(scanner.Scan(), Scanned.From);
        }
        else
        {
            if (this.networkScanner is not { } scanner)
            {
                await WebAppBridgeResults.NotSupported(context, "Scanning for network printers");
                return;
            }

            source = new Select<DiscoveredNetworkPrinter, Scanned>(scanner.Scan(), Scanned.From);
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

        // Insertion-ordered and de-duplicated: a Bluetooth printer advertises over and over.
        var seen = new Dictionary<string, Scanned>(StringComparer.OrdinalIgnoreCase);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (source.Subscribe(new Observer<Scanned>(
                x =>
                {
                    lock (this.gate)
                    {
                        seen[x.Response.Id] = x;
                        this.found[x.Response.Id] = x;
                    }
                },
                ex => failed.TrySetResult(ex)
            )))
            {
                var window = Task.Delay(Math.Clamp(body.ScanMs ?? DefaultScanMs, 1_000, MaxScanMs), context.RequestAborted);
                if (await Task.WhenAny(window, failed.Task) == failed.Task)
                {
                    await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "scan_failed", failed.Task.Result.Message);
                    return;
                }
                await window;
            }
        }
        finally
        {
            lock (this.gate)
                this.scanning = false;
        }

        IReadOnlyList<Client.DiscoveredPrinter> response;
        lock (this.gate)
            response = [.. seen.Values.Select(x => x.Response)];

        await WebAppBridgeResults.Json(context, response, PrintersJsonContext.Default.IReadOnlyListDiscoveredPrinter);
    }

    async ValueTask ConnectAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, PrintersJsonContext.Default.PrinterConnectRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"id\": \"…\" } from a scan, or { \"transport\": \"Network\", \"host\": \"192.168.1.50\", \"port\": 9100 }.");
            return;
        }

        Scanned? scanned = null;
        if (body.Id is { } id)
        {
            lock (this.gate)
                this.found.TryGetValue(id, out scanned);

            if (scanned is null)
            {
                await WebAppBridgeResults.NotFound(context, $"No printer '{id}' was found by a scan. Scan for it first.");
                return;
            }
        }

        if (body.ChunkSize is < 20 or > 512 || body.ChunkDelayMs is < 0 or > 500)
        {
            await WebAppBridgeResults.BadRequest(context, "chunkSize is 20 to 512 bytes, and chunkDelayMs 0 to 500.");
            return;
        }

        if (body.Capabilities is { } custom && Validate(custom) is { } invalid)
        {
            await WebAppBridgeResults.BadRequest(context, invalid);
            return;
        }

        var transport = scanned?.Response.Transport ?? body.Transport;
        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is { } existing)
            {
                if (existing.Printer.IsConnected)
                {
                    await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "already_connected", $"Already connected to {existing.Info.Id}. DELETE /_bridge/printers/connection first.");
                    return;
                }
                this.Drop(existing, publish: true);
            }

            await this.Guarded(context, () => transport == PrinterTransport.Ble
                ? this.ConnectBleAsync(context, body, scanned)
                : this.ConnectNetworkAsync(context, body, scanned));
        }
        finally
        {
            this.exchange.Release();
        }
    }

    async ValueTask ConnectBleAsync(HttpContext context, PrinterConnectRequest body, Scanned? scanned)
    {
        if (this.ble is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Bluetooth LE printers");
            return;
        }

        IPeripheral? peripheral;
        BlePrinterConfig config;
        if (scanned?.Ble is { } discovered)
        {
            peripheral = discovered.Peripheral;
            config = discovered.Config;
        }
        else
        {
            if (body.PeripheralUuid is not { Length: > 0 } uuid || body.ServiceUuid is null || body.WriteCharacteristicUuid is null)
            {
                await WebAppBridgeResults.BadRequest(context, "Expected { \"id\": \"…\" } from a scan, or a peripheralUuid with its serviceUuid and writeCharacteristicUuid.");
                return;
            }

            if (!BleUuid().IsMatch(body.ServiceUuid) || !BleUuid().IsMatch(body.WriteCharacteristicUuid))
            {
                await WebAppBridgeResults.BadRequest(context, "serviceUuid and writeCharacteristicUuid must be 4-digit short UUIDs or full GUIDs.");
                return;
            }

            peripheral = FindPeripheral(b, uuid);
            if (peripheral is null)
            {
                await WebAppBridgeResults.NotFound(context, $"No known peripheral '{uuid}'. Scan for it first.");
                return;
            }

            config = new BlePrinterConfig
            {
                ServiceUuid = body.ServiceUuid,
                WriteCharacteristicUuid = body.WriteCharacteristicUuid,
                Capabilities = Native.PrinterCapabilities.Paper58mm
            };
        }

        var capabilities = this.ResolveCapabilities(body, config.Capabilities);
        config = config with
        {
            Capabilities = capabilities,
            ChunkSize = body.ChunkSize ?? config.ChunkSize,
            InterChunkDelay = body.ChunkDelayMs is { } delay ? TimeSpan.FromMilliseconds(delay) : config.InterChunkDelay,
            ProtocolFactory = body.Language is { } language ? () => Protocol(language, body.Label) : config.ProtocolFactory
        };

        var printer = (Printer)await new BlePrinterManager(b).Connect(peripheral, config, context.RequestAborted, ConnectTimeout(body));
        var info = new ConnectedPrinter(
            peripheral.Uuid,
            peripheral.Name ?? scanned?.Response.Name,
            PrinterTransport.Ble,
            Language(printer.Protocol),
            ToContract(capabilities)
        );
        await this.OpenedAsync(context, printer, info);
    }

    async ValueTask ConnectNetworkAsync(HttpContext context, PrinterConnectRequest body, Scanned? scanned)
    {
        string host;
        int port;
        Native.PrinterCapabilities fallback;

        if (scanned?.Network is { } discovered)
        {
            (host, port, fallback) = (discovered.Host, discovered.Port, discovered.Capabilities);
        }
        else
        {
            port = body.Port ?? 9100;
            if (body.Host is null || !IPAddress.TryParse(body.Host, out var address) || !LocalNetwork.IsLocal(address) || !this.options.NetworkPorts.Contains(port))
            {
                await WebAppBridgeResults.BadRequest(
                    context,
                    $"host must be an IP address on the local network (loopback, private or link-local), and port one of {String.Join(", ", this.options.NetworkPorts.Order())}."
                );
                return;
            }
            (host, fallback) = (address.ToString(), Native.PrinterCapabilities.Paper80mm);
        }

        var capabilities = this.ResolveCapabilities(body, fallback);
        var config = new NetworkPrinterConfig
        {
            Host = host,
            Port = port,
            Capabilities = capabilities,
            ProtocolFactory = () => Protocol(body.Language ?? PrinterLanguage.EscPos, body.Label)
        };

        var printer = (Printer)await this.network.Connect(config, context.RequestAborted, ConnectTimeout(body));
        var info = new ConnectedPrinter(
            $"{host}:{port}",
            scanned?.Response.Name,
            PrinterTransport.Network,
            Language(printer.Protocol),
            ToContract(capabilities)
        );
        await this.OpenedAsync(context, printer, info);
    }

    /// <summary>Becomes the active printer, watching for it to go away. Runs holding the exchange.</summary>
    async ValueTask OpenedAsync(HttpContext context, Printer printer, ConnectedPrinter info)
    {
        var opened = new ActivePrinter(printer, info);
        this.active = opened;

        // Replays the current state first; only a later drop means the printer went away.
        opened.Watch = printer.Connection.WhenStatusChanged().Subscribe(new Observer<PrinterConnectionState>(
            state =>
            {
                if (state == PrinterConnectionState.Disconnected && !printer.IsConnected)
                    this.Drop(opened, publish: true);
            },
            _ => { }
        ));

        await WebAppBridgeResults.Json(context, info, PrintersJsonContext.Default.ConnectedPrinter);
    }

    async ValueTask DisconnectAsync(HttpContext context)
    {
        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is { } a)
                this.Drop(a, publish: false);
        }
        finally
        {
            this.exchange.Release();
        }
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask PrintAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, PrintersJsonContext.Default.PrintReceiptRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"elements\": [{ \"type\": \"Line\", \"text\": \"…\" }] }.");
            return;
        }

        await this.exchange.WaitAsync(context.RequestAborted);
        try
        {
            if (this.active is not { } a || !a.Printer.IsConnected)
            {
                if (this.active is { } lost)
                    this.Drop(lost, publish: true);

                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_connected", "No printer is connected. POST /_bridge/printers/connection first.");
                return;
            }

            byte[] bytes;
            try
            {
                var document = PrintElementMapper.ToDocument(body.Elements, a.Printer.Capabilities, forPrinter: true);
                bytes = a.Printer.Protocol.Encode(document, a.Printer.Capabilities);
            }
            catch (PrintElementException ex)
            {
                await WebAppBridgeResults.BadRequest(context, ex.Message);
                return;
            }

            try
            {
                await a.Printer.PrintRaw(bytes, context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or BleException)
            {
                // A raw socket only notices a printer gone when writing to it fails.
                this.Drop(a, publish: true);
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "printer_failed", ex.Message);
                return;
            }

            await WebAppBridgeResults.Json(context, new PrintReceiptResult(bytes.Length), PrintersJsonContext.Default.PrintReceiptResult);
        }
        finally
        {
            this.exchange.Release();
        }
    }

    async ValueTask RenderAsync(HttpContext context)
    {
        if (this.renderer is not { } r)
        {
            await WebAppBridgeResults.NotSupported(context, "Rendering a receipt to PDF");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, PrintersJsonContext.Default.PrintRenderRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"elements\": [{ \"type\": \"Line\", \"text\": \"…\" }] }.");
            return;
        }

        var page = new PrintRenderOptions();
        page = page with
        {
            PageWidth = body.PageWidth ?? page.PageWidth,
            PageHeight = body.PageHeight ?? page.PageHeight,
            Margin = body.Margin ?? page.Margin,
            FontSize = body.FontSize ?? page.FontSize
        };

        if (page.PageWidth is < 72 or > 14_400 || page.PageHeight is < 72 or > 14_400 || page.Margin < 0 || page.Margin * 2 >= Math.Min(page.PageWidth, page.PageHeight) || page.FontSize is < 4 or > 72)
        {
            await WebAppBridgeResults.BadRequest(context, "pageWidth and pageHeight are 72 to 14400 points, margin leaves room on the page, and fontSize is 4 to 72.");
            return;
        }

        var capabilities = body.Paper is { } paper ? Preset(paper) : this.active?.Printer.Capabilities ?? Native.PrinterCapabilities.Paper80mm;

        byte[] pdf;
        try
        {
            pdf = r.RenderToPdf(PrintElementMapper.ToDocument(body.Elements, capabilities, forPrinter: false), page);
        }
        catch (PrintElementException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            // SkiaSharp without its native library — a Linux app that has not added SkiaSharp.NativeAssets.Linux.
            await WebAppBridgeResults.NotSupported(context, "Rendering a receipt to PDF");
            return;
        }

        await context.Response.WriteBytesAsync(pdf, "application/pdf", context.RequestAborted);
    }

    async ValueTask Guarded(HttpContext context, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (TimeoutException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status504GatewayTimeout, "printer_timeout", ex.Message);
        }
        catch (BleException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "ble_failed", ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException && !context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "connection_failed", ex.Message);
        }
    }

    Native.PrinterCapabilities ResolveCapabilities(PrinterConnectRequest body, Native.PrinterCapabilities fallback)
        => body.Capabilities is { } custom ? FromContract(custom)
            : body.Paper is { } paper ? Preset(paper)
            : fallback;

    /// <summary>Forgets the active printer and closes it. Safe to call twice; only the first publishes.</summary>
    void Drop(ActivePrinter a, bool publish)
    {
        if (Interlocked.CompareExchange(ref this.active, null, a) != a)
            return;

        a.Watch?.Dispose();
        try
        {
            a.Printer.Dispose();
        }
        catch (Exception)
        {
            // The link is being thrown away; failing to close it cleanly changes nothing.
        }

        if (publish)
            this.disconnected.Publish(new PrinterDisconnected(a.Info.Id, a.Info.Transport));
    }

    static TimeSpan ConnectTimeout(PrinterConnectRequest body)
        => TimeSpan.FromMilliseconds(Math.Clamp(body.TimeoutMs ?? DefaultConnectTimeoutMs, 1_000, MaxConnectTimeoutMs));

    static IPrinterProtocol Protocol(PrinterLanguage language, PrinterLabel? label) => language switch
    {
        PrinterLanguage.Tspl => new TsplProtocol(label?.WidthMm ?? 50, label?.HeightMm ?? 30, label?.GapMm ?? 3),
        _ => new EscPosProtocol()
    };

    static PrinterLanguage Language(IPrinterProtocol protocol)
        => protocol is TsplProtocol ? PrinterLanguage.Tspl : PrinterLanguage.EscPos;

    static Native.PrinterCapabilities Preset(PrinterPaper paper)
        => paper == PrinterPaper.Paper58mm ? Native.PrinterCapabilities.Paper58mm : Native.PrinterCapabilities.Paper80mm;

    static string? Validate(PrinterCapabilities c)
        => c.CharactersPerLine is < 8 or > 256 ? "capabilities.charactersPerLine is 8 to 256."
            : c.DotsPerLine is < 8 or > 4_096 ? "capabilities.dotsPerLine is 8 to 4096."
            : c.Dpi is < 72 or > 1_200 ? "capabilities.dpi is 72 to 1200."
            : c.PaperWidthMm is < 10 or > 300 ? "capabilities.paperWidthMm is 10 to 300."
            : null;

    internal static PrinterCapabilities ToContract(Native.PrinterCapabilities c) => new(
        c.CharactersPerLine,
        c.CharactersPerLineFontB,
        c.Dpi,
        c.PaperWidthMm,
        c.DotsPerLine,
        c.SupportsImages,
        c.SupportsCut,
        c.SupportsQrCodes,
        [.. Enum.GetValues<Native.BarcodeFormat>().Where(c.Supports).Select(BridgeEnum.Convert<Native.BarcodeFormat, PrintBarcodeFormat>)]
    );

    static Native.PrinterCapabilities FromContract(PrinterCapabilities c) => new()
    {
        CharactersPerLine = c.CharactersPerLine,
        CharactersPerLineFontB = c.CharactersPerLineFontB,
        Dpi = c.Dpi,
        PaperWidthMm = c.PaperWidthMm,
        DotsPerLine = c.DotsPerLine,
        SupportsImages = c.SupportsImages,
        SupportsCut = c.SupportsCut,
        SupportsQrCodes = c.SupportsQrCodes,
        SupportedBarcodes = (c.SupportedBarcodes ?? []).Select(BridgeEnum.Convert<PrintBarcodeFormat, Native.BarcodeFormat>).ToHashSet()
    };

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

    [GeneratedRegex("^([0-9A-Fa-f]{4}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$")]
    private static partial Regex BleUuid();

    public void Dispose()
    {
        if (this.active is { } a)
            this.Drop(a, publish: false);
    }

    sealed class ActivePrinter(Printer printer, ConnectedPrinter info)
    {
        public Printer Printer { get; } = printer;
        public ConnectedPrinter Info { get; } = info;
        public IDisposable? Watch { get; set; }
    }

    /// <summary>A printer a scan found: what the page sees, and what connecting to it needs.</summary>
    sealed record Scanned(Client.DiscoveredPrinter Response, Native.BluetoothLE.DiscoveredPrinter? Ble, DiscoveredNetworkPrinter? Network)
    {
        public static Scanned From(Native.BluetoothLE.DiscoveredPrinter p) => new(
            new(p.Uuid, p.Name, PrinterTransport.Ble, null, null, p.Rssi, KnownPrinterProfiles.All.FirstOrDefault(x => x.Config == p.Config)?.Name, ToContract(p.Config.Capabilities)),
            p,
            null
        );

        public static Scanned From(DiscoveredNetworkPrinter p) => new(
            new($"{p.Host}:{p.Port}", p.Name, PrinterTransport.Network, p.Host, p.Port, null, null, ToContract(p.Capabilities)),
            null,
            p
        );
    }

    /// <summary>Enough Rx to watch a scan and a connection without taking a dependency for it.</summary>
    sealed class Observer<T>(Action<T> next, Action<Exception> error) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception ex) => error(ex);
        public void OnCompleted() { }
    }

    sealed class Select<TFrom, TTo>(IObservable<TFrom> source, Func<TFrom, TTo> map) : IObservable<TTo>
    {
        public IDisposable Subscribe(IObserver<TTo> observer)
            => source.Subscribe(new Observer<TFrom>(x => observer.OnNext(map(x)), observer.OnError));
    }
}

public static class PrintersBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/printers</c> and registers Shiny's Bluetooth LE service for the platform, mDNS for finding network
    /// printers, and the PDF renderer — there is nothing else to call.
    /// <code>
    /// bridge.AddPrintersBridge(o => o.NetworkPorts.Add(9200));
    /// </code>
    /// <para>
    /// Network printers work everywhere. Bluetooth LE printers work wherever Shiny.BluetoothLE does — on Linux once the app
    /// registers Shiny.BluetoothLE.Linux. Declare <c>NSBluetoothAlwaysUsageDescription</c>, <c>NSLocalNetworkUsageDescription</c>
    /// and the <c>_pdl-datastream._tcp</c> and <c>_printer._tcp</c> <c>NSBonjourServices</c> on Apple platforms, and
    /// <c>BLUETOOTH_SCAN</c> / <c>BLUETOOTH_CONNECT</c> on Android. Rendering on Linux needs SkiaSharp.NativeAssets.Linux in
    /// the app; without it, render answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddPrintersBridge<TBuilder>(this TBuilder bridge, Action<PrintersBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(PrintersBridgeOptions))?.ImplementationInstance as PrintersBridgeOptions;
        if (options is null)
        {
            options = new PrintersBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

#if MACOS
        services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        // Registers once however many bridges call it: Shiny checks for an existing BleManager first.
        services.AddBluetoothLE();
#endif

        services.AddNetworkPrinting();
        services.AddPrintDocumentRendering();
        bridge.AddBridge<PrintersBridge>();
        return bridge;
    }
}
