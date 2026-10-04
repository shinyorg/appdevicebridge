using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Printers;
using Shiny.AppDeviceBridge.Printers.Client;
using Shiny.Printers.Document;
using Shiny.Printers.Network;
using Shiny.Printers.Protocols.EscPos;
using Shiny.Printing.Rendering;
using SkiaSharp;
using Native = Shiny.Printers;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The receipt printer bridge against a printer on a loopback socket: the bytes a receipt becomes, the guards on where the
/// page may connect, element validation, scanning through a fake mDNS scanner, rendering to PDF, and the event when the
/// printer goes away. Bluetooth printers need a radio and Shiny.BluetoothLE's platform builds; here, with none, they are 501.
/// </summary>
public class PrintersBridgeTests
{
    [Fact]
    public async Task Reports_what_can_be_used()
    {
        await using var fixture = await PrintersFixture.StartAsync(scanner: new FakeNetworkScanner());

        var status = await fixture.Client.GetStatusAsync();

        Assert.Null(status.Printer);
        Assert.False(status.BluetoothSupported);
        Assert.Null(status.BluetoothAccess);
        Assert.True(status.NetworkScanSupported);
        Assert.True(status.RenderSupported);
        Assert.False(status.Scanning);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "printers").IsSupported);
    }

    [Fact]
    public async Task Prints_a_receipt_as_esc_pos_to_a_network_printer()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);

        var connected = await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port));

        Assert.Equal($"127.0.0.1:{printer.Port}", connected.Id);
        Assert.Equal(PrinterTransport.Network, connected.Transport);
        Assert.Equal(PrinterLanguage.EscPos, connected.Language);
        Assert.Equal(48, connected.Capabilities.CharactersPerLine); // a network printer is assumed 80mm
        Assert.True(connected.Capabilities.SupportsCut);
        var reported = (await fixture.Client.GetStatusAsync()).Printer!;
        Assert.Equal(connected.Id, reported.Id);
        Assert.Equal(connected.Capabilities.SupportedBarcodes, reported.Capabilities.SupportedBarcodes);
        Assert.Contains(PrintBarcodeFormat.Code128, reported.Capabilities.SupportedBarcodes);

        var receipt = new PrintElements()
            .AlignCenter().Bold().Size(2).Line("SHINY MART").ResetStyle()
            .AlignLeft().Line("Coffee                     3.50")
            .Barcode(PrintBarcodeFormat.Code128, "ORDER-1", height: 60)
            .QrCode("https://example.com/r/1", moduleSize: 4, PrintQrCorrection.High)
            .Feed(2)
            .Cut(PrintCutMode.Full, 1);
        var result = await fixture.Client.PrintAsync(receipt.ToRequest());

        var expected = new EscPosProtocol().Encode(
            new PrintDocument()
                .AlignCenter().Bold().Size(2).Line("SHINY MART").ResetStyle()
                .AlignLeft().Line("Coffee                     3.50")
                .Barcode(Native.BarcodeFormat.Code128, "ORDER-1", height: 60)
                .QrCode("https://example.com/r/1", 4, Native.QrCorrectionLevel.High)
                .Feed(2)
                .Cut(Native.CutMode.Full, 1),
            Native.PrinterCapabilities.Paper80mm
        );
        Assert.Equal(expected.Length, result.Bytes);
        Assert.Equal(expected, await printer.ReceiveAsync(expected.Length));
    }

    [Fact]
    public async Task A_printer_without_a_cutter_feeds_instead()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port, Paper: PrinterPaper.Paper58mm));

        await fixture.Client.PrintAsync(new PrintElements().Line("x").Cut(feedBefore: 4).ToRequest());

        var expected = new EscPosProtocol().Encode(new PrintDocument().Line("x").Feed(4), Native.PrinterCapabilities.Paper58mm);
        Assert.Equal(expected, await printer.ReceiveAsync(expected.Length));
    }

    [Fact]
    public async Task Scales_an_image_down_to_the_printers_width()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port, Paper: PrinterPaper.Paper58mm));

        var result = await fixture.Client.PrintAsync(new PrintElements().Image(Png(800, 100), PrintDithering.FloydSteinberg).ToRequest());

        // GS v 0: raster bit image, then the width in bytes (xL xH) — 384 dots on 58mm paper are 48 bytes — and the height.
        var sent = await printer.ReceiveAsync(result.Bytes);
        var raster = IndexOf(sent, [0x1D, 0x76, 0x30]);
        Assert.True(raster >= 0, "no raster image in what was sent");
        Assert.Equal(384 / 8, sent[raster + 4] | sent[raster + 5] << 8);
        Assert.Equal(48, sent[raster + 6] | sent[raster + 7] << 8); // 100 * 384 / 800
    }

    [Theory]
    [InlineData("8.8.8.8", 9100)]        // not the local network
    [InlineData("printer.local", 9100)]  // a name could resolve anywhere
    [InlineData("192.168.1.50", 22)]     // not a printer port
    [InlineData(null, 9100)]
    public async Task Only_connects_to_a_printer_port_on_the_local_network(string? host, int port)
    {
        await using var fixture = await PrintersFixture.StartAsync();

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: host, Port: port)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("9100, 9101, 9102", refused.Message);
    }

    [Fact]
    public async Task A_second_connection_waits_for_the_first_to_be_dropped()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        var request = new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port);
        await fixture.Client.ConnectAsync(request);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(request));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("already_connected", refused.Code);

        await fixture.Client.DisconnectAsync();
        Assert.Null((await fixture.Client.GetStatusAsync()).Printer);
        await fixture.Client.ConnectAsync(request);
    }

    [Fact]
    public async Task Printing_needs_a_printer()
    {
        await using var fixture = await PrintersFixture.StartAsync();

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintElements().Line("x").ToRequest()));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_connected", refused.Code);
    }

    [Theory]
    [InlineData("""{ "elements": [] }""", "elements[0]: a receipt needs at least one element.")]
    [InlineData("""{ "elements": [{ "type": "Line" }, { "type": "Size", "width": 9 }] }""", "elements[1]: size width and height are 1 to 8.")]
    [InlineData("""{ "elements": [{ "type": "Barcode", "text": "1" }] }""", "elements[0]: barcode needs a barcode format.")]
    [InlineData("""{ "elements": [{ "type": "QrCode" }] }""", "elements[0]: qrCode needs text.")]
    [InlineData("""{ "elements": [{ "type": "Image", "data": "not base64!" }] }""", "elements[0]: image data is not base64.")]
    [InlineData("""{ "elements": [{ "type": "Image", "data": "AAAA" }] }""", "elements[0]: image data is not a PNG or JPEG.")]
    [InlineData("""{ "elements": [{ "type": "Align" }] }""", "elements[0]: align needs an align.")]
    public async Task Refuses_elements_it_cannot_print(string body, string message)
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port));

        using var response = await fixture.WebView.PostAsync("/_bridge/printers/print", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Refuses_a_barcode_the_printer_does_not_print()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        var caps = PrintersBridge.ToContract(Native.PrinterCapabilities.Paper80mm) with { SupportedBarcodes = [PrintBarcodeFormat.Code39] };
        await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port, Capabilities: caps));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintElements().Barcode(PrintBarcodeFormat.Ean13, "4006381333931").ToRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("elements[0]: this printer does not print Ean13 barcodes.", refused.Message);
    }

    [Fact]
    public async Task Connects_to_a_printer_a_scan_found_on_the_port_it_advertised()
    {
        await using var printer = FakeNetworkPrinter.Start();
        var scanner = new FakeNetworkScanner(
            new DiscoveredNetworkPrinter { Name = "Kitchen", Host = "127.0.0.1", Port = printer.Port, ServiceType = "_pdl-datastream._tcp" },
            new DiscoveredNetworkPrinter { Name = "Kitchen", Host = "127.0.0.1", Port = printer.Port, ServiceType = "_printer._tcp" }
        );

        // The port is not one the page may name — but the scan found the printer there.
        await using var fixture = await PrintersFixture.StartAsync(scanner);

        var found = Assert.Single(await fixture.Client.ScanAsync(new PrinterScanRequest(PrinterTransport.Network, ScanMs: 1_000)));
        Assert.Equal($"127.0.0.1:{printer.Port}", found.Id);
        Assert.Equal("Kitchen", found.Name);
        Assert.Equal(PrinterTransport.Network, found.Transport);

        var connected = await fixture.Client.ConnectAsync(new PrinterConnectRequest(Id: found.Id, Paper: PrinterPaper.Paper58mm));
        Assert.Equal("Kitchen", connected.Name);
        Assert.Equal(32, connected.Capabilities.CharactersPerLine);

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new PrinterConnectRequest(Id: "10.0.0.9:9100")));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Bluetooth_and_network_scanning_answer_501_where_there_is_none()
    {
        await using var fixture = await PrintersFixture.StartAsync();

        var ble = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ScanAsync(new PrinterScanRequest(PrinterTransport.Ble)));
        Assert.True(ble.IsNotSupported);

        var network = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ScanAsync(new PrinterScanRequest(PrinterTransport.Network)));
        Assert.True(network.IsNotSupported);

        var connect = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Ble, PeripheralUuid: "1234")));
        Assert.True(connect.IsNotSupported);
    }

    [Fact]
    public async Task Renders_a_receipt_to_a_pdf_without_a_printer()
    {
        await using var fixture = await PrintersFixture.StartAsync();

        var pdf = await fixture.Client.RenderAsync(new PrintRenderRequest(
            new PrintElements().AlignCenter().Bold().Line("INVOICE").ResetStyle().QrCode("x").Image(Png(1000, 50)).Cut().Elements,
            PageWidth: 612,
            PageHeight: 792
        ));

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task Rendering_answers_501_without_a_renderer()
    {
        await using var fixture = await PrintersFixture.StartAsync(renderer: false);

        Assert.False((await fixture.Client.GetStatusAsync()).RenderSupported);
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RenderAsync(new PrintRenderRequest(new PrintElements().Line("x").Elements)));
        Assert.True(refused.IsNotSupported);
    }

    [Fact]
    public async Task A_printer_that_goes_away_is_dropped_and_announced()
    {
        await using var printer = FakeNetworkPrinter.Start();
        await using var fixture = await PrintersFixture.StartAsync(ports: printer.Port);
        await fixture.Client.ConnectAsync(new PrinterConnectRequest(PrinterTransport.Network, Host: "127.0.0.1", Port: printer.Port));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "printers.disconnected", timeout.Token);

        await printer.ResetAsync();

        // A raw socket only learns the far end is gone when a write fails; the first may still land in the send buffer.
        BridgeException? failed = null;
        for (var i = 0; i < 20 && failed is null; i++)
        {
            try
            {
                await fixture.Client.PrintAsync(new PrintElements().Line(new string('x', 2_000)).ToRequest(), timeout.Token);
                await Task.Delay(50, timeout.Token);
            }
            catch (BridgeException ex)
            {
                failed = ex;
            }
        }

        Assert.NotNull(failed);
        Assert.Equal("printer_failed", failed.Code);

        var gone = JsonSerializer.Deserialize(await stream.NextAsync("printers.disconnected", timeout.Token), PrintersJsonContext.Default.PrinterDisconnected)!;
        Assert.Equal($"127.0.0.1:{printer.Port}", gone.Id);
        Assert.Equal(PrinterTransport.Network, gone.Transport);
        Assert.Null((await fixture.Client.GetStatusAsync()).Printer);
    }

    static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    sealed class PrintersFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public PrintersBridgeClient Client { get; private set; } = null!;

        public static async Task<PrintersFixture> StartAsync(INetworkPrinterScanner? scanner = null, bool renderer = true, int? ports = null)
        {
            var fixture = new PrintersFixture();
            var services = new ServiceCollection();
            if (scanner is not null)
                services.AddSingleton(scanner);
            if (renderer)
                services.AddPrintDocumentRendering();

            var options = new PrintersBridgeOptions();
            if (ports is { } port)
                options.NetworkPorts.Add(port);
            services.AddSingleton(options);

            var provider = services.BuildServiceProvider();
            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new PrintersBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new PrintersBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class FakeNetworkScanner(params DiscoveredNetworkPrinter[] printers) : INetworkPrinterScanner
    {
        // Like mDNS: everything it knows straight away, then nothing more until the browse is dropped.
        public IObservable<DiscoveredNetworkPrinter> Scan() => printers.ToObservable().Concat(Observable.Never<DiscoveredNetworkPrinter>());

        public IObservable<DiscoveredNetworkPrinter> Scan(string serviceType) => this.Scan();
    }

    /// <summary>A port-9100 printer: accepts one connection and keeps whatever it is sent.</summary>
    sealed class FakeNetworkPrinter : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly MemoryStream received = new();
        readonly Task<TcpClient> accepted;
        readonly Task reading;

        FakeNetworkPrinter()
        {
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            this.accepted = this.listener.AcceptTcpClientAsync();
            this.reading = Task.Run(this.ReadAsync);
        }

        public static FakeNetworkPrinter Start() => new();

        public int Port { get; }

        async Task ReadAsync()
        {
            using var client = await this.accepted;
            var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    lock (this.received)
                        this.received.Write(buffer, 0, read);
                }
            }
            catch (IOException)
            {
                // Reset by ResetAsync.
            }
        }

        public async Task<byte[]> ReceiveAsync(int length)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (this.received)
                {
                    if (this.received.Length >= length)
                        return this.received.ToArray();
                }
                await Task.Delay(10);
            }
            lock (this.received)
                throw new TimeoutException($"Received {this.received.Length} of {length} bytes.");
        }

        /// <summary>Drops the connection with a reset, as a printer switched off mid-shift does.</summary>
        public async Task ResetAsync()
        {
            var client = await this.accepted;
            client.Client.LingerState = new LingerOption(true, 0);
            client.Close();
            this.listener.Stop();
        }

        public async ValueTask DisposeAsync()
        {
            this.listener.Stop();
            if (this.accepted.IsCompletedSuccessfully)
                this.accepted.Result.Dispose();
            await Task.WhenAny(this.reading, Task.Delay(1_000));
        }
    }
}
