using Shiny.AppDeviceBridge.Barcodes.Client;

namespace Shiny.AppDeviceBridge.Barcodes;

/// <summary>
/// Shows a scanner and reports what it reads. The bridge's one seam to the device: <see cref="MediaBarcodeScanSource"/>
/// is the Shiny Controls modal scanner, and tests stand in for it.
/// </summary>
public interface IBarcodeScanSource
{
    /// <summary>Whether this platform can scan at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Shows the scanner until it closes, calling <paramref name="detected"/> for each code as it is read, and answers why
    /// it closed. Cancelling <paramref name="cancellationToken"/> closes it; that ends the scan, it does not throw.
    /// </summary>
    Task<BarcodeScanEndReason> ScanAsync(BarcodeScanRequest request, Action<ScannedBarcode> detected, CancellationToken cancellationToken);
}
