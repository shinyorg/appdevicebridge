using System.Buffers;
using System.Text.Json.Serialization;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;

namespace Shiny.WebAppHost;

/// <summary>
/// <c>/_bridge/files</c> — the page's own files, confined to named roots (see
/// <see cref="WebAppHostOptions.FileRoots"/>). Paths are relative to the root, use forward slashes, and
/// travel in the <c>path</c> query parameter.
/// <code>
/// GET    /_bridge/files                                  the roots
/// GET    /_bridge/files/{root}/list?path=photos           a directory's entries
/// GET    /_bridge/files/{root}/info?path=a.txt            one entry; 404 when missing
/// GET    /_bridge/files/{root}/content?path=a.txt         the bytes, with ranges and ETags (&amp;download=name for an attachment)
/// PUT    /_bridge/files/{root}/content?path=a.txt         the body becomes the file (&amp;overwrite=false to refuse replacing)
/// POST   /_bridge/files/{root}/append?path=log.txt        the body is appended, creating the file
/// POST   /_bridge/files/{root}/directory?path=photos/2026 creates the directory and its parents
/// DELETE /_bridge/files/{root}/entry?path=photos          deletes a file, or an empty directory (&amp;recursive=true for any)
/// POST   /_bridge/files/{root}/move                       { "from": "a.txt", "to": "b/c.txt", "overwrite": false } — also renames
/// POST   /_bridge/files/{root}/copy                       same body; directories copy recursively
/// </code>
/// <para>
/// Parent directories are created as needed. Writes land in a temporary file beside the target and are
/// moved into place, so a failed or oversized upload never leaves half a file behind.
/// </para>
/// </summary>
public sealed class WebAppFilesBridge : IWebAppBridge
{
    const string TempSuffix = ".webapphost-tmp";

    readonly Dictionary<string, WebAppFileRoot> roots;
    readonly long maxWriteBytes;

    public WebAppFilesBridge(WebAppHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.roots = options.ResolveFileRoots().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        this.maxWriteBytes = options.MaxFileWriteBytes;
    }

