using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// <c>/_bridge/files</c> — the page's own files, confined to named roots (see
/// <see cref="AppDeviceBridgeOptions.FileRoots"/>). Paths are relative to the root, use forward slashes, and
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
/// Parent directories are created as needed, and a failed or oversized upload never leaves half a file behind. Roots
/// come from <see cref="WebAppFileRoots"/>, so a folder added while the app runs is served the same way, and the page hears
/// about every such change as the <c>files.roots</c> event.
/// </para>
/// </summary>
public sealed class WebAppFilesBridge : IWebAppBridge
{
    readonly WebAppFileRoots roots;
    readonly AppDeviceBridgeOptions options;

    public WebAppFilesBridge(WebAppFileRoots roots, AppDeviceBridgeOptions options, WebAppEventHub events)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(events);

        this.roots = roots;
        this.options = options;

        // Names and what happened, never where a root lives on disk. Nothing when files are switched off: the page has no
        // roots to hear about.
        roots.Changed += (_, e) =>
        {
            if (roots.Enabled)
                events.Publish(
            "files.roots",
                    new FileRootsChanged(e.Name, e.Change),
                    AppDeviceBridgeJsonContext.Default.FileRootsChanged
                );
        };
    }

    public string Name => "files";

    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.RootsAsync)
        .MapGet("/{root}/list", ctx => this.WithPath(ctx, (root, path) => this.ListAsync(ctx, root, path)))
        .MapGet("/{root}/info", ctx => this.WithPath(ctx, (root, path) => InfoAsync(ctx, root, path)))
        .MapGet("/{root}/content", ctx => this.WithPath(ctx, (root, path) => ReadAsync(ctx, root, path)))
        .MapPut("/{root}/content", ctx => this.WithPath(ctx, (root, path) => this.WriteAsync(ctx, root, path, append: false)))
        .MapPost("/{root}/append", ctx => this.WithPath(ctx, (root, path) => this.WriteAsync(ctx, root, path, append: true)))
        .MapPost("/{root}/directory", ctx => this.WithPath(ctx, (root, path) => CreateDirectoryAsync(ctx, root, path)))
        .MapDelete("/{root}/entry", ctx => this.WithPath(ctx, (root, path) => DeleteAsync(ctx, root, path)))
        .MapPost("/{root}/move", ctx => this.WithRoot(ctx, root => TransferAsync(ctx, root, move: true)))
        .MapPost("/{root}/copy", ctx => this.WithRoot(ctx, root => TransferAsync(ctx, root, move: false)));

    ValueTask RootsAsync(HttpContext context)
    {
        // Names only. Where a root lives on disk is none of the page's business.
        IReadOnlyList<string> names = [.. this.roots.All.Select(x => x.Name)];
        return WebAppBridgeResults.Json(context, names, AppDeviceBridgeJsonContext.Default.IReadOnlyListString);
    }

    async ValueTask ListAsync(HttpContext context, WebAppFileStore root, string path)
        => await WebAppBridgeResults.Json(context, await root.ListAsync(path, context.RequestAborted), AppDeviceBridgeJsonContext.Default.IReadOnlyListFileEntry);

    static async ValueTask InfoAsync(HttpContext context, WebAppFileStore root, string path)
        => await (await root.GetEntryAsync(path, context.RequestAborted) is { } entry
            ? WebAppBridgeResults.Json(context, entry, AppDeviceBridgeJsonContext.Default.FileEntry)
            : WebAppBridgeResults.NotFound(context, "No such file or directory."));

    static async ValueTask ReadAsync(HttpContext context, WebAppFileStore root, string path)
    {
        var download = context.Request.Query["download"].ToString();
        var result = await root.ReadAsync(path, download.Length > 0 ? download : null, context.RequestAborted);
        await result.ExecuteAsync(context);
    }

    async ValueTask WriteAsync(HttpContext context, WebAppFileStore root, string path, bool append)
    {
        // Refused before the body is read, when the request says up front that it is too big.
        if (context.Request.ContentLength > this.options.MaxFileWriteBytes)
            throw WebAppFileException.TooLarge();

        var mode = append ? FileWriteMode.Append
            : IsFalse(context.Request.Query["overwrite"].ToString()) ? FileWriteMode.CreateNew
            : FileWriteMode.Replace;

        var written = await root.WriteAsync(path, context.Request.Body, mode, this.options.MaxFileWriteBytes, context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            written.Entry,
            AppDeviceBridgeJsonContext.Default.FileEntry,
            written.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK
        );
    }

    static async ValueTask CreateDirectoryAsync(HttpContext context, WebAppFileStore root, string path)
    {
        var created = await root.CreateDirectoryAsync(path, context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            created.Entry,
            AppDeviceBridgeJsonContext.Default.FileEntry,
            created.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK
        );
    }

    static async ValueTask DeleteAsync(HttpContext context, WebAppFileStore root, string path)
    {
        await root.DeleteAsync(path, IsTrue(context.Request.Query["recursive"].ToString()), context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    static async ValueTask TransferAsync(HttpContext context, WebAppFileStore root, bool move)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, AppDeviceBridgeJsonContext.Default.FileTransfer);

        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"from\": \"…\", \"to\": \"…\" }.");
            return;
        }

        if (WebAppFilePath.Normalize(body.From) is not { } from || WebAppFilePath.Normalize(body.To) is not { } to)
            throw WebAppFileException.InvalidPath();

        var entry = await root.TransferAsync(from, to, body.Overwrite, move, context.RequestAborted);
        await WebAppBridgeResults.Json(context, entry, AppDeviceBridgeJsonContext.Default.FileEntry);
    }

    /// <summary>Finds the root and turns every store failure into the response the page switches on.</summary>
    async ValueTask WithRoot(HttpContext context, Func<WebAppFileStore, ValueTask> action)
    {
        var name = context.Request.RouteValues["root"] ?? String.Empty;

        if (!this.roots.TryGet(name, out var root))
        {
            await WebAppBridgeResults.NotFound(context, $"No file root '{name}'.");
            return;
        }

        try
        {
            await action(root);
        }
        catch (WebAppFileException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
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

    ValueTask WithPath(HttpContext context, Func<WebAppFileStore, string, ValueTask> action)
        => this.WithRoot(context, root => WebAppFilePath.Normalize(context.Request.Query["path"].ToString()) is { } path
            ? action(root, path)
            : throw WebAppFileException.InvalidPath());

    static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0";
}
