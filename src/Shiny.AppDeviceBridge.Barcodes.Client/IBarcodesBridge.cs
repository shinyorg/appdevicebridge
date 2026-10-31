using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Barcodes.Client;

/// <summary>
/// Scans barcodes with the device's camera: the device shows its own scanner, full screen, and the codes it reads come
/// back to the page. Not the camera of the machine showing the page: that is the browser's <c>BarcodeDetector</c>.
/// <para>
/// <see cref="BarcodeScanMode.LongRange"/> reads codes the platform scanners miss, such as a shelf label across an aisle,
/// and can zoom in on them by itself (<see cref="BarcodeScanRequest.AutoZoom"/>). iOS, Android, Mac Catalyst, macOS and
/// Windows; Linux answers 501.
/// </para>
/// </summary>
[BridgeClient("barcodes", typeof(BarcodesJsonContext))]
public interface IBarcodesBridge
{
    /// <summary>Whether this platform can scan, and whether a scan is running.</summary>
    [BridgeGet]
    Task<BarcodeScannerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the scanner and answers when it closes, with every code it read. A refused permission, a person backing
    /// out or a timeout is a result (<see cref="BarcodeScanResult.Reason"/>), not an error. Fails with 400 for a request
    /// out of range, and with <c>409 scan_in_progress</c> while another scan is running.
    /// </summary>
    [BridgePost("scan")]
    Task<BarcodeScanResult> ScanAsync(BarcodeScanRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the scanner. The running <see cref="ScanAsync"/> answers with what it read so far and
    /// <see cref="BarcodeScanEndReason.Stopped"/>. Does nothing when no scan is running.
    /// </summary>
    [BridgeDelete("scan")]
    Task StopScanAsync(CancellationToken cancellationToken = default);

    /// <summary>A code was read, as it happens, for a scan that collects many.</summary>
    [BridgeEvent("barcodes.detected")]
    Task<IAsyncDisposable> OnDetectedAsync(Func<ScannedBarcode, Task> handler);
}
