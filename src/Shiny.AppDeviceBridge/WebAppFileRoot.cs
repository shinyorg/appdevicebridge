using System.Buffers;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// A directory on disk the page may use through <c>/_bridge/files/{name}</c>, and nothing outside it.
/// <para>
/// Every path arrives from the page, and the page is only as trustworthy as the least careful script it loads. On top of
/// <see cref="WebAppFilePath"/>'s checks, no symbolic link anywhere along the way may lead out of the root. Writes land in
/// a temporary file beside the target and are moved into place, so a failed or oversized upload never leaves half a file.
/// </para>
/// </summary>
public sealed class WebAppFileRoot : WebAppFileStore
{
    const string TempSuffix = ".appdevicebridge-tmp";

    /// <summary>The file system decides case sensitivity, and the containment check has to agree with it.</summary>
    static readonly StringComparison PathComparison = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    public WebAppFileRoot(string name, string path) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>The absolute directory. Never sent to the page.</summary>
    public string FullPath { get; }

    /// <summary>
    /// The absolute path for a page-supplied relative one, or null when it is not a safe path inside the root. Null, empty
    /// and <c>/</c> name the root itself.
    /// </summary>
    public string? Resolve(string? relativePath)
    {
        if (WebAppFilePath.Segments(relativePath) is not { } segments)
            return null;

        var full = Path.GetFullPath(Path.Combine([this.FullPath, .. segments]));

        if (!this.Contains(full))
            return null;

        // Walk up from the target to the root: a link at any level redirects everything beneath it, and the entry itself
        // may not exist yet — a file about to be written into a linked directory.
        for (var probe = full; probe.Length > this.FullPath.Length; probe = Path.GetDirectoryName(probe)!)
        {
            FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);

            if (info.Exists && info.LinkTarget is not null
                && (info.ResolveLinkTarget(returnFinalTarget: true) is not { } target || !this.Contains(target.FullName)))
                return null;
        }

