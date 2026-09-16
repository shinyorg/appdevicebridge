using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// The storage behind one file root. <see cref="WebAppFileRoot"/> is a directory on disk; a bridge can add others — a
/// folder the user picked through Android's Storage Access Framework is not a path at all. The files bridge speaks to
/// every root through this, so a page reads and writes a picked folder exactly as it does <c>data</c>.
/// <para>
/// Paths arrive already checked by <see cref="WebAppFilePath.Normalize"/>: relative, <c>/</c>-separated, no <c>.</c> or
/// <c>..</c> segments, <c>""</c> for the root itself. A store refuses anything further of its own — a link out of the
/// root — with <see cref="WebAppFileException"/>, as it does every other failure the page should hear about.
/// </para>
/// </summary>
public abstract class WebAppFileStore
{
    protected WebAppFileStore(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' is not a valid file root name.", nameof(name));

        this.Name = name;
    }

    /// <summary>What the page calls the root: <c>data</c>, <c>cache</c>, or a name it chose for a picked folder.</summary>
    public string Name { get; }

    public static bool IsValidName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 64
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A file or directory, or null when there is nothing at the path.</summary>
    public abstract Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);

    /// <summary>A directory's entries, directories first, then by name.</summary>
    public abstract Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>A file's content as a response — with ranges, where the store can seek.</summary>
    public abstract Task<IResult> ReadAsync(string path, string? downloadName, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the content to a file, creating parent directories. A write that fails or passes
    /// <paramref name="maxBytes"/> leaves the file as it was.
    /// </summary>
    public abstract Task<FileWriteResult> WriteAsync(string path, Stream content, FileWriteMode mode, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Creates a directory and its parents.</summary>
    public abstract Task<FileWriteResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a directory — one with entries only when <paramref name="recursive"/>.</summary>
    public abstract Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Moves or copies within the root. Directories are never merged or replaced.</summary>
    public abstract Task<FileEntry> TransferAsync(string from, string to, bool overwrite, bool move, CancellationToken cancellationToken);

    /// <summary>
    /// The path on disk, for a bridge that has to hand the OS a file — to share it, attach it, upload it. Null for a
    /// store that has no paths, or a path that is not safe; those bridges then refuse the file.
    /// </summary>
    public virtual string? GetLocalPath(string path) => null;
}

public enum FileWriteMode
{
    /// <summary>Create the file, or replace it.</summary>
    Replace,

    /// <summary>Create the file; fail with <c>exists</c> if it is there.</summary>
    CreateNew,

    /// <summary>Add to the end, creating the file if needed.</summary>
    Append
}

/// <param name="Created">True when nothing was at the path before.</param>
public readonly record struct FileWriteResult(FileEntry Entry, bool Created);

/// <summary>A failure the page is told about, with the status and code the files bridge answers with.</summary>
public sealed class WebAppFileException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static WebAppFileException NotFound(string message = "No such file or directory.") => new(StatusCodes.Status404NotFound, "not_found", message);

    public static WebAppFileException InvalidPath() => new(
        StatusCodes.Status400BadRequest,
        "invalid_path",
        "Paths are relative to the root, use '/', and cannot contain '..', '\\', ':' or lead outside the root."
    );

    public static WebAppFileException IsADirectory() => new(StatusCodes.Status409Conflict, "is_a_directory", "The path is a directory.");

    public static WebAppFileException NotADirectory() => new(StatusCodes.Status409Conflict, "not_a_directory", "The path is a file.");

    public static WebAppFileException Exists(string message) => new(StatusCodes.Status409Conflict, "exists", message);

    public static WebAppFileException TooLarge() => new(StatusCodes.Status413PayloadTooLarge, "too_large", "The file would exceed the maximum size.");

    public static WebAppFileException BadRequest(string message) => new(StatusCodes.Status400BadRequest, "bad_request", message);
}

/// <summary>
/// The one check every page-supplied path passes before any store sees it. A path is refused rather than repaired: no
/// <c>..</c>, no backslashes (a separator on Windows), no drive or stream colons, no characters a file name cannot hold.
/// </summary>
public static class WebAppFilePath
{
    // The union of what Windows, macOS, Linux and Android refuse, so a path means the same thing everywhere.
    static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars().Union(['<', '>', '"', '|', '?', '*', ':', '\\', '/', '\0'])];

    /// <summary>The canonical relative path — segments joined by <c>/</c>, <c>""</c> for the root — or null.</summary>
    public static string? Normalize(string? path)
    {
        var segments = Segments(path);
        return segments is null ? null : String.Join('/', segments);
    }

    /// <summary>The path's segments, or null when it is not safe.</summary>
    public static string[]? Segments(string? path)
    {
        var text = path ?? String.Empty;

        if (text.Contains('\0') || text.Contains('\\'))
            return null;

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.AsSpan().IndexOfAny(Invalid) >= 0 || segment.Any(Char.IsControl))
                return null;
        }

        return segments;
    }

    /// <summary>The last segment: an entry's name. <c>""</c> for the root.</summary>
    public static string NameOf(string path) => path.Length == 0 ? String.Empty : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Everything before the last segment. <c>""</c> for an entry in the root.</summary>
    public static string ParentOf(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : String.Empty;

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or inside it.</summary>
    public static bool IsWithin(string path, string ancestor, StringComparison comparison)
        => ancestor.Length == 0
           || String.Equals(path, ancestor, comparison)
           || path.StartsWith(ancestor + "/", comparison);
}
