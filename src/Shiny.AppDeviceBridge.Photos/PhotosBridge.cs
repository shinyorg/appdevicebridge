using System.Buffers.Text;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Photos.Client;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Photos;

public static class PhotosBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/photos</c>: the system photo picker, and the photo library — there is nothing else to call.
    /// <code>
    /// builder.AddPhotosBridge();
    /// </code>
    /// <para>
    /// The picker needs no permission. The library needs <c>NSPhotoLibraryUsageDescription</c> on Apple platforms (plus
    /// the <c>com.apple.security.personal-information.photos-library</c> entitlement where the app is sandboxed), and
    /// <c>READ_MEDIA_IMAGES</c> — <c>READ_EXTERNAL_STORAGE</c> before Android 13 — on Android. Windows browses the user's
    /// Pictures folder. Linux has no photo library, so its library endpoints answer 501.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddPhotosBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddWebAppBridge<PhotosBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/photos</c>. Photos reach the page as files: copied into a file root under <c>photos/</c>, and read
/// through <c>/_bridge/files</c>.
/// <code>
/// GET    /_bridge/photos                                { "pickerSupported": true, "librarySupported": true, "libraryAccess": "Available" }
/// POST   /_bridge/photos/access
/// POST   /_bridge/photos/pick                           { "limit": 5, "root": "cache" }   → [{ "file": { "root", "path" }, … }]
/// GET    /_bridge/photos/library?offset=0&amp;limit=50
/// GET    /_bridge/photos/library/{id}/thumbnail?size=256   image/jpeg
/// POST   /_bridge/photos/library/{id}/export            { "root": "cache" }
/// </code>
/// </summary>
public sealed class PhotosBridge(WebAppFileRoots roots, AppDeviceBridgeOptions options) : IWebAppBridge
{
    const int MaxPick = 50;
    const int MaxPage = 200;

    public string Name => "photos";

    public bool IsSupported => roots.Enabled;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", ctx => Guarded(ctx, () => this.StatusAsync(ctx)))
        .MapPost("/access", ctx => Guarded(ctx, () => RequestAccessAsync(ctx)))
        .MapPost("/pick", ctx => Guarded(ctx, () => this.PickAsync(ctx)))
        .MapGet("/library", ctx => Guarded(ctx, () => LibraryAsync(ctx)))
        .MapGet("/library/{id}/thumbnail", ctx => Guarded(ctx, () => ThumbnailAsync(ctx)))
        .MapPost("/library/{id}/export", ctx => Guarded(ctx, () => this.ExportAsync(ctx)));

    async ValueTask StatusAsync(HttpContext context)
    {
        var access = PhotoLibrary.IsSupported ? await PhotoLibrary.GetAccessAsync() : ContractAccess.NotSupported;
        await WebAppBridgeResults.Json(
            context,
            new PhotoStatus(this.IsSupported, PhotoLibrary.IsSupported, access),
            PhotosJsonContext.Default.PhotoStatus
        );
    }

