using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.WebAppHost;

public enum WebAppHostState
{
    NotStarted,
    Starting,
    CheckingForUpdate,

    /// <summary>A required update is downloading. <see cref="WebAppHostStatus.Progress"/> says how far.</summary>
    Downloading,

    Ready,
    Failed
}

public sealed record WebAppHostStatus(
    WebAppHostState State,
    WebAppPackage? Package = null,
    WebAppDownloadProgress? Progress = null,
    string? Error = null
);

/// <summary>Thrown when there is no web app to show: nothing bundled, nothing installed, and no way to get one.</summary>
public sealed class WebAppUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Serves the web app and keeps it current.
/// <para>
/// <see cref="StartAsync"/> does everything the WebView needs before it can load: picks the newest
/// build this host can run (bundled or downloaded), starts the loopback server, asks the update
/// server whether that build is still acceptable, installs a required update before returning, and
/// starts an optional one in the background. It returns the URL to load.
/// </para>
/// </summary>
public sealed class WebAppHost : IAsyncDisposable
{
    readonly WebAppHostOptions options;
    readonly WebAppSession session;
    readonly WebAppEventHub events;
    readonly IReadOnlyList<IWebAppBridge> bridges;
    readonly ILogger logger;
    readonly HttpServerOptions serverOptions;
    readonly HttpServer server;
    readonly WebAppFileSource source = new();
    readonly WebAppInstallStore store;
    readonly StaticFileMiddleware staticFiles;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();

    volatile Uri? origin;
    volatile Uri? devServer;
    HttpClient? devClient;

    public WebAppHost(
        WebAppHostOptions options,
        WebAppSession session,
        WebAppEventHub events,
        IEnumerable<IWebAppBridge> bridges,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.session = session;
        this.events = events;
        this.bridges = [.. bridges];

        loggerFactory ??= NullLoggerFactory.Instance;
        this.logger = loggerFactory.CreateLogger<WebAppHost>();

        this.store = new WebAppInstallStore(options.ResolveInstallDirectory(), this.logger);
        this.Updater = new WebAppUpdater(options, this.store, this.logger);

        this.serverOptions = new HttpServerOptions
        {
            // Loopback, always. Nothing here is meant for the network.
            Address = IPAddress.Loopback,
            Port = options.Port
        };

        // The files bridge takes uploads up to its own limit; the server's 30 MB default would otherwise
        // refuse them first, with a less helpful response.
        if (this.bridges.OfType<WebAppFilesBridge>().Any()
            && options.MaxFileWriteBytes > (this.serverOptions.Limits.MaxRequestBodySize ?? Int64.MaxValue))
            this.serverOptions.Limits.MaxRequestBodySize = options.MaxFileWriteBytes;

        this.server = new HttpServer(this.serverOptions);

        this.staticFiles = new StaticFileMiddleware(new StaticFileOptions
        {
            Source = this.source,
            FallbackFile = options.SpaFallback ? options.EntryDocument : null,
            ServePrecompressedFiles = true,

            // Revalidate everything. The same URL serves different bytes after an update, and the
            // ETag — derived from each entry's CRC — makes a revalidation over loopback nearly free.
            OnPrepareResponse = x => x.HttpContext.Response.Headers["Cache-Control"] = "no-cache"
        });

        this.server.Use(this.GuardAsync);
        this.MapHostRoutes();

        foreach (var bridge in this.bridges)
            bridge.Map(new WebAppBridgeRoutes(this.server, bridge.Name, events));
    }

    public WebAppUpdater Updater { get; }

    public WebAppHostStatus Status { get; private set; } = new(WebAppHostState.NotStarted);

    /// <summary>Raised on a background thread. Marshal to the UI thread before touching controls.</summary>
    public event EventHandler<WebAppHostStatus>? StatusChanged;

    /// <summary>
    /// An optional update finished installing in the background. It is served from the next launch, or
    /// now if <see cref="ApplyPendingUpdate"/> is called and the WebView reloaded. Raised on a
    /// background thread.
    /// </summary>
    public event EventHandler<WebAppPackage>? UpdateInstalled;

    /// <summary>The build being served.</summary>
    public WebAppPackage? Package => this.source.Package;

