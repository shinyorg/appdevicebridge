using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.LifecycleEvents;
using Shiny.AppDeviceBridge.Folders.Client;
using Shiny.Net.HttpServer;
using Shiny.AppDeviceBridge.Maui;

namespace Shiny.AppDeviceBridge.Folders;

public static class FoldersBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/folders</c>: the platform's folder picker, and every picked folder remembered as a file root the
    /// page uses through <c>/_bridge/files</c> — there is nothing else to call. Also registers <see cref="FolderRoots"/>, for
    /// the app to keep folders of its own by path.
    /// <code>
    /// bridge.AddFoldersBridge();
    /// </code>
    /// <para>
    /// Android keeps access through a persisted Storage Access Framework grant, so a picked folder is not a path there:
    /// the files bridge reads and writes it, but bridges that hand the OS a file path (sharing, transfers, notification
    /// images) cannot use it. Apple platforms keep a security-scoped bookmark; Windows and Linux keep the path.
    /// </para>
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddFoldersBridge(this MauiAppDeviceBridgeBuilder bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID
        // The picker is an activity; its answer comes back through the activity that started it.
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddAndroid(android => android
            .OnActivityResult((_, requestCode, resultCode, data) => FolderPlatform.OnActivityResult(requestCode, resultCode, data))
        ));
#endif

        bridge.Services.TryAddSingleton<FolderRoots>();
        bridge.AddBridge<FoldersBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/folders</c> — <see cref="FolderRoots"/> for the page.
/// <code>
/// GET    /_bridge/folders           { "supported": true, "folders": [{ "root": "photos", "displayName": "Pictures", "available": true }] }
/// POST   /_bridge/folders/pick      { "root": "photos", "title": "Choose a folder" }   204 when cancelled
/// DELETE /_bridge/folders/{root}
/// </code>
/// <para>
/// The page can pick a folder and forget one, but never name a path: a folder by path is added by the app, through
/// <see cref="FolderRoots.Add"/>, where the decision about what the page may read belongs.
/// </para>
/// </summary>
public sealed class FoldersBridge(FolderRoots folders) : IWebAppBridge
{
    public string Name => "folders";

    public bool IsSupported => folders.CanPick;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.ListAsync)
        .MapPost("/pick", this.PickAsync)
        .MapDelete("/{root}", this.ForgetAsync);

    ValueTask ListAsync(HttpContext context)
        => WebAppBridgeResults.Json(context, new FolderList(this.IsSupported, folders.All), FoldersJsonContext.Default.FolderList);

    async ValueTask PickAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "A folder picker");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, FoldersJsonContext.Default.FolderPickRequest) ?? new FolderPickRequest();

        if (body.Title?.Length > 256)
        {
            await WebAppBridgeResults.BadRequest(context, "A title is at most 256 characters.");
            return;
        }

        try
        {
            if (await folders.PickAsync(body.Root, body.Title, context.RequestAborted) is { } picked)
                await WebAppBridgeResults.Json(context, picked, FoldersJsonContext.Default.PickedFolder);
            else
                await WebAppBridgeResults.NoContent(context);
        }
        catch (WebAppFileException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    ValueTask ForgetAsync(HttpContext context)
    {
        var root = context.Request.RouteValues["root"] ?? String.Empty;

        return folders.Forget(root)
            ? WebAppBridgeResults.NoContent(context)
            : WebAppBridgeResults.NotFound(context, $"No picked folder '{root}'.");
    }
}

/// <summary>
/// Folders kept as file roots across launches: the ones the user picks with the platform's picker, and the ones the app adds
/// by path — a folder mapped from a tray menu, a share an administrator published. Each is a root in
/// <see cref="WebAppFileRoots"/> under a name the page uses, remembered in the app's data directory and restored the next
/// time the app runs.
/// <code>
/// public sealed class ShareService(FolderRoots folders)
/// {
///     public PickedFolder Publish(string name, string path) => folders.Add(name, path, displayName: name);
///     public bool Unpublish(string name) => folders.Forget(name);
/// }
/// </code>
/// <para>
/// A folder that no longer opens — moved, deleted, its access revoked — stays listed with <see cref="PickedFolder.Available"/>
/// false, so the app can say so and offer to pick it again. Failures the page should hear about are
/// <see cref="WebAppFileException"/>, carrying the status and code the folders bridge answers with.
/// </para>
/// </summary>
public sealed class FolderRoots
{
    readonly WebAppFileRoots roots;
    readonly FolderMemory memory;
    readonly SemaphoreSlim picker = new(1, 1);
    readonly Lock gate = new();

