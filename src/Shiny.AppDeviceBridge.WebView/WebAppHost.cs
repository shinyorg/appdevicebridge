using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.AppDeviceBridge.WebView;

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
public sealed partial class WebAppHost : IAppDeviceBridgeServerExtension, IWebAppStatus, IAsyncDisposable
{
    readonly WebAppHostOptions options;
    readonly AppDeviceBridgeServer server;
    readonly WebAppSession session;
    readonly ILogger logger;
    readonly WebAppFileSource source = new();
    readonly WebAppInstallStore store;
    readonly StaticFileMiddleware staticFiles;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();

    volatile Uri? devServer;
    HttpClient? devClient;
    int disposed;

    public WebAppHost(
        WebAppHostOptions options,
        AppDeviceBridgeServer server,
        WebAppSession session,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(session);

        options.Validate();

        this.options = options;
        this.server = server;
        this.session = session;
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebAppHost>();

        this.store = new WebAppInstallStore(options.ResolveInstallDirectory(server.Options), this.logger);
        this.Updater = new WebAppUpdater(options, server.Options, this.store, this.logger);

        this.staticFiles = new StaticFileMiddleware(new StaticFileOptions
        {
            Source = this.source,
            FallbackFile = options.SpaFallback ? options.EntryDocument : null,

            // Strips the mount point, so everything downstream addresses the archive from its own root.
            RequestPath = server.Paths.Base,
            ServePrecompressedFiles = true,

            // Only the entry document, whose URL never changes across updates: a cached copy would keep
            // loading the old build. Every other file's caching is the app's to decide.
            OnPrepareResponse = x =>
            {
                if (String.Equals(x.File.Name, options.EntryDocument, StringComparison.OrdinalIgnoreCase))
                    x.HttpContext.Response.Headers["Cache-Control"] = "no-cache";
            }
        });
    }

    /// <summary>The bridge server the web app is served from.</summary>
    public AppDeviceBridgeServer Server => this.server;

    /// <summary>Where the server mounted everything. See <see cref="WebAppPaths"/>.</summary>
    public WebAppPaths Paths => this.server.Paths;

