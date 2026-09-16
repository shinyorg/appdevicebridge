using System.Diagnostics.CodeAnalysis;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Where everything sits under the server's origin, resolved once from
/// <see cref="AppDeviceBridgeOptions.BasePath"/> and <see cref="AppDeviceBridgeOptions.BridgePrefix"/>.
/// <code>
/// o.BasePath = "/kiosk";          //  http://127.0.0.1:5780/kiosk/
/// o.BridgePrefix = "/_native";    //  http://127.0.0.1:5780/kiosk/_native/app/info
/// </code>
/// <para>
/// <c>_host</c> does not move with them. It is how the page finds out where everything else is —
/// <c>GET {base}/_host/config</c> answers with the mount points — so it has to sit somewhere a client can
/// reach knowing nothing but the document it was served from.
/// </para>
/// </summary>
public sealed class WebAppPaths
{
    public const string DefaultBasePath = "/";
    public const string DefaultBridgePrefix = "/_bridge";

    /// <summary>The session and discovery endpoints, always directly under <see cref="Base"/>.</summary>
    public const string HostSegment = "/_host";

    WebAppPaths(string basePath, string bridgePrefix)
    {
        this.Base = basePath;
        this.BaseWithSlash = basePath.Length == 0 ? "/" : basePath + "/";
        this.Bridge = basePath + bridgePrefix;
        this.Host = basePath + HostSegment;
        this.Start = this.Host + "/start";
        this.Ping = this.Host + "/ping";
        this.Config = this.Host + "/config";
    }

    /// <summary>The mount point with no trailing slash — empty at the root, else <c>/kiosk</c>.</summary>
    public string Base { get; }

    /// <summary>The mount point as a directory: <c>/</c> at the root, else <c>/kiosk/</c>. This is the page's <c>&lt;base href&gt;</c>.</summary>
    public string BaseWithSlash { get; }

    /// <summary><c>/_bridge</c>, or wherever it was moved to, with <see cref="Base"/> already on the front.</summary>
    public string Bridge { get; }

    public string Host { get; }

    public string Start { get; }

    public string Ping { get; }

    /// <summary>What a page fetches to learn the other two. See <see cref="WebAppPathsResponse"/>.</summary>
    public string Config { get; }

    public static WebAppPaths From(AppDeviceBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new WebAppPaths(Normalize(options.BasePath), Normalize(options.BridgePrefix));
    }

    /// <summary>Trims a configured path to <c>""</c> or <c>/a/b</c>, so the rest of the code can just concatenate.</summary>
    public static string Normalize(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
            return String.Empty;

        var trimmed = value.Trim().Trim('/');
        return trimmed.Length == 0 ? String.Empty : "/" + trimmed;
    }

    /// <summary>
    /// Whether a configured path is one this server can mount: <c>/</c>, or segments of unreserved URL
    /// characters. Anything that would need escaping, or could climb out, is refused at registration rather
    /// than turning into a 404 nobody can explain.
    /// </summary>
    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
            return true;

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                return false;

            foreach (var c in segment)
            {
                if (!Char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '~'))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The request path with <see cref="Base"/> taken off the front, or false when the request landed outside
    /// the mount point entirely. <c>/kiosk</c> with no trailing slash strips to <c>/</c>.
    /// </summary>
    public bool TryStripBase(string path, [NotNullWhen(true)] out string? relative)
    {
        relative = null;

        if (this.Base.Length == 0)
        {
            relative = path;
            return true;
        }

        if (!path.StartsWith(this.Base, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = path[this.Base.Length..];
        if (rest.Length == 0)
        {
            relative = "/";
            return true;
        }

        if (rest[0] != '/')
            return false;      // /kioskether — a different path that merely starts the same way

        relative = rest;
        return true;
    }

    /// <summary>True when the path is the mount point without its trailing slash, which is worth a redirect.</summary>
    public bool IsBaseWithoutSlash(string path)
        => this.Base.Length > 0 && String.Equals(path, this.Base, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The answer to <c>GET {base}/_host/config</c>: where this host put everything, so a web app that ships
/// separately from it does not have to be told twice.
/// </summary>
/// <param name="Base">The mount point as a directory, matching the page's <c>&lt;base href&gt;</c>.</param>
/// <param name="Bridge">The bridge prefix, absolute and with a trailing slash.</param>
public sealed record WebAppPathsResponse(string Base, string Bridge);
