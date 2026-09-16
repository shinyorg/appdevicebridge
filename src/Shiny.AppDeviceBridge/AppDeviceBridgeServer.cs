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
/// The Shiny.Net.HttpServer the bridges live on. It is created from <see cref="AppDeviceBridgeOptions.Server"/>, configured
/// by <see cref="AppDeviceBridgeOptions.ConfigureServer"/> and by extensions such as the WebView host, and then carries
/// every registered bridge under the bridge prefix, each behind <see cref="AppDeviceBridgePolicies.Bridges"/>.
/// <para>
/// The pipeline is put together once, the first time the server starts — Shiny.Net.HttpServer composes its middleware on
/// first serve — so everything that adds to it has to be registered before then.
/// </para>
/// </summary>
public sealed class AppDeviceBridgeServer : IAsyncDisposable
{
    readonly IReadOnlyList<IWebAppBridge> bridges;
    readonly Func<IEnumerable<IAppDeviceBridgeServerExtension>> resolveExtensions;
    readonly IServiceProvider? services;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<IAppDeviceBridgeServerExtension> extensions = [];
    ServiceProvider? security;
    HttpServer? http;
    volatile Uri? origin;
    int disposed;

    public AppDeviceBridgeServer(
        AppDeviceBridgeOptions options,
        IEnumerable<IWebAppBridge> bridges,
        WebAppEventHub events,
        Func<IEnumerable<IAppDeviceBridgeServerExtension>>? extensions = null,
        IServiceProvider? services = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridges);
        ArgumentNullException.ThrowIfNull(events);

        options.Validate();

        this.Options = options;
        this.Events = events;
        this.Paths = WebAppPaths.From(options);
        this.services = services;
        this.resolveExtensions = extensions ?? (() => []);
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AppDeviceBridgeServer>();

        // Settings and files are registered with the server either way; switching them off leaves them unmapped.
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

    /// <summary>The Shiny.Net.HttpServer itself. Null until the server has been built, which <see cref="StartAsync"/> does.</summary>
    public HttpServer? Http => this.http;

    /// <summary><c>http://127.0.0.1:{port}/</c> — the address this device reaches the server at — once started.</summary>
    public Uri? Origin => this.origin;

    /// <summary>The extensions the server was built with.</summary>
    public IReadOnlyList<IAppDeviceBridgeServerExtension> Extensions => this.extensions;