    /// <summary>An installed update not yet being served.</summary>
    public WebAppPackage? PendingPackage { get; private set; }

    public WebAppUpdateCheckResult? LastCheck { get; private set; }

    /// <summary><c>http://127.0.0.1:{port}/</c> once started.</summary>
    public Uri? Origin => this.origin;

    /// <summary>The dev server pages are coming from, when <see cref="WebAppHostOptions.DevServer"/> answered at startup.</summary>
    public Uri? DevServer => this.devServer;

    internal WebAppSession Session => this.session;

    internal WebAppFileSource Source => this.source;

    /// <summary>
    /// Makes a build servable without starting anything else — for a background wake-up, where there is no
    /// WebView and an update check would only spend the few seconds the OS allowed.
    /// </summary>
    internal async Task<WebAppPackage?> EnsureActivatedAsync(CancellationToken cancellationToken)
    {
        if (this.source.Package is { } active)
            return active;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return this.source.Package ?? this.ActivateBestAvailable();
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// The loopback origin, for background script calls to the bridge: starts the server without the UI or an
    /// update check, and restarts it if a suspension took the socket.
    /// </summary>
    internal async Task<Uri> EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (this.origin is { } started)
        {
            await this.EnsureServerRunningAsync(cancellationToken).ConfigureAwait(false);
            return started;
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.source.Package is null)
                this.ActivateBestAvailable();

            if (this.origin is null)
                await this.StartServerAsync(cancellationToken).ConfigureAwait(false);

            return this.origin!;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.Status.State == WebAppHostState.Ready && this.origin is not null)
                return this.BuildStartUri();

            this.SetStatus(new WebAppHostStatus(WebAppHostState.Starting));

            var active = this.ActivateBestAvailable();

            // Before any download: pruning clears the pending folder.
            this.store.Prune(active);

            await this.StartServerAsync(cancellationToken).ConfigureAwait(false);

            if (this.options.DevServer is { } dev && await this.ProbeDevServerAsync(dev, cancellationToken).ConfigureAwait(false))
            {
                // Pages come from the machine running `dotnet watch`, so there is nothing to update.
                this.devServer = dev;
                this.logger.LogInformation("Serving pages from the dev server at {DevServer}", dev);
                this.SetStatus(new WebAppHostStatus(WebAppHostState.Ready, this.source.Package));

                return this.BuildStartUri();
            }

            if (this.options.UpdateServer is not null)
            {
                this.SetStatus(new WebAppHostStatus(WebAppHostState.CheckingForUpdate, active));

                var check = await this.Updater.CheckAsync(active?.Version, cancellationToken).ConfigureAwait(false);
                this.LastCheck = check;

                if (check.Status == WebAppUpdateStatus.Available)
                {
                    if (active is null || check.Kind == WebAppUpdateKind.Required)
                        active = await this.InstallRequiredAsync(check, active, cancellationToken).ConfigureAwait(false);
                    else
                        _ = this.InstallInBackgroundAsync(check);
                }
            }

            if (this.source.Package is null)
                throw new WebAppUnavailableException(
                    this.LastCheck?.Error is { } error
                        ? $"No web app is installed and none could be downloaded: {error}"
                        : "No web app is installed and none could be downloaded."
                );

            this.SetStatus(new WebAppHostStatus(WebAppHostState.Ready, this.source.Package));
            return this.BuildStartUri();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogError(ex, "Web app host failed to start");
            this.SetStatus(new WebAppHostStatus(WebAppHostState.Failed, this.source.Package, Error: ex.Message));
            throw;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Checks the loopback server still answers and restarts it on the same port if it does not. Call
    /// when the app returns to the foreground: iOS takes the listening socket away during suspension
    /// while the server still believes it is running. Returns true when it restarted, meaning the
    /// WebView should reload.
    /// </summary>
    public async Task<bool> EnsureServerRunningAsync(CancellationToken cancellationToken = default)
    {
        if (this.origin is not { } current)
            return false;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await PingAsync(current, cancellationToken).ConfigureAwait(false))
                return false;

            this.logger.LogInformation("Loopback server stopped answering; restarting on {Origin}", current);

            try
            {
                await this.server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogDebug(ex, "Stopping the stale listener failed");
            }

