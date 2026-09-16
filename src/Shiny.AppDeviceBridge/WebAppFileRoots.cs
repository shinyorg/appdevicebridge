using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Every file root the page can use: the ones the app configured (<see cref="WebAppHostOptions.FileRoots"/>, or
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

    public WebAppFileRoots(WebAppHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Enabled = options.EnableFiles;

        foreach (var root in options.ResolveFileRoots())
        {
            this.roots[root.Name] = root;
            this.configured.Add(root.Name);
        }
    }

    /// <summary>Whether the page has file roots at all — <see cref="WebAppHostOptions.EnableFiles"/>.</summary>
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

    /// <summary>Adds a root, or replaces one a bridge added earlier under the same name.</summary>
    public void Add(WebAppFileStore root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (this.IsConfigured(root.Name))
            throw new InvalidOperationException($"'{root.Name}' is a file root the app configured; it cannot be replaced.");

        this.roots[root.Name] = root;
    }

    /// <summary>Removes a root a bridge added. The app's own roots stay.</summary>
    public bool Remove(string name) => !this.IsConfigured(name) && this.roots.TryRemove(name, out _);

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
