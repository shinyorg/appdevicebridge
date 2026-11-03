using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Shiny.AppDeviceBridge.WebView;

public enum WebAppUpdateStatus
{
    /// <summary>No <see cref="WebAppHostOptions.UpdateProvider"/> is set.</summary>
    Disabled,

    /// <summary>The provider could not answer in time, or failed. Treat as offline.</summary>
    Unavailable,

    UpToDate,

    /// <summary>A release is ready to download. See <see cref="WebAppUpdateCheckResult.Update"/>.</summary>
    Available,

    /// <summary>The provider offered a release that failed verification. It is ignored.</summary>
    Rejected
}

/// <summary>An update check's outcome.</summary>
/// <param name="Status">What became of the web app release.</param>
/// <param name="Update">The web app release, when <see cref="WebAppUpdateStatus.Available"/>.</param>
/// <param name="Error">Why the check was unavailable or the release rejected.</param>
/// <param name="HostUpdate">
/// A native app update in the store — independent of <paramref name="Status"/>: a web app can be up to date on a native app
/// the server no longer accepts.
/// </param>
public sealed record WebAppUpdateCheckResult(
    WebAppUpdateStatus Status,
    UpdateInfo? Update = null,
    string? Error = null,
    HostUpdateInfo? HostUpdate = null
);

public readonly record struct WebAppDownloadProgress(long BytesReceived, long TotalBytes)
{
    /// <summary>Zero when the total is unknown — a provider that does not say how big the release is.</summary>
    public double Fraction => this.TotalBytes <= 0 ? 0 : (double)this.BytesReceived / this.TotalBytes;
}

/// <summary>
/// Checks for, downloads and installs releases through <see cref="WebAppHostOptions.UpdateProvider"/>. Whatever the provider,
/// nothing reaches the install store without passing, in order: the version going forward, the size and the hash when the
/// provider gave them, and the archive opening with its entry document in it.
/// </summary>
public sealed class WebAppUpdater : IDisposable
{
    readonly WebAppHostOptions options;
    readonly AppDeviceBridgeOptions bridge;
    readonly WebAppInstallStore store;
    readonly ILogger logger;
    readonly IUpdateProvider? provider;

    internal WebAppUpdater(WebAppHostOptions options, AppDeviceBridgeOptions bridge, WebAppInstallStore store, ILogger logger)
    {
        this.options = options;
        this.bridge = bridge;
        this.store = store;
        this.logger = logger;
        this.provider = options.UpdateProvider;

        if (this.provider is ReleaseServerUpdateProvider releaseServer)
            releaseServer.Bind(bridge);
    }

    /// <summary>The native host's version as <see cref="Version"/>: its numbers, without any prerelease label.</summary>
    internal static Version ToHostVersion(string hostVersion)
    {
        var text = hostVersion.Trim();
        var end = text.IndexOfAny(['-', '+']);
        if (end >= 0)
            text = text[..end];

        var parts = text.Split('.').Select(Int32.Parse).ToArray();
        return parts.Length switch
        {
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };
    }

    /// <summary>The folder installed builds are kept in, for a downloader of the app's own to stage files under.</summary>
    public string InstallDirectory => this.store.RootDirectory;

    /// <summary>The configured provider, for a downloader that needs <see cref="IUpdateProvider.GetDownloadRequest"/>.</summary>
    public IUpdateProvider? Provider => this.provider;

    public async Task<WebAppUpdateCheckResult> CheckAsync(WebAppVersion? current, CancellationToken cancellationToken = default)
    {
        if (this.provider is null)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Disabled);

        UpdateOffer offer;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(this.options.CheckTimeout);

            try
            {
                var request = new UpdateRequest(ToHostVersion(this.bridge.HostVersion), current, this.options.Culture());
                offer = await this.provider.CheckAsync(request, timeout.Token).ConfigureAwait(false) ?? UpdateOffer.None;
            }
            catch (InvalidDataException ex)
            {
                this.logger.LogWarning(ex, "Rejected release from {Provider}", this.provider.GetType().Name);
                return new WebAppUpdateCheckResult(WebAppUpdateStatus.Rejected, Error: ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Offline, captive portal, server down, too slow, a provider bug: all the same answer — run what we have.
                this.logger.LogInformation(ex, "Update check through {Provider} failed", this.provider.GetType().Name);
                return new WebAppUpdateCheckResult(WebAppUpdateStatus.Unavailable, Error: ex.Message);
            }
        }

