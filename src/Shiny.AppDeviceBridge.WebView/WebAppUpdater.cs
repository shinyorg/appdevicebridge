using System.Buffers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shiny.AppDeviceBridge.WebView;

public enum WebAppUpdateStatus
{
    /// <summary>No update server is configured.</summary>
    Disabled,

    /// <summary>The server could not be reached in time, or answered with an error. Treat as offline.</summary>
    Unavailable,

    UpToDate,

    /// <summary>A verified release is ready to download. See <see cref="WebAppUpdateCheckResult.Kind"/>.</summary>
    Available,

    /// <summary>The server offered a release that failed verification. It is ignored.</summary>
    Rejected
}

public sealed record WebAppUpdateCheckResult(
    WebAppUpdateStatus Status,
    WebAppUpdateKind Kind = WebAppUpdateKind.None,
    WebAppRelease? Release = null,
    Uri? DownloadUri = null,
    string? Error = null
);

public readonly record struct WebAppDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => this.TotalBytes <= 0 ? 0 : (double)this.BytesReceived / this.TotalBytes;
}

/// <summary>
/// Checks for, downloads and installs releases. Nothing reaches the install store without passing,
/// in order: the signature, the app id, the version going forward, host compatibility, the size, the
/// hash, and the archive opening with its entry document in it.
/// </summary>
public sealed class WebAppUpdater : IDisposable
{
    readonly WebAppHostOptions options;
    readonly AppDeviceBridgeOptions bridge;
    readonly WebAppInstallStore store;
    readonly ILogger logger;
    readonly HttpClient http;
    readonly ECDsa? publicKey;

    internal WebAppUpdater(WebAppHostOptions options, AppDeviceBridgeOptions bridge, WebAppInstallStore store, ILogger logger)
    {
        this.options = options;
        this.bridge = bridge;
        this.store = store;
        this.logger = logger;

        this.http = options.HttpMessageHandlerFactory is { } factory
            ? new HttpClient(factory(), disposeHandler: true)
            : new HttpClient();

        // Downloads can legitimately take minutes; every call carries its own cancellation instead.
        this.http.Timeout = Timeout.InfiniteTimeSpan;

        if (!String.IsNullOrWhiteSpace(options.PublicKey))
            this.publicKey = WebAppReleaseSignature.ImportPublicKey(options.PublicKey);
    }

    public async Task<WebAppUpdateCheckResult> CheckAsync(WebAppVersion? current, CancellationToken cancellationToken = default)
    {
        if (this.options.UpdateServer is null || this.publicKey is null)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Disabled);

        var uri = WebAppProtocol.BuildCheckUri(
            this.options.UpdateServer,
            this.bridge.AppId,
            current?.ToString(),
            this.bridge.Platform,
            this.bridge.HostVersion,
            this.options.Channel
        );

        WebAppUpdateResponse? response;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(this.options.CheckTimeout);

            try
            {
                using var message = await this.http
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (!message.IsSuccessStatusCode)
                {
                    this.logger.LogWarning("Update check to {Uri} answered {Status}", uri, (int)message.StatusCode);
                    return new WebAppUpdateCheckResult(WebAppUpdateStatus.Unavailable, Error: $"HTTP {(int)message.StatusCode}");
                }

                response = await message.Content
                    .ReadFromJsonAsync(WebAppJsonContext.Default.WebAppUpdateResponse, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Offline, captive portal, server down, too slow: all the same answer — run what we have.
                this.logger.LogInformation(ex, "Update check to {Uri} failed", uri);
                return new WebAppUpdateCheckResult(WebAppUpdateStatus.Unavailable, Error: ex.Message);
            }
        }

        if (response is null || response.Kind == WebAppUpdateKind.None || response.Release is null)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.UpToDate);

        if (this.Verify(response, current, uri) is { } rejection)
        {
            this.logger.LogWarning("Rejected release from {Uri}: {Reason}", uri, rejection);
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Rejected, Error: rejection);
        }

        var download = new Uri(uri, response.DownloadUrl);
        return new WebAppUpdateCheckResult(WebAppUpdateStatus.Available, response.Kind, response.Release, download);
    }

    string? Verify(WebAppUpdateResponse response, WebAppVersion? current, Uri checkUri)
    {
        var release = response.Release!;

        if (!WebAppReleaseSignature.Verify(release, response.Signature, this.publicKey!))
            return "signature does not verify";

        if (!String.Equals(release.AppId, this.bridge.AppId, StringComparison.Ordinal))
            return $"release is for app '{release.AppId}'";

        // A validly signed old release replayed by anything in the path must not roll the app back.
        if (!WebAppVersion.TryParse(release.Version, out var version) || (current is { } installed && version <= installed))
            return $"release {release.Version} is not newer than {current}";

        if (release.MinimumHostVersion is { } minimum
            && (!WebAppVersion.TryParse(minimum, out var minimumHost) || WebAppVersion.Parse(this.bridge.HostVersion) < minimumHost))
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

    /// <summary>
    /// Downloads, verifies and installs a release from <see cref="CheckAsync"/>. The package it returns
    /// is installed but not yet served; the host decides when to switch.
    /// </summary>
    public async Task<WebAppPackage> InstallAsync(
        WebAppUpdateCheckResult check,
        IProgress<WebAppDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(check);

        if (check is not { Status: WebAppUpdateStatus.Available, Release: { } release, DownloadUri: { } uri })
            throw new InvalidOperationException("Only an Available check result can be installed.");

        var pending = this.store.CreatePendingPath();

        try
        {
            using var message = await this.http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            message.EnsureSuccessStatusCode();

            if (message.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException($"Server sent {length} bytes; release {release.Version} is {release.Size}.");

            string sha256;

            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var body = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);

                try
                {
                    long total = 0;
                    int read;

                    progress?.Report(new WebAppDownloadProgress(0, release.Size));

                    while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;

                        // Stop the moment it is too long, rather than filling the disk with whatever
                        // a broken or hostile server keeps sending.
                        if (total > release.Size)
                            throw new InvalidDataException($"Download of {release.Version} exceeded its declared {release.Size} bytes.");

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new WebAppDownloadProgress(total, release.Size));
                    }

                    if (total != release.Size)
                        throw new InvalidDataException($"Download of {release.Version} ended at {total} of {release.Size} bytes.");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!String.Equals(sha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Download of {release.Version} does not match its signed hash.");

            // Opened once before it can become the installed build, so an archive that is signed but
            // unservable fails here instead of at the next launch.
            WebAppArchive.Open(pending, this.options.EntryDocument, this.options.ArchiveBasePath);

            var package = this.store.Commit(pending, release);
            this.logger.LogInformation("Installed web app {Version}", release.Version);
            return package;
        }
        catch
        {
            TryDelete(pending);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        this.http.Dispose();
        this.publicKey?.Dispose();
    }
}
