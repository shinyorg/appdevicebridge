#if ANDROID
using Android.Content;
using AndroidX.DocumentFile.Provider;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;

namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// A picked Android folder: a Storage Access Framework tree, walked by name through <see cref="DocumentFile"/>. Nothing
/// here can leave the tree — the provider only resolves children of the granted root.
/// <para>
/// Writes go to a file in the app's cache first and are copied in once complete and within the limit, so a failed upload
/// leaves the document as it was. Moves are a copy and a delete: providers differ in what they can move directly.
/// </para>
/// </summary>
sealed class SafFileStore(string name, Context context, DocumentFile tree) : WebAppFileStore(name)
{
    const int BufferSize = 81920;

    ContentResolver Resolver => context.ContentResolver!;

    public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(this.Find(path) is { } document ? this.Entry(document, path) : null);

    public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var directory = this.Find(path) ?? throw WebAppFileException.NotFound("No such directory.");

        if (!directory.IsDirectory)
            throw WebAppFileException.NotADirectory();

        IReadOnlyList<FileEntry> entries =
        [
            .. Children(directory)
                .Where(x => x.Name is not null)
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => this.Entry(x, Combine(path, x.Name!)))
        ];

        return Task.FromResult(entries);
    }

    public override Task<IResult> ReadAsync(string path, string? downloadName, CancellationToken cancellationToken)
    {
        var document = this.Find(path) ?? throw WebAppFileException.NotFound("No such file.");

        if (document.IsDirectory)
            throw WebAppFileException.IsADirectory();

        IResult result = FileDownloadResult.FromOpener(
            _ => ValueTask.FromResult(this.Resolver.OpenInputStream(UriOf(document)) ?? throw WebAppFileException.NotFound("No such file.")),
            document.Length(),
            document.Type ?? ContentTypes.ForFileName(document.Name ?? String.Empty),
            downloadName,
            document.Name,
            DateTimeOffset.FromUnixTimeMilliseconds(document.LastModified())
        );

        return Task.FromResult(result);
    }

    public override async Task<FileWriteResult> WriteAsync(string path, Stream content, FileWriteMode mode, long maxBytes, CancellationToken cancellationToken)
    {
        if (path.Length == 0)
            throw WebAppFileException.IsADirectory();

        var existing = this.Find(path);

        if (existing is { IsDirectory: true })
            throw WebAppFileException.IsADirectory();

        if (mode == FileWriteMode.CreateNew && existing is not null)
            throw WebAppFileException.Exists("The file exists and overwrite is false.");

        var append = mode == FileWriteMode.Append && existing is not null;
        var staging = Path.Combine(context.CacheDir!.AbsolutePath, $"saf-{Guid.NewGuid():n}.tmp");

        try
        {
            await using (var target = File.Create(staging))
                await WebAppFileRootCopy.LimitedAsync(content, target, maxBytes - (append ? existing!.Length() : 0), cancellationToken);

            var parent = this.Directory(WebAppFilePath.ParentOf(path), create: true);
            var document = existing ?? parent.CreateFile(ContentTypes.ForFileName(WebAppFilePath.NameOf(path)), WebAppFilePath.NameOf(path))
                ?? throw new IOException("The folder's provider refused to create the file.");

            // "wt" truncates; "wa" appends. Both are part of the Storage Access Framework's contract.
            await using (var output = this.Resolver.OpenOutputStream(UriOf(document), append ? "wa" : "wt") ?? throw new IOException("The folder's provider refused to open the file."))
            await using (var source = File.OpenRead(staging))
                await source.CopyToAsync(output, BufferSize, cancellationToken);

            return new FileWriteResult(this.Entry(this.Find(path)!, path), existing is null);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public override Task<FileWriteResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var existing = this.Find(path);

        if (existing is { IsDirectory: false })
            throw WebAppFileException.Exists("A file already has that name.");

        var directory = existing ?? this.Directory(path, create: true);
        return Task.FromResult(new FileWriteResult(this.Entry(directory, path), existing is null));
    }

    public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        if (path.Length == 0)
            throw WebAppFileException.BadRequest("A root cannot be deleted.");

        var document = this.Find(path) ?? throw WebAppFileException.NotFound();

        if (document.IsDirectory && !recursive && Children(document).Length > 0)
            throw new WebAppFileException(StatusCodes.Status409Conflict, "not_empty", "The directory is not empty; pass recursive=true.");

        if (!document.Delete())
            throw new IOException("The folder's provider refused to delete it.");

        return Task.CompletedTask;
    }

    public override async Task<FileEntry> TransferAsync(string from, string to, bool overwrite, bool move, CancellationToken cancellationToken)
    {
        if (from.Length == 0 || to.Length == 0)
            throw WebAppFileException.BadRequest("A root cannot be moved, copied or replaced.");

        var source = this.Find(from) ?? throw WebAppFileException.NotFound();

        if (source.IsDirectory && WebAppFilePath.IsWithin(to, from, StringComparison.Ordinal))
            throw WebAppFileException.BadRequest("A directory cannot be put inside itself.");

        if (this.Find(to) is { } existing)
        {
            if (!overwrite || existing.IsDirectory || source.IsDirectory)
                throw WebAppFileException.Exists(
                    existing.IsDirectory || source.IsDirectory
                        ? "The destination exists. Directories are never merged or replaced."
                        : "The destination exists and overwrite is false."
                );

            existing.Delete();
        }

        await this.CopyAsync(source, to, cancellationToken);

        if (move)
            source.Delete();

        return this.Entry(this.Find(to)!, to);
    }

    async Task CopyAsync(DocumentFile source, string to, CancellationToken cancellationToken)
    {
        if (source.IsDirectory)
        {
            this.Directory(to, create: true);

            foreach (var child in Children(source).Where(x => x.Name is not null))
                await this.CopyAsync(child, Combine(to, child.Name!), cancellationToken);

            return;
        }

        var parent = this.Directory(WebAppFilePath.ParentOf(to), create: true);
        var target = parent.CreateFile(source.Type ?? ContentTypes.ForFileName(WebAppFilePath.NameOf(to)), WebAppFilePath.NameOf(to))
            ?? throw new IOException("The folder's provider refused to create the file.");

        await using var input = this.Resolver.OpenInputStream(UriOf(source)) ?? throw new IOException("The folder's provider refused to open the file.");
        await using var output = this.Resolver.OpenOutputStream(UriOf(target), "wt") ?? throw new IOException("The folder's provider refused to open the file.");
        await input.CopyToAsync(output, BufferSize, cancellationToken);
    }

    DocumentFile? Find(string path)
    {
        var current = tree;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.IsDirectory || current.FindFile(segment) is not { } next)
                return null;

            current = next;
        }

        return current;
    }

    DocumentFile Directory(string path, bool create)
    {
        var current = tree;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.FindFile(segment);

            if (next is { IsDirectory: false })
                throw WebAppFileException.Exists($"'{segment}' is a file, not a directory.");

            current = next ?? (create ? current.CreateDirectory(segment) : null)
                ?? throw WebAppFileException.NotFound("No such directory.");
        }

        return current;
    }

    FileEntry Entry(DocumentFile document, string path) => new(
        path.Length == 0 ? this.Name : document.Name ?? WebAppFilePath.NameOf(path),
        path,
        document.IsDirectory,
        document.IsDirectory ? null : document.Length(),
        DateTimeOffset.FromUnixTimeMilliseconds(document.LastModified())
    );

    static Android.Net.Uri UriOf(DocumentFile document) => document.Uri ?? throw new IOException("The folder's provider gave no URI for the document.");

    static DocumentFile[] Children(DocumentFile directory) => directory.ListFiles() ?? [];

    static string Combine(string path, string name) => path.Length == 0 ? name : path + "/" + name;
}
#endif
