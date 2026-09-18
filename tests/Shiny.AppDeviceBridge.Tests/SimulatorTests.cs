using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Locations.Client;
using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Scenarios;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;
using Shiny.AppDeviceBridge.Wifi.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The simulator's catalog: every bridge the library ships, read from the interfaces the clients are generated from.</summary>
public class SimulatorCatalogTests
{
    [Fact]
    public void Simulates_every_bridge_client_interface_the_library_ships()
    {
        var declared = ClientAssemblies()
            .SelectMany(x => x.GetExportedTypes())
            .Where(x => x.IsInterface)
            .Select(x => x.GetCustomAttribute<BridgeClientAttribute>()?.Name)
            .OfType<string>()
            .Where(x => !BridgeCatalog.BuiltIn.Contains(x))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(declared, BridgeCatalog.All.Select(x => x.Name));
    }

    [Fact]
    public void References_every_client_project_in_the_repository()
    {
        var root = RepositoryRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Shiny.AppDeviceBridge.Simulator", "Shiny.AppDeviceBridge.Simulator.csproj"));

        var missing = Directory.EnumerateDirectories(Path.Combine(root, "src"), "Shiny.AppDeviceBridge.*.Client")
            .Select(Path.GetFileName)
            .Where(name => !csproj.Contains($"{name}/{name}.csproj", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Leaves_the_built_in_bridges_to_the_real_server()
    {
        Assert.DoesNotContain(BridgeCatalog.All, x => BridgeCatalog.BuiltIn.Contains(x.Name));
        Assert.NotNull(BridgeCatalog.Find("links"));
    }

    [Fact]
    public void Classifies_parameters_as_the_generated_clients_send_them()
    {
        var wifi = BridgeCatalog.Find("wifi")!;

        var forget = wifi.FindRoute("DELETE known")!;
        Assert.Equal(["id"], forget.QueryParameters);
        Assert.Null(forget.BodyType);

        var connect = wifi.FindRoute("POST connection")!;
        Assert.Equal(typeof(WifiConnectRequest), connect.BodyType);
        Assert.Equal(typeof(WifiNetworkInfo), connect.ResultType);

        Assert.Equal(ResponseKind.Empty, wifi.FindRoute("DELETE connection")!.Kind);
        Assert.Equal("wifi", wifi.FindRoute("GET")!.Path);

        var geofences = BridgeCatalog.Find("geofences")!;
        Assert.Empty(geofences.FindRoute("DELETE regions/{identifier}")!.QueryParameters);

        Assert.Equal(ResponseKind.Binary, BridgeCatalog.Find("photos")!.Routes.Single(x => x.Operation == "GetThumbnail").Kind);
        Assert.Contains(wifi.Events, x => x.Name == "wifi.changed" && x.PayloadType == typeof(WifiChangedEvent));
    }

    [Fact]
    public void Every_generated_sample_is_something_the_page_client_can_read()
    {
        var failures = new List<string>();

        foreach (var bridge in BridgeCatalog.All)
        {
            foreach (var route in bridge.Routes)
            {
                foreach (var type in new[] { route.ResultType, route.BodyType }.OfType<Type>().Where(x => x != typeof(Stream) && x != typeof(byte[])))
                {
                    if (!SampleJson.TryValidate(SampleJson.For(type, route.Json), type, route.Json, out var error))
                        failures.Add($"{route.Method} {route.Path} {type.Name}: {error}");
                }
            }

            foreach (var evt in bridge.Events)
            {
                if (!SampleJson.TryValidate(SampleJson.For(evt.PayloadType, evt.Json), evt.PayloadType, evt.Json, out var error))
                    failures.Add($"{evt.Name} {evt.PayloadType.Name}: {error}");
            }
        }

        Assert.True(failures.Count == 0, String.Join("\n", failures));
    }

    [Fact]
    public void Samples_use_the_contract_s_own_names_and_enum_spelling()
    {
        var json = JsonNode.Parse(SampleJson.For(typeof(WifiNetworkInfo), WifiJsonContext.Default))!.AsObject();

        Assert.Equal("Unknown", json["security"]!.GetValue<string>());
        Assert.True(json.ContainsKey("signalStrengthDbm"));
        Assert.IsType<JsonArray>(json["ipAddresses"]);
    }

    static IEnumerable<Assembly> ClientAssemblies()
        => typeof(BridgeCatalog).Assembly.GetReferencedAssemblies()
            .Where(x => x.Name!.StartsWith("Shiny.AppDeviceBridge.", StringComparison.Ordinal) && x.Name.EndsWith(".Client", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .Append(typeof(BridgeClientAttribute).Assembly)
            .Distinct();

    internal static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Shiny.AppDeviceBridge.slnx")))
            root = root.Parent;

        return root?.FullName ?? throw new InvalidOperationException("Not inside the repository.");
    }
}

/// <summary>The simulator's server, called the way a page calls it: the generated clients, over HTTP.</summary>
public class SimulatorServerTests : IAsyncLifetime
{
    SimulatorHost host = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Platform = "android" });
        var origin = await this.host.StartAsync(TestContext.Current.CancellationToken);
        this.http = new HttpClient { BaseAddress = origin };
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    SimulatorState State => this.host.State;

    WifiBridgeClient Wifi => new(new BuiltInClientTests.HttpTransport(this.http));

    [Fact]
    public async Task The_host_reports_every_simulated_bridge_and_the_chosen_platform()
    {
        var info = await new HostBridgeClient(new BuiltInClientTests.HttpTransport(this.http)).GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("android", info.Platform);
        Assert.Equal("simulator", info.AppId);
        foreach (var bridge in BridgeCatalog.All)
            Assert.Contains(info.Bridges, x => x.Name == bridge.Name && x.IsSupported);

        // The real built-in bridges are there too.
        Assert.Contains(info.Bridges, x => x.Name == "settings");
    }

    [Fact]
    public async Task A_route_answers_with_the_value_it_was_given()
    {
        this.State.SetValue("wifi", "GET current", """
            { "interfaceName": "en0", "ssid": "Coffee Shop", "security": "Wpa2Psk", "ipAddresses": ["10.0.0.7"], "dnsAddresses": [],
              "signalStrengthPercent": 71, "band": "FiveGhz", "channel": 36 }
            """);

        var current = await this.Wifi.GetCurrentNetworkAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(current);
        Assert.Equal("Coffee Shop", current.Ssid);
        Assert.Equal(WifiSecurity.Wpa2Psk, current.Security);
        Assert.Equal(WifiBand.FiveGhz, current.Band);
        Assert.Equal(1, this.State.FindRoute("wifi", "GET current")!.Hits);
    }

    [Fact]
    public async Task Every_json_route_answers_its_sample_before_anything_is_set()
    {
        var status = await this.Wifi.GetStatusAsync(TestContext.Current.CancellationToken);
        var networks = await this.Wifi.ScanAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Single(networks);
    }

    [Fact]
    public async Task Null_mode_answers_204_which_the_client_reads_as_null()
    {
        this.State.SetBehavior("wifi", "GET current", new RouteBehavior(ResponseMode.Null, ""));
        Assert.Null(await this.Wifi.GetCurrentNetworkAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_mode_fails_the_way_a_real_bridge_does()
    {
        this.State.SetBehavior("wifi", "POST connection", new RouteBehavior(ResponseMode.Error, "", 403, "access_denied", "Location permission was denied."));

        var ex = await Assert.ThrowsAsync<BridgeException>(() => this.Wifi.ConnectAsync(new WifiConnectRequest { Ssid = "Home" }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("access_denied", ex.Code);
        Assert.Equal("Location permission was denied.", ex.Message);
    }

    [Fact]
    public async Task An_unsupported_bridge_answers_501_everywhere_and_says_so_in_the_host_info()
    {
        this.State.SetSupported("wifi", false);

        var ex = await Assert.ThrowsAsync<BridgeException>(() => this.Wifi.GetStatusAsync(TestContext.Current.CancellationToken));
        Assert.True(ex.IsNotSupported);
        Assert.Equal("not_supported", ex.Code);

        var info = await new HostBridgeClient(new BuiltInClientTests.HttpTransport(this.http)).GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Contains(info.Bridges, x => x is { Name: "wifi", IsSupported: false });
    }

    [Fact]
    public async Task A_delay_holds_the_answer()
    {
        this.State.SetBehavior("wifi", "GET radio", this.State.FindRoute("wifi", "GET radio")!.Behavior with { DelayMs = 300 });

        var started = DateTimeOffset.UtcNow;
        await this.Wifi.GetRadioAsync(TestContext.Current.CancellationToken);

        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(280));
    }

    [Fact]
    public async Task A_write_of_what_a_get_returns_becomes_what_it_answers()
    {
        await this.Wifi.SetRadioAsync(new WifiRadioState(true), TestContext.Current.CancellationToken);
        Assert.True((await this.Wifi.GetRadioAsync(TestContext.Current.CancellationToken)).Enabled);

        await this.Wifi.SetRadioAsync(new WifiRadioState(false), TestContext.Current.CancellationToken);
        Assert.False((await this.Wifi.GetRadioAsync(TestContext.Current.CancellationToken)).Enabled);
    }

    [Fact]
    public async Task Sticky_writes_can_be_switched_off()
    {
        this.State.StickyWrites = false;
        this.State.SetValue("wifi", "GET radio", """{ "enabled": true }""");

        await this.Wifi.SetRadioAsync(new WifiRadioState(false), TestContext.Current.CancellationToken);

        Assert.True((await this.Wifi.GetRadioAsync(TestContext.Current.CancellationToken)).Enabled);
    }

    [Fact]
    public async Task Routes_with_a_route_parameter_are_matched()
    {
        var geofences = new GeofencesBridgeClient(new BuiltInClientTests.HttpTransport(this.http));

        await geofences.StopMonitoringAsync("home base", TestContext.Current.CancellationToken);
        var status = await geofences.GetStateAsync("office", TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Equal(1, this.State.FindRoute("geofences", "DELETE regions/{identifier}")!.Hits);
    }

    [Fact]
    public async Task A_binary_route_sends_a_placeholder_image_until_a_file_is_chosen()
    {
        using var response = await this.http.GetAsync("/_bridge/photos/library/p1/thumbnail", TestContext.Current.CancellationToken);
        var route = BridgeCatalog.Find("photos")!.Routes.Single(x => x.Operation == "GetThumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken))[..4]);
        Assert.Equal("GET library/{id}/thumbnail", route.Key);
    }

    [Fact]
    public async Task A_binary_route_sends_the_chosen_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.jpg");
        await File.WriteAllBytesAsync(file, [0xFF, 0xD8, 0xFF, 0xD9], TestContext.Current.CancellationToken);

        try
        {
            this.State.SetBehavior("photos", "GET library/{id}/thumbnail", new RouteBehavior(ResponseMode.Value, "", FilePath: file));
            using var response = await this.http.GetAsync("/_bridge/photos/library/p1/thumbnail", TestContext.Current.CancellationToken);

            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_value_that_is_not_the_route_s_contract_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => this.State.SetValue("wifi", "GET radio", """{ "enabled": "maybe" }"""));
        Assert.Contains("WifiRadioState", ex.Message);

        Assert.Throws<ArgumentException>(() => this.State.SetValue("wifi", "GET nope", "{}"));
        Assert.Throws<ArgumentException>(() => this.State.SetBehavior("wifi", "GET radio", new RouteBehavior(ResponseMode.Error, "", 200)));
    }

    [Fact]
    public async Task A_fired_event_reaches_the_page_s_stream_with_placeholders_filled_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = await TestEventStream.OpenAsync(this.http, "gps.reading", ct);

        Assert.Equal(1, this.State.FindEvent("gps.reading")!.Listeners);

        var reached = this.State.Fire("gps.reading", """
            { "latitude": 43.65, "longitude": -79.38, "positionAccuracy": 5, "timestamp": "$now", "heading": 90, "headingAccuracy": 5,
              "altitude": 76, "speed": 1.4, "speedAccuracy": 1, "floor": 0, "isStationary": false }
            """);

        var reading = JsonSerializer.Deserialize(await stream.NextAsync("gps.reading", ct), LocationsJsonContext.Default.GpsReading)!;

        Assert.Equal(1, reached);
        Assert.Equal(43.65, reading.Latitude);
        Assert.True(DateTimeOffset.UtcNow - reading.Timestamp < TimeSpan.FromMinutes(1));
        Assert.Equal(1, this.State.FindEvent("gps.reading")!.Fired);
    }

    [Fact]
    public void An_event_payload_that_is_not_its_contract_is_refused()
    {
        Assert.Throws<ArgumentException>(() => this.State.Fire("gps.reading", """{ "latitude": "north" }"""));
        Assert.Throws<ArgumentException>(() => this.State.Fire("no.such.event"));
    }

    [Fact]
    public async Task Every_call_is_in_the_traffic_recorder_with_its_bodies()
    {
        await this.Wifi.ConnectAsync(new WifiConnectRequest { Ssid = "Office", Passphrase = "hunter2" }, TestContext.Current.CancellationToken);

        // The exchange is added after the response has gone out, so the client can be back before it is.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await this.host.Traffic.WaitForAsync(x => x.Path == "/_bridge/wifi/connection", timeout.Token);

        var exchange = Assert.Single(this.host.Traffic.Snapshot(), x => x.Path == "/_bridge/wifi/connection");
        Assert.Equal("POST", exchange.Method);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Contains("Office", exchange.RequestBody.Text);
        Assert.Contains("interfaceName", exchange.ResponseBody.Text);
    }

    [Fact]
    public async Task The_root_explains_how_to_serve_a_page_when_none_is_given()
    {
        var html = await this.http.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Contains("--dev-server", html);
        Assert.Contains("/_bridge/wifi", html);
    }

    [Fact]
    public async Task A_caller_from_another_machine_is_refused_as_on_a_device()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_bridge/wifi");
        request.Headers.Host = "evil.example";

        using var response = await this.http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }
}

public class SimulatorPageServingTests
{
    [Fact]
    public async Task Serves_a_built_app_beside_the_bridges()
    {
        var app = Directory.CreateTempSubdirectory("sim-app").FullName;
        await File.WriteAllTextAsync(Path.Combine(app, "index.html"), "<h1>field app</h1>", TestContext.Current.CancellationToken);

        try
        {
            await using var host = SimulatorHost.Create(new SimulatorOptions { Port = 0, AppDirectory = app });
            using var http = new HttpClient { BaseAddress = await host.StartAsync(TestContext.Current.CancellationToken) };

            Assert.Contains("field app", await http.GetStringAsync("/", TestContext.Current.CancellationToken));
            Assert.Contains("field app", await http.GetStringAsync("/some/client/route", TestContext.Current.CancellationToken));

            var radio = await new WifiBridgeClient(new BuiltInClientTests.HttpTransport(http)).GetRadioAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(radio);
        }
        finally
        {
            Directory.Delete(app, recursive: true);
        }
    }

    [Fact]
    public async Task Serves_a_dev_server_s_pages_on_the_bridges_origin()
    {
        await using var dev = SimulatorHost.Create(new SimulatorOptions { Port = 0 });
        var devOrigin = await dev.StartAsync(TestContext.Current.CancellationToken);

        await using var host = SimulatorHost.Create(new SimulatorOptions { Port = 0, DevServer = devOrigin, Platform = "linux" });
        using var http = new HttpClient { BaseAddress = await host.StartAsync(TestContext.Current.CancellationToken) };

        // The dev server's root — here another simulator's landing page — comes through the proxy…
        Assert.Contains("Shiny.AppDeviceBridge simulator", await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        // …while the bridges are this simulator's own.
        var info = await new HostBridgeClient(new BuiltInClientTests.HttpTransport(http)).GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal("linux", info.Platform);
    }
}

public class SimulatorTrailTests
{
    static (SimulatorState State, FakeTimeProvider Time) NewState()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-18T12:00:00Z"));
        return (new SimulatorState(new WebAppEventHub(), new AppDeviceBridgeOptions { AppId = "test" }, time), time);
    }

    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(condition());
    }

    static Trail WifiTrail() => new()
    {
        Name = "drop",
        Steps =
        [
            new TrailStep { Bridge = "wifi", Route = "GET current", Mode = "null" },
            new TrailStep { DelayMs = 1000, Bridge = "wifi", Route = "GET radio", Value = JsonNode.Parse("""{ "enabled": true }""") },
            new TrailStep { DelayMs = 1000, Bridge = "wifi", Supported = false }
        ]
    };

    [Fact]
    public async Task Plays_each_step_after_its_delay()
    {
        var (state, time) = NewState();
        using var player = new TrailPlayer(state, WifiTrail());
        var done = player.Play();

        await Eventually(() => state.FindRoute("wifi", "GET current")!.Behavior.Mode == ResponseMode.Null);
        Assert.DoesNotContain("true", state.FindRoute("wifi", "GET radio")!.Behavior.Json);

        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => state.FindRoute("wifi", "GET radio")!.Behavior.Json.Contains("true"));
        Assert.True(state.Find("wifi")!.IsSupported);

        time.Advance(TimeSpan.FromSeconds(1));
        await done;

        Assert.False(state.Find("wifi")!.IsSupported);
        Assert.Equal(TrailPlayback.Finished, player.Playback);
    }

    [Fact]
    public async Task Speed_divides_the_delays()
    {
        var (state, time) = NewState();
        using var player = new TrailPlayer(state, WifiTrail()) { Speed = 4 };
        var done = player.Play();

        await Eventually(() => player.Position == 1);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await Eventually(() => state.FindRoute("wifi", "GET radio")!.Behavior.Json.Contains("true"));

        time.Advance(TimeSpan.FromMilliseconds(250));
        await done;
        Assert.False(state.Find("wifi")!.IsSupported);
    }

    [Fact]
    public async Task Pause_holds_playback_until_resumed_and_stop_ends_it()
    {
        var (state, time) = NewState();
        using var player = new TrailPlayer(state, WifiTrail());
        var done = player.Play();

        await Eventually(() => player.Position == 1);
        player.Pause();
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("true", state.FindRoute("wifi", "GET radio")!.Behavior.Json);
        Assert.Equal(TrailPlayback.Paused, player.Playback);

        player.Resume();
        await Eventually(() => state.FindRoute("wifi", "GET radio")!.Behavior.Json.Contains("true"));

        player.Stop();
        await done;
        Assert.Equal(TrailPlayback.Stopped, player.Playback);
        Assert.True(state.Find("wifi")!.IsSupported);
    }

    [Fact]
    public async Task A_looping_trail_starts_over_until_stopped()
    {
        var (state, time) = NewState();
        var trail = new Trail { Name = "tick", Loop = true, Steps = [new TrailStep { DelayMs = 100, Event = "wifi.changed", Payload = JsonNode.Parse("""{ "current": null }""") }] };
        using var player = new TrailPlayer(state, trail);
        var done = player.Play();

        // Advanced until each pass fires rather than once when the player looks ready: the player sets Position before it
        // registers its delay with the clock, and on a later pass Position is already 0, so a single Advance can land before
        // the delay exists and be lost. One delay is outstanding at a time, so each Advance fires at most one step.
        for (var i = 1; i <= 3; i++)
        {
            var pass = i;
            await Eventually(() =>
            {
                if (state.FindEvent("wifi.changed")!.Fired >= pass)
                    return true;

                time.Advance(TimeSpan.FromMilliseconds(100));
                return false;
            });
        }

        player.Stop();
        await done;
        Assert.True(player.Passes >= 2);
    }

    [Fact]
    public async Task A_bad_step_is_logged_and_the_rest_still_plays()
    {
        var (state, _) = NewState();
        var trail = new Trail
        {
            Name = "bad",
            Steps =
            [
                new TrailStep { Bridge = "wifi", Route = "GET nowhere", Mode = "null" },
                new TrailStep { Bridge = "wifi", Supported = false }
            ]
        };

        await new TrailPlayer(state, trail).Play();

        Assert.False(state.Find("wifi")!.IsSupported);
        Assert.Contains(state.Activity, x => x.Text.Contains("GET nowhere"));
    }

    [Fact]
    public async Task Records_what_is_done_as_a_trail_that_plays_it_back()
    {
        var (state, time) = NewState();
        var recording = new TrailRecording(state, "manual");

        state.SetValue("wifi", "GET radio", """{ "enabled": true }""");
        time.Advance(TimeSpan.FromSeconds(2));
        state.Fire("wifi.changed", """{ "current": null }""");
        time.Advance(TimeSpan.FromSeconds(3));
        state.SetSupported("ble", false);

        var trail = recording.Stop();
        state.SetSupported("ble", true);

        Assert.Equal([0, 2000, 3000], trail.Steps.Select(x => x.DelayMs));
        Assert.Equal("wifi.changed", trail.Steps[1].Event);
        Assert.Equal("GET radio", trail.Steps[0].Route);

        var (replay, replayTime) = NewState();
        var done = new TrailPlayer(replay, trail) { Speed = 1000 }.Play();
        await Eventually(() =>
        {
            replayTime.Advance(TimeSpan.FromMilliseconds(5));
            return done.IsCompleted;
        });

        Assert.Contains("true", replay.FindRoute("wifi", "GET radio")!.Behavior.Json);
        Assert.Equal(1, replay.FindEvent("wifi.changed")!.Fired);
        Assert.False(replay.Find("ble")!.IsSupported);
    }

    [Fact]
    public void Imports_a_gpx_track_as_a_walk_at_its_recorded_pace()
    {
        const string gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><trkseg>
                <trkpt lat="43.6426" lon="-79.3871"><ele>80</ele><time>2026-09-18T12:00:00Z</time></trkpt>
                <trkpt lat="43.6435" lon="-79.3871"><ele>81</ele><time>2026-09-18T12:00:10Z</time></trkpt>
                <trkpt lat="43.6435" lon="-79.3858"><ele>81</ele><time>2026-09-18T12:00:25Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;

        var trail = GpxImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(gpx)), "tower");
        var readings = trail.Steps.Where(x => x.Event == "gps.reading").ToList();

        Assert.Equal("tower", trail.Name);
        Assert.Equal(3, readings.Count);
        Assert.Equal([0, 10_000, 15_000], readings.Select(x => x.DelayMs));
        Assert.Equal(9, trail.Steps.Count);
        Assert.Contains(trail.Steps, x => x is { Bridge: "gps", Route: "GET current" });

        var second = JsonSerializer.Deserialize(readings[1].Payload!.ToJsonString().Replace("\"$now\"", "\"2026-09-18T12:00:10Z\""), LocationsJsonContext.Default.GpsReading)!;
        Assert.True(second.Heading is < 0.5 or > 359.5);        // due north
        Assert.InRange(second.Speed, 9.5, 10.5);                // ~100 m in 10 s
        Assert.Equal(81, second.Altitude);
        Assert.Equal("$now", readings[1].Payload!["timestamp"]!.GetValue<string>());
    }