    static async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (!PhotoLibrary.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "The photo library");
            return;
        }

        // The permission prompt is UI.
        var access = await OnMainThread(PhotoLibrary.RequestAccessAsync);
        await WebAppBridgeResults.Json(context, new PhotoAccessResult(access), PhotosJsonContext.Default.PhotoAccessResult);
    }

    async ValueTask PickAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, PhotosJsonContext.Default.PhotoPickRequest) ?? new PhotoPickRequest();

        if (body.Limit is < 1 or > MaxPick)
        {
            await WebAppBridgeResults.BadRequest(context, $"limit is 1 to {MaxPick}.");
            return;
        }

        if (!roots.TryGet(body.Root, out var root))
        {
            await WebAppBridgeResults.NotFound(context, $"No file root '{body.Root}'.");
            return;
        }

        var picked = await OnMainThread(() => MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = body.Limit }));
        var files = new List<PhotoFile>();

        foreach (var result in (picked ?? []).OfType<FileResult>().Take(body.Limit))
        {
            await using var content = await result.OpenReadAsync();
            files.Add(await this.SaveAsync(root, content, result.FileName, result.ContentType, context.RequestAborted));
        }

        await WebAppBridgeResults.Json(context, (IReadOnlyList<PhotoFile>)files, PhotosJsonContext.Default.IReadOnlyListPhotoFile);
    }

    static async ValueTask LibraryAsync(HttpContext context)
    {
        if (!await LibraryReadyAsync(context))
            return;

        var query = context.Request.Query;
        if (!TryReadInt(query["offset"].ToString(), 0, 0, Int32.MaxValue, out var offset) || !TryReadInt(query["limit"].ToString(), 50, 1, MaxPage, out var limit))
        {
            await WebAppBridgeResults.BadRequest(context, $"offset must be 0 or more, and limit between 1 and {MaxPage}.");
            return;
        }

        // One more than asked for says whether there is another page, without counting the whole library.
        var photos = await PhotoLibrary.GetPageAsync(offset, limit + 1, context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            new PhotoPage([.. photos.Take(limit)], offset, limit, photos.Count > limit),
            PhotosJsonContext.Default.PhotoPage
        );
    }

    static async ValueTask ThumbnailAsync(HttpContext context)
    {
        if (!await LibraryReadyAsync(context))
            return;

        if (!TryReadInt(context.Request.Query["size"].ToString(), 256, 32, 1024, out var size))
        {
            await WebAppBridgeResults.BadRequest(context, "size is 32 to 1024.");
            return;
        }

        if (Decode(context.Request.RouteValues["id"]) is not { } id || await PhotoLibrary.GetThumbnailAsync(id, size, context.RequestAborted) is not { } jpeg)
        {
            await WebAppBridgeResults.NotFound(context, "No such photo.");
            return;
        }

        context.Response.Headers["Cache-Control"] = "private, max-age=3600";
        await context.Response.WriteBytesAsync(jpeg, "image/jpeg", context.RequestAborted);
    }

    async ValueTask ExportAsync(HttpContext context)
    {
        if (!await LibraryReadyAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, PhotosJsonContext.Default.PhotoExportRequest) ?? new PhotoExportRequest();

        if (!roots.TryGet(body.Root, out var root))
        {
            await WebAppBridgeResults.NotFound(context, $"No file root '{body.Root}'.");
            return;
        }

        if (Decode(context.Request.RouteValues["id"]) is not { } id || await PhotoLibrary.OpenAsync(id, context.RequestAborted) is not { } photo)
        {
            await WebAppBridgeResults.NotFound(context, "No such photo.");
            return;
        }

        await using (photo.Content)
        {
            var file = await this.SaveAsync(root, photo.Content, photo.FileName, photo.ContentType, context.RequestAborted);
            await WebAppBridgeResults.Json(context, file, PhotosJsonContext.Default.PhotoFile);
        }
    }

    /// <summary>Copies a photo into the root under <c>photos/</c>, named uniquely so two picks never collide.</summary>
    async Task<PhotoFile> SaveAsync(WebAppFileStore root, Stream content, string? fileName, string? contentType, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName ?? String.Empty) is { Length: > 1 and <= 10 } ext && WebAppFilePath.Normalize(ext) is not null
            ? ext.ToLowerInvariant()
            : ExtensionFor(contentType);

        var path = $"photos/{Guid.NewGuid():n}{extension}";
        var written = await root.WriteAsync(path, content, FileWriteMode.CreateNew, options.MaxFileWriteBytes, cancellationToken);

        return new PhotoFile(new BridgeFile(root.Name, path), fileName ?? WebAppFilePath.NameOf(path), contentType, written.Entry.Size ?? 0);
    }

    static async ValueTask<bool> LibraryReadyAsync(HttpContext context)
    {
        if (!PhotoLibrary.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "The photo library");
            return false;
        }

        if (await PhotoLibrary.GetAccessAsync() is not (ContractAccess.Available or ContractAccess.Restricted))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "access_denied", "Photo library access has not been granted. POST /_bridge/photos/access first.");
            return false;
        }

        return true;
    }

    /// <summary>Essentials says "not here" by throwing; the page gets 501, and 403 for a missing permission.</summary>
    static async ValueTask Guarded(HttpContext context, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is FeatureNotSupportedException or PlatformNotSupportedException or NotImplementedException)
        {
            await WebAppBridgeResults.NotSupported(context, "Photos");
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is PermissionException or UnauthorizedAccessException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "access_denied", ex.Message);
        }
        catch (WebAppFileException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    /// <summary>Library ids travel base64url-encoded: PhotoKit's contain slashes, which a route segment cannot.</summary>
    internal static string Encode(string nativeId) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(nativeId));

    static string? Decode(string? id)
    {
        if (String.IsNullOrEmpty(id) || id.Length > 1024)
            return null;

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(id));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/heic" or "image/heif" => ".heic",
        "image/webp" => ".webp",
        "image/tiff" => ".tiff",
        _ => ".jpg"
    };

    static bool TryReadInt(string raw, int fallback, int min, int max, out int value)
    {
        if (raw.Length == 0)
        {
            value = fallback;
            return true;
        }

        return Int32.TryParse(raw, out value) && value >= min && value <= max;
    }

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();
}

/// <summary>A library photo opened for export.</summary>
sealed record PhotoData(Stream Content, string? FileName, string? ContentType);

/// <summary>The platform's photo library. <see cref="IsSupported"/> is false where there is none.</summary>
static partial class PhotoLibrary
{
#if !(ANDROID || IOS || MACCATALYST || MACOS || WINDOWS)
    public static bool IsSupported => false;

    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(ContractAccess.NotSupported);

    public static Task<ContractAccess> RequestAccessAsync() => Task.FromResult(ContractAccess.NotSupported);

    public static Task<IReadOnlyList<LibraryPhoto>> GetPageAsync(int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<LibraryPhoto>>([]);

    public static Task<byte[]?> GetThumbnailAsync(string id, int size, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public static Task<PhotoData?> OpenAsync(string id, CancellationToken cancellationToken) => Task.FromResult<PhotoData?>(null);
#endif
}
