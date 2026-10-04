using System.Text.Json;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Printing.Client;
using Shiny.Net.HttpServer;
using Shiny.Printing;
using Contract = Shiny.AppDeviceBridge.Printing.Client;

namespace Shiny.AppDeviceBridge.Printing;

/// <summary>
/// <c>/_bridge/printing</c> over Shiny.Printing's <see cref="IPrintService"/> — the operating system's own print pipeline.
/// <code>
/// GET  /_bridge/printing/capabilities
/// GET  /_bridge/printing/printers
/// POST /_bridge/printing/print         { "content": "Pdf", "data": "JVBERi0…", "options": { "jobName": "Invoice 1042" } }
///                                      { "content": "Html", "html": "&lt;h1&gt;Hi&lt;/h1&gt;" }
///                                      { "content": "Url", "url": "https://example.com/invoice/1042" }
/// POST /_bridge/printing/html-to-pdf  { "html": "&lt;h1&gt;Invoice&lt;/h1&gt;", "pageWidth": 612, "pageHeight": 792 }   application/pdf
/// POST /_bridge/printing/print/pdf     the PDF as the body (application/pdf); ?jobName=&amp;silent=&amp;printerId=&amp;copies=&amp;orientation=&amp;duplex=&amp;color=
/// POST /_bridge/printing/print/image   a PNG or JPEG as the body; the same query
/// </code>
/// <para>
/// The raw routes stream the document to a temporary file, so its size costs disk rather than memory. The JSON route
/// holds the whole request, base64 and all, so it is capped at <see cref="PrintingBridgeOptions.MaxJsonBytes"/>.
/// </para>
/// </summary>
public sealed class PrintingBridge(IServiceProvider services) : IWebAppBridge
{
    const int MaxCopies = 99;
    const int MaxJobNameLength = 256;
    const int MaxPrinterIdLength = 1_024;

    // A spooler handed a file may still be reading it when the job is reported submitted (Windows' shell verb, CUPS).
    static readonly TimeSpan SubmittedFileLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan AbandonedFileAge = TimeSpan.FromHours(1);

    readonly IPrintService? print = services.GetOptionalService<IPrintService>();
    readonly PrintingBridgeOptions options = services.GetOptionalService<PrintingBridgeOptions>() ?? new();

    public string Name => "printing";

    public bool IsSupported => this.print is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/capabilities", this.CapabilitiesAsync)
        .MapGet("/printers", this.PrintersAsync)
        .MapPost("/print", this.PrintJsonAsync)
        .MapPost("/html-to-pdf", this.HtmlToPdfAsync)
        .MapPost("/print/pdf", ctx => this.PrintBodyAsync(ctx, PrintJobContent.Pdf))
        .MapPost("/print/image", ctx => this.PrintBodyAsync(ctx, PrintJobContent.Image));

