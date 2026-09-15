namespace Shiny.WebAppHost;

/// <summary>
/// A directory the page may use through <c>/_bridge/files/{name}</c>, and nothing outside it.
/// <para>
/// Every path arrives from the page, and the page is only as trustworthy as the least careful script
/// it loads. So a path is refused rather than repaired: no <c>..</c>, no backslashes (a separator on
/// Windows), no drive or stream colons, no characters the platform will not put in a name — and no
/// symbolic link, anywhere along the way, that leads out of the root.
/// </para>
/// </summary>
public sealed class WebAppFileRoot
{
    /// <summary>The file system decides case sensitivity, and the containment check has to agree with it.</summary>
    static readonly StringComparison PathComparison = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    public WebAppFileRoot(string name, string path)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' is not a valid file root name.", nameof(name));

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.Name = name;
        this.FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public string Name { get; }

    /// <summary>The absolute directory. Never sent to the page.</summary>
    public string FullPath { get; }

    public static bool IsValidName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 64
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// The absolute path for a page-supplied relative one, or null when it is not a safe path inside the
    /// root. Null, empty and <c>/</c> name the root itself.
    /// </summary>
    public string? Resolve(string? relativePath)
    {
        var text = relativePath ?? String.Empty;

        if (text.Contains('\0') || text.Contains('\\'))
            return null;

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':') || segment.AsSpan().IndexOfAny(invalid) >= 0)
                return null;
        }

        var full = Path.GetFullPath(Path.Combine([this.FullPath, .. segments]));

        if (!this.Contains(full))
            return null;

        // Walk up from the target to the root: a link at any level redirects everything beneath it, and
        // the entry itself may not exist yet — a file about to be written into a linked directory.
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

    bool Contains(string fullPath)
        => fullPath.StartsWith(this.FullPath + Path.DirectorySeparatorChar, PathComparison)
           || String.Equals(fullPath, this.FullPath, PathComparison);
}
