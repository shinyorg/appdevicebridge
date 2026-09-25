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
    /// <summary>The release to move to, or null when there is nothing newer this host can run.</summary>
    /// <param name="currentHostVersion">The native app's version — <see cref="AppDeviceBridgeOptions.HostVersion"/>.</param>
    /// <param name="currentAppVersion">The web app being served, bundled or downloaded. Null when there is none at all.</param>
    Task<UpdateInfo?> GetUpdateInfoAsync(Version currentHostVersion, WebAppVersion? currentAppVersion, CancellationToken cancellationToken);

    /// <summary>Opens the zip for a release this provider returned. The host reads it to the end and disposes it.</summary>
    Task<Stream> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken);
}

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

    public string? WhatsNew { get; set; }

    public DateTimeOffset? ReleaseDate { get; set; }

    /// <summary>The zip's byte length. When set, a download of any other length is refused.</summary>
    public long? FileSize { get; set; }

    /// <summary>The zip's SHA-256 as hex. When set, a download that does not match it is refused.</summary>
    public string? Sha256 { get; set; }
}