    public FolderRoots(WebAppFileRoots roots, AppDeviceBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(options);

        this.roots = roots;
        this.memory = new FolderMemory(Path.Combine(options.ResolveDataDirectory(), "folders.json"));

        // Back as roots before the page asks for them.
        if (roots.Enabled)
        {
            foreach (var saved in this.memory.All)
            {
                if (!roots.IsConfigured(saved.Root) && TryRestore(saved) is { } store)
                    roots.Add(store);
            }
        }
    }

    /// <summary>Whether this platform has a folder picker, and files are switched on.</summary>
    public bool CanPick => FolderPlatform.IsSupported && this.roots.Enabled;

    /// <summary>Every remembered folder, picked or added, and whether it opens right now.</summary>
    public IReadOnlyList<PickedFolder> All
        => [.. this.memory.All.Select(x => new PickedFolder(x.Root, x.DisplayName, this.roots.TryGet(x.Root, out _)))];

    /// <summary>
    /// Shows the platform's folder picker and keeps the folder as a root. Null when the user cancels. A root name already in
    /// use by a remembered folder is replaced; one is chosen when <paramref name="root"/> is null.
    /// </summary>
    /// <exception cref="WebAppFileException">
    /// <c>not_supported</c> (501) without a picker; <c>bad_request</c> for a root name that is not 1–64 letters, digits,
    /// <c>-</c> or <c>_</c>; <c>configured_root</c> for one of the app's own roots; <c>picker_open</c> while a picker is on
    /// screen; <c>folder_unavailable</c> when the chosen folder cannot be opened.
    /// </exception>
    public async Task<PickedFolder?> PickAsync(string? root = null, string? title = null, CancellationToken cancellationToken = default)
    {
        if (!this.CanPick)
            throw new WebAppFileException(StatusCodes.Status501NotImplemented, "not_supported", "A folder picker is not available on this platform.");

        root ??= $"folder-{Guid.NewGuid():n}"[..15];
        this.CheckName(root);

        // One picker on screen at a time; a second request would stack sheets or be dropped by the OS.
        if (!await this.picker.WaitAsync(0, cancellationToken))
            throw new WebAppFileException(StatusCodes.Status409Conflict, "picker_open", "A folder picker is already open.");

        try
        {
            if (await FolderPlatform.PickAsync(title, cancellationToken) is not { } picked)
                return null;

            return this.Keep(new SavedFolder(root, picked.DisplayName, picked.Token), "The folder was picked but could not be opened.");
        }
        finally
        {
            this.picker.Release();
        }
    }

