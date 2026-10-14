using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Printers.Client;

public enum PrinterTransport { Ble, Network }

/// <summary>How a network printer was found.</summary>
public enum NetworkDiscoveryMethod
{
    /// <summary>It advertised itself over mDNS / DNS-SD.</summary>
    Mdns,

    /// <summary>It accepted a TCP connection during a subnet sweep. A swept printer has no name: its name is its address.</summary>
    PortSweep
}

/// <summary>The common receipt widths: 58mm is 32 columns and 384 dots, 80mm is 48 columns and 576 dots with a cutter.</summary>
public enum PrinterPaper { Paper58mm, Paper80mm }

/// <summary>The command language the printer speaks: ESC/POS for receipt printers, TSPL for label printers.</summary>
public enum PrinterLanguage { EscPos, Tspl }

public enum PrintAlign { Left, Center, Right }

public enum PrintBarcodeFormat { UpcA, UpcE, Ean13, Ean8, Code39, Itf, Codabar, Code93, Code128 }

/// <summary>Where the barcode's human-readable text goes.</summary>
public enum PrintBarcodeText { None, Above, Below, Both }

/// <summary>How much of a QR code can be damaged and still read: about 7, 15, 25 or 30%.</summary>
public enum PrintQrCorrection { Low, Medium, Quartile, High }

public enum PrintCutMode { Full, Partial }

/// <summary>How an image becomes black dots: <c>Threshold</c> for logos and line art, <c>FloydSteinberg</c> for photos.</summary>
public enum PrintDithering { Threshold, FloydSteinberg }

/// <summary>What a <see cref="PrintElement"/> does. Each reads only the fields its description names.</summary>
public enum PrintElementType
{
    /// <summary><c>text</c> with no line break.</summary>
    Text,
    /// <summary><c>text</c>, then a line break; no text prints an empty line.</summary>
    Line,
    /// <summary><c>align</c> for what follows.</summary>
    Align,
    /// <summary><c>on</c>; true when absent.</summary>
    Bold,
    /// <summary><c>on</c>; true when absent.</summary>
    Underline,
    /// <summary><c>width</c> and <c>height</c>, 1–8 times the normal character size.</summary>
    Size,
    /// <summary>Alignment, bold, underline and size back to their defaults.</summary>
    ResetStyle,
    /// <summary><c>lines</c> blank lines; one when absent.</summary>
    Feed,
    /// <summary><c>text</c> as a <c>barcode</c>, <c>height</c> dots tall (80) with bars <c>width</c> dots wide (2–6, 2) and <c>barcodeText</c> (Below).</summary>
    Barcode,
    /// <summary><c>text</c> as a QR code of <c>moduleSize</c> dots (1–16, 6) with <c>correction</c> (Medium).</summary>
    QrCode,
    /// <summary>
    /// <c>data</c>, a base64 PNG or JPEG, turned into dots by <c>dithering</c> (Threshold). An image wider than the printer
    /// is scaled down to fit.
    /// </summary>
    Image,
    /// <summary>
    /// Cuts the paper — <c>cut</c> (Partial) after feeding <c>lines</c> (3). A printer without a cutter only feeds, so a
    /// receipt written for any printer can end with one.
    /// </summary>
    Cut,
    /// <summary><c>data</c>, base64 bytes sent as they are: a cash drawer kick, a code page, anything printer-specific.</summary>
    Raw
}

/// <summary>One step of a receipt. In C#, <see cref="PrintElements"/> builds the list.</summary>
public sealed record PrintElement(
    PrintElementType Type,
    string? Text = null,
    PrintAlign? Align = null,
    bool? On = null,
    int? Width = null,
    int? Height = null,
    int? Lines = null,
    PrintBarcodeFormat? Barcode = null,
    PrintBarcodeText? BarcodeText = null,
    int? ModuleSize = null,
    PrintQrCorrection? Correction = null,
    PrintCutMode? Cut = null,
    string? Data = null,
    PrintDithering? Dithering = null
);