    public string Name => "files";

    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.RootsAsync)
        .MapGet("/{root}/list", ctx => this.WithPath(ctx, this.ListAsync))
        .MapGet("/{root}/info", ctx => this.WithPath(ctx, this.InfoAsync))
        .MapGet("/{root}/content", ctx => this.WithPath(ctx, this.ReadAsync))
        .MapPut("/{root}/content", ctx => this.WithPath(ctx, this.WriteAsync))
        .MapPost("/{root}/append", ctx => this.WithPath(ctx, this.AppendAsync))
        .MapPost("/{root}/directory", ctx => this.WithPath(ctx, this.CreateDirectoryAsync))
        .MapDelete("/{root}/entry", ctx => this.WithPath(ctx, this.DeleteAsync))
        .MapPost("/{root}/move", ctx => this.WithRoot(ctx, root => this.TransferAsync(ctx, root, move: true)))
        .MapPost("/{root}/copy", ctx => this.WithRoot(ctx, root => this.TransferAsync(ctx, root, move: false)));

    ValueTask RootsAsync(HttpContext context)
    {
        // Names only. Where a root lives on disk is none of the page's business.
        List<string> names = [.. this.roots.Keys.Order(StringComparer.Ordinal)];
        return WebAppBridgeResults.Json(context, names, WebAppFilesJsonContext.Default.ListString);
    }

    ValueTask ListAsync(HttpContext context, WebAppFileRoot root, string path)
    {
        var directory = new DirectoryInfo(path);

        if (!directory.Exists)
            return File.Exists(path)
                ? WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_a_directory", "The path is a file.")
                : WebAppBridgeResults.NotFound(context, "No such directory.");

        List<WebAppFileEntry> entries =
        [
            .. directory
                .EnumerateFileSystemInfos()
                .Where(x => !x.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                .OrderByDescending(x => x is DirectoryInfo)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => Entry(root, x))
        ];

        return WebAppBridgeResults.Json(context, entries, WebAppFilesJsonContext.Default.ListWebAppFileEntry);
    }

    ValueTask InfoAsync(HttpContext context, WebAppFileRoot root, string path)
        => Existing(path) is { } info
            ? WebAppBridgeResults.Json(context, Entry(root, info), WebAppFilesJsonContext.Default.WebAppFileEntry)
            : WebAppBridgeResults.NotFound(context, "No such file or directory.");

    ValueTask ReadAsync(HttpContext context, WebAppFileRoot root, string path)
    {
        if (Directory.Exists(path))
            return WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "is_a_directory", "The path is a directory.");

        if (!File.Exists(path))
            return WebAppBridgeResults.NotFound(context, "No such file.");

        var download = context.Request.Query["download"].ToString();
        IResult result = FileDownloadResult.FromFile(path, contentType: null, downloadName: download.Length > 0 ? download : null);

        return result.ExecuteAsync(context);
    }

    ValueTask WriteAsync(HttpContext context, WebAppFileRoot root, string path)
        => this.WriteBodyAsync(context, root, path, append: false);

    ValueTask AppendAsync(HttpContext context, WebAppFileRoot root, string path)
        => this.WriteBodyAsync(context, root, path, append: true);

    async ValueTask WriteBodyAsync(HttpContext context, WebAppFileRoot root, string path, bool append)
    {
        if (root.IsRoot(path) || Directory.Exists(path))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "is_a_directory", "The path is a directory.");
            return;
        }

        var exists = File.Exists(path);

        if (!append && exists && IsFalse(context.Request.Query["overwrite"].ToString()))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "exists", "The file exists and overwrite is false.");
            return;
        }

        var existingLength = append && exists ? new FileInfo(path).Length : 0;
        var limit = this.maxWriteBytes - existingLength;

        if (context.Request.ContentLength > limit)
        {
            await TooLarge(context);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the target so the final move stays on one volume and is a rename, not a copy.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():n}{TempSuffix}");

        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (append && exists)
                {
                    await using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    await current.CopyToAsync(target, context.RequestAborted);
                }

                await CopyLimitedAsync(context.Request.Body, target, limit, context.RequestAborted);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (WriteLimitExceededException)
        {
            TryDelete(temp);
            await TooLarge(context);
            return;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        await WebAppBridgeResults.Json(
            context,
            Entry(root, new FileInfo(path)),
            WebAppFilesJsonContext.Default.WebAppFileEntry,
            exists ? StatusCodes.Status200OK : StatusCodes.Status201Created
        );
    }

    ValueTask CreateDirectoryAsync(HttpContext context, WebAppFileRoot root, string path)
    {
        if (File.Exists(path))
            return WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "exists", "A file already has that name.");

        var existed = Directory.Exists(path);
        var directory = Directory.CreateDirectory(path);

        return WebAppBridgeResults.Json(
            context,
            Entry(root, directory),
            WebAppFilesJsonContext.Default.WebAppFileEntry,
            existed ? StatusCodes.Status200OK : StatusCodes.Status201Created
        );
    }

    ValueTask DeleteAsync(HttpContext context, WebAppFileRoot root, string path)
    {
        if (root.IsRoot(path))
            return WebAppBridgeResults.BadRequest(context, "A root cannot be deleted.");

        if (File.Exists(path))
        {
            File.Delete(path);
            return WebAppBridgeResults.NoContent(context);
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
            return WebAppBridgeResults.NotFound(context, "No such file or directory.");

        var recursive = IsTrue(context.Request.Query["recursive"].ToString());
        if (!recursive && directory.EnumerateFileSystemInfos().Any())
            return WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_empty", "The directory is not empty; pass recursive=true.");

        // A link inside is deleted as a link — Directory.Delete does not follow them — so nothing outside
        // the root can go with it.
        directory.Delete(recursive);
        return WebAppBridgeResults.NoContent(context);
    }

    async ValueTask TransferAsync(HttpContext context, WebAppFileRoot root, bool move)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, WebAppFilesJsonContext.Default.WebAppFileTransfer);

        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"from\": \"…\", \"to\": \"…\" }.");
            return;
        }

        if (root.Resolve(body.From) is not { } from || root.Resolve(body.To) is not { } to)
        {
            await InvalidPath(context);
            return;
        }

        if (root.IsRoot(from) || root.IsRoot(to))
        {
            await WebAppBridgeResults.BadRequest(context, "A root cannot be moved, copied or replaced.");
            return;
        }

        if (Existing(from) is not { } source)
        {
            await WebAppBridgeResults.NotFound(context, "No such file or directory.");
            return;
        }

        if (source is DirectoryInfo && to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            await WebAppBridgeResults.BadRequest(context, "A directory cannot be put inside itself.");
            return;
        }

        var destination = Existing(to);

        // A case-only rename names the same entry on a case-insensitive file system; it is not a conflict.
        var sameEntry = destination is not null && String.Equals(source.FullName, destination.FullName, StringComparison.OrdinalIgnoreCase);

        if (destination is not null && !sameEntry && (!body.Overwrite || destination is DirectoryInfo || source is DirectoryInfo))
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status409Conflict,
                "exists",
                destination is DirectoryInfo || source is DirectoryInfo
                    ? "The destination exists. Directories are never merged or replaced."
                    : "The destination exists and overwrite is false."
            );
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        switch (source, move)
        {
            case (FileInfo, true):
                File.Move(from, to, body.Overwrite);
                break;

            case (FileInfo file, false):
                file.CopyTo(to, body.Overwrite);
                break;

            case (DirectoryInfo, true):
                Directory.Move(from, to);
                break;

            case (DirectoryInfo directory, false):
                CopyDirectory(directory, to);
                break;
        }

        await WebAppBridgeResults.Json(context, Entry(root, Existing(to)!), WebAppFilesJsonContext.Default.WebAppFileEntry);
    }

    async ValueTask WithRoot(HttpContext context, Func<WebAppFileRoot, ValueTask> action)
    {
        var name = context.Request.RouteValues["root"] ?? String.Empty;

        if (!this.roots.TryGetValue(name, out var root))
        {
            await WebAppBridgeResults.NotFound(context, $"No file root '{name}'.");
            return;
        }

        try
        {
            Directory.CreateDirectory(root.FullPath);
            await action(root);
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is UnauthorizedAccessException or IOException)
        {
            await (ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => WebAppBridgeResults.NotFound(context, "No such file or directory."),
                UnauthorizedAccessException => WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "access_denied", "The operating system refused access."),
                _ => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "io_error", ex.Message)
            });
        }
    }

    ValueTask WithPath(HttpContext context, Func<HttpContext, WebAppFileRoot, string, ValueTask> action)
        => this.WithRoot(context, root => root.Resolve(context.Request.Query["path"].ToString()) is { } path
            ? action(context, root, path)
            : InvalidPath(context));

    static ValueTask InvalidPath(HttpContext context)
        => WebAppBridgeResults.Error(
            context,
            StatusCodes.Status400BadRequest,
            "invalid_path",
            "Paths are relative to the root, use '/', and cannot contain '..', '\\', ':' or lead outside the root."
        );

    static ValueTask TooLarge(HttpContext context)
        => WebAppBridgeResults.Error(context, StatusCodes.Status413PayloadTooLarge, "too_large", "The file would exceed the maximum size.");

    static FileSystemInfo? Existing(string path)
        => Directory.Exists(path) ? new DirectoryInfo(path)
            : File.Exists(path) ? new FileInfo(path)
            : null;

    static WebAppFileEntry Entry(WebAppFileRoot root, FileSystemInfo info)
    {
        info.Refresh();

        return new WebAppFileEntry(
            root.IsRoot(info.FullName) ? root.Name : info.Name,
            root.ToRelative(info.FullName),
            info is DirectoryInfo,
            info is FileInfo file ? file.Length : null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        );
    }

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

    static async Task CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
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
                    throw new WriteLimitExceededException();

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0";

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

    sealed class WriteLimitExceededException : Exception;
}

/// <param name="Name">The entry's name; the root's name for the root itself.</param>
/// <param name="Path">Relative to the root, with forward slashes. Empty for the root.</param>
/// <param name="Size">Bytes, for files.</param>
public sealed record WebAppFileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTimeOffset Modified);

public sealed record WebAppFileTransfer(string From, string To, bool Overwrite = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(WebAppFileEntry))]
[JsonSerializable(typeof(List<WebAppFileEntry>))]
[JsonSerializable(typeof(WebAppFileTransfer))]
partial class WebAppFilesJsonContext : JsonSerializerContext;