        return full;
    }

    public bool IsRoot(string fullPath) => String.Equals(fullPath, this.FullPath, PathComparison);

    /// <summary>The page's view of an absolute path: relative to the root, with forward slashes.</summary>
    public string ToRelative(string fullPath)
    {
        var relative = Path.GetRelativePath(this.FullPath, fullPath);
        return relative == "." ? String.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public override string? GetLocalPath(string path) => this.Resolve(path) is { } full && !this.IsRoot(full) ? full : null;

    public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(Existing(this.Require(path)) is { } info ? this.Entry(info) : null);

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var full = this.Require(path);
        var directory = new DirectoryInfo(full);

        if (!directory.Exists)
            throw File.Exists(full) ? WebAppFileException.NotADirectory() : WebAppFileException.NotFound("No such directory.");

        IReadOnlyList<FileEntry> entries =
        [
            .. directory
                .EnumerateFileSystemInfos()
                .Where(x => !x.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                .OrderByDescending(x => x is DirectoryInfo)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(this.Entry)
        ];

        return Task.FromResult(entries);
    }

    public override Task<IResult> ReadAsync(string path, string? downloadName, CancellationToken cancellationToken)
    {
        var full = this.Require(path);

        if (Directory.Exists(full))
            throw WebAppFileException.IsADirectory();

        if (!File.Exists(full))
            throw WebAppFileException.NotFound("No such file.");

        return Task.FromResult<IResult>(FileDownloadResult.FromFile(full, contentType: null, downloadName: downloadName));
    }

    public override async Task<FileWriteResult> WriteAsync(string path, Stream content, FileWriteMode mode, long maxBytes, CancellationToken cancellationToken)
    {
        var full = this.Require(path);

        if (this.IsRoot(full) || Directory.Exists(full))
            throw WebAppFileException.IsADirectory();

        var exists = File.Exists(full);
        var append = mode == FileWriteMode.Append;

        if (mode == FileWriteMode.CreateNew && exists)
            throw WebAppFileException.Exists("The file exists and overwrite is false.");

        var limit = maxBytes - (append && exists ? new FileInfo(full).Length : 0);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Written beside the target so the final move stays on one volume and is a rename, not a copy.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():n}{TempSuffix}");

        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (append && exists)
                {
                    await using var current = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    await current.CopyToAsync(target, cancellationToken);
                }

                await WebAppFileRootCopy.LimitedAsync(content, target, limit, cancellationToken);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new FileWriteResult(this.Entry(new FileInfo(full)), !exists);
    }

    public override Task<FileWriteResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var full = this.Require(path);

        if (File.Exists(full))
            throw WebAppFileException.Exists("A file already has that name.");

        var existed = Directory.Exists(full);
        return Task.FromResult(new FileWriteResult(this.Entry(Directory.CreateDirectory(full)), !existed));
    }

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var full = this.Require(path);

        if (this.IsRoot(full))
            throw WebAppFileException.BadRequest("A root cannot be deleted.");

        if (File.Exists(full))
        {
            File.Delete(full);
            return Task.CompletedTask;
        }

        var directory = new DirectoryInfo(full);
        if (!directory.Exists)
            throw WebAppFileException.NotFound();

        if (!recursive && directory.EnumerateFileSystemInfos().Any())
            throw new WebAppFileException(StatusCodes.Status409Conflict, "not_empty", "The directory is not empty; pass recursive=true.");

        // A link inside is deleted as a link — Directory.Delete does not follow them — so nothing outside the root can go
        // with it.
        directory.Delete(recursive);
        return Task.CompletedTask;
    }

    public override Task<FileEntry> TransferAsync(string from, string to, bool overwrite, bool move, CancellationToken cancellationToken)
    {
        var source = this.Require(from);
        var destination = this.Require(to);

        if (this.IsRoot(source) || this.IsRoot(destination))
            throw WebAppFileException.BadRequest("A root cannot be moved, copied or replaced.");

        if (Existing(source) is not { } entry)
            throw WebAppFileException.NotFound();

        if (entry is DirectoryInfo && destination.StartsWith(source + Path.DirectorySeparatorChar, PathComparison))
            throw WebAppFileException.BadRequest("A directory cannot be put inside itself.");

        var existing = Existing(destination);

        // A case-only rename names the same entry on a case-insensitive file system; it is not a conflict.
        var sameEntry = existing is not null && String.Equals(entry.FullName, existing.FullName, StringComparison.OrdinalIgnoreCase);

        if (existing is not null && !sameEntry && (!overwrite || existing is DirectoryInfo || entry is DirectoryInfo))
        {
            throw WebAppFileException.Exists(
                existing is DirectoryInfo || entry is DirectoryInfo
                    ? "The destination exists. Directories are never merged or replaced."
                    : "The destination exists and overwrite is false."
            );
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        switch (entry, move)
        {
            case (FileInfo, true):
                File.Move(source, destination, overwrite);
                break;

            case (FileInfo file, false):
                file.CopyTo(destination, overwrite);
                break;

            case (DirectoryInfo, true):
                Directory.Move(source, destination);
                break;

            case (DirectoryInfo directory, false):
                CopyDirectory(directory, destination);
                break;
        }

        return Task.FromResult(this.Entry(Existing(destination)!));
    }

    string Require(string path) => this.Resolve(path) ?? throw WebAppFileException.InvalidPath();

    bool Contains(string fullPath)
        => fullPath.StartsWith(this.FullPath + Path.DirectorySeparatorChar, PathComparison)
           || String.Equals(fullPath, this.FullPath, PathComparison);

    FileEntry Entry(FileSystemInfo info)
    {
        info.Refresh();

        return new FileEntry(
            this.IsRoot(info.FullName) ? this.Name : info.Name,
            this.ToRelative(info.FullName),
            info is DirectoryInfo,
            info is FileInfo file ? file.Length : null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        );
    }

    static FileSystemInfo? Existing(string path)
        => Directory.Exists(path) ? new DirectoryInfo(path)
            : File.Exists(path) ? new FileInfo(path)
            : null;

    /// <summary>Links are skipped, not followed: a copy must not pull in what lies outside the root.</summary>
    static void CopyDirectory(DirectoryInfo source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                continue;

            var destination = Path.Combine(target, entry.Name);

            if (entry is DirectoryInfo directory)
                CopyDirectory(directory, destination);
            else
                ((FileInfo)entry).CopyTo(destination, overwrite: false);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>For stores: a body copied up to the write limit, and <c>too_large</c> past it.</summary>
public static class WebAppFileRootCopy
{
    /// <summary>Copies at most <paramref name="limit"/> bytes, failing with <c>too_large</c> past it.</summary>
    public static async Task LimitedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            long total = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > limit)
                    throw WebAppFileException.TooLarge();

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