            await this.StartServerAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Starts serving <see cref="PendingPackage"/> now. The caller reloads the WebView.</summary>
    public bool ApplyPendingUpdate()
    {
        if (this.PendingPackage is not { } pending)
            return false;

        this.Activate(pending);
        this.PendingPackage = null;
        this.SetStatus(new WebAppHostStatus(WebAppHostState.Ready, pending));

        return true;
    }

    Uri BuildStartUri()
    {
        var start = $"{WebAppSession.StartPath}?token={Uri.EscapeDataString(this.session.Token)}";

        // A link that launched the app rides through the token exchange, whose redirect accepts only local paths.
        if (this.bridges.OfType<WebAppLinksBridge>().FirstOrDefault()?.Links.TakeStartRoute() is { } route)
            start += $"&path={Uri.EscapeDataString(route)}";

        return new(this.origin!, start);
    }

    /// <summary>
    /// Serves the newer of the bundled and installed builds, falling back to the other when one will not
    /// open. The bundled build wins a tie, and wins outright when an app store update ships a baseline
    /// newer than the last download.
    /// </summary>
    WebAppPackage? ActivateBestAvailable()
    {
        var installed = this.store.ReadInstalled(this.options.ParsedHostVersion);

        WebAppPackage? baseline = this.options.Baseline is { } b
            ? new WebAppPackage(WebAppVersion.Parse(b.Version), WebAppPackageOrigin.Baseline, null, b)
            : null;

        var candidates = installed is not null && baseline is not null && baseline.Version >= installed.Version
            ? new[] { baseline, installed }
            : new[] { installed, baseline };

        foreach (var candidate in candidates)
        {
            if (candidate is null)
                continue;

            try
            {
                this.Activate(candidate);

                if (candidate.Origin == WebAppPackageOrigin.Baseline && installed is not null)
                    this.store.ClearInstalled();

                this.logger.LogInformation("Serving web app {Version} ({Origin})", candidate.Version, candidate.Origin);
                return candidate;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                this.logger.LogWarning(ex, "Web app {Version} ({Origin}) could not be opened", candidate.Version, candidate.Origin);

                if (candidate.Origin == WebAppPackageOrigin.Installed)
                    this.store.ClearInstalled();
            }
        }

        return null;
    }

    void Activate(WebAppPackage package)
        => this.source.Activate(package, package.Open(this.options.EntryDocument, this.options.ArchiveBasePath));

    async Task<WebAppPackage?> InstallRequiredAsync(WebAppUpdateCheckResult check, WebAppPackage? active, CancellationToken cancellationToken)
    {
        var progress = new StatusProgress(this, active);

        try
        {
            this.SetStatus(new WebAppHostStatus(WebAppHostState.Downloading, active, new WebAppDownloadProgress(0, check.Release!.Size)));

            var package = await this.Updater.InstallAsync(check, progress, cancellationToken).ConfigureAwait(false);
            this.Activate(package);
            this.store.Prune(package);

            return package;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A dropped connection halfway through is the same situation as being offline at the
            // check: run what is installed and try again next launch — unless there is nothing
            // installed, or the app asked to be strict.
            if (active is null || this.options.BlockOnRequiredUpdateFailure)
                throw new WebAppUnavailableException("A required update could not be installed.", ex);

            this.logger.LogWarning(ex, "Required update failed; serving {Version} until the next launch", active.Version);
            return active;
        }
    }

    async Task InstallInBackgroundAsync(WebAppUpdateCheckResult check)
    {
        try
        {
            var package = await this.Updater.InstallAsync(check, null, this.lifetime.Token).ConfigureAwait(false);
            this.PendingPackage = package;
            this.UpdateInstalled?.Invoke(this, package);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Background update to {Version} failed; it will be retried next launch", check.Release?.Version);
        }
    }

    async Task StartServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (this.origin is null && this.options.AllowPortFallback && this.serverOptions.Port != 0 && IsAddressInUse(ex))
        {
            this.logger.LogWarning(
                "Port {Port} is in use; serving on a random port. The web app's storage will be empty for this launch.",
                this.serverOptions.Port
            );

            this.serverOptions.Port = 0;
            await this.server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var port = Uri.TryCreate(this.server.ListenUrl, UriKind.Absolute, out var listening) && listening.Port > 0
            ? listening.Port
            : this.serverOptions.Port;

        // Pinned from here on, so a restart after resume comes back on the same origin.
        this.serverOptions.Port = port;
        this.origin = new Uri($"http://127.0.0.1:{port}/");
    }