    [Fact]
    public void Uses_a_fixed_interval_when_the_gpx_has_no_times()
    {
        var points = new[]
        {
            new GpxImporter.GpxPoint(1, 1, null, null),
            new GpxImporter.GpxPoint(1.001, 1, null, null)
        };

        var trail = GpxImporter.FromPoints(points, "route", TimeSpan.FromMilliseconds(750));
        Assert.Equal([0, 750], trail.Steps.Where(x => x.Event is not null).Select(x => x.DelayMs));
    }

    [Fact]
    public async Task A_gpx_walk_plays_into_the_gps_bridge()
    {
        var (state, time) = NewState();
        var trail = GpxImporter.FromPoints([new(10, 20, 5, null), new(10.001, 20, 5, null)], "walk", TimeSpan.FromSeconds(1));
        var done = new TrailPlayer(state, trail).Play();

        await Eventually(() => state.FindEvent("gps.reading")!.Fired == 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await done;

        Assert.Equal(2, state.FindEvent("gps.reading")!.Fired);
        Assert.Contains("10.001", state.FindRoute("gps", "GET current")!.Behavior.Json);
    }
}

public class SimulatorScenarioTests
{
    [Fact]
    public void A_captured_scenario_applies_the_same_setup_to_a_fresh_simulator()
    {
        var state = new SimulatorState(new WebAppEventHub(), new AppDeviceBridgeOptions { AppId = "a" });
        state.Platform = "windows";
        state.StickyWrites = false;
        state.SetSupported("ble", false);
        state.SetValue("wifi", "GET radio", """{ "enabled": true }""");
        state.SetBehavior("wifi", "POST connection", new RouteBehavior(ResponseMode.Error, "", 409, "conflict", "busy", DelayMs: 1500));
        state.SetBehavior("wifi", "GET current", new RouteBehavior(ResponseMode.Null, ""));
        state.SetEventPayload("wifi.changed", """{ "current": null }""");

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.scenario.json");
        try
        {
            Scenario.Capture(state, [new Trail { Name = "t", Steps = [new TrailStep { Bridge = "ble", Supported = true }] }]).Save(path);

            var fresh = new SimulatorState(new WebAppEventHub(), new AppDeviceBridgeOptions { AppId = "b" });
            var loaded = Scenario.Load(path);
            Assert.Empty(loaded.Apply(fresh));

            Assert.Equal("windows", fresh.Platform);
            Assert.False(fresh.StickyWrites);
            Assert.False(fresh.Find("ble")!.IsSupported);
            Assert.Contains("true", fresh.FindRoute("wifi", "GET radio")!.Behavior.Json);
            Assert.Equal(new RouteBehavior(ResponseMode.Error, fresh.FindRoute("wifi", "POST connection")!.DefaultJson, 409, "conflict", "busy", 1500), fresh.FindRoute("wifi", "POST connection")!.Behavior);
            Assert.Equal(ResponseMode.Null, fresh.FindRoute("wifi", "GET current")!.Behavior.Mode);
            Assert.Contains("null", fresh.FindEvent("wifi.changed")!.Json);
            Assert.Equal("t", Assert.Single(loaded.Trails).Name);

            // Only what differs is written.
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("\"gps\"", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_scenario_reports_what_it_cannot_apply_and_applies_the_rest()
    {
        var scenario = new Scenario
        {
            Bridges =
            {
                ["nope"] = new ScenarioBridge { Supported = false },
                ["wifi"] = new ScenarioBridge
                {
                    Supported = false,
                    Routes = { ["GET radio"] = new ScenarioRoute { Value = JsonNode.Parse("""{ "enabled": 3 }""") } }
                }
            }
        };

        var state = new SimulatorState(new WebAppEventHub(), new AppDeviceBridgeOptions { AppId = "a" });
        var problems = scenario.Apply(state);

        Assert.Equal(2, problems.Count);
        Assert.False(state.Find("wifi")!.IsSupported);
    }

    [Fact]
    public async Task The_host_applies_a_scenario_and_plays_a_trail_on_start()
    {
        var dir = Directory.CreateTempSubdirectory("sim-scenario").FullName;
        var scenarioPath = Path.Combine(dir, "setup.json");
        var trailPath = Path.Combine(dir, "offline.trail.json");

        new Scenario { Platform = "macos", Bridges = { ["ble"] = new ScenarioBridge { Supported = false } } }.Save(scenarioPath);
        TrailLibrary.WriteFile(new Trail { Name = "", Steps = [new TrailStep { Bridge = "wifi", Route = "GET current", Mode = "null" }] }, trailPath);

        try
        {
            var options = SimulatorOptions.Parse(["--port", "0", "--scenario", scenarioPath, "--trail", trailPath, "--play", "offline"]);
            await using var host = SimulatorHost.Create(options);
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.Trails.Find("offline")!.Completion;

            Assert.Equal("macos", host.State.Platform);
            Assert.False(host.State.Find("ble")!.IsSupported);
            Assert.Equal(ResponseMode.Null, host.State.FindRoute("wifi", "GET current")!.Behavior.Mode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class SimulatorOptionsTests
{
    [Fact]
    public void Parses_every_option()
    {
        var options = SimulatorOptions.Parse(["--dev-server", "http://localhost:5080", "--port", "6000", "--platform", "Android", "--app-id", "field", "--play", "walk", "--speed", "2.5", "--headless"]);

        Assert.Equal(new Uri("http://localhost:5080/"), options.DevServer);
        Assert.Equal(6000, options.Port);
        Assert.Equal("android", options.Platform);
        Assert.Equal("field", options.AppId);
        Assert.Equal(["walk"], options.Play);
        Assert.Equal(2.5, options.Speed);
        Assert.True(options.Headless);
    }

    [Theory]
    [InlineData("--port", "nope")]
    [InlineData("--platform", "amiga")]
    [InlineData("--dev-server", "ftp://x")]
    [InlineData("--speed", "0")]
    [InlineData("--trail", "/no/such/file.gpx")]
    [InlineData("--bogus", "")]
    public void Refuses_a_bad_option(string option, string value)
        => Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse(value.Length == 0 ? [option] : [option, value]));

    [Fact]
    public void Refuses_both_a_built_app_and_a_dev_server()
        => Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse(["--app", Path.GetTempPath(), "--dev-server", "http://localhost:5000"]));
}

public class PayloadTokenTests
{
    static readonly FakeTimeProvider Time = new(DateTimeOffset.Parse("2026-09-18T12:00:00Z"));

    [Fact]
    public void Fills_in_now_and_offsets_from_it()
    {
        var json = JsonNode.Parse(PayloadTokens.Expand("""{ "a": "$now", "b": ["$now-5m", { "c": "$now+2h" }], "d": "$now+1d" }""", Time))!;

        Assert.Equal(DateTimeOffset.Parse("2026-09-18T12:00:00Z"), DateTimeOffset.Parse(json["a"]!.GetValue<string>()));
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T11:55:00Z"), DateTimeOffset.Parse(json["b"]![0]!.GetValue<string>()));
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T14:00:00Z"), DateTimeOffset.Parse(json["b"]![1]!["c"]!.GetValue<string>()));
        Assert.Equal(DateTimeOffset.Parse("2026-09-19T12:00:00Z"), DateTimeOffset.Parse(json["d"]!.GetValue<string>()));
    }

    [Fact]
    public void Fills_in_a_new_uuid_each_time()
    {
        var a = JsonNode.Parse(PayloadTokens.Expand("""{ "id": "$uuid" }"""))!["id"]!.GetValue<string>();
        var b = JsonNode.Parse(PayloadTokens.Expand("""{ "id": "$uuid" }"""))!["id"]!.GetValue<string>();

        Assert.True(Guid.TryParse(a, out _));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("""{ "a": "at $now" }""")]
    [InlineData("""{ "a": "$later" }""")]
    [InlineData("""{ "a": "$now+5x" }""")]
    [InlineData("not json $now")]
    public void Leaves_anything_else_as_written(string json)
        => Assert.Equal(json.Contains('{') ? JsonNode.Parse(json)!.ToJsonString() : json, PayloadTokens.Expand(json, Time));
}
