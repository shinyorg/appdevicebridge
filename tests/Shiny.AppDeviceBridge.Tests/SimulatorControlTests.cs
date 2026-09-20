using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Simulator.Control;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The control surface an agent drives the simulator through. Everything here runs against a real simulator host, because
/// the point of the surface is that it changes what the bridges actually answer.
/// </summary>
public class SimulatorControlTests : IAsyncLifetime
{
    SimulatorHost host = null!;
    SimulatorControl control = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Platform = "android" });
        var origin = await this.host.StartAsync(TestContext.Current.CancellationToken);
        this.http = new HttpClient { BaseAddress = origin };
        this.control = this.host.Control;
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    [Fact]
    public void Reports_what_the_simulator_is()
    {
        var status = this.control.GetStatus();

        Assert.Equal("android", status.Platform);
        Assert.Equal("simulator", status.AppId);
        Assert.True(status.StickyWrites);
        Assert.NotNull(status.Origin);
        Assert.NotEmpty(this.control.ListBridges());
    }

    [Fact]
    public void Describes_a_route_with_a_sample_of_its_contract()
    {
        var route = this.control.GetRoute("wifi", "GET current");

        Assert.Equal("GET", route.Method);
        Assert.Equal("wifi/current", route.Path);
        Assert.Equal("value", route.Mode);

        // The sample is what an agent reads before writing a value; without it there is nothing to shape one from.
        Assert.NotNull(route.Sample);
        Assert.Contains("ssid", route.Sample!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_value_set_here_is_what_the_page_gets()
    {
        this.control.SetRoute("wifi", "GET current", value: JsonNode.Parse("""{"interfaceName":"en0","ssid":"Cafe"}"""));

        var answered = await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Contains("Cafe", answered);
    }

    [Fact]
    public async Task An_error_set_here_is_the_error_the_page_sees()
    {
        this.control.SetRoute("wifi", "GET current", mode: "error", status: 403, code: "access_denied", message: "The user said no.");

        var response = await this.http.GetAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("access_denied", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Null_mode_answers_204()
    {
        this.control.SetRoute("wifi", "GET current", mode: "null");

        var response = await this.http.GetAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public void What_is_not_given_is_kept()
    {
        this.control.SetRoute("wifi", "GET current", value: JsonNode.Parse("""{"interfaceName":"en0","ssid":"Home"}"""));
        var withDelay = this.control.SetRoute("wifi", "GET current", delayMs: 250);

        Assert.Equal(250, withDelay.DelayMs);
        Assert.Equal("value", withDelay.Mode);
        Assert.Contains("Home", withDelay.Value!.ToJsonString());
    }

    [Fact]
    public void A_value_that_is_not_the_contract_is_refused()
    {
        var bad = Assert.Throws<ArgumentException>(
            () => this.control.SetRoute("wifi", "GET current", value: JsonNode.Parse("""{"ssid":42}"""))
        );

        Assert.Contains("WifiNetworkInfo", bad.Message);

        // And the route still answers what it did before.
        Assert.Equal("value", this.control.GetRoute("wifi", "GET current").Mode);
    }

    [Fact]
    public void Names_what_exists_when_something_does_not()
    {
        Assert.Contains("wifi", Assert.Throws<ArgumentException>(() => this.control.DescribeBridge("wifii")).Message);
        Assert.Contains("GET current", Assert.Throws<ArgumentException>(() => this.control.GetRoute("wifi", "GET nonsense")).Message);
        Assert.Contains("wifi.changed", Assert.Throws<ArgumentException>(() => this.control.FireEvent("wifi.nonsense")).Message);
    }

    [Fact]
    public async Task A_bridge_switched_off_answers_501_everywhere()
    {
        var summary = this.control.SetBridgeSupported("wifi", false);
        Assert.False(summary.IsSupported);

        var response = await this.http.GetAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, response.StatusCode);

        this.control.SetBridgeSupported("wifi", true);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await this.http.GetAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task The_platform_is_what_the_host_reports()
    {
        this.control.SetPlatform("ios");

        var info = await new HostBridgeClient(new BuiltInClientTests.HttpTransport(this.http)).GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ios", info.Platform);
    }

    [Fact]
    public void Only_a_platform_the_library_has_a_head_for()
    {
        var bad = Assert.Throws<ArgumentException>(() => this.control.SetPlatform("symbian"));
        Assert.Contains("maccatalyst", bad.Message);
    }

    [Fact]
    public void Firing_an_event_with_no_listener_says_so()
    {
        // Nothing is subscribed, which is nearly always why an event "did not arrive" — so the count has to come back.
        var fired = this.control.FireEvent("wifi.changed");
        Assert.Equal(0, fired.Listeners);
        Assert.Equal("wifi.changed", fired.Event);
    }

    [Fact]
    public void A_payload_that_is_not_the_contract_is_refused()
        => Assert.Throws<ArgumentException>(() => this.control.FireEvent("wifi.changed", JsonNode.Parse("""{"current":12}""")));

    [Fact]
    public async Task Traffic_shows_what_the_page_asked_for()
    {
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        await this.host.Traffic.WaitForAsync(x => x.Path.Contains("wifi") && x.StatusCode != 0, TestContext.Current.CancellationToken);

        var traffic = this.control.GetTraffic("wifi", bodies: true);
        var entry = Assert.Single(traffic);

        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.Status);
        Assert.NotNull(entry.ResponseBody);

        this.control.ClearTraffic();
        Assert.Empty(this.control.GetTraffic());
    }

    [Fact]
    public async Task Waits_for_the_page_to_ask_and_ignores_what_came_before()
    {
        // An earlier matching request must not satisfy the wait — an agent asking to wait means the *next* one.
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        await this.host.Traffic.WaitForAsync(x => x.Path.Contains("wifi") && x.StatusCode != 0, TestContext.Current.CancellationToken);

        var waiting = this.control.WaitForRequestAsync("GET /_bridge/wifi/current", 10_000, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);

        var entry = await waiting;
        Assert.Equal(200, entry.Status);
    }

    [Fact]
    public async Task Says_what_did_arrive_when_the_wait_times_out()
    {
        var waiting = this.control.WaitForRequestAsync("GET /_bridge/never", 150, cancellationToken: TestContext.Current.CancellationToken);
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.Contains("wifi", timeout.Message);
    }

    [Fact]
    public async Task Waits_until_the_page_goes_quiet()
    {
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);

        var status = await this.control.WaitForQuietAsync(100, 10_000, TestContext.Current.CancellationToken);
        Assert.NotNull(status.Origin);
    }

    [Fact]
    public async Task A_trail_composed_here_plays_against_the_page()
    {
        this.control.LoadTrail(JsonNode.Parse("""
            {
              "name": "lose-wifi",
              "steps": [
                { "delayMs": 0, "bridge": "wifi", "route": "GET current", "mode": "null" }
              ]
            }
            """)!);

        Assert.Equal("lose-wifi", Assert.Single(this.control.ListTrails()).Name);

        this.control.PlayTrail("lose-wifi", speed: 100);
        await this.host.Trails.Find("lose-wifi")!.Completion;

        Assert.Equal("null", this.control.GetRoute("wifi", "GET current").Mode);

        this.control.RemoveTrail("lose-wifi");
        Assert.Empty(this.control.ListTrails());
    }

    [Fact]
    public void An_empty_trail_is_refused()
        => Assert.Throws<ArgumentException>(() => this.control.LoadTrail(JsonNode.Parse("""{ "name": "nothing", "steps": [] }""")!));

    [Fact]
    public void A_captured_scenario_replays_what_was_set_up()
    {
        this.control.SetRoute("wifi", "GET current", mode: "error", status: 409, code: "conflict");
        this.control.SetBridgeSupported("gps", false);
        this.control.SetPlatform("linux");

        var captured = this.control.CaptureScenario();

        this.control.ResetRoute("wifi", "GET current");
        this.control.SetBridgeSupported("gps", true);
        this.control.SetPlatform("ios");

        Assert.Empty(this.control.ApplyScenario(captured));

        Assert.Equal("error", this.control.GetRoute("wifi", "GET current").Mode);
        Assert.Equal(409, this.control.GetRoute("wifi", "GET current").Status);
        Assert.False(this.control.DescribeBridge("gps").IsSupported);
        Assert.Equal("linux", this.control.GetStatus().Platform);
    }

    [Fact]
    public void A_scenario_naming_something_that_does_not_exist_reports_it_rather_than_throwing()
    {
        var problems = this.control.ApplyScenario(JsonNode.Parse("""{ "bridges": { "teleporter": { "supported": false } } }""")!);
        Assert.Contains(problems, x => x.Contains("teleporter"));
    }

    [Fact]
    public async Task A_sequence_answers_a_different_value_each_call_and_then_repeats()
    {
        this.control.SetRouteSequence("wifi", "GET current",
        [
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"First"}"""),
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Second"}"""),
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Third"}""")
        ]);

        Assert.Contains("First", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
        Assert.Contains("Second", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
        Assert.Contains("Third", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));

        // The last one repeats: a page that keeps polling keeps seeing the end state rather than falling off the end.
        Assert.Contains("Third", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_sequence_is_checked_in_full_before_any_of_it_is_used()
    {
        var bad = Assert.Throws<ArgumentException>(() => this.control.SetRouteSequence("wifi", "GET current",
        [
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Fine"}"""),
            JsonNode.Parse("""{"ssid":42}""")
        ]));

        Assert.Contains("WifiNetworkInfo", bad.Message);

        // Nothing was applied, so the route still answers what it did.
        Assert.Null(this.control.GetRoute("wifi", "GET current").Sequence);
    }

    [Fact]
    public void An_empty_sequence_is_refused()
        => Assert.Throws<ArgumentException>(() => this.control.SetRouteSequence("wifi", "GET current", []));

    [Fact]
    public async Task A_plain_value_replaces_a_sequence()
    {
        this.control.SetRouteSequence("wifi", "GET current",
        [
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Sequenced"}"""),
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Also sequenced"}""")
        ]);

        this.control.SetRoute("wifi", "GET current", value: JsonNode.Parse("""{"interfaceName":"en0","ssid":"Plain"}"""));

        Assert.Null(this.control.GetRoute("wifi", "GET current").Sequence);
        Assert.Contains("Plain", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
        Assert.Contains("Plain", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_captured_scenario_keeps_a_sequence()
    {
        this.control.SetRouteSequence("wifi", "GET current",
        [
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"One"}"""),
            JsonNode.Parse("""{"interfaceName":"en0","ssid":"Two"}""")
        ]);

        var captured = this.control.CaptureScenario();
        this.control.ResetRoute("wifi", "GET current");
        Assert.Empty(this.control.ApplyScenario(captured));

        // Replayed from the start, not from wherever the captured one had got to.
        Assert.Contains("One", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
        Assert.Contains("Two", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_trail_step_can_set_a_sequence()
    {
        this.control.LoadTrail(JsonNode.Parse("""
            {
              "name": "polling",
              "steps": [
                {
                  "delayMs": 0,
                  "bridge": "wifi",
                  "route": "GET current",
                  "values": [
                    { "interfaceName": "en0", "ssid": "Connecting" },
                    { "interfaceName": "en0", "ssid": "Connected" }
                  ]
                }
              ]
            }
            """)!);

        this.control.PlayTrail("polling", speed: 100);
        await this.host.Trails.Find("polling")!.Completion;

        Assert.Contains("Connecting", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
        Assert.Contains("Connected", await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void What_an_agent_changes_is_written_where_the_person_can_see_it()
    {
        this.control.SetRoute("wifi", "GET current", mode: "null");

        // The terminal UI shows this log; an agent quietly rewriting the device behind someone's back is the thing to avoid.
        Assert.Contains(this.control.GetActivity(), x => x.Text.Contains("agent set wifi/current"));
    }
}
