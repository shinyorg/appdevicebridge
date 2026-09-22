using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Simulator.Control;
using Shiny.AppDeviceBridge.Simulator.Scenarios;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.AppDeviceBridge.Simulator.Hosting;

/// <summary>
/// The simulator's server: the real bridge server on Shiny.Net.HttpServer — its guard, policy, event stream, host, settings
/// and files bridges — with every device bridge replaced by a <see cref="SimulatedBridge"/>, a traffic recorder in front,
/// and the page served from the same origin.
/// <para>
/// Nothing about the bridge policy is loosened: the server listens on loopback only and admits callers on this machine,
/// exactly as a release build of an app does.
/// </para>
/// </summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    readonly ServiceProvider services;
    readonly string? ownedDataDirectory;

    SimulatorHost(SimulatorOptions options, ServiceProvider services, string? ownedDataDirectory, string? mcpToken, string? webToken)
    {
        this.Options = options;
        this.McpToken = mcpToken;
        this.WebToken = webToken;
        this.services = services;
        this.ownedDataDirectory = ownedDataDirectory;
        this.Server = services.GetRequiredService<AppDeviceBridgeServer>();
        this.State = services.GetRequiredService<SimulatorState>();
        this.Traffic = services.GetRequiredService<TrafficRecorder>();
        this.Trails = services.GetRequiredService<TrailLibrary>();
    }

    public SimulatorOptions Options { get; }

    public AppDeviceBridgeServer Server { get; }

    public SimulatorState State { get; }

    /// <summary>Every request the page made and what it got back, as the MAUI traffic monitor shows it.</summary>
    public TrafficRecorder Traffic { get; }

    public TrailLibrary Trails { get; }

    /// <summary>The container, for the stdio transport, which needs the same services the HTTP endpoint runs against.</summary>
    internal IServiceProvider Services => this.services;

    /// <summary>The control surface, for a caller holding the host rather than reaching it over MCP.</summary>
    public SimulatorControl Control => this.services.GetRequiredService<SimulatorControl>();

    /// <summary>The token the MCP endpoint requires; null when it is not being served.</summary>
    public string? McpToken { get; }

    /// <summary>Where an MCP client connects, once started. Null when the endpoint is not being served.</summary>
    public Uri? McpEndpoint => this.McpToken is null || this.Origin is not { } origin ? null : new Uri(origin, McpSetup.Path.TrimStart('/'));

    /// <summary>The token the web panel's API requires; null when the panel is not being served.</summary>
    public string? WebToken { get; }

    /// <summary>The web panel's address with its token, once started. Null when the panel is not being served.</summary>
    public Uri? PanelUrl => this.WebToken is not { } token || this.Origin is not { } origin ? null : WebPanel.Url(origin, token);

    /// <summary>Where the page is served, once started.</summary>
    public Uri? Origin => this.Server.Origin;

    /// <summary>Builds the server, applies the scenario and loads the trails. Nothing listens until <see cref="StartAsync"/>.</summary>
    /// <param name="time">For tests: drives delays and trails.</param>
    public static SimulatorHost Create(SimulatorOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? owned = null;
        var data = options.DataDirectory;
        if (data is null)
        {
            data = owned = Path.Combine(Path.GetTempPath(), "shiny-bridge-sim", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(data);
        }

        var bridgeOptions = new AppDeviceBridgeOptions
        {
            AppId = options.AppId,
            HostVersion = typeof(SimulatorHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            Platform = options.Platform,
            DataDirectory = data,

            // The rules a shipped app has: callers on this machine only, whatever this tool was built as.
            IsDebug = false,

            // The page must know the port it was given; a silent move to another would lose the browser's storage and its tab.
            AllowPortFallback = false
        };

        var hub = new WebAppEventHub();
        var state = new SimulatorState(hub, bridgeOptions, time);
        // Both modes need the server and its tools; only --mcp puts an endpoint on the network, and only that endpoint
        // needs a token. An agent speaking over stdio already has the process.
        var mcp = options.Mcp || options.McpStdio;
        var token = options.Mcp ? options.McpToken ?? McpSetup.NewToken() : null;
        var webToken = options.Web ? options.WebToken ?? McpSetup.NewToken() : null;

        // The tools are built as the container is configured and the control they drive is only resolvable once it has
        // been built. Nothing reads this before a tool is called, which is long after.
        ServiceProvider? built = null;

        var services = new ServiceCollection();
        services.AddSingleton(bridgeOptions);
        services.AddSingleton(hub);
        services.AddSingleton(state);
        services.AddSingleton(sp => new TrailLibrary(sp.GetRequiredService<SimulatorState>()));
        services.AddSingleton(sp => new SimulatorControl(
            sp.GetRequiredService<SimulatorState>(),
            sp.GetRequiredService<TrafficRecorder>(),
            sp.GetRequiredService<TrailLibrary>(),
            sp.GetRequiredService<AppDeviceBridgeServer>()
        ));

        if (mcp)
            services.AddMcp(() => built!.GetRequiredService<SimulatorControl>());

        foreach (var bridge in state.Bridges)
            services.AddSingleton<IWebAppBridge>(new SimulatedBridge(state, bridge));

        services.AddShinyHttpServer(
            http =>
            {
                http.Options.Address = IPAddress.Loopback;
                http.Options.Port = options.Port;
                http.AddAppDeviceBridge();
                http.AddTrafficRecorder(o =>
                {
                    o.MaxExchanges = 1000;
                    o.Skip = WebPanel.IsControl;
                });

                if (token is not null)
                    McpSetup.MapMcp(http, token);

                // Its own control, so the activity log says a change came from the panel rather than an agent.
                if (webToken is not null)
                {
                    SimulatorControl? panel = null;
                    WebPanel.MapWebPanel(http, webToken, () => panel ??= Panel(built!));
                }

                http.Configure(server => ServePages(server, options, state));
            },
            autoStart: false
        );

        var provider = services.BuildServiceProvider();
        built = provider;
        var host = new SimulatorHost(options, provider, owned, token, webToken);

        // Resolving the server composes the bridges onto it, which maps their events; mapping them all now means a page can
        // subscribe to any of them before the first is fired.
        _ = host.Server.Http;
        state.MapEvents();

        if (options.Scenario is { } scenario)
        {
            var loaded = Scenario.Load(scenario);
            foreach (var problem in loaded.Apply(state))
                state.Log($"scenario: {problem}");

            foreach (var trail in loaded.Trails)
                host.Trails.Add(trail);

            state.Log($"applied scenario {Path.GetFileName(scenario)}");
        }

        foreach (var path in options.Trails)
            host.Trails.Load(path);

        return host;
    }

    static SimulatorControl Panel(IServiceProvider services) => new(
        services.GetRequiredService<SimulatorState>(),
        services.GetRequiredService<TrafficRecorder>(),
        services.GetRequiredService<TrailLibrary>(),
        services.GetRequiredService<AppDeviceBridgeServer>(),
        "panel"
    );

    /// <summary>Starts listening and plays the trails named by <see cref="SimulatorOptions.Play"/>.</summary>
    /// <exception cref="ArgumentException">A trail to play was never loaded.</exception>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        var origin = await this.Server.StartAsync(cancellationToken);
        this.State.Log($"serving {origin}");

        if (this.McpToken is { } token && this.McpEndpoint is { } endpoint)
        {
            this.State.Log($"mcp control at {endpoint} — token {token}");
            this.WriteMcpConfig(endpoint, token);
        }

        if (this.PanelUrl is { } panel)
            this.State.Log($"web panel at {panel}");

        foreach (var name in this.Options.Play)
        {
            var player = this.Trails.Find(name) ?? throw new ArgumentException($"--play: no trail is named '{name}'. Loaded: {String.Join(", ", this.Trails.Trails.Select(x => x.Name))}.");
            player.Speed = this.Options.Speed;
            _ = player.Play();
        }

        return origin;
    }

    /// <summary>
    /// Writes the client configuration beside the simulator's data, so connecting an agent is a copy rather than a
    /// transcription of a generated token. Best effort: a simulator that cannot write it still runs.
    /// </summary>
    void WriteMcpConfig(Uri endpoint, string token)
    {
        try
        {
            // The simulator's own directory, not the shared temp root: two simulators running at once would otherwise
            // overwrite each other's config, and the one you read would hold the other one's token.
            var path = Path.Combine(this.ownedDataDirectory ?? this.Options.DataDirectory!, "mcp.json");
            File.WriteAllText(path, McpSetup.ClientConfig(this.Origin!, token));
            this.State.Log($"mcp client config written to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.State.Log($"mcp client config could not be written: {ex.Message}");
        }
    }

    static void ServePages(HttpServer server, SimulatorOptions options, SimulatorState state)
    {
        if (options.DevServer is { } dev)
        {
            var proxy = new DevServerProxy(dev);
            server.Use((context, next) => IsBridgeServer(context) ? next(context) : proxy.ForwardAsync(context));
            return;
        }

        if (options.AppDirectory is { } app)
        {
            if (Directory.Exists(Path.Combine(app, "_framework")))
                server.UseBlazorWebAssembly(app);
            else
                server.UseStaticFiles(app, o => o.FallbackFile = "index.html");

            return;
        }

        server.MapGet("/", context => context.Response.WriteTextAsync(LandingPage.Html(state), "text/html; charset=utf-8", context.RequestAborted));
    }

    /// <summary>
    /// What the simulator answers itself rather than forwarding to a dev server: the bridges, the host, and — when they are
    /// being served — the web panel and the MCP control endpoint, which are on this origin and would otherwise be proxied to the page's
    /// dev server, which knows nothing about it.
    /// </summary>
    static bool IsBridgeServer(HttpContext context)
        => AppDeviceBridgeServer.IsUnder(context.Request.Path, WebAppPaths.DefaultBridgePrefix)
           || AppDeviceBridgeServer.IsUnder(context.Request.Path, WebAppPaths.HostSegment)
           || WebPanel.IsControl(context);

    public async ValueTask DisposeAsync()
    {
        this.Trails.Dispose();

        if (this.Server.Http is { IsRunning: true } http)
            await http.StopAsync();

        await this.services.DisposeAsync();

        if (this.ownedDataDirectory is not null)
        {
            try
            {
                Directory.Delete(this.ownedDataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
