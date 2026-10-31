using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Barcodes.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Maui.Controls.Camera.Barcode;
using Shiny.Maui.Controls.Camera.Media;
using Native = Shiny.Controls.Camera;
using BarcodeScanMode = Shiny.AppDeviceBridge.Barcodes.Client.BarcodeScanMode;
using NativeMode = Shiny.Maui.Controls.Camera.Barcode.BarcodeScanMode;

namespace Shiny.AppDeviceBridge.Barcodes;

/// <summary>
/// The scanner <see cref="IMediaService"/> shows: the camera full screen with a <see cref="BarcodeAnalyzer"/> attached,
/// its permission prompt, and its own done and close buttons. It puts itself on the UI thread.
/// </summary>
sealed class MediaBarcodeScanSource(IServiceProvider services) : IBarcodeScanSource
{
#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
    public bool IsSupported => true;
#else
    /// <summary>The Linux head: the camera control has no handler there.</summary>
    public bool IsSupported => false;
#endif

    public async Task<BarcodeScanEndReason> ScanAsync(BarcodeScanRequest request, Action<ScannedBarcode> detected, CancellationToken cancellationToken)
    {
        var media = services.GetRequiredService<IMediaService>();

        // The scanner reports why it closed after the last code and before the sequence ends.
        var ended = MediaScanEndReason.Stopped;
        var options = ToOptions(request, reason => ended = reason);

        await foreach (var barcode in media.ScanBarcodesAsync(request.FilterDuplicates, ToNative(request.Formats), options, cancellationToken))
            detected(ToContract(barcode));

        return ToContract(ended);
    }

    internal static BarcodeScanOptions ToOptions(BarcodeScanRequest r, Action<MediaScanEndReason> ended)
    {
        var single = r.MaxResults == 1;
        return new BarcodeScanOptions
        {
            Mode = r.Mode == BarcodeScanMode.LongRange ? NativeMode.LongRange : NativeMode.Standard,
            AutoZoom = r.AutoZoom,
            MaxResults = r.MaxResults == 0 ? null : r.MaxResults,
            FilterDuplicates = r.FilterDuplicates,
            Timeout = r.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            ScanWindow = r.ScanWindow is { } w ? new RectF((float)w.X, (float)w.Y, (float)w.Width, (float)w.Height) : null,
            IsTorchOn = r.TorchOn,
            Title = r.Title,
            Instructions = r.Instructions,

            // A one-code scan closes itself on the first read: a count and a done button would only be noise.
            ShowResultCount = !single,
            ShowDoneButton = !single,

            // The long-range tuning is on the analyzer rather than the options; the scanner hands over its camera, which
            // carries the analyzer, before it starts.
            ConfigureCamera = camera =>
            {
                if (camera.Analyzer is BarcodeAnalyzer analyzer)
                    Configure(analyzer, r);
            },
            OnEnded = ended
        };
    }

    /// <summary>The long-range settings a request names; the analyzer's defaults for the rest.</summary>
    internal static void Configure(BarcodeAnalyzer analyzer, BarcodeScanRequest r)
    {
        if (r.MaxAutoZoom is { } zoom)
            analyzer.MaxAutoZoom = zoom;

        if (r.FullFrameInterval is { } interval)
            analyzer.FullFrameInterval = interval;

        if (r.MaxNativeCropsPerFrame is { } crops)
            analyzer.MaxNativeCropsPerFrame = crops;

        if (r.UncheckedConfirmations is { } confirmations)
            analyzer.UncheckedConfirmations = confirmations;
    }

    internal static List<Native.BarcodeFormat>? ToNative(IReadOnlyList<BarcodeFormat>? formats)
        => formats is null || formats.Count == 0
            ? null
            : formats.Select(BridgeEnum.Convert<BarcodeFormat, Native.BarcodeFormat>).Distinct().ToList();

    internal static ScannedBarcode ToContract(Native.DetectedBarcode b) => new(
        b.Value,
        // explicit rather than BridgeEnum.Convert: a symbology the contract does not know is Unknown, never the first member
        Enum.TryParse<BarcodeFormat>(b.Format.ToString(), out var format) ? format : BarcodeFormat.Unknown,
        new BarcodeRegion(b.BoundingBox.X, b.BoundingBox.Y, b.BoundingBox.Width, b.BoundingBox.Height)
    );

    internal static BarcodeScanEndReason ToContract(MediaScanEndReason reason) => reason switch
    {
        MediaScanEndReason.MaxResults => BarcodeScanEndReason.MaxResults,
        MediaScanEndReason.Done => BarcodeScanEndReason.Done,
        MediaScanEndReason.Dismissed => BarcodeScanEndReason.Dismissed,
        MediaScanEndReason.Timeout => BarcodeScanEndReason.Timeout,
        MediaScanEndReason.PermissionDenied => BarcodeScanEndReason.PermissionDenied,
        MediaScanEndReason.CameraUnavailable => BarcodeScanEndReason.CameraUnavailable,
        // Cancelled and Stopped are both the bridge closing the scanner: the page stopped it, or went away
        _ => BarcodeScanEndReason.Stopped
    };
}
