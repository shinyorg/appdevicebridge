using System.Net;
using Microsoft.Extensions.DependencyInjection;
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

    SimulatorHost(SimulatorOptions options, ServiceProvider services, string? ownedDataDirectory)
    {
        this.Options = options;
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

        var services = new ServiceCollection();
        services.AddSingleton(bridgeOptions);
        services.AddSingleton(hub);
        services.AddSingleton(state);
        services.AddSingleton(sp => new TrailLibrary(sp.GetRequiredService<SimulatorState>()));

        foreach (var bridge in state.Bridges)
            services.AddSingleton<IWebAppBridge>(new SimulatedBridge(state, bridge));

        services.AddShinyHttpServer(
            http =>
            {
                http.Options.Address = IPAddress.Loopback;
                http.Options.Port = options.Port;
                http.AddAppDeviceBridge();
                http.AddTrafficRecorder(o => o.MaxExchanges = 1000);
                http.Configure(server => ServePages(server, options, state));
            },
            autoStart: false
        );

        var provider = services.BuildServiceProvider();
        var host = new SimulatorHost(options, provider, owned);

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

    /// <summary>Starts listening and plays the trails named by <see cref="SimulatorOptions.Play"/>.</summary>
    /// <exception cref="ArgumentException">A trail to play was never loaded.</exception>
    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        var origin = await this.Server.StartAsync(cancellationToken);
        this.State.Log($"serving {origin}");

        foreach (var name in this.Options.Play)
        {
            var player = this.Trails.Find(name) ?? throw new ArgumentException($"--play: no trail is named '{name}'. Loaded: {String.Join(", ", this.Trails.Trails.Select(x => x.Name))}.");
            player.Speed = this.Options.Speed;
            _ = player.Play();
        }

        return origin;
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

    static bool IsBridgeServer(HttpContext context)
        => AppDeviceBridgeServer.IsUnder(context.Request.Path, WebAppPaths.DefaultBridgePrefix)
           || AppDeviceBridgeServer.IsUnder(context.Request.Path, WebAppPaths.HostSegment);

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
