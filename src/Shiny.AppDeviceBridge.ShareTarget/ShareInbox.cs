using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.ShareTarget.Client;

namespace Shiny.AppDeviceBridge.ShareTarget;

public sealed class ShareTargetOptions
{
    /// <summary>
    /// The file root shared files are copied into, under <c>shares/{id}/</c>. The default, <c>data</c>, survives a
    /// restart; <c>cache</c> is for an app that acts on a share at once.
    /// </summary>
    public string Root { get; set; } = "data";

    /// <summary>The most shares kept waiting. Past it the oldest is dropped, with its files.</summary>
    public int MaxPending { get; set; } = 20;

    /// <summary>The most files taken from one share; the rest are ignored.</summary>
    public int MaxFiles { get; set; } = 20;

    /// <summary>The longest text kept from one share, in characters; longer text is cut.</summary>
    public int MaxTextLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Linux: treat the files the app was launched with as a share — the desktop's "Open With" passes them as arguments
    /// when the app's .desktop file has <c>%F</c> in its <c>Exec</c> line. Off by default, because any argument that is
    /// a readable file would be handed to the page.
    /// </summary>
    public bool AcceptCommandLineFiles { get; set; }
}

/// <summary>
/// What a platform delivered, before the bridge copies it: text, a link and files the bridge reads once. Platform code
/// the bridge does not hook itself — a share extension's handoff, an AppKit app delegate — builds one and passes it to
/// <see cref="ShareTarget.ReceiveAsync"/>.
/// </summary>
/// <param name="Text">Shared text. Text that is only an http or https link becomes the share's URL.</param>
/// <param name="Subject">The subject or title the sending app gave.</param>
/// <param name="Url">A shared link. Anything but an absolute http or https URL is kept as text instead.</param>
/// <param name="Files">Shared files.</param>
public sealed record IncomingShare(
    string? Text = null,
    string? Subject = null,
    string? Url = null,
    IReadOnlyList<IncomingShareFile>? Files = null
);

/// <summary>A shared file, opened only when the bridge copies it.</summary>
/// <param name="FileName">The name the sending app gave; one is made up when it gave none.</param>
/// <param name="ContentType">Its media type, when known.</param>
/// <param name="OpenAsync">Opens the file for reading. The bridge disposes the stream.</param>
public sealed record IncomingShareFile(string? FileName, string? ContentType, Func<CancellationToken, Task<Stream>> OpenAsync)
{
    /// <summary>A file on this device's file system.</summary>
    public static IncomingShareFile FromPath(string path, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(Path.GetFileName(path), contentType, _ => Task.FromResult<Stream>(File.OpenRead(path)));
    }
}

/// <summary>
/// The queue of shares waiting for the page. Platform code hands shares to <see cref="ReceiveAsync"/>; their files are
/// copied into <see cref="ShareTargetOptions.Root"/> under <c>shares/{id}/</c>, and the share waits — kept on disk across
/// restarts — until the page removes it.
/// <para>
/// Unlike a link, every share is kept: it is content the user chose to send here, and dropping all but the latest would
/// lose some of it.
/// </para>
/// </summary>
public sealed class ShareInbox
{
    const int MaxSubjectLength = 1024;
    const int MaxFileNameLength = 120;

    // What WebAppFilePath refuses in a segment, so a cleaned name is always a valid path on every platform.
    static readonly HashSet<char> InvalidNameChars = [.. Path.GetInvalidFileNameChars(), '<', '>', '"', '|', '?', '*', ':', '\\', '/', '\0'];

    readonly ShareTargetOptions options;
    readonly WebAppFileRoots roots;
    readonly long maxFileBytes;
    readonly string indexPath;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    List<SharedContent>? pending;

    public ShareInbox(ShareTargetOptions options, WebAppFileRoots roots, AppDeviceBridgeOptions bridgeOptions, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(bridgeOptions);

        this.options = options;
        this.roots = roots;
        this.maxFileBytes = bridgeOptions.MaxFileWriteBytes;
        this.indexPath = Path.Combine(bridgeOptions.ResolveDataDirectory(), "sharetarget.json");
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ShareInbox>();
    }

    public ShareTargetOptions Options => this.options;

    /// <summary>Whether shared files have somewhere to go: the configured root exists.</summary>
    public bool IsSupported => this.roots.TryGet(this.options.Root, out _);

    /// <summary>Raised for each share once its files are copied, on the thread that copied them.</summary>
    public event Action<SharedContent>? Received;