        var host = offer.Host;

        if (offer.WebApp is not { } update)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.UpToDate, HostUpdate: host);

        if (Verify(update, current) is { } rejection)
        {
            this.logger.LogWarning("Rejected release {Version} from {Provider}: {Reason}", update.Version, this.provider.GetType().Name, rejection);
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Rejected, Error: rejection, HostUpdate: host);
        }

        return new WebAppUpdateCheckResult(WebAppUpdateStatus.Available, update, HostUpdate: host);
    }

    static string? Verify(UpdateInfo update, WebAppVersion? current)
    {
        // An old release replayed by anything in the path, or a provider that got its ordering wrong, must not roll the app back.
        if (current is { } installed && update.Version <= installed)
            return $"release {update.Version} is not newer than {current}";

        if (update.FileSize is <= 0)
            return "release has no size";

        if (update.Sha256 is { } sha && (sha.Length != 64 || !sha.All(Char.IsAsciiHexDigit)))
            return "release SHA-256 is not 64 hex characters";

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

        if (check is not { Status: WebAppUpdateStatus.Available, Update: { } update } || this.provider is null)
            throw new InvalidOperationException("Only an Available check result can be installed.");

        var pending = this.store.CreatePendingPath();

        try
        {
            string sha256;
            long total = 0;
            var expected = update.FileSize;

            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var body = await this.provider.DownloadAsync(update, cancellationToken).ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);

                try
                {
                    int read;
                    progress?.Report(new WebAppDownloadProgress(0, expected ?? 0));

                    while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;

                        // Stop the moment it is too long, rather than filling the disk with whatever
                        // a broken or hostile server keeps sending.
                        if (total > expected)
                            throw new InvalidDataException($"Download of {update.Version} exceeded its declared {expected} bytes.");

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new WebAppDownloadProgress(total, expected ?? 0));
                    }

                    if (expected is { } size && total != size)
                        throw new InvalidDataException($"Download of {update.Version} ended at {total} of {size} bytes.");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            return this.VerifyAndCommit(pending, update, sha256, total);
        }
        catch
        {
            TryDelete(pending);
            throw;
        }
    }

    /// <summary>
    /// Verifies and installs a release some other downloader fetched — an OS background transfer, say — exactly as
    /// <see cref="InstallAsync"/> verifies its own: the version must be newer than <paramref name="current"/>, and the size,
    /// SHA-256 and archive must check out. The file is moved into the install store, or deleted when it fails.
    /// </summary>
    /// <param name="update">The release from <see cref="CheckAsync"/> the file is meant to be.</param>
    /// <param name="downloadedFile">The downloaded zip. Consumed either way.</param>
    /// <param name="current">The newest build already installed or bundled, which the release must be newer than.</param>
    public async Task<WebAppPackage> InstallDownloadedAsync(
        UpdateInfo update,
        string downloadedFile,
        WebAppVersion? current,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedFile);

        try
        {
            if (Verify(update, current) is { } rejection)
                throw new InvalidDataException(rejection);

            var info = new FileInfo(downloadedFile);
            if (!info.Exists)
                throw new FileNotFoundException("The downloaded release is not there.", downloadedFile);

            if (update.FileSize is { } size && info.Length != size)
                throw new InvalidDataException($"Download of {update.Version} is {info.Length} bytes, not {size}.");

            string sha256;
            await using (var stream = new FileStream(downloadedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            return this.VerifyAndCommit(downloadedFile, update, sha256, info.Length);
        }
        catch
        {
            TryDelete(downloadedFile);
            throw;
        }
    }

    WebAppPackage VerifyAndCommit(string file, UpdateInfo update, string sha256, long size)
    {
        if (update.Sha256 is { } declared && !String.Equals(sha256, declared, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Download of {update.Version} does not match its SHA-256.");

        // Opened once before it can become the installed build, so an archive that downloads
        // but is unservable fails here instead of at the next launch.
        WebAppArchive.Open(file, this.options);

        var package = this.store.Commit(file, update.Version, sha256, size);
        this.logger.LogInformation("Installed web app {Version}", update.Version);
        return package;
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

    /// <summary>Disposes the provider when it is disposable: the host owns it from the moment it is set on the options.</summary>
    public void Dispose() => (this.provider as IDisposable)?.Dispose();
}
