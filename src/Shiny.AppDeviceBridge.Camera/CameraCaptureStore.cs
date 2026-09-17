using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>What a camera screen took, handed over to be filed.</summary>
/// <param name="Kind">A photo or a video.</param>
/// <param name="Content">The bytes. The store reads it to the end; the caller disposes it.</param>
/// <param name="Extension">The file extension, with its dot: <c>.jpg</c>, or what the platform recorded — <c>.mov</c> on Apple, <c>.mp4</c> elsewhere.</param>
/// <param name="Width">A photo's width.</param>
/// <param name="Height">A photo's height.</param>
/// <param name="Duration">A video's length, where the platform reports one.</param>
public sealed record CameraCaptureContent(
    CameraCaptureKind Kind,
    Stream Content,
    string Extension,
    int? Width = null,
    int? Height = null,
    TimeSpan? Duration = null
);

/// <summary>
/// Where captures go. The default files them into <see cref="CameraBridgeOptions.Root"/>/<see cref="CameraBridgeOptions.Folder"/>;
/// register your own before <c>AddCameraBridge</c> to file them somewhere else — through the app's own storage, with its
/// own naming and history.
/// </summary>
public interface ICameraCaptureStore
{
    Task<CameraCapture> SaveAsync(CameraCaptureContent capture, CancellationToken cancellationToken);
}

/// <summary>
/// Files captures into a file root, named for when they were taken — <c>IMG_20260916-201502.jpg</c>, <c>VID_…</c> — and never
/// over an existing file. No size limit: <see cref="AppDeviceBridgeOptions.MaxFileWriteBytes"/> is for what a page uploads,
/// and a long recording is the device's own.
/// </summary>
sealed class FileRootCameraCaptureStore(WebAppFileRoots roots, CameraBridgeOptions options, TimeProvider time) : ICameraCaptureStore
{
    public async Task<CameraCapture> SaveAsync(CameraCaptureContent capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (!roots.TryGet(options.Root, out var root))
            throw WebAppFileException.NotFound($"No file root '{options.Root}' to file the capture in.");

        var extension = capture.Extension.StartsWith('.') && capture.Extension.Length is > 1 and <= 10 && WebAppFilePath.Normalize(capture.Extension) is not null
            ? capture.Extension.ToLowerInvariant()
            : capture.Kind == CameraCaptureKind.Photo ? ".jpg" : ".mp4";

        var folder = WebAppFilePath.Normalize(options.Folder) ?? String.Empty;
        var stamp = $"{(capture.Kind == CameraCaptureKind.Photo ? "IMG" : "VID")}_{time.GetLocalNow():yyyyMMdd-HHmmss}";
        var path = await this.FreePathAsync(root, folder, stamp, extension, cancellationToken).ConfigureAwait(false);

        var written = await root.WriteAsync(path, capture.Content, FileWriteMode.CreateNew, Int64.MaxValue, cancellationToken).ConfigureAwait(false);

        return new CameraCapture(
            new BridgeFile(root.Name, path),
            capture.Kind,
            written.Entry.Size ?? 0,
            capture.Width,
            capture.Height,
            capture.Duration?.TotalSeconds
        );
    }

    /// <summary>The stamp, or the stamp with a counter when two captures land in the same second.</summary>
    async Task<string> FreePathAsync(WebAppFileStore root, string folder, string stamp, string extension, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = attempt == 1 ? stamp + extension : $"{stamp}-{attempt}{extension}";
            var path = folder.Length == 0 ? name : $"{folder}/{name}";

            if (await root.GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is null)
                return path;
        }

        throw WebAppFileException.Exists("Too many captures in the same second.");
    }
}