    /// <summary>The waiting shares, oldest first.</summary>
    public async Task<IReadOnlyList<SharedContent>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. this.Load()];
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<SharedContent?> FindAsync(string id, CancellationToken cancellationToken = default)
        => (await this.GetPendingAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(x => x.Id == id);

    /// <summary>
    /// Copies a share's files and queues it. Returns null when nothing usable arrived — no text, no link and no file that
    /// could be read. One file that fails to copy is left out rather than failing the share.
    /// </summary>
    public async Task<SharedContent?> ReceiveAsync(IncomingShare share, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(share);

        var (text, url) = this.Classify(share.Text, share.Url);
        var subject = Clip(share.Subject, MaxSubjectLength);
        var id = Guid.NewGuid().ToString("n");
        var files = await this.CopyFilesAsync(id, share.Files ?? [], cancellationToken).ConfigureAwait(false);

        if (text is null && url is null && files.Count == 0)
            return null;

        var content = new SharedContent(id, DateTimeOffset.UtcNow, text, subject, url, files);
        List<SharedContent> dropped;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = this.Load();
            list.Add(content);

            var excess = Math.Max(0, list.Count - Math.Max(1, this.options.MaxPending));
            dropped = list.GetRange(0, excess);
            list.RemoveRange(0, excess);

            this.Save(list);
        }
        finally
        {
            this.gate.Release();
        }

        foreach (var old in dropped)
            await this.DeleteFilesAsync(old.Id).ConfigureAwait(false);

        this.Received?.Invoke(content);
        return content;
    }

    /// <summary>Takes a share off the queue and deletes its files. Returns false when there is no share by the id.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = this.Load();
            if (list.RemoveAll(x => x.Id == id) == 0)
                return false;

            this.Save(list);
        }
        finally
        {
            this.gate.Release();
        }

        // Only an id found in the queue reaches the file system, so a page cannot name a path through it.
        await this.DeleteFilesAsync(id).ConfigureAwait(false);
        return true;
    }

    (string? Text, string? Url) Classify(string? text, string? url)
    {
        text = Clip(text, this.options.MaxTextLength);
        url = url?.Trim();

        // A link the page may navigate to: http and https only, never javascript:, file: or an app's own scheme.
        if (!String.IsNullOrEmpty(url) && !IsWebLink(url))
        {
            text ??= Clip(url, this.options.MaxTextLength);
            url = null;
        }

        // Android, and most apps' share sheets, send a link as text.
        if (String.IsNullOrEmpty(url) && text is not null && IsWebLink(text))
            return (null, text);

        return (text, String.IsNullOrEmpty(url) ? null : url);
    }

    async Task<IReadOnlyList<SharedFile>> CopyFilesAsync(string id, IReadOnlyList<IncomingShareFile> incoming, CancellationToken cancellationToken)
    {
        if (incoming.Count == 0)
            return [];

        if (!this.roots.TryGet(this.options.Root, out var root))
        {
            this.logger.LogWarning("Shared files were dropped: there is no file root '{Root}'.", this.options.Root);
            return [];
        }

        var files = new List<SharedFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in incoming.Take(Math.Max(0, this.options.MaxFiles)))
        {
            var name = UniqueName(SafeName(file.FileName, files.Count + 1), names);
            var path = $"shares/{id}/{name}";

            try
            {
                await using var content = await file.OpenAsync(cancellationToken).ConfigureAwait(false);
                var written = await root.WriteAsync(path, content, FileWriteMode.CreateNew, this.maxFileBytes, cancellationToken).ConfigureAwait(false);
                files.Add(new SharedFile(new BridgeFile(root.Name, path), file.FileName ?? name, file.ContentType, written.Entry.Size ?? 0));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A provider that revoked access, a file over the size limit: the share keeps whatever else arrived.
                this.logger.LogWarning(ex, "A shared file '{FileName}' could not be copied.", file.FileName);
            }
        }

        return files;
    }

    async Task DeleteFilesAsync(string id)
    {
        if (!this.roots.TryGet(this.options.Root, out var root))
            return;

        try
        {
            await root.DeleteAsync($"shares/{id}", recursive: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebAppFileException or IOException or UnauthorizedAccessException)
        {
            // Already gone (the page deleted or moved the files), or locked; the queue entry is gone either way.
            this.logger.LogDebug(ex, "The files of share {Id} could not be deleted.", id);
        }
    }

    /// <summary>
    /// A file name that is one safe path segment: the sending app's name, without any directories it claimed, with
    /// characters no platform allows replaced, and short enough to keep its extension.
    /// </summary>
    internal static string SafeName(string? fileName, int ordinal)
    {
        var name = fileName?.Replace('\\', '/');
        name = name is null ? null : name[(name.LastIndexOf('/') + 1)..];

        if (!String.IsNullOrWhiteSpace(name))
        {
            var chars = name.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Char.IsControl(chars[i]) || InvalidNameChars.Contains(chars[i]))
                    chars[i] = '_';
            }

            name = new String(chars).TrimStart('.');
        }

        if (String.IsNullOrWhiteSpace(name))
            return $"file{ordinal}";

        if (name.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(name);
            extension = extension.Length is > 1 and <= 16 ? extension : String.Empty;
            name = name[..(MaxFileNameLength - extension.Length)] + extension;
        }

        return WebAppFilePath.Normalize(name) is { Length: > 0 } safe && !safe.Contains('/') ? safe : $"file{ordinal}";
    }

    static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];

        for (var i = 2; !taken.Add(candidate); i++)
            candidate = $"{stem}-{i}{extension}";

        return candidate;
    }

    static bool IsWebLink(string value)
        => !value.Any(Char.IsWhiteSpace)
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";

    static string? Clip(string? value, int max)
    {
        value = value?.Trim();
        if (String.IsNullOrEmpty(value))
            return null;

        return value.Length <= max ? value : value[..Math.Max(0, max)];
    }

    List<SharedContent> Load()
    {
        if (this.pending is not null)
            return this.pending;

        try
        {
            this.pending = File.Exists(this.indexPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(this.indexPath), ShareTargetJsonContext.Default.ListSharedContent) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            // A damaged queue forgets its shares rather than stopping the app from starting.
            this.logger.LogWarning(ex, "The share queue at {Path} could not be read and was reset.", this.indexPath);
            this.pending = [];
        }

        return this.pending;
    }

    void Save(List<SharedContent> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.indexPath)!);

        var temp = this.indexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, ShareTargetJsonContext.Default.ListSharedContent));
        File.Move(temp, this.indexPath, overwrite: true);
    }
}

