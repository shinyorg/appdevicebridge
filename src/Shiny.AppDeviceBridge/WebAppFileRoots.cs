using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Every file root the page can use: the ones the app configured (<see cref="AppDeviceBridgeOptions.FileRoots"/>, or
/// <c>data</c> and <c>cache</c>), and any a bridge adds while the app runs — a folder the user picked, for one.
/// <para>
/// Bridges that take a file from the page — to share it, upload it, attach it — resolve it here by the same
/// <c>{ root, path }</c> the files bridge uses, so the page cannot hand a bridge anything it could not already read.
/// </para>
/// </summary>
public sealed class WebAppFileRoots
{
    readonly ConcurrentDictionary<string, WebAppFileStore> roots = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> configured = new(StringComparer.OrdinalIgnoreCase);

    public WebAppFileRoots(AppDeviceBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Enabled = options.EnableFiles;

        foreach (var root in options.ResolveFileRoots())
        {
            this.roots[root.Name] = root;
            this.configured.Add(root.Name);
        }
    }

    /// <summary>Whether the page has file roots at all — <see cref="AppDeviceBridgeOptions.EnableFiles"/>.</summary>
    public bool Enabled { get; }

    /// <summary>Every root, by name.</summary>
    public IReadOnlyList<WebAppFileStore> All => [.. this.roots.Values.OrderBy(x => x.Name, StringComparer.Ordinal)];

    public bool TryGet(string? name, [NotNullWhen(true)] out WebAppFileStore? root)
    {
        root = null;
        return this.Enabled && name is not null && this.roots.TryGetValue(name, out root);
    }

    /// <summary>Whether the app configured the root, as opposed to a bridge adding it. Configured roots cannot be replaced.</summary>
    public bool IsConfigured(string name) => this.configured.Contains(name);

    /// <summary>
    /// Raised after a root is added, replaced or removed while the app runs — by a bridge, or by the app mapping a folder
    /// or a share. Raised on the thread that made the change. The files bridge passes it to the page as the
    /// <c>files.roots</c> event.
    /// </summary>
    public event EventHandler<WebAppFileRootsChangedEventArgs>? Changed;

    /// <summary>
    /// Adds a root, or replaces one added earlier under the same name. For a directory, pass a
    /// <see cref="WebAppFileRoot"/>; the name is what the page calls it, so a folder the user named "Team Docs" is added as
    /// something like <c>team-docs</c> and shown by its own name elsewhere.
    /// </summary>
    public void Add(WebAppFileStore root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (this.IsConfigured(root.Name))
            throw new InvalidOperationException($"'{root.Name}' is a file root the app configured; it cannot be replaced.");

        var replaced = false;
        this.roots.AddOrUpdate(root.Name, root, (_, _) =>
        {
            replaced = true;
            return root;
        });

        this.Changed?.Invoke(this, new WebAppFileRootsChangedEventArgs(root.Name, replaced ? FileRootChange.Replaced : FileRootChange.Added));
    }

    /// <summary>Removes a root added while the app runs. The app's configured roots stay.</summary>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (this.IsConfigured(name) || !this.roots.TryRemove(name, out var removed))
            return false;

        this.Changed?.Invoke(this, new WebAppFileRootsChangedEventArgs(removed.Name, FileRootChange.Removed));
        return true;
    }

    /// <summary>
    /// The absolute path for a page-supplied root name and relative path. False when files are disabled, the root does not
    /// exist or has no paths on disk, or the path is not a safe path inside it. The file itself need not exist.
    /// </summary>
    public bool TryResolve(string? root, string? path, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        if (!this.TryGet(root, out var store) || WebAppFilePath.Normalize(path) is not { } normalized)
            return false;

        fullPath = store.GetLocalPath(normalized);
        return fullPath is not null;
    }
}

/// <param name="Name">The root's name, as the page knows it.</param>
public sealed class WebAppFileRootsChangedEventArgs(string name, FileRootChange change) : EventArgs
{
    public string Name { get; } = name;

    public FileRootChange Change { get; } = change;
}
