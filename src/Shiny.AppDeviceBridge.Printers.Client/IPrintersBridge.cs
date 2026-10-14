using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Printers.Client;

/// <summary>
/// Receipt and label printers — ESC/POS or TSPL over Bluetooth LE or the network (raw TCP, port 9100), one at a time:
/// find and connect to a printer, then print receipts built in the page. Printing fails with 409 when none is connected.
/// For an office printer, render the same receipt to a PDF and print that through the printing bridge.
/// </summary>
[BridgeClient("printers", typeof(PrintersJsonContext))]
public interface IPrintersBridge
{
    /// <summary>The connected printer, and what can be used here.</summary>
    [BridgeGet("status")]
    Task<PrintersStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The printers seen during the scan window: Bluetooth printers matching a known model, or network printers
    /// advertising over mDNS (<c>_pdl-datastream._tcp</c>, <c>_printer._tcp</c>) — plus, with
    /// <see cref="PrinterScanRequest.Sweep"/>, any host on the local subnet that accepts a connection on a printer port.
    /// </summary>
    [BridgePost("scan")]
    Task<IReadOnlyList<DiscoveredPrinter>> ScanAsync(PrinterScanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Connects to a printer. Fails with 409 while another is connected.</summary>
    [BridgePost("connection")]
    Task<ConnectedPrinter> ConnectAsync(PrinterConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects the printer.</summary>
    [BridgeDelete("connection")]
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the connected printer's paper, cover, cutter and drawer sensors — check before a receipt, or poll for a
    /// drawer closing. Fails with 409 when no printer is connected, 501 when this printer cannot report its status
    /// (<see cref="ConnectedPrinter.SupportsStatus"/>), and 504 when it does not answer.
    /// </summary>
    [BridgeGet("connection/status")]
    Task<PrinterSensorStatus> GetPrinterStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Prints a receipt on the connected printer.</summary>
    [BridgePost("print")]
    Task<PrintReceiptResult> PrintAsync(PrintReceiptRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renders a receipt as a PDF. Needs no printer.</summary>
    [BridgePost("render")]
    Task<byte[]> RenderAsync(PrintRenderRequest request, CancellationToken cancellationToken = default);

    /// <summary>The printer went away.</summary>
    [BridgeEvent("printers.disconnected")]
    Task<IAsyncDisposable> OnDisconnectedAsync(Func<PrinterDisconnected, Task> handler);
}
