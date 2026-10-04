using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Printing;
using Shiny.AppDeviceBridge.Printing.Client;
using Native = Shiny.Printing;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The printing bridge over a fake <see cref="Native.IPrintService"/>: capabilities, the job the page's request becomes, the
/// result it answers with, and what is refused. AirPrint, PrintManager, the Windows spooler and CUPS are Shiny.Printing's.
/// </summary>
public class PrintingBridgeTests
{
    static readonly string Pdf = Convert.ToBase64String("%PDF-1.7\n%%EOF"u8.ToArray());

    [Fact]
    public async Task Answers_501_without_a_print_service()
    {
        await using var fixture = await PrintingFixture.StartAsync(service: null);

        var capabilities = await fixture.Client.GetCapabilitiesAsync();
        Assert.False(capabilities.Supported);
        Assert.False(capabilities.Pdf);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetPrintersAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintJobRequest(PrintJobContent.Pdf, Pdf)))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "printing").IsSupported);
    }

    [Fact]
    public async Task Reports_the_platforms_capabilities()
    {
        var service = new FakePrintService
        {
            Capabilities = Native.PrintingCapabilities.Pdf | Native.PrintingCapabilities.Image | Native.PrintingCapabilities.Silent | Native.PrintingCapabilities.EnumeratePrinters
        };
        await using var fixture = await PrintingFixture.StartAsync(service);

        Assert.Equal(new PrintingCapabilities(true, true, true, false, false, true, true, false), await fixture.Client.GetCapabilitiesAsync());
    }

    [Fact]
    public async Task Lists_installed_printers()
    {
        var service = new FakePrintService { Printers = [new("hp-1", "HP LaserJet", true), new("office", "Office", false)] };
        await using var fixture = await PrintingFixture.StartAsync(service);

        var printers = await fixture.Client.GetPrintersAsync();

        Assert.Equal([new InstalledPrinter("hp-1", "HP LaserJet", true), new InstalledPrinter("office", "Office", false)], printers);
    }

    [Fact]
    public async Task Prints_a_pdf_with_its_options()
    {
        var service = new FakePrintService { Result = new(Native.PrintStatus.Submitted, "hp-1") };
        await using var fixture = await PrintingFixture.StartAsync(service);

        var result = await fixture.Client.PrintAsync(new PrintJobRequest(
            PrintJobContent.Pdf,
            Pdf,
            Options: new PrintJobOptions("Invoice 1042", Silent: true, PrinterId: "hp-1", Copies: 2, PrintJobOrientation.Landscape, PrintJobDuplex.TwoSidedLongEdge, PrintJobColor.Monochrome)
        ));

        Assert.Equal(new PrintJobResult(PrintJobStatus.Submitted, "hp-1", null), result);

        var job = Assert.Single(service.Jobs);
        Assert.Equal(Native.PrintContentKind.Pdf, job.Kind);
        Assert.Equal(
            new Native.PrintOptions
            {
                JobName = "Invoice 1042",
                PreferSilent = true,
                PrinterId = "hp-1",
                Copies = 2,
                Orientation = Native.PrintOrientation.Landscape,
                Duplex = Native.PrintDuplex.TwoSidedLongEdge,
                Color = Native.PrintColorMode.Monochrome
            },
            job.Options
        );
    }

    [Theory]
    [InlineData(PrintJobContent.Image, Native.PrintContentKind.Image)]
    [InlineData(PrintJobContent.Html, Native.PrintContentKind.Html)]
    [InlineData(PrintJobContent.Url, Native.PrintContentKind.HtmlUrl)]
    public async Task Prints_each_kind_of_content(PrintJobContent content, Native.PrintContentKind kind)
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        await fixture.Client.PrintAsync(new PrintJobRequest(content, Data: Pdf, Html: "<h1>Hi</h1>", Url: "https://example.com/invoice"));

        Assert.Equal(kind, Assert.Single(service.Jobs).Kind);
        Assert.Equal(new Native.PrintOptions(), service.Jobs[0].Options);
    }

    [Fact]
    public async Task A_cancelled_dialog_is_a_result()
    {
        var service = new FakePrintService { Result = new(Native.PrintStatus.Cancelled) };
        await using var fixture = await PrintingFixture.StartAsync(service);

        var result = await fixture.Client.PrintAsync(new PrintJobRequest(PrintJobContent.Pdf, Pdf));

        Assert.Equal(PrintJobStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Content_the_platform_cannot_print_answers_501()
    {
        // Windows and CUPS: no HTML.
        var service = new FakePrintService { Capabilities = Native.PrintingCapabilities.Pdf | Native.PrintingCapabilities.Image };
        await using var fixture = await PrintingFixture.StartAsync(service);

        var html = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintJobRequest(PrintJobContent.Html, Html: "<p>x</p>")));
        var url = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintJobRequest(PrintJobContent.Url, Url: "https://example.com")));

        Assert.True(html.IsNotSupported);
        Assert.True(url.IsNotSupported);
        Assert.Empty(service.Jobs);
    }

    [Theory]
    [InlineData("""{ "content": "Pdf" }""")]
    [InlineData("""{ "content": "Pdf", "data": "not base64!" }""")]
    [InlineData("""{ "content": "Image", "data": "" }""")]
    [InlineData("""{ "content": "Html" }""")]
    [InlineData("""{ "content": "Url", "url": "file:///etc/hosts" }""")]
    [InlineData("""{ "content": "Url", "url": "invoice.html" }""")]
    [InlineData("""{ "content": "Pdf", "data": "JVBERg==", "options": { "copies": 0 } }""")]
    [InlineData("""{ "content": "Pdf", "data": "JVBERg==", "options": { "copies": 100 } }""")]
    [InlineData("not json")]
    public async Task Refuses_jobs_it_cannot_read(string body)
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        using var response = await fixture.WebView.PostAsync("/_bridge/printing/print", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(service.Jobs);
    }

    [Fact]
    public async Task Streams_a_pdf_body_to_a_file_and_prints_it()
    {
        var service = new FakePrintService { Result = new(Native.PrintStatus.Completed, "hp-1") };
        await using var fixture = await PrintingFixture.StartAsync(service);
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 300_000) + "\n%%EOF");

        var result = await fixture.Client.PrintPdfAsync(
            new MemoryStream(pdf),
            jobName: "Invoice 1042",
            silent: true,
            printerId: "hp-1",
            copies: 3,
            orientation: PrintJobOrientation.Landscape,
            duplex: PrintJobDuplex.TwoSidedShortEdge,
            color: PrintJobColor.Color
        );

        Assert.Equal(new PrintJobResult(PrintJobStatus.Completed, "hp-1", null), result);
        var job = Assert.Single(service.Jobs);
        Assert.Equal(Native.PrintContentKind.File, job.Kind);
        Assert.Equal(
            new Native.PrintOptions
            {
                JobName = "Invoice 1042",
                PreferSilent = true,
                PrinterId = "hp-1",
                Copies = 3,
                Orientation = Native.PrintOrientation.Landscape,
                Duplex = Native.PrintDuplex.TwoSidedShortEdge,
                Color = Native.PrintColorMode.Color
            },
            job.Options
        );

        // What the print service was handed: the whole body, under the extension the platforms route by.
        var (name, bytes) = Assert.Single(service.Files);
        Assert.EndsWith(".pdf", name);
        Assert.Equal(pdf, bytes);
        Assert.False(File.Exists(name)); // a completed job's file is gone
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, ".jpg")]
    public async Task Names_an_image_body_by_its_bytes(byte[] image, string extension)
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        await fixture.Client.PrintImageAsync(new MemoryStream(image));

        Assert.EndsWith(extension, Assert.Single(service.Files).Name);
        Assert.Equal(new Native.PrintOptions(), service.Jobs[0].Options);
    }

    [Theory]
    [InlineData("pdf", "not a pdf")]
    [InlineData("image", "GIF89a....")]
    [InlineData("pdf", "")]
    public async Task Refuses_a_body_that_is_not_what_the_route_prints(string route, string body)
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        using var response = await fixture.WebView.PostAsync($"/_bridge/printing/print/{route}", new ByteArrayContent(Encoding.ASCII.GetBytes(body)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(service.Jobs);
    }

    [Theory]
    [InlineData("copies=0")]
    [InlineData("copies=two")]
    [InlineData("silent=maybe")]
    [InlineData("orientation=Sideways")]
    public async Task Refuses_a_query_it_cannot_read(string query)
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        using var response = await fixture.WebView.PostAsync($"/_bridge/printing/print/pdf?{query}", new ByteArrayContent("%PDF-1.7"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(service.Jobs);
    }

    [Fact]
    public async Task Holds_each_route_to_its_size_limit()
    {
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service, new PrintingBridgeOptions { MaxJsonBytes = 1_024, MaxFileBytes = 2_048 });

        var json = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintAsync(new PrintJobRequest(PrintJobContent.Pdf, Convert.ToBase64String(new byte[2_000]))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, json.StatusCode);
        Assert.Equal("too_large", json.Code);

        var file = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintPdfAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-" + new string('x', 3_000)))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, file.StatusCode);

        // A body sent without a length is counted as it arrives.
        var chunked = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-" + new string('x', 3_000))));
        chunked.Headers.ContentLength = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_bridge/printing/print/pdf") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        using var response = await fixture.WebView.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        Assert.Empty(service.Jobs);
        await fixture.Client.PrintPdfAsync(new MemoryStream("%PDF-1.7"u8.ToArray()));
        Assert.Single(service.Jobs);
    }

    [Fact]
    public async Task Streaming_answers_501_without_a_print_service_or_for_content_the_platform_cannot_print()
    {
        await using (var none = await PrintingFixture.StartAsync(service: null))
            Assert.True((await Assert.ThrowsAsync<BridgeException>(() => none.Client.PrintPdfAsync(new MemoryStream("%PDF-1.7"u8.ToArray())))).IsNotSupported);

        var pdfOnly = new FakePrintService { Capabilities = Native.PrintingCapabilities.Pdf };
        await using var fixture = await PrintingFixture.StartAsync(pdfOnly);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PrintImageAsync(new MemoryStream([0xFF, 0xD8, 0xFF])))).IsNotSupported);
    }

    [Fact]
    public async Task Turns_html_into_a_pdf_on_the_page_asked_for()
    {
        var service = new FakePrintService { Capabilities = Native.PrintingCapabilities.Pdf | Native.PrintingCapabilities.HtmlToPdf };
        await using var fixture = await PrintingFixture.StartAsync(service);

        Assert.True((await fixture.Client.GetCapabilitiesAsync()).HtmlToPdf);
        var pdf = await fixture.Client.HtmlToPdfAsync(new HtmlToPdfRequest("<h1>Invoice</h1>", 612, 792, PrintJobOrientation.Landscape, 18));

        Assert.Equal(FakePrintService.Pdf, pdf);
        var (html, page) = Assert.Single(service.Conversions);
        Assert.Equal("<h1>Invoice</h1>", html);
        Assert.Equal(new Native.PdfPageOptions { PageWidth = 612, PageHeight = 792, Orientation = Native.PrintOrientation.Landscape, Margin = 18 }, page);
        Assert.Empty(service.Jobs); // nothing printed
    }

    [Fact]
    public async Task Html_to_pdf_defaults_to_an_a4_portrait_page()
    {
        var service = new FakePrintService { Capabilities = Native.PrintingCapabilities.HtmlToPdf };
        await using var fixture = await PrintingFixture.StartAsync(service);

        await fixture.Client.HtmlToPdfAsync(new HtmlToPdfRequest("<p>x</p>"));

        Assert.Equal(Native.PdfPageOptions.A4, Assert.Single(service.Conversions).Page);
    }

    [Fact]
    public async Task Html_to_pdf_answers_501_where_the_platform_cannot()
    {
        // Windows and CUPS: Capabilities says no, and the service would throw PlatformNotSupportedException.
        var service = new FakePrintService();
        await using var fixture = await PrintingFixture.StartAsync(service);

        Assert.False((await fixture.Client.GetCapabilitiesAsync()).HtmlToPdf);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.HtmlToPdfAsync(new HtmlToPdfRequest("<p>x</p>")))).IsNotSupported);
        Assert.Empty(service.Conversions);

        await using var none = await PrintingFixture.StartAsync(service: null);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => none.Client.HtmlToPdfAsync(new HtmlToPdfRequest("<p>x</p>")))).IsNotSupported);
    }

    [Theory]
    [InlineData("""{ "html": "" }""")]
    [InlineData("""{ "pageWidth": 612 }""")]
    [InlineData("""{ "html": "<p>x</p>", "pageWidth": 10 }""")]
    [InlineData("""{ "html": "<p>x</p>", "margin": 400 }""")]
    [InlineData("not json")]
    public async Task Refuses_an_html_to_pdf_request_it_cannot_lay_out(string body)
    {
        var service = new FakePrintService { Capabilities = Native.PrintingCapabilities.HtmlToPdf };
        await using var fixture = await PrintingFixture.StartAsync(service);

        using var response = await fixture.WebView.PostAsync("/_bridge/printing/html-to-pdf", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(service.Conversions);
    }

    [Fact]
    public async Task Html_to_pdf_is_held_to_the_json_limit()
    {
        var service = new FakePrintService { Capabilities = Native.PrintingCapabilities.HtmlToPdf };
        await using var fixture = await PrintingFixture.StartAsync(service, new PrintingBridgeOptions { MaxJsonBytes = 1_024 });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.HtmlToPdfAsync(new HtmlToPdfRequest(new string('x', 2_000))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Empty(service.Conversions);
    }

    sealed class PrintingFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public PrintingBridgeClient Client { get; private set; } = null!;

        public static async Task<PrintingFixture> StartAsync(Native.IPrintService? service, PrintingBridgeOptions? options = null)
        {
            var fixture = new PrintingFixture();
            var services = new ServiceCollection();
            if (service is not null)
                services.AddSingleton(service);
            if (options is not null)
                services.AddSingleton(options);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new PrintingBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new PrintingBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class FakePrintService : Native.IPrintService
    {
        public Native.PrintingCapabilities Capabilities { get; init; } =
            Native.PrintingCapabilities.Pdf | Native.PrintingCapabilities.Image | Native.PrintingCapabilities.Html | Native.PrintingCapabilities.SystemDialog;

        public IReadOnlyList<Native.PrinterInfo> Printers { get; init; } = [];
        public Native.PrintResult Result { get; init; } = new(Native.PrintStatus.Completed);
        public List<Native.PrintJob> Jobs { get; } = [];

        /// <summary>The file a streamed job was handed, read while it still exists.</summary>
        public List<(string Name, byte[] Bytes)> Files { get; } = [];

        public Task<Native.PrintResult> Print(Native.PrintJob job, CancellationToken cancellationToken = default)
        {
            this.Jobs.Add(job);
            if (job.Kind == Native.PrintContentKind.File)
            {
                var folder = Path.Combine(Path.GetTempPath(), "appdevicebridge-printing");
                var newest = new DirectoryInfo(folder).GetFiles().Where(x => x.Extension != "").OrderByDescending(x => x.LastWriteTimeUtc).First();
                this.Files.Add((newest.FullName, File.ReadAllBytes(newest.FullName)));
            }
            return Task.FromResult(this.Result);
        }

        public Task<IReadOnlyList<Native.PrinterInfo>> GetPrinters(CancellationToken cancellationToken = default)
            => Task.FromResult(this.Printers);

        public static readonly byte[] Pdf = "%PDF-1.7\n%%EOF"u8.ToArray();

        public List<(string Html, Native.PdfPageOptions Page)> Conversions { get; } = [];

        public Task<byte[]> HtmlToPdf(string html, Native.PdfPageOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!this.Capabilities.HasFlag(Native.PrintingCapabilities.HtmlToPdf))
                throw new PlatformNotSupportedException();

            this.Conversions.Add((html, options ?? Native.PdfPageOptions.A4));
            return Task.FromResult(Pdf);
        }
    }
}