    WebAppPaths paths => this.server.Paths;

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
    public Uri? Origin => this.server.Origin;

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
        if (this.server.Origin is not null)
            await this.server.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        await this.EnsureActivatedAsync(cancellationToken).ConfigureAwait(false);
        return await this.server.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.Status.State == WebAppHostState.Ready && this.server.Origin is not null)
                return this.BuildStartUri();

            this.SetStatus(new WebAppHostStatus(WebAppHostState.Starting));

            var active = this.ActivateBestAvailable();

            // Before any download: pruning clears the pending folder.
            this.store.Prune(active);

            await this.server.StartAsync(cancellationToken).ConfigureAwait(false);

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
    /// Checks the loopback server still answers and restarts it on the same port if it does not. Call when the app
    /// returns to the foreground. Returns true when it restarted, meaning the WebView should reload.
    /// </summary>
    public Task<bool> EnsureServerRunningAsync(CancellationToken cancellationToken = default)
        => this.server.Origin is null ? Task.FromResult(false) : this.server.EnsureRunningAsync(cancellationToken);

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
        var start = $"{this.paths.Start}?token={Uri.EscapeDataString(this.session.Token)}";

        // A link that launched the app rides through the token exchange, whose redirect accepts only local paths.
        if (this.server.Bridges.OfType<WebAppLinksBridge>().FirstOrDefault()?.Links.TakeStartRoute() is { } route)
            start += $"&path={Uri.EscapeDataString(route)}";

        return new(this.server.Origin!, start);
    }

    /// <summary>
    /// Serves the newer of the bundled and installed builds, falling back to the other when one will not
    /// open. The bundled build wins a tie, and wins outright when an app store update ships a baseline
    /// newer than the last download.
    /// </summary>
    WebAppPackage? ActivateBestAvailable()
    {
        var installed = this.store.ReadInstalled(WebAppVersion.Parse(this.server.Options.HostVersion));

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

    /// <summary>
    /// On top of "a caller on this device": the caller is this app's own WebView, holding the launch cookie. Binding to
    /// loopback keeps other machines out but not other apps — on Android any app can open a socket to 127.0.0.1 — so
    /// the session is what keeps the bridges to the page.
    /// </summary>
    bool IAppDeviceBridgeServerExtension.AdmitsBridgeCaller(HttpContext context) => this.HasSession(context);

    void IAppDeviceBridgeServerExtension.ConfigurePipeline(AppDeviceBridgeServer bridgeServer)
        => bridgeServer.Http!.Use(this.ServeAsync);

    string? IWebAppStatus.WebAppVersion => this.source.Package?.Version.ToString();

    WebAppOrigin? IWebAppStatus.WebAppOrigin => this.source.Package is { } package
        ? BridgeEnum.Convert<WebAppPackageOrigin, WebAppOrigin>(package.Origin)
        : null;

    string? IWebAppStatus.PendingVersion => this.PendingPackage?.Version.ToString();

    bool HasSession(HttpContext context)
        => BridgeCallers.IsLocalConnection(context)
           && this.session.IsValid(context.Request.Cookies[WebAppSession.CookieName]);

    /// <summary>
    /// The session exchange, and the web app's files — everything under the mount point that is not a bridge, <c>_host</c>
    /// or one of the app's own endpoints. The rest of the app's server passes through.
    /// </summary>
    async ValueTask ServeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        // A tunnel delivers from loopback, so the address alone would count the internet as this device.
        var isLocal = BridgeCallers.IsLocalConnection(context);

        if (request.Path == this.paths.Start)
        {
            // The session belongs to this device's WebView; handing a launch token to the network would undo the
            // point of having one.
            if (isLocal)
                this.BeginSession(context);
            else
                context.Response.StatusCode = StatusCodes.Status403Forbidden;

            return;
        }

        // Bridges, _host, the app's own endpoints, and anything outside the mount point are not the web app's. Straight past
        // the static files, whose SPA fallback would otherwise answer an unknown bridge route with index.html and a 200.
        if (AppDeviceBridgeServer.IsUnder(request.Path, this.paths.Bridge)
            || AppDeviceBridgeServer.IsUnder(request.Path, this.paths.Host)
            || this.server.MatchesEndpoint(request)
            || !this.paths.TryStripBase(request.Path, out var relative))
        {
            await next(context);
            return;
        }

        // The same names the bridges answer to: a page is not served to a hostile site that pointed its own name here.
        if (!this.server.IsAllowedHost(context))
        {
            context.Response.StatusCode = 421;
            return;
        }

        var allowed = this.server.AllowsAnyCaller(context)
                      || (isLocal ? this.HasSession(context) : this.options.ServeWebAppRemotely);

        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await this.ServeWebAppAsync(context, relative, isLocal, next);
    }

    /// <summary>
    /// The web app's own files, from the dev server or the installed build. <paramref name="relative"/> is the
    /// path with the mount point taken off: everything downstream serves from the root of the archive and knows
    /// nothing about where the app was mounted.
    /// </summary>
    async ValueTask ServeWebAppAsync(HttpContext context, string relative, bool isLocal, RequestDelegate next)
    {
        // Never for another machine: that proxy exists so this device's WebView can reach a machine on the
        // developer's desk, and is not something to relay for the network.
        if (this.devServer is { } dev && isLocal)
        {
            await this.ProxyToDevServerAsync(context, dev, relative);
            return;
        }

        // Mounted somewhere other than the root, the entry document's <base href> has to say so, or every
        // relative URL on the page resolves against the origin instead.
        if (this.paths.Base.Length > 0 && this.IsEntryDocument(relative) && await this.TryServeEntryDocumentAsync(context))
            return;

        await this.staticFiles.InvokeAsync(context, next);
    }

    /// <summary>
    /// Whether the static middleware would answer this with the entry document: it is the entry document, or
    /// it is a route the SPA fallback picks up.
    /// </summary>
    bool IsEntryDocument(string relative)
    {
        var path = relative.TrimStart('/');

        if (path.Length == 0 || String.Equals(path, this.options.EntryDocument, StringComparison.OrdinalIgnoreCase))
            return true;

        return this.options.SpaFallback && !this.source.TryGetFile(path, out _);
    }

    async ValueTask<bool> TryServeEntryDocumentAsync(HttpContext context)
    {
        if (!this.source.TryGetFile(this.options.EntryDocument, out var file))
            return false;

        string html;
        try
        {
            await using var stream = await file.Open(context.RequestAborted).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "The entry document could not be read to rewrite its base href");
            return false;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await Results.Text(this.RewriteBaseHref(html), "text/html; charset=utf-8").ExecuteAsync(context);
        return true;
    }

    /// <summary>
    /// Points the document's <c>&lt;base href&gt;</c> at the mount point, adding one after <c>&lt;head&gt;</c>
    /// when the document has none. A Blazor publish ships <c>&lt;base href="/" /&gt;</c>, which is what makes
    /// this worth doing rather than asking every app to republish for its mount point.
    /// </summary>
    internal string RewriteBaseHref(string html)
    {
        var replacement = $"<base href=\"{this.paths.BaseWithSlash}\" />";

        if (BaseHrefPattern().IsMatch(html))
            return BaseHrefPattern().Replace(html, replacement, 1);

        return HeadPattern().IsMatch(html)
            ? HeadPattern().Replace(html, m => m.Value + replacement, 1)
            : replacement + html;
    }

    [GeneratedRegex("<base\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseHrefPattern();

    [GeneratedRegex("<head\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadPattern();

    void BeginSession(HttpContext context)
    {
        if (!this.session.IsValid(context.Request.Query["token"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Only a local path: anything else would make this an open redirect.
        var path = context.Request.Query["path"].ToString();
        path = WebAppLinks.IsLocalRoute(path) ? this.paths.Base + path : this.paths.BaseWithSlash;

        context.Response.Cookies.Append(WebAppSession.CookieName, this.session.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = this.paths.BaseWithSlash
        });

        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Redirect(path);
    }

    /// <summary>Writes a rewritten body, dropping the headers that described the one it replaced.</summary>
    async Task ReplaceBodyAsync(HttpContext context, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);

        context.Response.Headers.Remove("ETag");
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
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

    async ValueTask ProxyToDevServerAsync(HttpContext context, Uri devServer, string relative)
    {
        var request = context.Request;
        var query = String.IsNullOrEmpty(request.QueryString) ? String.Empty
            : request.QueryString.StartsWith('?') ? request.QueryString
            : "?" + request.QueryString;

        // The hot reload script is rewritten below, which needs it uncompressed.
        var isRefreshScript = relative.Contains("browser-refresh", StringComparison.OrdinalIgnoreCase);

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(devServer, relative + query));

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
                await this.ReplaceBodyAsync(context, RewriteLoopbackHosts(script, devServer.Host)).ConfigureAwait(false);
                return;
            }

            // The dev server knows nothing about the mount point, so its index.html still says
            // <base href="/">. Same correction the installed build gets, applied to the proxied bytes.
            if (this.paths.Base.Length > 0
                && response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                var html = await response.Content.ReadAsStringAsync(context.RequestAborted).ConfigureAwait(false);
                await this.ReplaceBodyAsync(context, this.RewriteBaseHref(html)).ConfigureAwait(false);
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
        // The container holds this twice — as itself and as a server extension — and disposes both.
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        await this.lifetime.CancelAsync().ConfigureAwait(false);

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