/// <summary>
/// What a printer can do. Lay a receipt out against these, never against a fixed width: <see cref="CharactersPerLine"/>
/// columns of text, and images up to <see cref="DotsPerLine"/> wide.
/// </summary>
/// <param name="CharactersPerLineFontB">Columns in the smaller font.</param>
/// <param name="PaperWidthMm">The printable width.</param>
public sealed record PrinterCapabilities(
    int CharactersPerLine,
    int CharactersPerLineFontB,
    int Dpi,
    double PaperWidthMm,
    int DotsPerLine,
    bool SupportsImages,
    bool SupportsCut,
    bool SupportsQrCodes,
    IReadOnlyList<PrintBarcodeFormat> SupportedBarcodes
);

/// <summary>A label printer's stock, for <see cref="PrinterLanguage.Tspl"/>.</summary>
/// <param name="WidthMm">50 when null.</param>
/// <param name="HeightMm">30 when null.</param>
/// <param name="GapMm">The gap between labels; 3 when null.</param>
public sealed record PrinterLabel(int? WidthMm = null, int? HeightMm = null, int? GapMm = null);

/// <param name="Printer">The connected printer, if any.</param>
/// <param name="BluetoothSupported">Whether Bluetooth LE printers can be used here.</param>
/// <param name="NetworkScanSupported">Whether network printers can be found by scanning; one can always be connected by address.</param>
/// <param name="RenderSupported">Whether <c>POST render</c> can turn a receipt into a PDF here.</param>
public sealed record PrintersStatus(
    ConnectedPrinter? Printer,
    bool BluetoothSupported,
    AccessState? BluetoothAccess,
    bool NetworkScanSupported,
    bool RenderSupported,
    bool Scanning
);

/// <param name="Id">The Bluetooth peripheral id, or <c>host:port</c> on the network.</param>
/// <param name="SupportsStatus">
/// Whether <see cref="IPrintersBridge.GetPrinterStatusAsync"/> can read this printer's sensors — an ESC/POS printer on the
/// network. Most Bluetooth printers are write-only.
/// </param>
public sealed record ConnectedPrinter(
    string Id,
    string? Name,
    PrinterTransport Transport,
    PrinterLanguage Language,
    PrinterCapabilities Capabilities,
    bool SupportsStatus = false
);

/// <param name="ScanMs">Up to 30,000 ms; 5 seconds when null.</param>
/// <param name="Sweep">
/// Network only: also try a TCP connection to every host on this device's IPv4 subnets (a /24 at most) on the
/// <see cref="PrinterConnectRequest.Port"/>s the app allows — 9100 to 9102 by default — to find printers that do not
/// advertise over mDNS.
/// </param>
public sealed record PrinterScanRequest(PrinterTransport Transport = PrinterTransport.Ble, int? ScanMs = null, bool Sweep = false);

/// <summary>A point-in-time read of the connected printer's own sensors.</summary>
/// <param name="IsOnline">False when the printer has taken itself offline: cover open, paper out, an error, or feeding.</param>
/// <param name="IsPaperNearEnd">The roll is running low. Not every printer has the sensor.</param>
/// <param name="IsPaperOut">The roll is empty, so printing has stopped.</param>
/// <param name="IsCutterError">The auto-cutter jammed or failed.</param>
/// <param name="HasUnrecoverableError">An error the printer cannot clear itself; it needs a power cycle or service.</param>
/// <param name="HasAutoRecoverableError">An error that clears on its own, such as the print head overheating.</param>
/// <param name="IsDrawerSensorHigh">
/// The cash-drawer sensor (kick-out connector pin 3) reads high. Whether high means open depends on the drawer.
/// </param>
/// <param name="CanPrint">Online, cover shut, paper loaded and no error.</param>
public sealed record PrinterSensorStatus(
    bool IsOnline,
    bool IsCoverOpen,
    bool IsPaperNearEnd,
    bool IsPaperOut,
    bool IsCutterError,
    bool HasUnrecoverableError,
    bool HasAutoRecoverableError,
    bool IsDrawerSensorHigh,
    bool CanPrint
);

