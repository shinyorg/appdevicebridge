using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// The bridges, on the app's own Shiny.Net.HttpServer. Registered with
/// <see cref="AppDeviceBridgeHttpServerBuilderExtensions.AddAppDeviceBridge"/>, it composes itself onto the
/// <see cref="HttpServer"/> the container builds: every registered bridge under the bridge prefix, each behind
/// <see cref="AppDeviceBridgePolicies.Bridges"/>, plus <c>_host</c> and whatever an extension such as the WebView host adds.
/// <para>
/// It owns what it mounts and nothing else. The app's own middleware, endpoints, authentication and fallback policy are
/// the app's, and a request for any path the bridge server did not mount passes through it untouched.
/// </para>
/// </summary>
public sealed class AppDeviceBridgeServer : IAsyncDisposable
{
    readonly IReadOnlyList<IWebAppBridge> bridges;
    readonly IServiceProvider services;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<IAppDeviceBridgeServerExtension> extensions = [];
    IReadOnlyList<IAppDeviceBridgeTunnel> tunnels = [];
    IAuthenticationHandler[] authentication = [];
    AuthorizationPolicy? bridgePolicy;
    HttpServer? http;
    int disposed;

    public AppDeviceBridgeServer(
        AppDeviceBridgeOptions options,
        IEnumerable<IWebAppBridge> bridges,
        WebAppEventHub events,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridges);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(services);

        options.Validate();

        this.Options = options;
        this.Events = events;
        this.Paths = WebAppPaths.From(options);
        this.services = services;
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AppDeviceBridgeServer>();