    /// <summary>
    /// Keeps a folder the app already reaches by path as a root, replacing a remembered folder under the same name. For the
    /// folders the app decides on — a path the user typed into the app's settings, one its own native dialog returned —
    /// never a path the page supplied. On Apple platforms a folder the app currently has security-scoped access to is kept
    /// as a bookmark, so the access outlives the launch.
    /// </summary>
    /// <param name="root">The name the page uses: 1–64 letters, digits, <c>-</c> or <c>_</c>.</param>
    /// <param name="path">An absolute path to a directory that exists.</param>
    /// <param name="displayName">What to call it where the page shows folders. The directory's own name by default.</param>
    /// <exception cref="WebAppFileException">
    /// <c>bad_request</c> for an invalid name or a relative path; <c>configured_root</c> for one of the app's own roots;
    /// <c>folder_unavailable</c> when the directory does not exist or cannot be opened.
    /// </exception>
    public PickedFolder Add(string root, string path, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.CheckName(root);

        if (!Path.IsPathFullyQualified(path))
            throw WebAppFileException.BadRequest($"A folder is an absolute path; '{path}' is not.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
            throw new WebAppFileException(StatusCodes.Status409Conflict, "folder_unavailable", "The folder does not exist.");

        var name = String.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(full) is { Length: > 0 } last ? last : full
            : displayName;

        return this.Keep(new SavedFolder(root, name, FolderPlatform.TokenForPath(full)), "The folder exists but could not be opened.");
    }

    /// <summary>Forgets a remembered folder: its root goes away and any access the platform granted is given back. False when there is none by that name.</summary>
    public bool Forget(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        SavedFolder? saved;
        lock (this.gate)
        {
            saved = this.memory.Find(root);
            if (saved is null)
                return false;

            this.memory.Remove(root);
            this.roots.Remove(saved.Root);
        }

        FolderPlatform.Release(saved.Token);
        return true;
    }

    void CheckName(string root)
    {
        if (!WebAppFileStore.IsValidName(root))
            throw WebAppFileException.BadRequest("A root name is 1-64 letters, digits, '-' or '_'.");

        if (this.roots.IsConfigured(root))
            throw new WebAppFileException(StatusCodes.Status409Conflict, "configured_root", $"'{root}' is one of the app's own file roots.");
    }

    /// <summary>Opens the folder, then remembers it and makes it a root — in that order, so nothing is remembered that does not open.</summary>
    PickedFolder Keep(SavedFolder saved, string unavailable)
    {
        if (TryRestore(saved) is not { } store)
        {
            FolderPlatform.Release(saved.Token);
            throw new WebAppFileException(StatusCodes.Status409Conflict, "folder_unavailable", unavailable);
        }

        SavedFolder? previous;
        lock (this.gate)
        {
            previous = this.memory.Find(saved.Root);
            this.roots.Add(store);
            this.memory.Save(saved);
        }

        if (previous is not null && previous.Token != saved.Token)
            FolderPlatform.Release(previous.Token);

        return new PickedFolder(saved.Root, saved.DisplayName);
    }

    static WebAppFileStore? TryRestore(SavedFolder saved)
    {
        try
        {
            return FolderPlatform.Restore(saved.Root, saved.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>What the platform picker hands back: a name to show, and whatever the platform needs to open it again.</summary>
readonly record struct PickedLocation(string DisplayName, string Token);

sealed record SavedFolder(string Root, string DisplayName, string Token);

/// <summary>
/// The picked folders, in a file beside the installed builds. Tokens — bookmarks, content URIs, paths — are what reopen a
/// folder; none of them is sent to the page.
/// </summary>
sealed class FolderMemory(string path)
{
    readonly Lock gate = new();
    List<SavedFolder>? folders;

    public IReadOnlyList<SavedFolder> All
    {
        get
        {
            lock (this.gate)
                return [.. this.Load()];
        }
    }

    public SavedFolder? Find(string root)
    {
        lock (this.gate)
            return this.Load().FirstOrDefault(x => String.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(SavedFolder folder)
    {
        lock (this.gate)
        {
            var list = this.Load();
            list.RemoveAll(x => String.Equals(x.Root, folder.Root, StringComparison.OrdinalIgnoreCase));
            list.Add(folder);
            this.Write(list);
        }
    }

    public void Remove(string root)
    {
        lock (this.gate)
        {
            var list = this.Load();
            if (list.RemoveAll(x => String.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase)) > 0)
                this.Write(list);
        }
    }

    List<SavedFolder> Load()
    {
        if (this.folders is not null)
            return this.folders;

        try
        {
            this.folders = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), FolderMemoryJsonContext.Default.ListSavedFolder) ?? []
                : [];
        }
        catch (JsonException)
        {
            // A damaged file forgets the folders rather than stopping the app from starting.
            this.folders = [];
        }

        return this.folders;
    }

    void Write(List<SavedFolder> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, FolderMemoryJsonContext.Default.ListSavedFolder));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SavedFolder>))]
partial class FolderMemoryJsonContext : JsonSerializerContext;
