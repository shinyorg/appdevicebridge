namespace Shiny.AppDeviceBridge.AspNetCore;

/// <summary>A release as the server knows it: the signed part, plus who may see it.</summary>
/// <param name="Release">What the host receives, and what gets signed.</param>
/// <param name="Channel">Null for the stable channel, which every host sees. Otherwise only hosts asking for this channel see it.</param>
/// <param name="Platforms">Null or empty for every platform; otherwise the platform names allowed, such as <c>ios</c> or <c>android</c>.</param>
/// <param name="DownloadUrl">An absolute URL to serve the zip from instead of this server — a CDN. Null uses the built-in download endpoint.</param>
public sealed record WebAppReleaseEntry(
    WebAppRelease Release,
    string? Channel = null,
    IReadOnlyList<string>? Platforms = null,
    string? DownloadUrl = null
);

/// <summary>Per-app rules that are not a property of any one release.</summary>
public sealed record WebAppPolicy
{
    /// <summary>Hosts running below this version must update before the app is shown.</summary>
    public string? MinimumVersion { get; init; }
}

/// <summary>Where releases come from. <see cref="FileSystemWebAppReleaseStore"/> is the built-in one.</summary>
public interface IWebAppReleaseStore
{
    /// <summary>Every release of an app, or null when the app is unknown.</summary>
    Task<IReadOnlyList<WebAppReleaseEntry>?> GetReleasesAsync(string appId, CancellationToken cancellationToken);

    Task<WebAppPolicy?> GetPolicyAsync(string appId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a release's zip, or returns null when there is no such release. The version arrives from
    /// the URL: an implementation must look it up, never splice it into a path.
    /// </summary>
    Task<Stream?> OpenReleaseAsync(string appId, string version, CancellationToken cancellationToken);
}
