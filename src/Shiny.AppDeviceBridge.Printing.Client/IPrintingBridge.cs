using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Printing.Client;

/// <summary>
/// The operating system's own printing — AirPrint on iOS and Mac Catalyst, the print dialog on Android, the spooler on
/// Windows, CUPS on Linux and macOS — for any printer it knows. A receipt printer needs the printers bridge instead.
/// </summary>
[BridgeClient("printing", typeof(PrintingJsonContext))]
public interface IPrintingBridge
{
    /// <summary>What printing can do here.</summary>
    [BridgeGet("capabilities")]
    Task<PrintingCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>The installed printers; empty where they cannot be listed.</summary>
    [BridgeGet("printers")]
    Task<IReadOnlyList<InstalledPrinter>> GetPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints a document, through the print dialog unless the job is silent and the platform allows it. Answers once the
    /// dialog closes or the job is spooled; a cancelled dialog is a result, not an error. Fails with 501 for content the
    /// platform cannot print — HTML on Windows and CUPS, for one: render it to a PDF first.
    /// </summary>
    [BridgePost("print")]
    Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lays an HTML document out on pages with the engine printing uses and returns it as a PDF, with no print UI — to save,
    /// share or attach a document. iOS, Mac Catalyst and Android; 501 elsewhere, where
    /// <see cref="PrintingCapabilities.HtmlToPdf"/> is false. Capped at the JSON limit (16 MB by default).
    /// </summary>
    [BridgePost("html-to-pdf")]
    Task<byte[]> HtmlToPdfAsync(HtmlToPdfRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints a PDF sent as the raw body, streamed to a temporary file on the device rather than held in memory — the
    /// route for a document of any size. Options travel in the query. Fails with 400 when the body is not a PDF, and with
    /// 413 past the bridge's limit (512 MB by default).
    /// </summary>
    [BridgePost("print/pdf")]
    Task<PrintJobResult> PrintPdfAsync(
        [BridgeBody("application/pdf")] Stream pdf,
        string? jobName = null,
        bool silent = false,
        string? printerId = null,
        int? copies = null,
        PrintJobOrientation orientation = PrintJobOrientation.Auto,
        PrintJobDuplex duplex = PrintJobDuplex.Default,
        PrintJobColor color = PrintJobColor.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Prints a PNG or JPEG sent as the raw body, streamed to a temporary file like <see cref="PrintPdfAsync"/>. The format
    /// is read from the bytes.
    /// </summary>
    [BridgePost("print/image")]
    Task<PrintJobResult> PrintImageAsync(
        [BridgeBody("application/octet-stream")] Stream image,
        string? jobName = null,
        bool silent = false,
        string? printerId = null,
        int? copies = null,
        PrintJobOrientation orientation = PrintJobOrientation.Auto,
        PrintJobDuplex duplex = PrintJobDuplex.Default,
        PrintJobColor color = PrintJobColor.Default,
        CancellationToken cancellationToken = default
    );
}
