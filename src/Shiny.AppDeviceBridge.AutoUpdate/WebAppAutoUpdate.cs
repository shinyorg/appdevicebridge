using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.WebView;
using Shiny.Jobs;
using Shiny.Net.Http;

namespace Shiny.AppDeviceBridge.AutoUpdate;

public sealed class WebAppAutoUpdateOptions
{
    /// <summary>
    /// The network the background check waits for. <see cref="InternetAccess.Any"/> by default; <see cref="InternetAccess.Unmetered"/>
    /// keeps checks — and the downloads they start — to Wi-Fi.
    /// </summary>
    public InternetAccess RequiredInternetAccess { get; set; } = InternetAccess.Any;

    /// <summary>Only check while the device is charging.</summary>
    public bool RequireCharging { get; set; }

    /// <summary>Let a release download over a metered (cellular) connection. On by default: releases are small.</summary>
    public bool UseMeteredConnection { get; set; } = true;

    /// <summary>
    /// Registers Shiny's transfer service. Off when the app registers it itself; the update's transfer delegate is added
    /// alongside the app's, and Shiny runs both. <c>AddHttpTransfersBridge</c> and this share one registration in either order.
    /// </summary>
    public bool RegisterTransferService { get; set; } = true;
}

public static class WebAppAutoUpdateExtensions
{
    /// <summary>
    /// Keeps the web app up to date while the app is closed. A background job checks the update provider whenever the OS
    /// runs it, and any newer release — optional or required — downloads as an OS background transfer, verified exactly as a
    /// foreground download and served from the next launch. While the app is open, optional releases go the same way instead
    /// of downloading in-process. Required releases found at startup still install on the splash screen, where the user is
    /// waiting for them.
    /// <code>
    /// bridge.AddWebAppAutoUpdate();
    /// bridge.AddWebAppAutoUpdate(o => o.RequiredInternetAccess = InternetAccess.Unmetered);
    /// </code>
    /// <para>
    /// Needs <see cref="WebAppHostOptions.UpdateProvider"/>. A provider whose release is not a plain GET
    /// (<see cref="IUpdateProvider.GetDownloadRequest"/> answers null, as GitHub's does with a token) downloads in-process
    /// during the job instead.
    /// </para>
    /// <para>
    /// Platform setup is Shiny.Jobs' and Shiny.Net.Http's: iOS needs <c>BGTaskSchedulerPermittedIdentifiers</c>
    /// (<c>com.shiny.jobnet</c>, and <c>com.shiny.jobpowernet</c> with <see cref="WebAppAutoUpdateOptions.RequireCharging"/>), the
    /// <c>processing</c> background mode, and <c>HandleEventsForBackgroundUrl</c> forwarded to Shiny; Android needs
    /// <c>FOREGROUND_SERVICE</c> and <c>FOREGROUND_SERVICE_DATA_SYNC</c>.
    /// </para>
    /// </summary>
    public static TBuilder AddWebAppAutoUpdate<TBuilder>(this TBuilder bridge, Action<WebAppAutoUpdateOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var options = new WebAppAutoUpdateOptions();
        configure?.Invoke(options);

        var services = bridge.Services;
        if (services.Any(x => x.ServiceType == typeof(WebAppAutoUpdater)))
            throw new InvalidOperationException("AddWebAppAutoUpdate is already registered.");

        services.AddSingleton(options);
        services.AddSingleton<WebAppAutoUpdater>();
        services.AddSingleton<IWebAppUpdateDownloader>(sp => sp.GetRequiredService<WebAppAutoUpdater>());

        services.AddJob<WebAppUpdateJob>(job => job with
        {
            RequiredInternetAccess = options.RequiredInternetAccess,
            DeviceCharging = options.RequireCharging
        });

        AddTransfers(services, options.RegisterTransferService);
        return bridge;
    }

