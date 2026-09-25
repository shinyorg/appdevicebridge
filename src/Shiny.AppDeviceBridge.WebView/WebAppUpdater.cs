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

public sealed record WebAppUpdateCheckResult(
    WebAppUpdateStatus Status,
    UpdateInfo? Update = null,
    string? Error = null
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

    public async Task<WebAppUpdateCheckResult> CheckAsync(WebAppVersion? current, CancellationToken cancellationToken = default)
    {
        if (this.provider is null)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Disabled);

        UpdateInfo? update;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(this.options.CheckTimeout);

            try
            {
                update = await this.provider
                    .GetUpdateInfoAsync(ToHostVersion(this.bridge.HostVersion), current, timeout.Token)
                    .ConfigureAwait(false);
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

        if (update is null)
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.UpToDate);

        if (Verify(update, current) is { } rejection)
        {
            this.logger.LogWarning("Rejected release {Version} from {Provider}: {Reason}", update.Version, this.provider.GetType().Name, rejection);
            return new WebAppUpdateCheckResult(WebAppUpdateStatus.Rejected, Error: rejection);
        }

        return new WebAppUpdateCheckResult(WebAppUpdateStatus.Available, update);
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

            if (update.Sha256 is { } declared && !String.Equals(sha256, declared, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Download of {update.Version} does not match its SHA-256.");

            // Opened once before it can become the installed build, so an archive that downloads
            // but is unservable fails here instead of at the next launch.
            WebAppArchive.Open(pending, this.options);

            var package = this.store.Commit(pending, update.Version, sha256, total);
            this.logger.LogInformation("Installed web app {Version}", update.Version);
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

    /// <summary>Disposes the provider when it is disposable: the host owns it from the moment it is set on the options.</summary>
    public void Dispose() => (this.provider as IDisposable)?.Dispose();
}
