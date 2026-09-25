using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// Releases from a <c>Shiny.AppDeviceBridge.AspNetCore</c> release server — the prefix given to <c>MapWebAppReleases</c>,
/// such as <c>https://api.example.com/webapps</c>. Every release it offers is signed: one whose signature does not verify
/// against <paramref name="publicKey"/>, that is for another app, or that needs a newer host is rejected before anything is
/// downloaded.
/// <code>
/// webApp.UpdateProvider = new ReleaseServerUpdateProvider(new Uri("https://api.example.com/webapps"), WebAppKeys.Public);
/// </code>
/// </summary>
/// <param name="server">The release server's base address.</param>
/// <param name="publicKey">The ECDSA P-256 public key releases are signed with, as PEM or base64.</param>
/// <param name="httpMessageHandlerFactory">Supplies the handler for checks and downloads — for certificate pinning, a proxy, or tests.</param>
public sealed class ReleaseServerUpdateProvider(Uri server, string publicKey, Func<HttpMessageHandler>? httpMessageHandlerFactory = null)
    : IUpdateProvider, IDisposable
{
    readonly ECDsa key = WebAppReleaseSignature.ImportPublicKey(publicKey);
    // Created on first use, so a handler factory can depend on something not built yet when the provider is.
    readonly Lazy<HttpClient> http = new(() => CreateClient(httpMessageHandlerFactory));

    /// <summary>A prerelease channel to follow, such as <c>beta</c>. Null follows stable releases only.</summary>
    public string? Channel { get; set; }

    /// <summary>The app id releases are published under. Defaults to <see cref="AppDeviceBridgeOptions.AppId"/>.</summary>
    public string? AppId { get; set; }

    /// <summary>The platform sent to the server, for platform-specific releases. Defaults to <see cref="AppDeviceBridgeOptions.Platform"/>.</summary>
    public string? Platform { get; set; }

    // The host's version as configured, prerelease label and all, for the server's minimum-host comparison.
    string? hostVersion;

    public Uri Server { get; } = server ?? throw new ArgumentNullException(nameof(server));

    static HttpClient CreateClient(Func<HttpMessageHandler>? factory)
    {
        var client = factory is null ? new HttpClient() : new HttpClient(factory(), disposeHandler: true);

        // Downloads can legitimately take minutes; the host times out the check and cancels the rest.
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    internal void Bind(AppDeviceBridgeOptions bridge)
    {
        this.AppId ??= bridge.AppId;
        this.Platform ??= bridge.Platform;
        this.hostVersion = bridge.HostVersion;
    }

    public async Task<UpdateInfo?> GetUpdateInfoAsync(Version currentHostVersion, WebAppVersion? currentAppVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentHostVersion);

        if (String.IsNullOrWhiteSpace(this.AppId))
            throw new InvalidOperationException("ReleaseServerUpdateProvider.AppId is not set.");

        var host = this.hostVersion ?? currentHostVersion.ToString();
        var uri = WebAppProtocol.BuildCheckUri(
            this.Server,
            this.AppId,
            currentAppVersion?.ToString(),
            this.Platform ?? String.Empty,
            host,
            this.Channel
        );

        using var message = await this.http.Value
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        message.EnsureSuccessStatusCode();

        var response = await message.Content
            .ReadFromJsonAsync(WebAppJsonContext.Default.WebAppUpdateResponse, cancellationToken)
            .ConfigureAwait(false);

        if (response is null || response.Kind == WebAppUpdateKind.None || response.Release is not { } release)
            return null;

        if (this.Verify(response, host, uri) is { } rejection)
            throw new InvalidDataException(rejection);

        return new ReleaseServerUpdateInfo
        {
            Version = WebAppVersion.Parse(release.Version),
            IsOptional = response.Kind == WebAppUpdateKind.Optional,
            WhatsNew = release.ReleaseNotes,
            ReleaseDate = release.PublishedAt,
            FileSize = release.Size,
            Sha256 = release.Sha256,
            DownloadUri = new Uri(uri, response.DownloadUrl)
        };
    }

    string? Verify(WebAppUpdateResponse response, string host, Uri checkUri)
    {
        var release = response.Release!;

        // Only the signed fields are acted on: size and hash bind the download, app id and minimum host bind who may run it.
        if (!WebAppReleaseSignature.Verify(release, response.Signature, this.key))
            return "signature does not verify";

        if (!String.Equals(release.AppId, this.AppId, StringComparison.Ordinal))
            return $"release is for app '{release.AppId}'";

        if (!WebAppVersion.TryParse(release.Version, out _))
            return $"release version '{release.Version}' does not parse";

        if (release.MinimumHostVersion is { } minimum
            && (!WebAppVersion.TryParse(minimum, out var minimumHost) || WebAppVersion.Parse(host) < minimumHost))
            return $"release needs host {minimum}";

        if (release.Size <= 0)
            return "release has no size";

        if (String.IsNullOrWhiteSpace(response.DownloadUrl) || !Uri.TryCreate(checkUri, response.DownloadUrl, out var download))
            return "release has no download URL";

        // Plain HTTP is allowed only when the check itself was, which in practice means development.
        if (download.Scheme != Uri.UriSchemeHttps && checkUri.Scheme == Uri.UriSchemeHttps)
            return "download URL downgrades to plain HTTP";

        return null;
    }

    public async Task<Stream> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (update is not ReleaseServerUpdateInfo { DownloadUri: { } uri })
            throw new ArgumentException("Not a release from this provider.", nameof(update));

        var message = await this.http.Value
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            message.EnsureSuccessStatusCode();

            // Disposing the content stream releases the response with it.
            return await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (this.http.IsValueCreated)
            this.http.Value.Dispose();

        this.key.Dispose();
    }

    sealed class ReleaseServerUpdateInfo : UpdateInfo
    {
        public required Uri DownloadUri { get; init; }
    }
}
