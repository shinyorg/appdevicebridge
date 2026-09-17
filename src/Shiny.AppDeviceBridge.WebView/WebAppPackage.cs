using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.AppDeviceBridge.WebView;

public enum WebAppPackageOrigin
{
    /// <summary>The zip compiled into the app.</summary>
    Baseline,

    /// <summary>A verified download.</summary>
    Installed
}

/// <summary>A web app build that can be served.</summary>
public sealed record WebAppPackage(WebAppVersion Version, WebAppPackageOrigin Origin, string? ZipPath, WebAppBaseline? Baseline)
{
    internal WebAppContent Open(WebAppHostOptions options) => this.Origin switch
    {
        WebAppPackageOrigin.Installed => WebAppArchive.Open(this.ZipPath!, options),
        _ => WebAppArchive.Open(this.Baseline!, options)
    };
}

/// <summary>A package opened for serving: one source per variant, keyed by name — a single <c>""</c> without variants.</summary>
sealed record WebAppContent(IReadOnlyDictionary<string, IStaticFileSource> Variants);

static class WebAppArchive
{
    /// <summary>A Blazor publish carries .br and .gz beside every asset, already compressed at maximum effort.</summary>
    static readonly string[] Encodings = ["br", "gzip"];

    public static WebAppContent Open(string zipPath, WebAppHostOptions options)
        => Open(path => new ZipFileSource(zipPath, path) { PrecompressedEncodings = Encodings }, options);

    public static WebAppContent Open(WebAppBaseline baseline, WebAppHostOptions options)
        => Open(
            path => new ZipFileSource(baseline.Assembly, baseline.ResourceName, path) { PrecompressedEncodings = Encodings },
            options
        );

    /// <summary>
    /// Opens every variant the options declare, and refuses the archive if any of them has no entry document: a release that
    /// installs cleanly and then shows one kind of browser a 404 is worse than one that never installs.
    /// </summary>
    static WebAppContent Open(Func<string?, ZipFileSource> open, WebAppHostOptions options)
    {
        var variants = new Dictionary<string, IStaticFileSource>(StringComparer.Ordinal);

        if (options.VariantNames.Count == 0)
        {
            variants[String.Empty] = Resolve(open, options.EntryDocument, options.ArchiveBasePath, null);
        }
        else
        {
            foreach (var variant in options.VariantNames)
                variants[variant] = Resolve(open, options.EntryDocument, options.ArchiveBasePath, variant);
        }

        return new WebAppContent(variants);
    }

    /// <summary>
    /// Opens the archive at the folder that holds the entry document: <paramref name="basePath"/> when the app named one,
    /// otherwise the root and then <c>wwwroot/</c> — each under the variant's own folder when there is a variant. An
    /// archive without one is refused: it would install cleanly and then show a 404 in place of the app.
    /// </summary>
    static ZipFileSource Resolve(Func<string?, ZipFileSource> open, string entryDocument, string? basePath, string? variant)
    {
        var where = variant is null ? String.Empty : $"the '{variant}' variant's ";

        if (basePath is not null)
        {
            var path = variant is null ? basePath : $"{basePath.TrimEnd('/')}/{variant}";
            var explicitSource = open(path);
            return explicitSource.TryGetFile(entryDocument, out _)
                ? explicitSource
                : throw new InvalidDataException($"The archive has no '{entryDocument}' under {where}'{path}/'.");
        }

        var root = open(variant);
        if (root.TryGetFile(entryDocument, out _))
            return root;

        var wwwroot = open(variant is null ? "wwwroot" : $"{variant}/wwwroot");
        if (wwwroot.TryGetFile(entryDocument, out _))
            return wwwroot;

        throw new InvalidDataException(variant is null
            ? $"The archive has no '{entryDocument}' at its root or under wwwroot/."
            : $"The archive has no '{entryDocument}' under '{variant}/' or '{variant}/wwwroot/'.");
    }
}

/// <summary>
/// The static file source the server is built with, whose content can be replaced while it runs.
/// <para>
/// The server's pipeline is composed once, so the source it holds has to stay the same object. This is
/// that object; the package behind it changes. Read directly, it is the default variant; <see cref="For"/> is one variant.
/// </para>
/// </summary>
public sealed class WebAppFileSource : IPrecompressedFileSource
{
    readonly string defaultVariant;
    volatile Active? active;

    public WebAppFileSource() : this(String.Empty)
    {
    }

    internal WebAppFileSource(string defaultVariant) => this.defaultVariant = defaultVariant;

    public WebAppPackage? Package => this.active?.Package;

    internal void Activate(WebAppPackage package, WebAppContent content)
        => this.active = new Active(package, content);

    /// <summary>One variant of whatever package is active when a file is asked for.</summary>
    internal IPrecompressedFileSource For(string variant) => new VariantSource(this, variant);

    public bool TryGetFile(string relativePath, out StaticFile file)
        => this.TryGetFile(this.defaultVariant, relativePath, null, out file);

    public bool TryGetFile(string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
        => this.TryGetFile(this.defaultVariant, relativePath, acceptedEncodings, out file);

    bool TryGetFile(string variant, string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
    {
        // Read once: a swap between the lookup and the call must not mix two packages.
        if (this.active is { } current && current.Content.Variants.TryGetValue(variant, out var source))
        {
            if (acceptedEncodings is not null && source is IPrecompressedFileSource precompressed)
                return precompressed.TryGetFile(relativePath, acceptedEncodings, out file);

            return source.TryGetFile(relativePath, out file);
        }

        file = default;
        return false;
    }

    sealed record Active(WebAppPackage Package, WebAppContent Content);

    sealed class VariantSource(WebAppFileSource owner, string variant) : IPrecompressedFileSource
    {
        public bool TryGetFile(string relativePath, out StaticFile file)
            => owner.TryGetFile(variant, relativePath, null, out file);

        public bool TryGetFile(string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
            => owner.TryGetFile(variant, relativePath, acceptedEncodings, out file);
    }
}
