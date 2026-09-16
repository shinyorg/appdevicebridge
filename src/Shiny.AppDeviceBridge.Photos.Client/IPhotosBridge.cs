using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Photos.Client;

/// <summary>
/// Photos, two ways. The system picker needs no permission — the user chooses — and works on every head. The library is
/// browsing every photo on the device, newest first, with thumbnails; it needs library access and is not available on
/// Linux. Either way a photo reaches the page as a file in a file root, read through <see cref="IFilesBridge"/>.
/// </summary>
[BridgeClient("photos", typeof(PhotosJsonContext))]
public interface IPhotosBridge
{
    /// <summary>What works here, and whether the app has library access — without prompting.</summary>
    [BridgeGet]
    Task<PhotoStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests photo library access.</summary>
    [BridgePost("access")]
    Task<PhotoAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the system photo picker and copies what the user chose into a file root. Empty when the user cancels. Not
    /// every platform enforces <see cref="PhotoPickRequest.Limit"/>.
    /// </summary>
    [BridgePost("pick")]
    Task<IReadOnlyList<PhotoFile>> PickAsync(PhotoPickRequest request, CancellationToken cancellationToken = default);

    /// <summary>A page of the photo library, newest first. Fails with 403 without library access.</summary>
    /// <param name="limit">1–200.</param>
    [BridgeGet("library")]
    Task<PhotoPage> GetLibraryAsync(int offset = 0, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>A JPEG thumbnail that fits a square of <paramref name="size"/> pixels, 32–1024.</summary>
    [BridgeGet("library/{id}/thumbnail")]
    Task<byte[]> GetThumbnailAsync(string id, int size = 256, CancellationToken cancellationToken = default);

    /// <summary>Copies a library photo at full size into a file root.</summary>
    [BridgePost("library/{id}/export")]
    Task<PhotoFile> ExportAsync(string id, PhotoExportRequest request, CancellationToken cancellationToken = default);
}
