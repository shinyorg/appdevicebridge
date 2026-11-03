namespace Shiny.AppDeviceBridge.AspNetCore;

/// <summary>A release as the server knows it: the signed part, plus who may see it.</summary>
/// <param name="Release">What the host receives, and what gets signed.</param>
/// <param name="Channel">Null for the stable channel, which every host sees. Otherwise only hosts asking for this channel see it.</param>
/// <param name="Platforms">Null or empty for every platform; otherwise the platform names allowed, such as <c>ios</c> or <c>android</c>.</param>
/// <param name="DownloadUrl">An absolute URL to serve the zip from instead of this server — a CDN. Null uses the built-in download endpoint.</param>
/// <param name="ReleaseNotes">
/// Release notes in several languages. The check answers with the one matching the host's culture, in place of
/// <see cref="WebAppRelease.ReleaseNotes"/>; release notes are not signed, so this never touches the signature.
/// </param>
public sealed record WebAppReleaseEntry(
    WebAppRelease Release,
    string? Channel = null,
    IReadOnlyList<string>? Platforms = null,
    string? DownloadUrl = null,
    WebAppLocalizedText? ReleaseNotes = null
);

/// <summary>Per-app rules that are not a property of any one release.</summary>
public sealed record WebAppPolicy
{
    /// <summary>Web app builds below this version must update before the app is shown.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>
    /// Rules for the native app, keyed by platform — <c>ios</c>, <c>android</c>, <c>maccatalyst</c>, <c>macos</c>,
    /// <c>windows</c>, <c>linux</c> — or <c>*</c> for every platform without its own. Each store ships on its own schedule, so
    /// each platform has its own minimum.
    /// </summary>
    public IReadOnlyDictionary<string, WebAppHostPolicy>? Hosts { get; init; }
}

/// <summary>What the server requires of the native app on one platform.</summary>
public sealed record WebAppHostPolicy
{
    /// <summary>
    /// Native apps below this version are stopped at the splash screen with a link to the store. This is the only update
    /// a host cannot apply by itself, so reserve it for builds that genuinely cannot run what the server now serves.
    /// </summary>
    public string? MinimumVersion { get; init; }

    /// <summary>The version in the store. Native apps below it, and at or above the minimum, are told one is available.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>The store page, for hosts without Shiny's <c>IAppStore</c>. <c>https</c> or a store's own scheme.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>What the store build changes, shown on the splash screen when the update is required.</summary>
    public WebAppLocalizedText? ReleaseNotes { get; init; }
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
