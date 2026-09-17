#if WINDOWS
using Shiny.AppDeviceBridge.Photos.Client;
using Windows.Storage;
using Windows.Storage.FileProperties;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Photos;

/// <summary>
/// Windows has no photo library API for a desktop app; the user's Pictures folder is where photos live. An id is the path
/// relative to it, and nothing outside it is served.
/// </summary>
static partial class PhotoLibrary
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp", ".tif", ".tiff" };

    static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    public static bool IsSupported => Directory.Exists(Pictures);

    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(IsSupported ? ContractAccess.Available : ContractAccess.NotSupported);

    public static Task<ContractAccess> RequestAccessAsync() => GetAccessAsync();

    public static Task<IReadOnlyList<LibraryPhoto>> GetPageAsync(int offset, int count, CancellationToken cancellationToken) => Task.Run(() =>
    {
        IReadOnlyList<LibraryPhoto> page =
        [
            .. new DirectoryInfo(Pictures)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System | System.IO.FileAttributes.ReparsePoint })
                .Where(x => Extensions.Contains(x.Extension))
                .OrderByDescending(x => x.CreationTimeUtc)
                .Skip(offset)
                .Take(count)
                .Select(x => new LibraryPhoto(
                    PhotosBridge.Encode(Path.GetRelativePath(Pictures, x.FullName)),
                    new DateTimeOffset(x.CreationTimeUtc, TimeSpan.Zero),
                    0,
                    0,
                    x.Name
                ))
        ];

        return page;
    }, cancellationToken);

    public static async Task<byte[]?> GetThumbnailAsync(string id, int size, CancellationToken cancellationToken)
    {
        if (Resolve(id) is not { } path)
            return null;

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size, ThumbnailOptions.ResizeThumbnail);

        if (thumbnail is null)
            return null;

        await using var stream = thumbnail.AsStreamForRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public static Task<PhotoData?> OpenAsync(string id, CancellationToken cancellationToken)
        => Task.FromResult(Resolve(id) is { } path
            ? new PhotoData(File.OpenRead(path), Path.GetFileName(path), null)
            : null);

    /// <summary>The photo's path, when the id names an image inside Pictures and nothing else.</summary>
    static string? Resolve(string id)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Pictures));
        var full = Path.GetFullPath(Path.Combine(root, id));

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               && Extensions.Contains(Path.GetExtension(full))
               && File.Exists(full)
            ? full
            : null;
    }
}
#endif
