using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Barcodes.Client;

/// <summary>A barcode symbology.</summary>
public enum BarcodeFormat
{
    QrCode,
    Aztec,
    DataMatrix,
    Pdf417,
    Code128,
    Code39,
    Code93,
    Codabar,
    Ean8,
    Ean13,
    UpcA,
    UpcE,
    Itf,

    /// <summary>Read by the platform, but not one of the named symbologies. Never accepted in <see cref="BarcodeScanRequest.Formats"/>.</summary>
    Unknown
}

/// <summary>How the scanner reads a frame.</summary>
public enum BarcodeScanMode
{
    /// <summary>
    /// The platform's scanner over the whole frame, every frame: Apple Vision, Android ML Kit, ZXing.Net on Windows. The
    /// quickest read of a code that fills a good part of the frame.
    /// </summary>
    Standard,

    /// <summary>
    /// For codes far from the camera, small in frame, rotated or a little soft: a shelf label across an aisle. Codes are
    /// located in the full-resolution frame before anything is decoded, 1D codes (EAN/UPC, Code 128, Code 39, ITF) are read
    /// by a blur-tolerant decoder down to about a pixel per module, 2D codes go to the platform's scanner cut out and
    /// enlarged, and codes are tracked across frames. A big, close code can read a frame or two later than in
    /// <see cref="Standard"/>.
    /// </summary>
    LongRange
}

/// <summary>Why a scan ended.</summary>
public enum BarcodeScanEndReason
{
    /// <summary><see cref="BarcodeScanRequest.MaxResults"/> codes were read. How a one-code scan ends when it finds one.</summary>
    MaxResults,

    /// <summary>The person tapped the done (✓) button.</summary>
    Done,

    /// <summary>The person closed the scanner.</summary>
    Dismissed,

    /// <summary>Nothing was read within <see cref="BarcodeScanRequest.TimeoutSeconds"/> of opening or of the last code.</summary>
    Timeout,

    /// <summary>The page stopped the scan.</summary>
    Stopped,

    /// <summary>The camera permission was refused, so the scanner never opened.</summary>
    PermissionDenied,

    /// <summary>The app had no window to show the scanner on.</summary>
    CameraUnavailable
}

/// <summary>A rectangle in normalized (0–1) coordinates of the upright camera image.</summary>
public sealed record BarcodeRegion(double X, double Y, double Width, double Height);

/// <param name="Value">The decoded payload.</param>
/// <param name="Format">The symbology.</param>
/// <param name="Bounds">Where it was in the upright camera image.</param>
public sealed record ScannedBarcode(string Value, BarcodeFormat Format, BarcodeRegion Bounds);

/// <summary>
/// A scan. Every member is optional: an empty request reads the first code of any symbology, in
/// <see cref="BarcodeScanMode.Standard"/> mode, and waits for the person as long as they take.
/// </summary>
/// <param name="Formats">The symbologies to read; null or empty reads all. Narrowing it is the biggest speed-up.</param>
/// <param name="Mode">How frames are read.</param>
/// <param name="AutoZoom">
/// <see cref="BarcodeScanMode.LongRange"/> only: zoom in on a code that has been found but is too small to read, and zoom
/// back out once nothing is in view. A pinch takes control back at once.
/// </param>
/// <param name="MaxAutoZoom">
/// <see cref="BarcodeScanMode.LongRange"/> only: the furthest auto-zoom goes, as a multiple of the starting zoom. 1 to 10;
/// the scanner's default is 4.
/// </param>
/// <param name="FullFrameInterval">
/// <see cref="BarcodeScanMode.LongRange"/> only: run the platform's scanner over the whole frame every this many frames,
/// which catches close codes and Code 93 / Codabar. 0 (never) to 30; the scanner's default is 3.
/// </param>
/// <param name="MaxNativeCropsPerFrame">
/// <see cref="BarcodeScanMode.LongRange"/> only: candidates handed to the platform's scanner per frame, cut out and
/// enlarged. 0 to 8; the scanner's default is 2.
/// </param>
/// <param name="UncheckedConfirmations">
/// <see cref="BarcodeScanMode.LongRange"/> only: frames a code without a check digit (Code 39, short ITF) must read on
/// before it is reported. 1 to 10; the scanner's default is 2.
/// </param>
/// <param name="MaxResults">
/// Close the scanner after this many codes. 1 by default, a one-code scan; 0 keeps scanning until the person taps done,
/// <paramref name="TimeoutSeconds"/> passes or the page stops it. At most 1000.
/// </param>
/// <param name="FilterDuplicates">Report each symbology and value once per scan. On by default.</param>
/// <param name="TimeoutSeconds">
/// Close the scanner after this long without reading anything; each code restarts the clock. Null waits as long as it
/// takes. At most an hour.
/// </param>
/// <param name="ScanWindow">Only read codes inside this region, which the scanner also shows as an aiming guide.</param>
/// <param name="TorchOn">Start with the torch lit, where there is one.</param>
/// <param name="Title">Shown at the top of the scanner. At most 256 characters.</param>
/// <param name="Instructions">Shown under the title. At most 256 characters.</param>
public sealed record BarcodeScanRequest(
    IReadOnlyList<BarcodeFormat>? Formats = null,
    BarcodeScanMode Mode = BarcodeScanMode.Standard,
    bool AutoZoom = false,
    double? MaxAutoZoom = null,
    int? FullFrameInterval = null,
    int? MaxNativeCropsPerFrame = null,
    int? UncheckedConfirmations = null,
    int MaxResults = 1,
    bool FilterDuplicates = true,
    double? TimeoutSeconds = null,
    BarcodeRegion? ScanWindow = null,
    bool TorchOn = false,
    string? Title = null,
    string? Instructions = null
);

/// <param name="Barcodes">Every code read, in the order it was read. Empty when the scan ended without one.</param>
/// <param name="Reason">Why the scan ended.</param>
public sealed record BarcodeScanResult(IReadOnlyList<ScannedBarcode> Barcodes, BarcodeScanEndReason Reason);

/// <param name="Supported">Whether this platform can scan. False on Linux.</param>
/// <param name="Scanning">Whether a scan is running now. The device runs one at a time.</param>
public sealed record BarcodeScannerStatus(bool Supported, bool Scanning);

/// <summary>Serialization for every barcode contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BarcodeScannerStatus))]
[JsonSerializable(typeof(BarcodeScanRequest))]
[JsonSerializable(typeof(BarcodeScanResult))]
[JsonSerializable(typeof(ScannedBarcode))]
public partial class BarcodesJsonContext : JsonSerializerContext;
