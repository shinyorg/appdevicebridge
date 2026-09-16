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
    internal ZipFileSource Open(string entryDocument, string? basePath) => this.Origin switch
    {
        WebAppPackageOrigin.Installed => WebAppArchive.Open(this.ZipPath!, entryDocument, basePath),
        _ => WebAppArchive.Open(this.Baseline!, entryDocument, basePath)
    };
}

static class WebAppArchive
{
    /// <summary>A Blazor publish carries .br and .gz beside every asset, already compressed at maximum effort.</summary>
    static readonly string[] Encodings = ["br", "gzip"];

    public static ZipFileSource Open(string zipPath, string entryDocument, string? basePath)
        => Resolve(path => new ZipFileSource(zipPath, path) { PrecompressedEncodings = Encodings }, entryDocument, basePath);

    public static ZipFileSource Open(WebAppBaseline baseline, string entryDocument, string? basePath)
        => Resolve(
            path => new ZipFileSource(baseline.Assembly, baseline.ResourceName, path) { PrecompressedEncodings = Encodings },
            entryDocument,
            basePath
        );

    /// <summary>
    /// Opens the archive at the folder that holds the entry document. An archive without one is refused:
    /// it would install cleanly and then show a 404 in place of the app.
    /// </summary>
    static ZipFileSource Resolve(Func<string?, ZipFileSource> open, string entryDocument, string? basePath)
    {
        if (basePath is not null)
        {
            var explicitSource = open(basePath);
            return explicitSource.TryGetFile(entryDocument, out _)
                ? explicitSource
                : throw new InvalidDataException($"The archive has no '{entryDocument}' under '{basePath}/'.");
        }

        var root = open(null);
        if (root.TryGetFile(entryDocument, out _))
            return root;

        var wwwroot = open("wwwroot");
        if (wwwroot.TryGetFile(entryDocument, out _))
            return wwwroot;

        throw new InvalidDataException($"The archive has no '{entryDocument}' at its root or under wwwroot/.");
    }
}

/// <summary>
/// The static file source the server is built with, whose content can be replaced while it runs.
/// <para>
/// The server's pipeline is composed once, so the source it holds has to stay the same object. This is
/// that object; the package behind it changes.
/// </para>
/// </summary>
public sealed class WebAppFileSource : IPrecompressedFileSource
{
    volatile Active? active;

    public WebAppPackage? Package => this.active?.Package;

    internal void Activate(WebAppPackage package, IStaticFileSource source)
        => this.active = new Active(package, source);

    public bool TryGetFile(string relativePath, out StaticFile file)
    {
        if (this.active is { } current)
            return current.Source.TryGetFile(relativePath, out file);

        file = default;
        return false;
    }

    public bool TryGetFile(string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
    {
        // Read once: a swap between the type check and the call must not mix two packages.
        var current = this.active;

        if (current?.Source is IPrecompressedFileSource precompressed)
            return precompressed.TryGetFile(relativePath, acceptedEncodings, out file);

        if (current is not null)
            return current.Source.TryGetFile(relativePath, out file);

        file = default;
        return false;
    }

    sealed record Active(WebAppPackage Package, IStaticFileSource Source);
}
