using System.Diagnostics.CodeAnalysis;

namespace Shiny.WebAppHost;

/// <summary>
/// For bridges that take a file from the page — to share it, upload it, attach it — by the same
/// <c>{ root, path }</c> the files bridge uses, so the page cannot hand a bridge anything it could not already
/// read through <c>/_bridge/files</c>.
/// </summary>
public static class WebAppFileRoots
{
    /// <summary>
    /// The absolute path for a page-supplied root name and relative path. False when files are disabled, the
    /// root does not exist, or the path is not a safe path inside it. The file itself need not exist.
    /// </summary>
    public static bool TryResolve(WebAppHostOptions options, string? root, string? path, [NotNullWhen(true)] out string? fullPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        fullPath = null;

        if (!options.EnableFiles || !WebAppFileRoot.IsValidName(root))
            return false;

        var match = options.ResolveFileRoots().FirstOrDefault(x => String.Equals(x.Name, root, StringComparison.OrdinalIgnoreCase));
        if (match?.Resolve(path) is not { } resolved || match.IsRoot(resolved))
            return false;

        fullPath = resolved;
        return true;
    }
}