    /// <summary>Builds the pipeline if it has not been, and starts listening. Safe to call again; a running server is left alone.</summary>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        if (this.origin is { } started)
            return started;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.origin is { } raced)
                return raced;

            var server = this.Build();
            var serverOptions = this.Options.Server;

            try
            {
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (this.Options.AllowPortFallback && serverOptions.Port != 0 && IsAddressInUse(ex))
            {
                this.logger.LogWarning("Port {Port} is in use; serving on a random port. A page's web storage will be empty for this launch.", serverOptions.Port);
                serverOptions.Port = 0;
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            var port = Uri.TryCreate(server.ListenUrl, UriKind.Absolute, out var listening) && listening.Port > 0
                ? listening.Port
                : serverOptions.Port;

            // Pinned from here on, so a restart after resume comes back on the same origin.
            serverOptions.Port = port;

            var scheme = serverOptions.Https is null ? "http" : "https";
            this.origin = new Uri($"{scheme}://127.0.0.1:{port}/");

            return this.origin;
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
        if (this.origin is not { } current || this.http is not { } server)
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
    /// Creates the server and composes everything onto it, if that has not happened yet. <see cref="StartAsync"/> calls
    /// this; call it yourself only to inspect the routes without listening.
    /// </summary>
    public HttpServer Build()
    {
        if (this.http is { } built)
            return built;

        this.extensions = [.. this.resolveExtensions()];
        this.security = this.BuildSecurity();

        // Uploads to the files bridge go up to its own limit; the server's 30 MB default would otherwise decide first.
        var limits = this.Options.Server.Limits;
        if (this.bridges.OfType<WebAppFilesBridge>().Any() && this.Options.MaxFileWriteBytes > (limits.MaxRequestBodySize ?? Int64.MaxValue))
            limits.MaxRequestBodySize = this.Options.MaxFileWriteBytes;

        var server = new HttpServer(
            this.Options.Server,
            this.services is null ? this.security : new WebAppServiceProvider(this.security, this.services)
        );
        this.http = server;

        server.Use(this.GuardAsync);

        var before = server.Router.Endpoints.ToHashSet();
        var appServices = this.services ?? this.security;

        foreach (var configure in this.Options.ServerConfigurations)
            configure(server, appServices);

        var appEndpoints = server.Router.Endpoints.Where(x => !before.Contains(x)).ToList();

        foreach (var extension in this.extensions)
            extension.ConfigurePipeline(this);

        server.UseAuthentication();
        server.UseAuthorization();

        this.MapHostRoutes(server);

        foreach (var bridge in this.bridges)
            bridge.Map(new WebAppBridgeRoutes(server, this.Paths.Bridge, bridge.Name, this.Events));

        foreach (var extension in this.extensions)
            extension.Map(this);

        this.MountAppEndpoints(server, appEndpoints);
        return server;
    }

    ServiceProvider BuildSecurity()
    {
        var container = new ServiceCollection();
        var http = new ShinyHttpServerBuilder(container);
        var authentication = http.AddAuthentication();

        foreach (var extension in this.extensions)
            extension.ConfigureAuthentication(authentication);

        foreach (var configure in this.Options.Authentication)
            configure(authentication);

        // One call, carrying every policy: a second AddAuthorization would be ignored.
        http.AddAuthorization(o =>
        {
            // Secure by default: an endpoint of the app's that says nothing needs a caller who authenticated somehow.
            o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());

            o.AddPolicy(AppDeviceBridgePolicies.Bridges, p =>
            {
                if (this.Options.BridgePolicy is { } custom)
                {
                    custom(p);
                    return;
                }

                p.RequireAssertion(
                    ctx => this.AllowsAnyCaller || BridgeCallers.IsOnDevice(ctx.HttpContext),
                    "a caller on this device"
                );

                foreach (var extension in this.extensions)
                    extension.ConfigureDefaultBridgePolicy(p);
            });

            foreach (var extension in this.extensions)
                extension.ConfigureAuthorization(o);

            foreach (var configure in this.Options.Authorization)
                configure(o);
        });

        return container.BuildServiceProvider();
    }

    /// <summary>Whether the default bridge policy lets anyone in: a debug build with <see cref="AppDeviceBridgeOptions.AllowAnyCallerInDebug"/>.</summary>
    public bool AllowsAnyCaller => this.Options.IsDebug && this.Options.AllowAnyCallerInDebug;

    async ValueTask GuardAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;

        // A request by a name this server does not answer to is DNS rebinding — a hostile site pointing its own name at
        // this device arrives carrying that name — or a mistake. 421 Misdirected Request either way.
        if (!this.IsAllowedHost(request.Host))
        {
            context.Response.StatusCode = 421;
            return;
        }

        // /kiosk means /kiosk/: the difference decides what every relative URL on a page resolves against.
        if (this.Paths.IsBaseWithoutSlash(request.Path))
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Redirect(this.Paths.BaseWithSlash);
            return;
        }

        if (!this.Paths.TryStripBase(request.Path, out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
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

        if (!IsUnder(request.Path, this.Paths.Bridge))
        {
            await next(context);
            return;
        }

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

    /// <summary>Loopback, an IP address, or a name in <see cref="AppDeviceBridgeOptions.AllowedHosts"/>.</summary>
    bool IsAllowedHost(string? host)
        => BridgeCallers.HostName(host) is { } name
           && (BridgeCallers.IsLoopbackHost(host) || IPAddress.TryParse(name, out _) || this.Options.AllowedHosts.Contains(name));

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
        server.MapGet($"{this.Paths.Bridge}/events", this.Events.StreamAsync).RequireAuthorization(AppDeviceBridgePolicies.Bridges);

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

    /// <summary>
    /// Moves the app's own endpoints under the base path. Mapped from the root and moved afterwards, because a
    /// source-generated <c>[Route]</c> class can only map at the template it was written with; method, constraints and
    /// metadata — <c>[Authorize]</c> and <c>[AllowAnonymous]</c> included — carry over.
    /// </summary>
    void MountAppEndpoints(HttpServer server, IReadOnlyList<RouteEndpoint> added)
    {
        foreach (var endpoint in added)
        {
            var template = endpoint.Template.ToString() ?? String.Empty;
            var mounted = this.Paths.Base + (template.StartsWith('/') ? template : "/" + template);

            if (IsUnder(mounted, this.Paths.Bridge) || IsUnder(mounted, this.Paths.Host))
                throw new InvalidOperationException(
                    $"The endpoint {endpoint.Method} {template} would sit under {(IsUnder(mounted, this.Paths.Bridge) ? this.Paths.Bridge : this.Paths.Host)}, which the bridge server reserves. Map it somewhere else."
                );

            if (this.Paths.Base.Length > 0)
            {
                server.Unmap(endpoint);
                server.MapRoute(endpoint.Method, mounted, endpoint.RequestDelegate, [.. endpoint.Metadata]);
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="prefix"/> or under it.</summary>
    public static bool IsUnder(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        if (this.http is { } server)
            await server.DisposeAsync().ConfigureAwait(false);

        if (this.security is not null)
            await this.security.DisposeAsync().ConfigureAwait(false);

        this.gate.Dispose();
    }
}
