using System.Globalization;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// Where web app releases come from — the Shiny release server (<see cref="ReleaseServerUpdateProvider"/>), GitHub
/// releases (<see cref="GitHubReleasesUpdateProvider"/>), a manifest file, an endpoint of the app's own. Set it on
/// <see cref="WebAppHostOptions.UpdateProvider"/>.
/// <para>
/// The provider only answers "is there something newer" and hands over its bytes. The host does the rest: it times the
/// check out after <see cref="WebAppHostOptions.CheckTimeout"/>, refuses anything not newer than what it runs, checks the
/// size and SHA-256 when the provider supplies them, and opens the archive before installing it. An exception from either
/// method is treated as being offline — except <see cref="InvalidDataException"/>, which rejects the release.
/// </para>
/// </summary>
public interface IUpdateProvider
{
    /// <summary>
    /// The web app release to move to, and anything the server says about the native app. <see cref="UpdateOffer.None"/>
    /// when there is nothing newer this host can run.
    /// </summary>
    Task<UpdateOffer> CheckAsync(UpdateRequest request, CancellationToken cancellationToken);

    /// <summary>Opens the zip for a release this provider returned. The host reads it to the end and disposes it.</summary>
    Task<Stream> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken);

    /// <summary>
    /// The release as a plain HTTP GET, for an OS background transfer that outlives the process —
    /// <c>Shiny.AppDeviceBridge.AutoUpdate</c> uses it. Null, the default, when the download needs more than a URL and
    /// headers: the release is then downloaded in-process through <see cref="DownloadAsync"/> instead. Whatever arrives is
    /// verified exactly as <see cref="DownloadAsync"/>'s bytes are.
    /// </summary>
    UpdateDownloadRequest? GetDownloadRequest(UpdateInfo update) => null;
}

/// <summary>What the host is running, for an update check.</summary>
/// <param name="HostVersion">The native app's version — <see cref="AppDeviceBridgeOptions.HostVersion"/> — without a prerelease label.</param>
/// <param name="AppVersion">The web app being served, bundled or downloaded. Null when there is none at all.</param>
/// <param name="Culture">The user's language, for release notes — <see cref="WebAppHostOptions.Culture"/>.</param>
public sealed record UpdateRequest(Version HostVersion, WebAppVersion? AppVersion, CultureInfo Culture);

/// <summary>The answer to an update check.</summary>
/// <param name="WebApp">The web app release to move to. Null when there is nothing newer.</param>
/// <param name="Host">A native app update in the store. Null when the provider knows of none, or knows nothing of the native app.</param>
public sealed record UpdateOffer(UpdateInfo? WebApp = null, HostUpdateInfo? Host = null)
{
    public static UpdateOffer None { get; } = new();
}

/// <summary>An HTTP GET a background transfer can perform without the provider.</summary>
public sealed record UpdateDownloadRequest(Uri Uri, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// A release offered by an <see cref="IUpdateProvider"/>. Providers derive from it to carry what their
/// <see cref="IUpdateProvider.DownloadAsync"/> needs, such as a download URL.
/// </summary>
public class UpdateInfo
{
    public required WebAppVersion Version { get; set; }

    /// <summary>
    /// False makes the host install the release before showing the app. True downloads it in the background and serves it
    /// from the next launch. With nothing installed at all, every release is required.
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>The release notes, in <see cref="UpdateRequest.Culture"/> when the provider has them.</summary>
    public string? WhatsNew { get; set; }

    public DateTimeOffset? ReleaseDate { get; set; }

    /// <summary>The zip's byte length. When set, a download of any other length is refused.</summary>
    public long? FileSize { get; set; }

    /// <summary>The zip's SHA-256 as hex. When set, a download that does not match it is refused.</summary>
    public string? Sha256 { get; set; }
}

/// <summary>
/// A newer native app, which only the app store can deliver. A required one stops the host at its splash screen with an
/// <b>Update</b> button, because the web app it would serve is not one this native app can run.
/// </summary>
public sealed class HostUpdateInfo
{
    /// <summary>True when this native app is below the server's minimum: the host shows nothing but the way to the store.</summary>
    public bool IsRequired { get; init; }

    /// <summary>The oldest native app version the server accepts.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>The version in the store.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>
    /// The store page, used when the app has no Shiny <c>IAppStore</c> registered. Only <c>https</c> and the stores' own
    /// schemes — see <see cref="WebAppProtocol.IsAllowedStoreUrl"/>.
    /// </summary>
    public Uri? StoreUri { get; init; }

    /// <summary>What the store build changes, in <see cref="UpdateRequest.Culture"/> when the server has it.</summary>
    public string? WhatsNew { get; init; }
}
