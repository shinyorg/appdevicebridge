using System.Net;
using System.Text.Json;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Obd.Client;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The simulator's OBD playback: a Shiny.Obd emulator profile loaded as a vehicle the page reaches through the real OBD
/// bridge, and the simulated bridge back once it is unloaded.
/// </summary>
public class SimulatorObdTests : IAsyncLifetime
{
    static readonly string Profile = Path.Combine(SimulatorCatalogTests.RepositoryRoot(), "samples", "simulator", "f150.obd.json");

    SimulatorHost host = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Platform = "android", ObdProfile = Profile });
        this.http = new HttpClient { BaseAddress = await this.host.StartAsync(TestContext.Current.CancellationToken) };
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    ObdBridgeClient Obd => new(new BuiltInClientTests.HttpTransport(this.http));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<ObdStatus> ConnectAsync()
    {
        var found = await this.Obd.ScanAsync(new ObdScanRequest(ObdTransport.Ble), Ct);
        var adapter = Assert.Single(found);
        return await this.Obd.ConnectAsync(new ObdConnectRequest(ObdTransport.Ble, PeripheralUuid: adapter.Id), Ct);
    }

    [Fact]
    public async Task The_page_finds_the_loaded_vehicle_and_reads_it_through_the_real_bridge()
    {
        var status = await this.ConnectAsync();

        Assert.True(status.Connected);
        Assert.Equal(ObdEmulation.AdapterId, status.AdapterId);
        Assert.Contains("F-150", status.AdapterName);
        Assert.Equal("1FTEW1EG5HFA12345", (await this.Obd.GetVinAsync(Ct)).Vin);

        var codes = await this.Obd.GetTroubleCodesAsync(Ct);
        Assert.Equal(["P0299"], codes.Stored);
        Assert.Equal(["P0420"], codes.Permanent);

        var rpm = await this.Obd.ReadAsync(new ObdReadRequest("engineRpm"), Ct);
        Assert.Equal("rpm", rpm.Unit);
        Assert.NotNull(rpm.Value);
    }

    [Fact]
    public async Task The_real_bridge_s_rules_hold_against_the_emulator()
    {
        var notConnected = await Assert.ThrowsAsync<BridgeException>(() => this.Obd.GetVinAsync(Ct));
        await this.ConnectAsync();
        var writes = await Assert.ThrowsAsync<BridgeException>(() => this.Obd.SendRawAsync(new ObdRawRequest("04"), Ct));
        var raw = await this.Obd.SendRawAsync(new ObdRawRequest("0100"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, notConnected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, writes.StatusCode);
        // As an adapter does, the first request that needs the bus is answered after SEARCHING...
        Assert.Contains("41 00", raw.Response);
    }

    [Fact]
    public async Task A_drive_moves_what_the_monitor_reports()
    {
        await using var stream = await TestEventStream.OpenAsync(this.http, "obd.reading", Ct);
        await this.ConnectAsync();
        this.host.Obd.Drive("Commute");

        await this.Obd.StartMonitorAsync(new ObdMonitorRequest(["vehicleSpeed"], 250), Ct);

        // Idle for five seconds, then pulling away: wait for the truck to move.
        var speed = 0.0;
        for (var i = 0; i < 60 && speed <= 0; i++)
        {
            var reading = JsonSerializer.Deserialize(await stream.NextAsync("obd.reading", Ct), ObdJsonContext.Default.ObdMonitorReading)!;
            Assert.Null(reading.Error);
            speed = reading.Value ?? 0;
        }

        Assert.True(speed > 0, "The truck never moved.");
        Assert.True(this.host.Control.GetObd().Driving);
    }

    [Fact]
    public async Task Unloading_takes_the_vehicle_away_and_brings_the_simulated_bridge_back()
    {
        await using var stream = await TestEventStream.OpenAsync(this.http, "obd.disconnected,obd.reading", Ct);
        await this.ConnectAsync();

        this.host.Obd.Unload();

        // Answered from the route's value again, which is the generated sample until one is set.
        this.host.State.SetValue("obd", "GET vin", """{ "vin": "SIMULATED00000000" }""");
        Assert.Equal("SIMULATED00000000", (await this.Obd.GetVinAsync(Ct)).Vin);

        // An event fired by hand still reaches the page's stream, which carries both sides.
        this.host.State.Fire("obd.reading", """{ "command": "vehicleSpeed", "value": 42, "unit": "km/h", "timestamp": "$now" }""");
        var fired = JsonSerializer.Deserialize(await stream.NextAsync("obd.reading", Ct), ObdJsonContext.Default.ObdMonitorReading)!;
        Assert.Equal(42, fired.Value);

        // Loading it again, the real bridge finds its old connection gone and the page connects afresh.
        await this.host.Obd.LoadAsync(Profile, Ct);
        var lost = await Assert.ThrowsAsync<BridgeException>(() => this.Obd.GetVinAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.True((await this.ConnectAsync()).Connected);
    }

    [Fact]
    public void Lists_the_profile_s_scenarios_ahead_of_the_built_in_ones()
    {
        var obd = this.host.Control.GetObd();

        Assert.True(obd.Loaded);
        Assert.Equal("f150", obd.Profile);
        Assert.Equal("Commute", obd.Scenario);
        Assert.Contains(obd.Scenarios, x => x is { Name: "Commute", FromProfile: true });
        Assert.Contains(obd.Scenarios, x => !x.FromProfile);
        Assert.Equal(["P0299"], obd.StoredDtcs);
        Assert.Equal("f150", this.host.Control.GetStatus().ObdProfile);
    }

    [Fact]
    public void Refuses_a_scenario_it_does_not_have_and_a_file_that_is_not_a_profile()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.json");
        File.WriteAllText(bad, """{ "formatVersion": 99 }""");
        try
        {
            Assert.Throws<ArgumentException>(() => this.host.Control.DriveObd("Moon landing"));
            Assert.Throws<ArgumentException>(() => this.host.Control.LoadObdProfile(bad));
            Assert.Equal("f150", this.host.Obd.Profile);
        }
        finally
        {
            File.Delete(bad);
        }
    }
}

public class SimulatorObdUnloadedTests : IAsyncLifetime
{
    SimulatorHost host = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0 });
        this.http = new HttpClient { BaseAddress = await this.host.StartAsync(TestContext.Current.CancellationToken) };
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_profile_the_obd_bridge_answers_the_values_set_on_it()
    {
        var obd = new ObdBridgeClient(new BuiltInClientTests.HttpTransport(this.http));
        this.host.State.SetBehavior("obd", "POST connection", new RouteBehavior(ResponseMode.Error, "", 504, "obd_timeout", "No adapter answered."));

        var timeout = await Assert.ThrowsAsync<BridgeException>(() => obd.ConnectAsync(new ObdConnectRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.GatewayTimeout, timeout.StatusCode);
        Assert.False(this.host.Control.GetObd().Loaded);
        Assert.Throws<ArgumentException>(() => this.host.Control.DriveObd());
    }
}
