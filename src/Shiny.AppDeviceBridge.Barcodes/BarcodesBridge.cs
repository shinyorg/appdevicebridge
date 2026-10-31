using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Barcodes.Client;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Barcodes;

public static class BarcodesBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/barcodes</c>: the device's camera as a barcode scanner, shown full screen when a page asks for a
    /// scan, with long-range scanning and auto-zoom for codes across an aisle.
    /// <code>
    /// bridge.AddBarcodesBridge();
    /// </code>
    /// <para>
    /// Declare <c>NSCameraUsageDescription</c> on Apple platforms (and the camera entitlement where sandboxed),
    /// <c>CAMERA</c> on Android, and the <c>webcam</c> capability on Windows. Linux has no camera: 501.
    /// </para>
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddBarcodesBridge(this MauiAppDeviceBridgeBuilder bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (bridge.Services.Any(x => x.ServiceType == typeof(IBarcodeScanSource)))
            return bridge;

        bridge.Maui.UseShinyCamera();
        bridge.Services.TryAddSingleton<IBarcodeScanSource, MediaBarcodeScanSource>();
        bridge.AddBridge<BarcodesBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/barcodes</c>, over the Shiny Controls modal scanner.
/// <code>
/// GET    /_bridge/barcodes         { "supported": true, "scanning": false }
/// POST   /_bridge/barcodes/scan    { "mode": "LongRange", "autoZoom": true, "formats": [ "Ean13" ] }  →  { "barcodes": [ … ], "reason": "MaxResults" }
/// DELETE /_bridge/barcodes/scan    204: the running scan answers with what it read
/// events: barcodes.detected
/// </code>
/// </summary>
public sealed class BarcodesBridge(IBarcodeScanSource scanner) : IWebAppBridge
{
    internal const string DetectedEvent = "barcodes.detected";
    internal const int MaxTextLength = 256;
    internal const int MaxResultsLimit = 1000;

    readonly Lock gate = new();
    readonly WebAppEventSource<ScannedBarcode> detected = new();
    CancellationTokenSource? stop;

    public string Name => "barcodes";

    public bool IsSupported => scanner.IsSupported;

    internal bool Scanning
    {
        get
        {
            lock (this.gate)
                return this.stop != null;
        }
    }

    internal int DetectedListeners => this.detected.ListenerCount;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent(DetectedEvent, ct => this.detected.ListenAsync(ct), BarcodesJsonContext.Default.ScannedBarcode)
        .MapGet("", this.StatusAsync)
        .MapPost("/scan", this.ScanAsync)
        .MapDelete("/scan", this.StopAsync);

    ValueTask StatusAsync(HttpContext context)
        => WebAppBridgeResults.Json(context, new BarcodeScannerStatus(this.IsSupported, this.Scanning), BarcodesJsonContext.Default.BarcodeScannerStatus);

    async ValueTask ScanAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Barcode scanning");
            return;
        }

        var request = await WebAppBridgeResults.ReadBodyAsync(context, BarcodesJsonContext.Default.BarcodeScanRequest);
        var invalid = request is null ? "Expected a scan request: { } scans for one code of any kind." : Validate(request);
        if (invalid is not null)
        {
            await WebAppBridgeResults.BadRequest(context, invalid);
            return;
        }

        CancellationTokenSource? stopped = null;
        lock (this.gate)
        {
            if (this.stop is null)
                this.stop = stopped = new CancellationTokenSource();
        }

        if (stopped is null)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "scan_in_progress", "A barcode scan is already running. Stop it first, or wait for it to answer.");
            return;
        }

        var barcodes = new List<ScannedBarcode>();
        BarcodeScanEndReason reason;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopped.Token);
            reason = await scanner.ScanAsync(request!, barcode =>
            {
                barcodes.Add(barcode);
                this.detected.Publish(barcode);
            }, linked.Token);

            // A scan the page stopped ends as the scanner's own cancel; the page asked for it, so it says so.
            if (stopped.IsCancellationRequested)
                reason = BarcodeScanEndReason.Stopped;
        }
        finally
        {
            lock (this.gate)
                this.stop = null;

            stopped.Dispose();
        }

        await WebAppBridgeResults.Json(context, new BarcodeScanResult(barcodes, reason), BarcodesJsonContext.Default.BarcodeScanResult);
    }

    ValueTask StopAsync(HttpContext context)
    {
        lock (this.gate)
            this.stop?.Cancel();

        return WebAppBridgeResults.NoContent(context);
    }

    /// <summary>Why the request cannot be scanned, or null when it can.</summary>
    internal static string? Validate(BarcodeScanRequest r)
    {
        if (r.Formats?.Contains(BarcodeFormat.Unknown) == true)
            return "formats cannot include Unknown: it names codes the platform could not identify.";

        if (r.MaxResults is < 0 or > MaxResultsLimit)
            return $"maxResults is 0 (until the person is done) to {MaxResultsLimit}.";

        if (r.TimeoutSeconds is { } timeout && (!(timeout > 0) || timeout > 3600))
            return "timeoutSeconds is more than 0 and at most 3600.";

        if (r.MaxAutoZoom is { } zoom && !(zoom is >= 1 and <= 10))
            return "maxAutoZoom is 1 to 10.";

        if (r.FullFrameInterval is < 0 or > 30)
            return "fullFrameInterval is 0 (never) to 30.";

        if (r.MaxNativeCropsPerFrame is < 0 or > 8)
            return "maxNativeCropsPerFrame is 0 to 8.";

        if (r.UncheckedConfirmations is < 1 or > 10)
            return "uncheckedConfirmations is 1 to 10.";

        if (r.ScanWindow is { } w && !IsNormalized(w))
            return "scanWindow is a non-empty rectangle inside 0–1 on both axes.";

        if ((r.Title?.Length ?? 0) > MaxTextLength || (r.Instructions?.Length ?? 0) > MaxTextLength)
            return $"title and instructions are at most {MaxTextLength} characters.";

        return null;
    }

    // written so NaN fails every comparison
    static bool IsNormalized(BarcodeRegion w) =>
        w.X >= 0 && w.Y >= 0 &&
        w.Width > 0 && w.Height > 0 &&
        w.X + w.Width <= 1 && w.Y + w.Height <= 1;
}
