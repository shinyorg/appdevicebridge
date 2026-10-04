using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Printing.Client;

public enum PrintJobContent { Pdf, Image, Html, Url }

public enum PrintJobOrientation { Auto, Portrait, Landscape }

public enum PrintJobDuplex { Default, OneSided, TwoSidedLongEdge, TwoSidedShortEdge }

public enum PrintJobColor { Default, Color, Monochrome }

/// <summary>
/// <see cref="PrintJobStatus.Completed"/> and <see cref="PrintJobStatus.Submitted"/> both mean it printed, or will:
/// submitted is a job handed to a spooler whose end the platform does not report.
/// </summary>
public enum PrintJobStatus { Completed, Submitted, Cancelled, Failed }

/// <summary>What printing can do here. Feature-detect with these rather than printing to find out.</summary>
/// <param name="SystemDialog">Printing shows the platform's print dialog, where the person picks the printer.</param>
/// <param name="Silent">A job with <see cref="PrintJobOptions.Silent"/> skips the dialog: Windows and CUPS (Linux, macOS).</param>
/// <param name="ListPrinters">Installed printers can be listed and named in <see cref="PrintJobOptions.PrinterId"/>.</param>
/// <param name="HtmlToPdf">HTML can be laid out on pages and returned as a PDF, with no print UI: iOS, Mac Catalyst and Android.</param>
public sealed record PrintingCapabilities(
    bool Supported,
    bool Pdf,
    bool Image,
    bool Html,
    bool SystemDialog,
    bool Silent,
    bool ListPrinters,
    bool HtmlToPdf
);

/// <param name="Id">Pass back as <see cref="PrintJobOptions.PrinterId"/>.</param>
public sealed record InstalledPrinter(string Id, string DisplayName, bool IsDefault);

/// <param name="JobName">What the print queue shows.</param>
/// <param name="Silent">Print without the dialog where <see cref="PrintingCapabilities.Silent"/>; the dialog shows anyway elsewhere.</param>
/// <param name="PrinterId">The printer for a silent job; the default printer when null.</param>
/// <param name="Copies">1–99; one when null.</param>
public sealed record PrintJobOptions(
    string? JobName = null,
    bool Silent = false,
    string? PrinterId = null,
    int? Copies = null,
    PrintJobOrientation Orientation = PrintJobOrientation.Auto,
    PrintJobDuplex Duplex = PrintJobDuplex.Default,
    PrintJobColor Color = PrintJobColor.Default
);

/// <summary>
/// One document: <see cref="Data"/> as base64 for a PDF or a PNG/JPEG image, <see cref="Html"/> for markup, or
/// <see cref="Url"/> for an http or https page.
/// </summary>
public sealed record PrintJobRequest(
    PrintJobContent Content,
    string? Data = null,
    string? Html = null,
    string? Url = null,
    PrintJobOptions? Options = null
);

/// <summary>An HTML document to lay out on pages with the platform's own engine — the one printing uses — and return as a PDF.</summary>
/// <param name="Html">A complete HTML document.</param>
/// <param name="PageWidth">Portrait page width in points; A4's 595 when null. Letter is 612.</param>
/// <param name="PageHeight">Portrait page height in points; A4's 842 when null. Letter is 792.</param>
/// <param name="Orientation"><see cref="PrintJobOrientation.Landscape"/> swaps width and height; anything else is portrait.</param>
/// <param name="Margin">A uniform margin in points; 36 (half an inch) when null. A CSS <c>@page { margin }</c> in the document wins on Android.</param>
public sealed record HtmlToPdfRequest(
    string Html,
    float? PageWidth = null,
    float? PageHeight = null,
    PrintJobOrientation Orientation = PrintJobOrientation.Auto,
    float? Margin = null
);

/// <param name="Error">Why, when <see cref="Status"/> is failed.</param>
public sealed record PrintJobResult(PrintJobStatus Status, string? PrinterId, string? Error);

/// <summary>Serialization for every printing contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PrintingCapabilities))]
[JsonSerializable(typeof(IReadOnlyList<InstalledPrinter>))]
[JsonSerializable(typeof(PrintJobRequest))]
[JsonSerializable(typeof(PrintJobResult))]
[JsonSerializable(typeof(HtmlToPdfRequest))]
public partial class PrintingJsonContext : JsonSerializerContext;