/// <summary>
/// For shares the bridge cannot hook itself — an AppKit app delegate's <c>application:openURLs:</c>, a share extension's
/// handoff, a head with no lifecycle events.
/// <code>
/// // maui-labs AppKit head: files opened with the app arrive before MauiProgram runs
/// public override void OpenUrls(NSApplication application, NSUrl[] urls)
///     => _ = ShareTarget.ReceiveAsync(new IncomingShare(Files: [.. urls.Where(x => x.IsFileUrl).Select(x => IncomingShareFile.FromPath(x.Path!))]));
/// </code>
/// </summary>
public static class ShareTarget
{
    const int MaxEarly = 20;

    static readonly Lock Gate = new();
    static readonly List<(IncomingShare Share, TaskCompletionSource<SharedContent?> Done)> Early = [];
    static ShareInbox? current;

    /// <summary>
    /// Offers a share to the web app, and completes once its files are copied — so a caller holding the files open (a
    /// security-scoped URL, a Windows share operation) knows when it may let go. One that arrives before
    /// <c>AddShareTargetBridge</c>'s inbox exists is held until it does. Never throws: a share that cannot be read
    /// completes with null, so a platform callback can fire and forget.
    /// </summary>
    public static Task<SharedContent?> ReceiveAsync(IncomingShare share)
    {
        ArgumentNullException.ThrowIfNull(share);

        ShareInbox? inbox;
        lock (Gate)
        {
            inbox = current;
            if (inbox is null)
            {
                if (Early.Count >= MaxEarly)
                {
                    Early[0].Done.TrySetResult(null);
                    Early.RemoveAt(0);
                }

                var done = new TaskCompletionSource<SharedContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Early.Add((share, done));
                return done.Task;
            }
        }

        return Deliver(inbox, share);
    }

    internal static void Attach(ShareInbox inbox)
    {
        (IncomingShare Share, TaskCompletionSource<SharedContent?> Done)[] held;
        lock (Gate)
        {
            current = inbox;
            held = [.. Early];
            Early.Clear();
        }

        if (held.Length > 0)
            _ = Task.Run(async () =>
            {
                foreach (var (share, done) in held)
                    done.TrySetResult(await Deliver(inbox, share).ConfigureAwait(false));
            });
    }

    static async Task<SharedContent?> Deliver(ShareInbox inbox, IncomingShare share)
    {
        try
        {
            return await inbox.ReceiveAsync(share).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Platform callbacks cannot act on a failure, and the inbox has logged what it could not copy.
            return null;
        }
    }
}
