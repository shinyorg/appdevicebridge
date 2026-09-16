using System.Text;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// The page's own files, confined to named roots the app configured — <c>data</c> and <c>cache</c> by default. Paths
/// are relative to the root and use <c>/</c>. Parent directories are created as needed, and a write that fails leaves
/// no partial file behind.
/// </summary>
[BridgeClient("files", typeof(AppDeviceBridgeJsonContext))]
public interface IFilesBridge
{
    /// <summary>The names of the roots the page can use.</summary>
    [BridgeGet]
    Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>A directory's entries, directories first. Fails with 404 when it does not exist.</summary>
    [BridgeGet("{root}/list")]
    Task<IReadOnlyList<FileEntry>> ListAsync(string root, string path = "", CancellationToken cancellationToken = default);

    /// <summary>One file or directory. Fails with 404 when it does not exist.</summary>
    [BridgeGet("{root}/info")]
    Task<FileEntry> GetInfoAsync(string root, string path, CancellationToken cancellationToken = default);

    /// <summary>A file's content as a stream. Fails with 404 when it does not exist.</summary>
    [BridgeGet("{root}/content")]
    Task<Stream> OpenReadAsync(string root, string path, CancellationToken cancellationToken = default);

    /// <summary>A file's content. Fails with 404 when it does not exist.</summary>
    [BridgeGet("{root}/content")]
    Task<byte[]> ReadBytesAsync(string root, string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a file from a stream. With <paramref name="overwrite"/> false, fails with 409 when it exists.</summary>
    [BridgePut("{root}/content")]
    Task<FileEntry> WriteAsync(string root, string path, [BridgeBody("application/octet-stream")] Stream content, bool overwrite = true, CancellationToken cancellationToken = default);

    /// <summary>Writes a file. With <paramref name="overwrite"/> false, fails with 409 when it exists.</summary>
    [BridgePut("{root}/content")]
    Task<FileEntry> WriteBytesAsync(string root, string path, [BridgeBody("application/octet-stream")] byte[] content, bool overwrite = true, CancellationToken cancellationToken = default);

    /// <summary>Writes a UTF-8 text file. With <paramref name="overwrite"/> false, fails with 409 when it exists.</summary>
    [BridgePut("{root}/content")]
    Task<FileEntry> WriteTextAsync(string root, string path, [BridgeBody("text/plain; charset=utf-8")] string text, bool overwrite = true, CancellationToken cancellationToken = default);

    /// <summary>Appends UTF-8 text to a file, creating it if needed.</summary>
    [BridgePost("{root}/append")]
    Task<FileEntry> AppendTextAsync(string root, string path, [BridgeBody("text/plain; charset=utf-8")] string text, CancellationToken cancellationToken = default);

    /// <summary>Appends bytes to a file, creating it if needed.</summary>
    [BridgePost("{root}/append")]
    Task<FileEntry> AppendBytesAsync(string root, string path, [BridgeBody("application/octet-stream")] byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory and its parents.</summary>
    [BridgePost("{root}/directory")]
    Task<FileEntry> CreateDirectoryAsync(string root, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or a directory. A directory that is not empty fails with 409 unless <paramref name="recursive"/>.
    /// </summary>
    [BridgeDelete("{root}/entry")]
    Task DeleteAsync(string root, string path, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>Moves or renames a file or directory within a root.</summary>
    [BridgePost("{root}/move")]
    Task<FileEntry> MoveAsync(string root, FileTransfer transfer, CancellationToken cancellationToken = default);

    /// <summary>Copies a file, or a directory recursively, within a root.</summary>
    [BridgePost("{root}/copy")]
    Task<FileEntry> CopyAsync(string root, FileTransfer transfer, CancellationToken cancellationToken = default);
}

/// <summary>A file or directory.</summary>
/// <param name="Name">The entry's name; the root's name for the root itself.</param>
/// <param name="Path">Relative to the root, with forward slashes. Empty for the root.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <param name="Size">Bytes, for files.</param>
/// <param name="Modified">When it last changed.</param>
public sealed record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset Modified);

/// <summary>A move or copy within a root.</summary>
/// <param name="From">The entry to move or copy.</param>
/// <param name="To">Where it goes, including its new name.</param>
/// <param name="Overwrite">Replace an existing file at <paramref name="To"/>. Directories are never merged or replaced.</param>
public sealed record FileTransfer(string From, string To, bool Overwrite = false);

/// <summary>Files as text, and files handed over by other bridges.</summary>
public static class FilesBridgeExtensions
{
    /// <summary>A UTF-8 text file. Fails with 404 when it does not exist.</summary>
    public static async Task<string> ReadTextAsync(this IFilesBridge files, string root, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Encoding.UTF8.GetString(await files.ReadBytesAsync(root, path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A file another bridge returned, as a stream.</summary>
    public static Task<Stream> OpenReadAsync(this IFilesBridge files, BridgeFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(file);
        return files.OpenReadAsync(file.Root, file.Path, cancellationToken);
    }
}