    ValueTask CapabilitiesAsync(HttpContext context)
    {
        var c = this.print?.Capabilities ?? Shiny.Printing.PrintingCapabilities.None;
        return WebAppBridgeResults.Json(
            context,
            new Contract.PrintingCapabilities(
                this.print is not null,
                c.HasFlag(Shiny.Printing.PrintingCapabilities.Pdf),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.Image),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.Html),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.SystemDialog),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.Silent),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.EnumeratePrinters),
                c.HasFlag(Shiny.Printing.PrintingCapabilities.HtmlToPdf)
            ),
            PrintingJsonContext.Default.PrintingCapabilities
        );
    }

    async ValueTask PrintersAsync(HttpContext context)
    {
        if (this.print is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Printing");
            return;
        }

        IReadOnlyList<InstalledPrinter> printers = [.. (await p.GetPrinters(context.RequestAborted)).Select(x => new InstalledPrinter(x.Id, x.DisplayName, x.IsDefault))];
        await WebAppBridgeResults.Json(context, printers, PrintingJsonContext.Default.IReadOnlyListInstalledPrinter);
    }

    async ValueTask PrintJsonAsync(HttpContext context)
    {
        if (this.print is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Printing");
            return;
        }

        if (await ReadLimitedAsync(context, this.options.MaxJsonBytes) is not { } json)
        {
            await TooLarge(context, $"A JSON print request is at most {this.options.MaxJsonBytes:N0} bytes. Send a large PDF or image as the body of POST print/pdf or print/image.");
            return;
        }

        PrintJobRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(json, PrintingJsonContext.Default.PrintJobRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"content\": \"Pdf\", \"data\": \"<base64>\" }, { \"content\": \"Html\", \"html\": \"…\" } or { \"content\": \"Url\", \"url\": \"https://…\" }.");
            return;
        }

        if (await this.CheckAsync(context, p, body.Content, body.Options ?? new PrintJobOptions()) is not { } printOptions)
            return;

        PrintJob job;
        switch (body.Content)
        {
            case PrintJobContent.Pdf or PrintJobContent.Image:
                if (Decode(body.Data) is not { Length: > 0 } bytes)
                {
                    await WebAppBridgeResults.BadRequest(context, $"A {(body.Content == PrintJobContent.Pdf ? "PDF" : "image")} needs its bytes as base64 data.");
                    return;
                }
                job = body.Content == PrintJobContent.Pdf ? PrintJob.Pdf(bytes, printOptions) : PrintJob.Image(bytes, printOptions);
                break;

            case PrintJobContent.Html:
                if (String.IsNullOrEmpty(body.Html))
                {
                    await WebAppBridgeResults.BadRequest(context, "HTML needs html.");
                    return;
                }
                job = PrintJob.Html(body.Html, printOptions);
                break;

            default:
                // A page on the web, not the device: file: and the like stay out of reach.
                if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                {
                    await WebAppBridgeResults.BadRequest(context, "url must be an absolute http or https URL.");
                    return;
                }
                job = PrintJob.HtmlUrl(url, printOptions);
                break;
        }

        await Result(context, await p.Print(job, context.RequestAborted));
    }

    async ValueTask HtmlToPdfAsync(HttpContext context)
    {
        if (this.print is not { } p || !p.Capabilities.HasFlag(Shiny.Printing.PrintingCapabilities.HtmlToPdf))
        {
            await WebAppBridgeResults.NotSupported(context, "Turning HTML into a PDF");
            return;
        }

        if (await ReadLimitedAsync(context, this.options.MaxJsonBytes) is not { } json)
        {
            await TooLarge(context, $"An HTML to PDF request is at most {this.options.MaxJsonBytes:N0} bytes.");
            return;
        }

        HtmlToPdfRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(json, PrintingJsonContext.Default.HtmlToPdfRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null || String.IsNullOrEmpty(body.Html))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"html\": \"<html>…</html>\" }.");
            return;
        }

        var page = new PdfPageOptions();
        page = page with
        {
            PageWidth = body.PageWidth ?? page.PageWidth,
            PageHeight = body.PageHeight ?? page.PageHeight,
            Margin = body.Margin ?? page.Margin,
            Orientation = body.Orientation == PrintJobOrientation.Landscape ? PrintOrientation.Landscape : PrintOrientation.Portrait
        };

        if (page.PageWidth is < 72 or > 14_400 || page.PageHeight is < 72 or > 14_400 || page.Margin < 0 || page.Margin * 2 >= Math.Min(page.PageWidth, page.PageHeight))
        {
            await WebAppBridgeResults.BadRequest(context, "pageWidth and pageHeight are 72 to 14400 points, and margin leaves room on the page.");
            return;
        }

        byte[] pdf;
        try
        {
            pdf = await p.HtmlToPdf(body.Html, page, context.RequestAborted);
        }
        catch (PlatformNotSupportedException)
        {
            await WebAppBridgeResults.NotSupported(context, "Turning HTML into a PDF");
            return;
        }

        await context.Response.WriteBytesAsync(pdf, "application/pdf", context.RequestAborted);
    }

    async ValueTask PrintBodyAsync(HttpContext context, PrintJobContent content)
    {
        if (this.print is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Printing");
            return;
        }

        var query = context.Request.Query;
        if (!TryQuery(query, out var o, out var invalid))
        {
            await WebAppBridgeResults.BadRequest(context, invalid);
            return;
        }

        if (await this.CheckAsync(context, p, content, o) is not { } printOptions)
            return;

        if (context.Request.ContentLength > this.options.MaxFileBytes)
        {
            await TooLarge(context, $"A document is at most {this.options.MaxFileBytes:N0} bytes.");
            return;
        }

        var folder = TempFolder();
        var path = Path.Combine(folder, Guid.NewGuid().ToString("n"));
        PrintStatus? status = null;
        try
        {
            string? extension;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
            {
                var buffer = new byte[81_920];
                var head = new byte[8];
                var headLength = 0;
                long total = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    total += read;
                    if (total > this.options.MaxFileBytes)
                    {
                        await TooLarge(context, $"A document is at most {this.options.MaxFileBytes:N0} bytes.");
                        return;
                    }

                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, read);
                        buffer.AsSpan(0, take).CopyTo(head.AsSpan(headLength));
                        headLength += take;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }

                extension = Sniff(head.AsSpan(0, headLength), content);
            }

            if (extension is null)
            {
                await WebAppBridgeResults.BadRequest(context, content == PrintJobContent.Pdf
                    ? "The body is not a PDF."
                    : "The body is not a PNG or JPEG.");
                return;
            }

            // The platforms route a file by its extension.
            var named = path + extension;
            File.Move(path, named);
            path = named;

            var result = await p.Print(PrintJob.File(path, printOptions), context.RequestAborted);
            status = result.Status;
            await Result(context, result);
        }
        finally
        {
            if (status == PrintStatus.Submitted)
                _ = DeleteLaterAsync(path);
            else
                TryDelete(path);
        }
    }

    /// <summary>The checks every print shares: options in range, and content the platform prints. Null once answered.</summary>
    async ValueTask<PrintOptions?> CheckAsync(HttpContext context, IPrintService p, PrintJobContent content, PrintJobOptions o)
    {
        if (o.Copies is < 1 or > MaxCopies || o.JobName is { Length: > MaxJobNameLength } || o.PrinterId is { Length: > MaxPrinterIdLength })
        {
            await WebAppBridgeResults.BadRequest(context, $"copies is 1 to {MaxCopies}, jobName at most {MaxJobNameLength} characters and printerId at most {MaxPrinterIdLength}.");
            return null;
        }

        var (needs, what) = content switch
        {
            PrintJobContent.Pdf => (Shiny.Printing.PrintingCapabilities.Pdf, "Printing a PDF"),
            PrintJobContent.Image => (Shiny.Printing.PrintingCapabilities.Image, "Printing an image"),
            _ => (Shiny.Printing.PrintingCapabilities.Html, "Printing HTML")
        };
        if (!p.Capabilities.HasFlag(needs))
        {
            await WebAppBridgeResults.NotSupported(context, what);
            return null;
        }

        return new PrintOptions
        {
            JobName = o.JobName,
            PreferSilent = o.Silent,
            PrinterId = o.PrinterId,
            Copies = o.Copies ?? 1,
            Orientation = BridgeEnum.Convert<PrintJobOrientation, PrintOrientation>(o.Orientation),
            Duplex = BridgeEnum.Convert<PrintJobDuplex, PrintDuplex>(o.Duplex),
            Color = BridgeEnum.Convert<PrintJobColor, PrintColorMode>(o.Color)
        };
    }

    static ValueTask Result(HttpContext context, PrintResult result) => WebAppBridgeResults.Json(
        context,
        new PrintJobResult(BridgeEnum.Convert<PrintStatus, PrintJobStatus>(result.Status), result.PrinterId, result.Error),
        PrintingJsonContext.Default.PrintJobResult
    );

    static ValueTask TooLarge(HttpContext context, string message)
        => WebAppBridgeResults.Error(context, StatusCodes.Status413PayloadTooLarge, "too_large", message);

    /// <summary>The query of the raw routes, as the options the JSON route takes.</summary>
    static bool TryQuery(QueryCollection query, out PrintJobOptions options, out string invalid)
    {
        options = new PrintJobOptions();
        invalid = String.Empty;

        var copies = query["copies"].ToString();
        int? copiesValue = null;
        if (copies.Length > 0)
        {
            if (!Int32.TryParse(copies, out var c))
            {
                invalid = "copies must be a number.";
                return false;
            }
            copiesValue = c;
        }

        var silent = query["silent"].ToString();
        if (silent.Length > 0 && !Boolean.TryParse(silent, out _))
        {
            invalid = "silent must be true or false.";
            return false;
        }

        if (!TryEnum<PrintJobOrientation>(query["orientation"].ToString(), out var orientation)
            || !TryEnum<PrintJobDuplex>(query["duplex"].ToString(), out var duplex)
            || !TryEnum<PrintJobColor>(query["color"].ToString(), out var color))
        {
            invalid = "orientation, duplex or color is not one of its values.";
            return false;
        }

        options = new PrintJobOptions(
            NullIfEmpty(query["jobName"].ToString()),
            String.Equals(silent, "true", StringComparison.OrdinalIgnoreCase),
            NullIfEmpty(query["printerId"].ToString()),
            copiesValue,
            orientation,
            duplex,
            color
        );
        return true;
    }

    static bool TryEnum<T>(string value, out T result) where T : struct, Enum
    {
        result = default;
        return value.Length == 0 || (Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result));
    }

    static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>The extension the platforms route by, read from the first bytes rather than a content type that can say anything.</summary>
    static string? Sniff(ReadOnlySpan<byte> head, PrintJobContent content)
    {
        if (content == PrintJobContent.Pdf)
            return head.StartsWith("%PDF-"u8) ? ".pdf" : null;

        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ".png";

        return head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg" : null;
    }

    /// <summary>The request body, or null past <paramref name="limit"/> bytes.</summary>
    static async ValueTask<byte[]?> ReadLimitedAsync(HttpContext context, long limit)
    {
        if (context.Request.ContentLength > limit)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The bridge's own folder under the temp directory, cleared of files a crash or a quit left behind.</summary>
    static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "appdevicebridge-printing");
        Directory.CreateDirectory(folder);

        var cutoff = DateTime.UtcNow - AbandonedFileAge;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
                TryDelete(file);
        }
        return folder;
    }

    static async Task DeleteLaterAsync(string path)
    {
        await Task.Delay(SubmittedFileLifetime).ConfigureAwait(false);
        TryDelete(path);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still open in a spooler; the next upload's sweep takes it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static byte[]? Decode(string? base64)
    {
        if (String.IsNullOrEmpty(base64))
            return null;

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed class PrintingBridgeOptions
{
    /// <summary>
    /// The largest JSON <c>POST print</c> request, base64 data included: 16 MB by default. It is read whole, so a large
    /// document belongs in the body of <c>POST print/pdf</c> or <c>print/image</c> instead.
    /// </summary>
    public long MaxJsonBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// The largest document <c>POST print/pdf</c> and <c>print/image</c> take: 512 MB by default. It streams to a temporary
    /// file, so this costs disk, not memory. The server's own body limit applies first.
    /// </summary>
    public long MaxFileBytes { get; set; } = 512L * 1024 * 1024;

    internal void Validate()
    {
        if (this.MaxJsonBytes <= 0 || this.MaxFileBytes <= 0)
            throw new InvalidOperationException("PrintingBridgeOptions.MaxJsonBytes and MaxFileBytes must be positive.");
    }
}

public static class PrintingBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/printing</c> and registers the platform's print service — there is nothing else to call.
    /// <code>
    /// bridge.AddPrintingBridge(o => o.MaxFileBytes = 1024L * 1024 * 1024);
    /// </code>
    /// <para>
    /// iOS and Mac Catalyst print through AirPrint, Android through its print dialog (while the app is in the foreground),
    /// Windows through the spooler — a PDF through the registered PDF app — and Linux and macOS through CUPS (<c>lp</c>),
    /// which a sandboxed macOS app needs the <c>com.apple.security.print</c> entitlement for.
    /// </para>
    /// </summary>
    public static TBuilder AddPrintingBridge<TBuilder>(this TBuilder bridge, Action<PrintingBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(PrintingBridgeOptions))?.ImplementationInstance as PrintingBridgeOptions;
        if (options is null)
        {
            options = new PrintingBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        services.AddNativePrinting();
        bridge.AddBridge<PrintingBridge>();
        return bridge;
    }
}