    static void AddTransfers(IServiceCollection services, bool register)
    {
        // One manager, whoever registers it first: two would each run their own NSURLSession or foreground service.
        var managerRegistered = services.Any(x => x.ServiceType == typeof(IHttpTransferManager));

        if (!register || managerRegistered)
        {
            services.AddSingleton<IHttpTransferDelegate, WebAppUpdateTransferDelegate>();
            return;
        }

#if ANDROID || IOS || MACCATALYST || WINDOWS
        services.AddHttpTransfers<WebAppUpdateTransferDelegate>();
#elif MACOS
        services.AddConnectivity();
        services.AddHttpClientTransfers<WebAppUpdateTransferDelegate>();
#elif TRANSFERS_LINUX
        if (OperatingSystem.IsLinux())
        {
            services.AddConnectivity();
            services.AddHttpClientTransfers<WebAppUpdateTransferDelegate>();
        }
        else
        {
            services.AddSingleton<IHttpTransferDelegate, WebAppUpdateTransferDelegate>();
        }
#endif
    }
}

/// <summary>
/// The background update itself: queueing releases as transfers, and installing them when they land. A transfer can finish
/// in a later process than the one that queued it — iOS relaunches the app for it — so what the release must be (version,
/// size, hash, notes) is written beside the download and read back then.
/// <code>
/// {install directory}/transfers/{id}.zip    the download, in flight or finished
/// {install directory}/transfers/{id}.json   the release it must verify as
/// </code>
/// </summary>
public sealed class WebAppAutoUpdater(
    IServiceProvider services,
    WebAppAutoUpdateOptions options,
    ILogger<WebAppAutoUpdater> logger
) : IWebAppUpdateDownloader
{
    internal const string IdentifierPrefix = "appdevicebridge.webapp-update.";

    readonly SemaphoreSlim gate = new(1, 1);

    WebAppHost Host => services.GetRequiredService<WebAppHost>();

    IHttpTransferManager? Transfers => services.GetService<IHttpTransferManager>();

    /// <summary>
    /// One background run: check, and download whatever is newer. True when a release was queued or installed.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var host = this.Host;
        var check = await host.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);

        if (check is not { Status: WebAppUpdateStatus.Available, Update: { } update })
        {
            logger.LogDebug("Background update check: {Status}", check.Status);
            return false;
        }

        if (await this.QueueAsync(host, update, cancellationToken).ConfigureAwait(false))
            return true;

        // No plain GET to hand the OS: download it now, in the time the job has.
        await host.Updater.InstallAsync(check, null, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Installed web app {Version} in the background; it is served from the next launch", update.Version);
        return true;
    }

    /// <summary>
    /// Hands a release to the OS. False when the provider has no plain GET for it, or no transfer manager is registered —
    /// the caller then downloads it itself. A release already downloading is not queued twice; an older one still in flight
    /// is cancelled in its favour.
    /// </summary>
    public async Task<bool> QueueAsync(WebAppHost host, UpdateInfo update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(update);

        if (this.Transfers is not { } transfers || host.Updater.Provider?.GetDownloadRequest(update) is not { } download)
            return false;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = this.TransfersDirectory(host);
            Directory.CreateDirectory(directory);

            var inFlight = (await transfers.GetTransfers().ConfigureAwait(false))
                .Where(x => x.Request.Identifier.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var transfer in inFlight)
            {
                var id = transfer.Request.Identifier[IdentifierPrefix.Length..];

                if (ReadRecord(directory, id) is { } record && WebAppVersion.TryParse(record.Version, out var version) && version >= update.Version)
                {
                    logger.LogDebug("Web app {Version} is already downloading", record.Version);
                    return true;
                }

                await transfers.Cancel(transfer.Request.Identifier).ConfigureAwait(false);
                Forget(directory, id);
            }

            var newId = Guid.NewGuid().ToString("n");
            var zip = Path.Combine(directory, newId + ".zip");

            WriteRecord(directory, newId, new UpdateRecord(
                update.Version.ToString(),
                update.FileSize,
                update.Sha256,
                update.WhatsNew,
                update.IsOptional,
                update.ReleaseDate
            ));

            try
            {
                await transfers.Queue(new HttpTransferRequest(
                    IdentifierPrefix + newId,
                    download.Uri.AbsoluteUri,
                    TransferType.Download,
                    zip,
                    options.UseMeteredConnection,
                    null,
                    download.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) : null
                )).ConfigureAwait(false);
            }
            catch
            {
                Forget(directory, newId);
                throw;
            }

            logger.LogInformation("Downloading web app {Version} in the background", update.Version);
            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>A transfer landed. Ours are verified and installed; anything else is not this updater's.</summary>
    internal async Task OnCompletedAsync(HttpTransferRequest request)
    {
        if (!TryGetId(request.Identifier, out var id))
            return;

        var host = this.Host;
        var directory = this.TransfersDirectory(host);
        var zip = Path.Combine(directory, id + ".zip");

        try
        {
            if (ReadRecord(directory, id) is not { } record || !WebAppVersion.TryParse(record.Version, out var version))
            {
                logger.LogWarning("A web app download finished without the release it was for; discarding it");
                return;
            }

            // The manager wrote where it was asked to; the record says where that was meant to be.
            var downloaded = File.Exists(request.LocalFilePath) ? request.LocalFilePath : zip;

            var update = new UpdateInfo
            {
                Version = version,
                FileSize = record.FileSize,
                Sha256 = record.Sha256,
                WhatsNew = record.WhatsNew,
                IsOptional = record.IsOptional,
                ReleaseDate = record.ReleaseDate
            };

            await host.InstallDownloadedAsync(update, downloaded).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not newer any more, the wrong size or hash, an archive that will not open: the next check offers it again.
            logger.LogWarning(ex, "A web app downloaded in the background was not installed");
        }
        finally
        {
            Forget(directory, id);
        }
    }

    internal Task OnErrorAsync(HttpTransferRequest request, int statusCode, Exception ex)
    {
        if (!TryGetId(request.Identifier, out var id))
            return Task.CompletedTask;

        logger.LogWarning(ex, "A web app download failed ({StatusCode}); the next check retries it", statusCode);
        Forget(this.TransfersDirectory(this.Host), id);
        return Task.CompletedTask;
    }

    string TransfersDirectory(WebAppHost host) => Path.Combine(host.Updater.InstallDirectory, "transfers");

    static bool TryGetId(string identifier, out string id)
    {
        id = String.Empty;
        if (!identifier.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
            return false;

        id = identifier[IdentifierPrefix.Length..];

        // Becomes a file name: only what this updater ever writes.
        return id.Length == 32 && id.All(Char.IsAsciiHexDigitLower);
    }

    static UpdateRecord? ReadRecord(string directory, string id)
    {
        try
        {
            var path = Path.Combine(directory, id + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), AutoUpdateJsonContext.Default.UpdateRecord) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void WriteRecord(string directory, string id, UpdateRecord record)
        => File.WriteAllBytes(Path.Combine(directory, id + ".json"), JsonSerializer.SerializeToUtf8Bytes(record, AutoUpdateJsonContext.Default.UpdateRecord));

    static void Forget(string directory, string id)
    {
        foreach (var path in new[] { Path.Combine(directory, id + ".json"), Path.Combine(directory, id + ".zip") })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>The Shiny job: one check per run, whenever the OS grants one.</summary>
public sealed class WebAppUpdateJob(WebAppAutoUpdater updater) : IJob
{
    public Task Run(CancellationToken cancelToken) => updater.RunAsync(cancelToken);
}

/// <summary>Passes the update's own transfers to <see cref="WebAppAutoUpdater"/>; ignores everyone else's.</summary>
public sealed class WebAppUpdateTransferDelegate(WebAppAutoUpdater updater) : IHttpTransferDelegate
{
    public Task OnCompleted(HttpTransferRequest request) => updater.OnCompletedAsync(request);

    public Task OnError(HttpTransferRequest request, int statusCode, Exception ex) => updater.OnErrorAsync(request, statusCode, ex);
}

sealed record UpdateRecord(string Version, long? FileSize, string? Sha256, string? WhatsNew, bool IsOptional, DateTimeOffset? ReleaseDate);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateRecord))]
partial class AutoUpdateJsonContext : JsonSerializerContext;