        // Settings and files are registered either way; switching them off leaves them unmapped.
        this.bridges =
        [
            .. bridges.Where(x => x switch
            {
                WebAppSettingsBridge => options.EnableSettings,
                WebAppFilesBridge => options.EnableFiles,
                _ => true
            })
        ];
    }

    public AppDeviceBridgeOptions Options { get; }

    public WebAppEventHub Events { get; }

    /// <summary>Where everything is mounted. See <see cref="WebAppPaths"/>.</summary>
    public WebAppPaths Paths { get; }

    /// <summary>The bridges this server carries.</summary>
    public IReadOnlyList<IWebAppBridge> Bridges => this.bridges;

    /// <summary>
    /// The app's Shiny.Net.HttpServer, which the bridges are composed onto. Resolving it is what composes them, so this is
    /// safe to read at any time.
    /// </summary>
    public HttpServer Http => this.http ?? this.services.GetRequiredService<HttpServer>();

    /// <summary>
    /// <c>http://127.0.0.1:{port}/</c> — the address this device reaches the server at — while the server is running;
    /// null while it is not. Read from the running server each time, so an app that restarts it on another port is
    /// followed.
    /// </summary>
    public Uri? Origin => this.http is { IsRunning: true } server ? OriginOf(server) : null;

    /// <summary>The extensions the server was composed with.</summary>
    public IReadOnlyList<IAppDeviceBridgeServerExtension> Extensions => this.extensions;

    /// <summary>The tunnels registered in the container, resolved when the server was composed.</summary>
    public IReadOnlyList<IAppDeviceBridgeTunnel> Tunnels => this.tunnels;

    /// <summary>
    /// Starts the app's server if it is not running, and returns the loopback origin. Safe to call again, and safe to call
    /// on a server the app already started. When <see cref="AppDeviceBridgeOptions.AllowPortFallback"/> is on and the
    /// port is taken, serves on any free port instead and keeps that port for later restarts.
    /// </summary>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        var server = this.Http;
        if (this.Origin is { } running)
            return running;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.Origin is { } raced)
                return raced;

            try
            {
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (this.Options.AllowPortFallback && server.Options.Port != 0 && IsAddressInUse(ex))
            {
                this.logger.LogWarning("Port {Port} is in use; serving on a random port. A page's web storage will be empty for this launch.", server.Options.Port);
                server.Options.Port = 0;
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            // Pinned from here on, so a restart after resume comes back on the same origin.
            if (ListeningPort(server) is { } port)
                server.Options.Port = port;

            return OriginOf(server) ?? throw new InvalidOperationException("The server started but reports no port to reach it on.");
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Checks the server still answers and restarts it on the same port if it does not. Call when the app returns to the
    /// foreground: iOS takes the listening socket away during suspension while the server still believes it is running.
    /// True when it restarted, meaning a WebView showing it should reload.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (this.Origin is not { } current || this.http is not { } server)
        {
            await this.StartAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (await PingAsync(new Uri(current, this.Paths.Ping), cancellationToken).ConfigureAwait(false))
            return false;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            this.logger.LogInformation("The bridge server stopped answering; restarting on {Origin}", current);

            try
            {
                await server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogDebug(ex, "Stopping the stale listener failed");
            }

            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Puts the bridges onto the server. Called once, from the server's own configuration when the container builds it —
    /// <see cref="AppDeviceBridgeHttpServerBuilderExtensions.AddAppDeviceBridge"/> arranges that.
    /// </summary>
    internal void Compose(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (Interlocked.CompareExchange(ref this.http, server, null) is not null)
            throw new InvalidOperationException("The bridge server is already composed onto a server.");

        this.extensions = [.. this.services.GetServices<IAppDeviceBridgeServerExtension>()];
        this.tunnels = [.. this.services.GetServices<IAppDeviceBridgeTunnel>()];
        this.authentication = [.. this.services.GetServices<IAuthenticationHandler>()];

        // Raised whatever is registered. The limit is the server's, checked before any route is chosen, so a file too big
        // for it is refused with 413 before the files bridge, a bridge taking a photo or an app's own upload endpoint ever
        // sees it. Only ever raised: a limit the app set higher, or turned off, stands.
        var limits = server.Options.Limits;
        if (this.Options.MaxFileWriteBytes > (limits.MaxRequestBodySize ?? Int64.MaxValue))
            limits.MaxRequestBodySize = this.Options.MaxFileWriteBytes;

        // Ahead of the guard, so a request it turns away — a 401, a 403, a 421 — is recorded too.
        if (this.services.GetService<TrafficRecorder>() is { } traffic)
            server.Use(traffic.RecordAsync);

        server.Use(this.GuardAsync);

        foreach (var extension in this.extensions)
            extension.ConfigurePipeline(this);

        this.MapHostRoutes(server);

        foreach (var bridge in this.bridges)
            bridge.Map(new WebAppBridgeRoutes(server, this.Paths.Bridge, bridge.Name, this.Events));

        foreach (var extension in this.extensions)
            extension.Map(this);
    }

    /// <summary>
    /// Whether the default rules let this caller in without asking who it is: a debug build with
    /// <see cref="AppDeviceBridgeOptions.AllowAnyCallerInDebug"/>, for any caller that did not come through a tunnel. That
    /// allowance is for a browser on the development machine or a device on the same network; a tunnel is the internet,
    /// and opening one in a debug build must not hand it the device.
    /// </summary>
    public bool AllowsAnyCaller(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return this.Options.IsDebug && this.Options.AllowAnyCallerInDebug && !context.Connection.IsTunneled;
    }

    /// <summary>
    /// The default bridge policy's decision: any caller the debug allowance lets in, or a caller on this device that every
    /// extension also admits — the WebView host admits only its own launch session. Not consulted once
    /// <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/> has replaced the policy.
    /// </summary>
    public bool IsDefaultBridgeCaller(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (this.AllowsAnyCaller(context))
            return true;

        if (!BridgeCallers.IsOnDevice(context))
            return false;

        foreach (var extension in this.extensions)
        {
            if (!extension.AdmitsBridgeCaller(context))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the request names this server as something it answers to: loopback, an IP address, or a name in
    /// <see cref="AppDeviceBridgeOptions.AllowedHosts"/>. A request that came through a tunnel must name the tunnel itself —
    /// its current public host, or an allowed name such as a custom domain in front of it. Loopback and bare addresses are
    /// refused there: nobody on the internet reaches this device by those names, so one arriving through a tunnel is a
    /// caller trying to pass for something it is not.
    /// <para>
    /// Applied to everything the bridge server answers — the bridges, <c>_host</c>, and the web app's files when the WebView
    /// host serves them. The app's own endpoints are the app's to check.
    /// </para>
    /// </summary>
    public bool IsAllowedHost(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.Request.Host;
        if (BridgeCallers.HostName(host) is not { } name)
            return false;

        if (this.Options.AllowedHosts.Contains(name))
            return true;

        if (context.Connection.IsTunneled)
            return this.IsTunnelHost(name);

        return BridgeCallers.IsLoopbackHost(host) || IPAddress.TryParse(name, out _);
    }

    /// <summary>Whether a host name is the public host of a tunnel open right now. Read per request: a free tunnel's address changes when it reconnects.</summary>
    bool IsTunnelHost(string name)
    {
        foreach (var tunnel in this.tunnels)
        {
            if (tunnel.PublicUrl is { } url && String.Equals(url.IdnHost, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    async ValueTask GuardAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;

        // /kiosk means /kiosk/: the difference decides what every relative URL on a page resolves against.
        if (this.Paths.IsBaseWithoutSlash(request.Path))
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Redirect(this.Paths.BaseWithSlash);
            return;
        }

        var isBridge = IsUnder(request.Path, this.Paths.Bridge);

        // Not the bridge server's: the app's own endpoints, or the web app's files, which the WebView host guards.
        if (!isBridge && !IsUnder(request.Path, this.Paths.Host))
        {
            await next(context);
            return;
        }

        // A request by a name this server does not answer to is DNS rebinding — a hostile site pointing its own name at
        // this device arrives carrying that name — or a mistake. 421 Misdirected Request either way.
        if (!this.IsAllowedHost(context))
        {
            context.Response.StatusCode = 421;
            return;
        }

        if (request.Path == this.Paths.Ping)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // How a page that shipped separately finds out where the bridges are. Paths only; nothing to protect.
        if (request.Path == this.Paths.Config)
        {
            await WebAppBridgeResults.Json(
                context,
                new WebAppPathsResponse(this.Paths.BaseWithSlash, this.Paths.Bridge + "/"),
                WebAppBridgeJsonContext.Default.WebAppPathsResponse
            );
            return;
        }

        if (!isBridge)
        {
            await next(context);
            return;
        }

        // Enforced here, before routing, rather than left to the app's UseAuthorization: the bridges are device access, and
        // whether they are protected must not depend on whether, or in what order, the app put authorization in its
        // pipeline. Every path under the prefix is checked, so a caller the policy refuses cannot tell which bridges exist.
        if (!await this.AuthorizeBridgeCallerAsync(context))
            return;

        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
        {
            this.logger.LogError(ex, "Bridge call {Method} {Path} failed", request.Method, request.Path);
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "bridge_failed", "The native call failed.");
        }
    }

    /// <summary>Authenticates the caller if nothing has yet, and evaluates <see cref="AppDeviceBridgePolicies.Bridges"/>. Answers 401 or 403 and returns false when it refuses.</summary>
    async ValueTask<bool> AuthorizeBridgeCallerAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            foreach (var handler in this.authentication)
            {
                var result = await handler.AuthenticateAsync(context).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    context.User = result.Principal!;
                    break;
                }

                // Credentials offered and rejected: the caller meant to authenticate and got it wrong.
                if (result.Attempted)
                    break;
            }
        }

        this.bridgePolicy ??= this.services.GetRequiredService<AuthorizationOptions>().GetPolicy(AppDeviceBridgePolicies.Bridges);

        var failure = await this.bridgePolicy.EvaluateAsync(new AuthorizationContext(context, context.User)).ConfigureAwait(false);
        if (failure is null)
            return true;

        var authenticated = context.User.Identity?.IsAuthenticated == true;
        this.logger.LogInformation(
            "Denied bridge call {Method} {Path} for {Caller}: requires {Requirement}",
            context.Request.Method,
            context.Request.Path,
            authenticated ? context.User.Identity!.Name ?? "an authenticated caller" : "an anonymous caller",
            failure
        );

        context.Response.StatusCode = authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        context.Response.ContentLength = 0;
        return false;
    }

    /// <summary>
    /// Whether one of the app's own endpoints answers this request — a route matches, or the path matches with another
    /// method, which the router turns into a 405.
    /// </summary>
    public bool MatchesEndpoint(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (this.http is not { } server)
            return false;

        var match = server.Router.Match(request.Method, request.Path, new RouteValueDictionary());
        return match.IsMatch || match.IsMethodNotAllowed;
    }

    void MapHostRoutes(HttpServer server)
    {
        server.MapGet($"{this.Paths.Bridge}/events", context => this.Events.StreamAsync(context, this.Options.EventStreamHeartbeat)).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
        server.MapPut($"{this.Paths.Bridge}/events/{{id}}", this.Events.UpdateTopicsAsync).RequireAuthorization(AppDeviceBridgePolicies.Bridges);

        server.MapGet($"{this.Paths.Bridge}/host", context =>
        {
            var status = this.extensions.OfType<IWebAppStatus>().FirstOrDefault();
            var info = new HostInfo(
                this.Options.AppId,
                this.Options.HostVersion,
                this.Options.Platform,
                status?.WebAppVersion,
                status?.WebAppOrigin,
                status?.PendingVersion,
                [.. this.bridges.Select(x => new BridgeCapability(x.Name, x.IsSupported))],
                status?.DevServer?.AbsoluteUri
            );

            return WebAppBridgeResults.Json(context, info, AppDeviceBridgeJsonContext.Default.HostInfo);
        }).RequireAuthorization(AppDeviceBridgePolicies.Bridges);

        // Lets a page offer "an update is ready — reload now?" and do the reload itself.
        server.MapPost($"{this.Paths.Bridge}/host/apply-update", context =>
        {
            var status = this.extensions.OfType<IWebAppStatus>().FirstOrDefault();
            var version = status?.PendingVersion;
            var applied = status?.ApplyPendingUpdate() == true;

            return WebAppBridgeResults.Json(
                context,
                new ApplyUpdateResult(applied, applied ? version : null),
                AppDeviceBridgeJsonContext.Default.ApplyUpdateResult
            );
        }).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="prefix"/> or under it.</summary>
    public static bool IsUnder(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    static int? ListeningPort(HttpServer server)
        => Uri.TryCreate(server.ListenUrl, UriKind.Absolute, out var listening) && listening.Port > 0 ? listening.Port : null;

    static Uri? OriginOf(HttpServer server)
    {
        var port = ListeningPort(server) ?? (server.Options.Port > 0 ? server.Options.Port : (int?)null);
        if (port is null)
            return null;

        var scheme = server.Options.Https is null ? "http" : "https";
        return new Uri($"{scheme}://127.0.0.1:{port}/");
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

    static async Task<bool> PingAsync(Uri ping, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            // The server's own certificate may be one only this app trusts.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        try
        {
            using var response = await client.GetAsync(ping, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>The server itself belongs to the container that built it, and is disposed with that container.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            this.gate.Dispose();

        return ValueTask.CompletedTask;
    }
}
