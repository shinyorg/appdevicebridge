using System.Net;
using System.Text;
using System.Text.Json;
using Shiny.AppDeviceBridge.Barcodes;
using Shiny.AppDeviceBridge.Barcodes.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Maui.Controls.Camera;
using Shiny.Maui.Controls.Camera.Barcode;
using Shiny.Maui.Controls.Camera.Media;
using Native = Shiny.Controls.Camera;
using NativeMode = Shiny.Maui.Controls.Camera.Barcode.BarcodeScanMode;

namespace Shiny.AppDeviceBridge.Tests;

// Inside the namespace: the controls' own types are otherwise found first, through the enclosing Shiny namespace.
using BarcodeFormat = Shiny.AppDeviceBridge.Barcodes.Client.BarcodeFormat;
using BarcodeScanMode = Shiny.AppDeviceBridge.Barcodes.Client.BarcodeScanMode;

/// <summary>
/// The barcode bridge with a fake scanner: everything between a request and the Shiny Controls modal scanner — the
/// refusals, one scan at a time, stopping, the events, and how a request becomes the scanner's options and the long-range
/// analyzer's settings. The scanner itself is platform UI, checked in the sample app.
/// </summary>
public class BarcodesBridgeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static readonly ScannedBarcode Upc = new("036000291452", BarcodeFormat.UpcA, new BarcodeRegion(0.4, 0.45, 0.2, 0.1));
    static readonly ScannedBarcode Qr = new("https://shinylib.net", BarcodeFormat.QrCode, new BarcodeRegion(0.1, 0.1, 0.3, 0.3));

    [Fact]
    public async Task Answers_with_the_codes_read_and_why_the_scanner_closed()
    {
        var scanner = new FakeScanner { Reads = [Upc], Ends = BarcodeScanEndReason.MaxResults };
        await using var fixture = await BarcodesFixture.StartAsync(scanner);

        var request = new BarcodeScanRequest(Formats: [BarcodeFormat.UpcA, BarcodeFormat.Ean13], Mode: BarcodeScanMode.LongRange, AutoZoom: true, MaxAutoZoom: 6);
        var result = await fixture.Client.ScanAsync(request);

        Assert.Equal(BarcodeScanEndReason.MaxResults, result.Reason);
        Assert.Equal([Upc], result.Barcodes);

        // The request reaches the scanner as the page sent it.
        var received = Assert.Single(scanner.Requests);
        Assert.Equal(BarcodeScanMode.LongRange, received.Mode);
        Assert.True(received.AutoZoom);
        Assert.Equal(6, received.MaxAutoZoom);
        Assert.Equal([BarcodeFormat.UpcA, BarcodeFormat.Ean13], received.Formats);
    }

    [Fact]
    public async Task Raises_each_code_as_it_is_read()
    {
        var scanner = new FakeScanner { Reads = [Upc, Qr], Ends = BarcodeScanEndReason.Done };
        await using var fixture = await BarcodesFixture.StartAsync(scanner);
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        var result = await fixture.Client.ScanAsync(new BarcodeScanRequest(MaxResults: 0));

        Assert.Equal(BarcodeScanEndReason.Done, result.Reason);
        Assert.Equal(Upc, await NextDetected(stream, timeout.Token));
        Assert.Equal(Qr, await NextDetected(stream, timeout.Token));
    }

    [Fact]
    public async Task A_refused_permission_is_a_result_not_an_error()
    {
        await using var fixture = await BarcodesFixture.StartAsync(new FakeScanner { Ends = BarcodeScanEndReason.PermissionDenied });

        var result = await fixture.Client.ScanAsync(new BarcodeScanRequest());

        Assert.Equal(BarcodeScanEndReason.PermissionDenied, result.Reason);
        Assert.Empty(result.Barcodes);
    }

    [Fact]
    public async Task Runs_one_scan_at_a_time_and_stops_it_on_request()
    {
        var scanner = new FakeScanner { Reads = [Upc], HoldOpen = true, Ends = BarcodeScanEndReason.Dismissed };
        await using var fixture = await BarcodesFixture.StartAsync(scanner);

        var scan = fixture.Client.ScanAsync(new BarcodeScanRequest(MaxResults: 0));
        await scanner.Opened.Task.WaitAsync(Wait);
        Assert.True((await fixture.Client.GetStatusAsync()).Scanning);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ScanAsync(new BarcodeScanRequest()));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("scan_in_progress", refused.Code);

        await fixture.Client.StopScanAsync();
        var result = await scan.WaitAsync(Wait);

        // What it read before it was stopped, and that the page stopped it — whatever the scanner itself reported.
        Assert.Equal(BarcodeScanEndReason.Stopped, result.Reason);
        Assert.Equal([Upc], result.Barcodes);
        Assert.False((await fixture.Client.GetStatusAsync()).Scanning);

        // and the next scan is free to run
        scanner.HoldOpen = false;
        Assert.Equal(BarcodeScanEndReason.Dismissed, (await fixture.Client.ScanAsync(new BarcodeScanRequest())).Reason);
    }

    [Fact]
    public async Task Stopping_with_no_scan_running_does_nothing()
    {
        await using var fixture = await BarcodesFixture.StartAsync(new FakeScanner());

        await fixture.Client.StopScanAsync();

        Assert.False((await fixture.Client.GetStatusAsync()).Scanning);
    }

    [Theory]
    [InlineData("{ \"formats\": [ \"Unknown\" ] }")]
    [InlineData("{ \"formats\": [ \"Barcode39\" ] }")]
    [InlineData("{ \"mode\": \"Telescope\" }")]
    [InlineData("{ \"maxResults\": -1 }")]
    [InlineData("{ \"maxResults\": 1001 }")]
    [InlineData("{ \"timeoutSeconds\": 0 }")]
    [InlineData("{ \"timeoutSeconds\": 3601 }")]
    [InlineData("{ \"maxAutoZoom\": 0.5 }")]
    [InlineData("{ \"maxAutoZoom\": 11 }")]
    [InlineData("{ \"fullFrameInterval\": -1 }")]
    [InlineData("{ \"fullFrameInterval\": 31 }")]
    [InlineData("{ \"maxNativeCropsPerFrame\": 9 }")]
    [InlineData("{ \"uncheckedConfirmations\": 0 }")]
    [InlineData("{ \"scanWindow\": { \"x\": 0.5, \"y\": 0, \"width\": 0.6, \"height\": 1 } }")]
    [InlineData("{ \"scanWindow\": { \"x\": 0, \"y\": 0, \"width\": 0, \"height\": 1 } }")]
    [InlineData("not json")]
    public async Task Refuses_a_request_out_of_range_without_opening_the_scanner(string body)
    {
        var scanner = new FakeScanner();
        await using var fixture = await BarcodesFixture.StartAsync(scanner);

        var response = await fixture.WebView.PostAsync("/_bridge/barcodes/scan", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(scanner.Requests);
    }

    [Fact]
    public async Task Refuses_a_title_longer_than_256_characters()
    {
        var scanner = new FakeScanner();
        await using var fixture = await BarcodesFixture.StartAsync(scanner);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ScanAsync(new BarcodeScanRequest(Title: new string('a', 257))));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(scanner.Requests);
    }

    [Fact]
    public async Task Answers_501_where_there_is_no_scanner()
    {
        await using var fixture = await BarcodesFixture.StartAsync(new FakeScanner { IsSupported = false });

        Assert.False((await fixture.Client.GetStatusAsync()).Supported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ScanAsync(new BarcodeScanRequest()))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "barcodes").IsSupported);
    }

    [Fact]
    public void The_linux_head_has_no_scanner()
        // This test host is the net10.0 build, which is the one the maui-labs Linux head runs.
        => Assert.False(new MediaBarcodeScanSource(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()).IsSupported);

    [Fact]
    public void A_one_code_scan_closes_on_the_first_read_with_no_done_button()
    {
        var options = MediaBarcodeScanSource.ToOptions(new BarcodeScanRequest(), _ => { });

        Assert.Equal(NativeMode.Standard, options.Mode);
        Assert.False(options.AutoZoom);
        Assert.Equal(1, options.MaxResults);
        Assert.False(options.ShowDoneButton);
        Assert.False(options.ShowResultCount);
        Assert.Null(options.Timeout);
        Assert.Null(options.ScanWindow);
    }

    [Fact]
    public void A_long_range_request_becomes_the_scanners_options()
    {
        MediaScanEndReason? ended = null;
        var options = MediaBarcodeScanSource.ToOptions(
            new BarcodeScanRequest(
                Mode: BarcodeScanMode.LongRange,
                AutoZoom: true,
                MaxResults: 0,
                FilterDuplicates: false,
                TimeoutSeconds: 30,
                ScanWindow: new BarcodeRegion(0.1, 0.4, 0.8, 0.2),
                TorchOn: true,
                Title: "Aisle 4",
                Instructions: "Point at the shelf labels"
            ),
            r => ended = r
        );

        Assert.Equal(NativeMode.LongRange, options.Mode);
        Assert.True(options.AutoZoom);
        Assert.Null(options.MaxResults);
        Assert.True(options.ShowDoneButton);
        Assert.True(options.ShowResultCount);
        Assert.False(options.FilterDuplicates);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(new Microsoft.Maui.Graphics.RectF(0.1f, 0.4f, 0.8f, 0.2f), options.ScanWindow);
        Assert.True(options.IsTorchOn);
        Assert.Equal("Aisle 4", options.Title);
        Assert.Equal("Point at the shelf labels", options.Instructions);

        options.OnEnded!(MediaScanEndReason.Timeout);
        Assert.Equal(MediaScanEndReason.Timeout, ended);
    }

    [Fact]
    public void Tunes_the_long_range_analyzer_on_the_scanners_camera()
    {
        var analyzer = new BarcodeAnalyzer();
        var options = MediaBarcodeScanSource.ToOptions(
            new BarcodeScanRequest(Mode: BarcodeScanMode.LongRange, MaxAutoZoom: 6, FullFrameInterval: 0, MaxNativeCropsPerFrame: 4, UncheckedConfirmations: 3),
            _ => { }
        );

        options.ConfigureCamera!(new CameraView { Analyzer = analyzer });

        Assert.Equal(6, analyzer.MaxAutoZoom);
        Assert.Equal(0, analyzer.FullFrameInterval);
        Assert.Equal(4, analyzer.MaxNativeCropsPerFrame);
        Assert.Equal(3, analyzer.UncheckedConfirmations);
    }

    [Fact]
    public void Leaves_the_analyzers_defaults_for_settings_not_named()
    {
        var analyzer = new BarcodeAnalyzer();
        var defaults = (analyzer.MaxAutoZoom, analyzer.FullFrameInterval, analyzer.MaxNativeCropsPerFrame, analyzer.UncheckedConfirmations);

        MediaBarcodeScanSource.Configure(analyzer, new BarcodeScanRequest(Mode: BarcodeScanMode.LongRange));

        Assert.Equal(defaults, (analyzer.MaxAutoZoom, analyzer.FullFrameInterval, analyzer.MaxNativeCropsPerFrame, analyzer.UncheckedConfirmations));
    }

    [Fact]
    public void Maps_formats_both_ways_by_name()
    {
        // every contract symbology the page can ask for is one the scanner knows
        var named = Enum.GetValues<BarcodeFormat>().Where(x => x != BarcodeFormat.Unknown).ToList();
        var native = MediaBarcodeScanSource.ToNative(named)!;
        Assert.Equal(named.Select(x => x.ToString()), native.Select(x => x.ToString()));

        Assert.Null(MediaBarcodeScanSource.ToNative(null));
        Assert.Null(MediaBarcodeScanSource.ToNative([]));

        var read = MediaBarcodeScanSource.ToContract(new Native.DetectedBarcode("5012345678900", Native.BarcodeFormat.Ean13, new Microsoft.Maui.Graphics.RectF(0.25f, 0.5f, 0.5f, 0.125f)));
        Assert.Equal(new ScannedBarcode("5012345678900", BarcodeFormat.Ean13, new BarcodeRegion(0.25, 0.5, 0.5, 0.125)), read);

        Assert.Equal(BarcodeFormat.Unknown, MediaBarcodeScanSource.ToContract(new Native.DetectedBarcode("?", Native.BarcodeFormat.Unknown, default)).Format);
    }

    [Theory]
    [InlineData(MediaScanEndReason.MaxResults, BarcodeScanEndReason.MaxResults)]
    [InlineData(MediaScanEndReason.Done, BarcodeScanEndReason.Done)]
    [InlineData(MediaScanEndReason.Dismissed, BarcodeScanEndReason.Dismissed)]
    [InlineData(MediaScanEndReason.Timeout, BarcodeScanEndReason.Timeout)]
    [InlineData(MediaScanEndReason.Cancelled, BarcodeScanEndReason.Stopped)]
    [InlineData(MediaScanEndReason.Stopped, BarcodeScanEndReason.Stopped)]
    [InlineData(MediaScanEndReason.PermissionDenied, BarcodeScanEndReason.PermissionDenied)]
    [InlineData(MediaScanEndReason.CameraUnavailable, BarcodeScanEndReason.CameraUnavailable)]
    public void Maps_why_the_scanner_closed(MediaScanEndReason native, BarcodeScanEndReason expected)
        => Assert.Equal(expected, MediaBarcodeScanSource.ToContract(native));


    static async Task<ScannedBarcode> NextDetected(TestEventStream stream, CancellationToken ct)
        => JsonSerializer.Deserialize(await stream.NextAsync(BarcodesBridge.DetectedEvent, ct), BarcodesJsonContext.Default.ScannedBarcode)!;

    static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();

            await Task.Delay(20);
        }
    }


    sealed class BarcodesFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public BarcodesBridge Bridge { get; private set; } = null!;
        public WebAppEventHub Events { get; } = new();
        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public BarcodesBridgeClient Client { get; private set; } = null!;

        public async Task<TestEventStream> ListenAsync(CancellationToken ct)
        {
            var stream = await TestEventStream.OpenAsync(this.WebView, BarcodesBridge.DetectedEvent, ct);
            await WaitUntil(() => this.Bridge.DetectedListeners > 0);
            return stream;
        }

        public static async Task<BarcodesFixture> StartAsync(IBarcodeScanSource scanner)
        {
            var fixture = new BarcodesFixture { Bridge = new BarcodesBridge(scanner) };
            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(_ => [fixture.Bridge], fixture.Events, client => fixture.WebView = client);
            fixture.Client = new BarcodesBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }


    /// <summary>Reads <see cref="Reads"/>, then closes with <see cref="Ends"/> — straight away, or when cancelled if <see cref="HoldOpen"/>.</summary>
    sealed class FakeScanner : IBarcodeScanSource
    {
        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<ScannedBarcode> Reads { get; init; } = [];

        public BarcodeScanEndReason Ends { get; init; } = BarcodeScanEndReason.MaxResults;

        public bool HoldOpen { get; set; }

        public List<BarcodeScanRequest> Requests { get; } = [];

        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BarcodeScanEndReason> ScanAsync(BarcodeScanRequest request, Action<ScannedBarcode> detected, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            foreach (var read in this.Reads)
                detected(read);

            this.Opened.TrySetResult();
            if (this.HoldOpen)
            {
                // like the real scanner, closing on cancellation ends the scan rather than throwing
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
            return this.Ends;
        }
    }
}