    static bool IsAddressInUse(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse))
                return true;
        }

        return false;
    }

    static async Task<bool> PingAsync(Uri origin, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            using var response = await client.GetAsync(new Uri(origin, WebAppSession.PingPath), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    async ValueTask GuardAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;

        if (!this.IsOwnHost(request.Host))
        {
            // 421 Misdirected Request: the connection reached this server under a name that is not its own.
            context.Response.StatusCode = 421;
            return;
        }

        if (request.Path == WebAppSession.PingPath)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (request.Path == WebAppSession.StartPath)
        {
            this.BeginSession(context);
            return;
        }

        if (!this.session.IsValid(request.Cookies[WebAppSession.CookieName]))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (request.Path.StartsWith(WebAppSession.BridgePrefix + "/", StringComparison.Ordinal))
        {
            var requestOrigin = request.Headers["Origin"];
            if (!StringValues.IsNullOrEmpty(requestOrigin) && !this.IsOwnOrigin(requestOrigin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            try
            {
                // Straight to routing: the static file fallback would otherwise answer an unknown
                // bridge route with index.html and a 200.
                await next(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                this.logger.LogError(ex, "Bridge call {Method} {Path} failed", request.Method, request.Path);
                await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "bridge_failed", "The native call failed.");
            }

            return;
        }

        if (this.devServer is { } dev)
        {
            await this.ProxyToDevServerAsync(context, dev);
            return;
        }

        await this.staticFiles.InvokeAsync(context, next);
    }

    void BeginSession(HttpContext context)
    {
        if (!this.session.IsValid(context.Request.Query["token"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Only a local path: anything else would make this an open redirect.
        var path = context.Request.Query["path"].ToString();
        if (!WebAppLinks.IsLocalRoute(path))
            path = "/";

        context.Response.Cookies.Append(WebAppSession.CookieName, this.session.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });

        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Redirect(path);
    }

    bool IsOwnHost(string? host)
    {
        if (this.origin is not { } current || host is null)
            return false;

        return String.Equals(host, $"127.0.0.1:{current.Port}", StringComparison.OrdinalIgnoreCase)
               || String.Equals(host, $"localhost:{current.Port}", StringComparison.OrdinalIgnoreCase);
    }

    bool IsOwnOrigin(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var candidate)
           && candidate.Scheme == Uri.UriSchemeHttp
           && this.IsOwnHost($"{candidate.Host}:{candidate.Port}");

    void MapHostRoutes()
    {
        this.server.MapGet($"{WebAppSession.BridgePrefix}/events", this.events.StreamAsync);

        this.server.MapGet($"{WebAppSession.BridgePrefix}/host", context =>
        {
            var package = this.source.Package;
            var info = new WebAppHostInfo(
                this.options.AppId,
                this.options.HostVersion,
                this.options.Platform,
                package?.Version.ToString(),
                package?.Origin,
                this.PendingPackage?.Version.ToString(),
                [.. this.bridges.Select(x => new WebAppBridgeCapability(x.Name, x.IsSupported))],
                this.devServer?.AbsoluteUri
            );

            return WebAppBridgeResults.Json(context, info, WebAppBridgeJsonContext.Default.WebAppHostInfo);
        });

        // Lets the page offer "an update is ready — reload now?" and do the reload itself.
        this.server.MapPost($"{WebAppSession.BridgePrefix}/host/apply-update", context =>
        {
            var version = this.PendingPackage?.Version.ToString();
            var applied = this.ApplyPendingUpdate();

            return WebAppBridgeResults.Json(
                context,
                new WebAppApplyUpdateResult(applied, applied ? version : null),
                WebAppBridgeJsonContext.Default.WebAppApplyUpdateResult
            );
        });
    }

    /// <summary>
    /// Headers that describe one hop rather than the request, plus the ones that belong to the device: the session
    /// cookie is the device's secret, and the development machine has no use for it.
    /// </summary>
    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
        "Cookie", "Set-Cookie", "Origin", "Referer", "Content-Length"
    };

    // No decompression and no cookies: bytes and headers pass through as the dev server sent them.
    HttpClient DevClient => this.devClient ??= new HttpClient(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    async Task<bool> ProbeDevServerAsync(Uri devServer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this.options.DevServerProbeTimeout);

        try
        {
            // Any answer at all means it is up; what it answers the root with is its business.
            using var response = await this.DevClient.GetAsync(devServer, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            this.logger.LogInformation("Dev server {DevServer} did not answer; serving the installed web app", devServer);
            return false;
        }
    }

    async ValueTask ProxyToDevServerAsync(HttpContext context, Uri devServer)
    {
        var request = context.Request;
        var query = String.IsNullOrEmpty(request.QueryString) ? String.Empty
            : request.QueryString.StartsWith('?') ? request.QueryString
            : "?" + request.QueryString;

        // The hot reload script is rewritten below, which needs it uncompressed.
        var isRefreshScript = request.Path.Contains("browser-refresh", StringComparison.OrdinalIgnoreCase);

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(devServer, request.Path + query));

        if (request.HasBody)
            message.Content = new StreamContent(request.Body);

        foreach (var (name, values) in request.Headers)
        {
            if (NotForwarded.Contains(name) || (isRefreshScript && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!message.Headers.TryAddWithoutValidation(name, values.ToString()))
                message.Content?.Headers.TryAddWithoutValidation(name, values.ToString());
        }

        HttpResponseMessage response;
        try
        {
            response = await this.DevClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(ex, "Dev server {DevServer} stopped answering", devServer);
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status502BadGateway,
                "dev_server_unreachable",
                $"The dev server at {devServer} did not answer. Is dotnet watch still running?"
            );
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (NotForwarded.Contains(name))
                    continue;

                // A redirect to the dev server's own address has to stay on the loopback origin.
                if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var location)
                    && location.Authority == devServer.Authority)
                {
                    context.Response.Headers[name] = location.PathAndQuery;
                    continue;
                }

                context.Response.Headers[name] = values.ToArray();
            }

            // Hot reload changes the bytes behind the same URLs from one moment to the next.
            context.Response.Headers["Cache-Control"] = "no-store";

            if (isRefreshScript && response.IsSuccessStatusCode)
            {
                var script = await response.Content.ReadAsStringAsync(context.RequestAborted).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(RewriteLoopbackHosts(script, devServer.Host));

                context.Response.Headers.Remove("ETag");
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// dotnet watch tells the page to reach its hot reload socket at localhost — which, on a device, is the device.
    /// The dev server's host is the one name for the development machine this device is known to reach.
    /// </summary>
    internal static string RewriteLoopbackHosts(string script, string host)
        => script
            .Replace("ws://localhost:", $"ws://{host}:", StringComparison.Ordinal)
            .Replace("wss://localhost:", $"wss://{host}:", StringComparison.Ordinal)
            .Replace("ws://127.0.0.1:", $"ws://{host}:", StringComparison.Ordinal)
            .Replace("wss://127.0.0.1:", $"wss://{host}:", StringComparison.Ordinal);

    /// <summary>A file from the dev server, for background.js in development. Null when there is none.</summary>
    internal async Task<string?> ReadDevServerFileAsync(string path, CancellationToken cancellationToken)
    {
        if (this.devServer is not { } dev)
            return null;

        using var response = await this.DevClient.GetAsync(new Uri(dev, path), cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }

    void SetStatus(WebAppHostStatus status)
    {
        this.Status = status;
        this.StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        await this.server.DisposeAsync().ConfigureAwait(false);

        this.Updater.Dispose();
        this.devClient?.Dispose();
        this.lifetime.Dispose();
        this.gate.Dispose();
    }

    /// <summary>Turns download progress into status updates, at most one per percent.</summary>
    sealed class StatusProgress(WebAppHost host, WebAppPackage? active) : IProgress<WebAppDownloadProgress>
    {
        int lastPercent = -1;

        public void Report(WebAppDownloadProgress value)
        {
            var percent = (int)(value.Fraction * 100);
            if (percent == this.lastPercent)
                return;

            this.lastPercent = percent;
            host.SetStatus(new WebAppHostStatus(WebAppHostState.Downloading, active, value));
        }
    }
}
