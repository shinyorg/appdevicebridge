using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
using Shiny.AppDeviceBridge.Folders.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Folders;

public static class FoldersBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/folders</c>: the platform's folder picker, and every picked folder remembered as a file root the
    /// page uses through <c>/_bridge/files</c> — there is nothing else to call.
    /// <code>
    /// builder.AddFoldersBridge();
    /// </code>
    /// <para>
    /// Android keeps access through a persisted Storage Access Framework grant, so a picked folder is not a path there:
    /// the files bridge reads and writes it, but bridges that hand the OS a file path (sharing, transfers, notification
    /// images) cannot use it. Apple platforms keep a security-scoped bookmark; Windows and Linux keep the path.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddFoldersBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID
        // The picker is an activity; its answer comes back through the activity that started it.
        builder.ConfigureLifecycleEvents(events => events.AddAndroid(android => android
            .OnActivityResult((_, requestCode, resultCode, data) => FolderPlatform.OnActivityResult(requestCode, resultCode, data))
        ));
#endif

        builder.Services.AddWebAppBridge<FoldersBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/folders</c>.
/// <code>
/// GET    /_bridge/folders           { "supported": true, "folders": [{ "root": "photos", "displayName": "Pictures", "available": true }] }
/// POST   /_bridge/folders/pick      { "root": "photos", "title": "Choose a folder" }   204 when cancelled
/// DELETE /_bridge/folders/{root}
/// </code>
/// </summary>
public sealed class FoldersBridge : IWebAppBridge
{
    readonly WebAppFileRoots roots;
    readonly FolderMemory memory;
    readonly SemaphoreSlim picker = new(1, 1);

    public FoldersBridge(WebAppFileRoots roots, WebAppHostOptions options)
    {
        this.roots = roots;
        this.memory = new FolderMemory(Path.Combine(options.ResolveInstallDirectory(), "folders.json"));

        // Back as roots before the page asks for them. One that no longer opens stays listed as unavailable, so the page
        // can tell the user and offer to pick it again.
        if (roots.Enabled && FolderPlatform.IsSupported)
        {
            foreach (var saved in this.memory.All)
            {
                if (!roots.IsConfigured(saved.Root) && TryRestore(saved) is { } store)
                    roots.Add(store);
            }
        }
    }

    public string Name => "folders";

    public bool IsSupported => FolderPlatform.IsSupported && this.roots.Enabled;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.ListAsync)
        .MapPost("/pick", this.PickAsync)
        .MapDelete("/{root}", this.ForgetAsync);

    ValueTask ListAsync(HttpContext context)
        => WebAppBridgeResults.Json(
            context,
            new FolderList(
                this.IsSupported,
                [.. this.memory.All.Select(x => new PickedFolder(x.Root, x.DisplayName, this.roots.TryGet(x.Root, out _)))]
            ),
            FoldersJsonContext.Default.FolderList
        );

    async ValueTask PickAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "A folder picker");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, FoldersJsonContext.Default.FolderPickRequest) ?? new FolderPickRequest();
        var root = body.Root ?? $"folder-{Guid.NewGuid():n}"[..15];

        if (!WebAppFileStore.IsValidName(root))
        {
            await WebAppBridgeResults.BadRequest(context, "A root name is 1-64 letters, digits, '-' or '_'.");
            return;
        }

        if (this.roots.IsConfigured(root))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "configured_root", $"'{root}' is one of the app's own file roots.");
            return;
        }

        if (body.Title?.Length > 256)
        {
            await WebAppBridgeResults.BadRequest(context, "A title is at most 256 characters.");
            return;
        }

        // One picker on screen at a time; a second request would stack sheets or be dropped by the OS.
        if (!await this.picker.WaitAsync(0))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "picker_open", "A folder picker is already open.");
            return;
        }

        try
        {
            if (await FolderPlatform.PickAsync(body.Title, context.RequestAborted) is not { } picked)
            {
                await WebAppBridgeResults.NoContent(context);
                return;
            }

            var saved = new SavedFolder(root, picked.DisplayName, picked.Token);
            if (TryRestore(saved) is not { } store)
            {
                FolderPlatform.Release(picked.Token);
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "folder_unavailable", "The folder was picked but could not be opened.");
                return;
            }

            if (this.memory.Find(root) is { } previous && previous.Token != picked.Token)
                FolderPlatform.Release(previous.Token);

            this.roots.Add(store);
            this.memory.Save(saved);

            await WebAppBridgeResults.Json(context, new PickedFolder(root, picked.DisplayName), FoldersJsonContext.Default.PickedFolder);
        }
        finally
        {
            this.picker.Release();
        }
    }

    ValueTask ForgetAsync(HttpContext context)
    {
        var root = context.Request.RouteValues["root"] ?? String.Empty;

        if (this.memory.Find(root) is not { } saved)
            return WebAppBridgeResults.NotFound(context, $"No picked folder '{root}'.");

        this.roots.Remove(root);
        this.memory.Remove(root);
        FolderPlatform.Release(saved.Token);

        return WebAppBridgeResults.NoContent(context);
    }

    static WebAppFileStore? TryRestore(SavedFolder saved)
    {
        try
        {
            return FolderPlatform.Open(saved.Root, saved.Token);
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