/// <summary>A printer a scan found. Connect with its <see cref="Id"/>.</summary>
/// <param name="Id">The Bluetooth peripheral id, or <c>host:port</c> on the network.</param>
/// <param name="Profile">The known printer model a Bluetooth printer matched.</param>
/// <param name="Capabilities">
/// What the printer is assumed to be. A network printer never says how wide its paper is, so it is assumed 80mm: pass
/// <see cref="PrinterConnectRequest.Paper"/> when you know better.
/// </param>
public sealed record DiscoveredPrinter(
    string Id,
    string? Name,
    PrinterTransport Transport,
    string? Host,
    int? Port,
    int? Rssi,
    string? Profile,
    PrinterCapabilities Capabilities,
    NetworkDiscoveryMethod? Discovery = null
);

/// <summary>
/// The printer to connect to: the <see cref="Id"/> of a printer the last scan found, or one named outright — a Bluetooth
/// <see cref="PeripheralUuid"/> with its <see cref="ServiceUuid"/> and <see cref="WriteCharacteristicUuid"/>, or a
/// network <see cref="Host"/> (an IP address on the local network) and <see cref="Port"/> (9100).
/// </summary>
/// <param name="Paper">The paper width; the scanned printer's, or 80mm on the network and 58mm over Bluetooth, when null.</param>
/// <param name="Capabilities">Everything about the printer, for one that neither preset describes. Wins over <see cref="Paper"/>.</param>
/// <param name="Language">The scanned printer's, or ESC/POS, when null.</param>
/// <param name="Label">The label stock, for TSPL.</param>
/// <param name="TimeoutMs">Up to 60,000 ms; 15 seconds when null.</param>
/// <param name="ChunkSize">Bytes per Bluetooth write, 20–512; the connection's MTU when null. Lower it when a cheap printer garbles a long receipt.</param>
/// <param name="ChunkDelayMs">A pause between Bluetooth writes, 0–500 ms, to let a small buffer drain.</param>
public sealed record PrinterConnectRequest(
    PrinterTransport Transport = PrinterTransport.Ble,
    string? Id = null,
    string? PeripheralUuid = null,
    string? ServiceUuid = null,
    string? WriteCharacteristicUuid = null,
    string? Host = null,
    int? Port = null,
    PrinterPaper? Paper = null,
    PrinterCapabilities? Capabilities = null,
    PrinterLanguage? Language = null,
    PrinterLabel? Label = null,
    int? TimeoutMs = null,
    int? ChunkSize = null,
    int? ChunkDelayMs = null
);

/// <summary>A receipt for the connected printer.</summary>
public sealed record PrintReceiptRequest(IReadOnlyList<PrintElement> Elements);

/// <param name="Bytes">What was sent to the printer.</param>
public sealed record PrintReceiptResult(int Bytes);

/// <summary>
/// A receipt to render as a PDF, for a printer that speaks no ESC/POS — an office printer through the printing bridge, or a
/// download — on pages of <see cref="PageWidth"/> by <see cref="PageHeight"/> points.
/// </summary>
/// <param name="Paper">The receipt width images are fitted to; the connected printer's, or 80mm, when null.</param>
/// <param name="PageWidth">Points; A4's 595 when null.</param>
/// <param name="PageHeight">Points; A4's 842 when null.</param>
/// <param name="Margin">Points; 36 when null.</param>
/// <param name="FontSize">Points; 12 when null.</param>
public sealed record PrintRenderRequest(
    IReadOnlyList<PrintElement> Elements,
    PrinterPaper? Paper = null,
    float? PageWidth = null,
    float? PageHeight = null,
    float? Margin = null,
    float? FontSize = null
);

public sealed record PrinterDisconnected(string Id, PrinterTransport Transport);

/// <summary>Serialization for every receipt printer contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PrintersStatus))]
[JsonSerializable(typeof(PrinterScanRequest))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveredPrinter>))]
[JsonSerializable(typeof(PrinterConnectRequest))]
[JsonSerializable(typeof(ConnectedPrinter))]
[JsonSerializable(typeof(PrinterSensorStatus))]
[JsonSerializable(typeof(PrintReceiptRequest))]
[JsonSerializable(typeof(PrintReceiptResult))]
[JsonSerializable(typeof(PrintRenderRequest))]
[JsonSerializable(typeof(PrinterDisconnected))]
public partial class PrintersJsonContext : JsonSerializerContext;
