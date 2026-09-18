using System.Net;
using Shiny.AppDeviceBridge.AppSupport;
using Shiny.AppDeviceBridge.AppSupport.Client;
using Shiny.AppDeviceBridge.Client;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;
using EssentialsSpeed = Microsoft.Maui.Devices.Sensors.SensorSpeed;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The sensors bridge over fake sensors, through the typed client and the real event stream.</summary>
public class SensorsBridgeTests
{
    [Fact]
    public async Task Reports_each_sensor_and_refuses_one_the_device_lacks()
    {
        await using var fixture = await SensorsFixture.StartAsync();

        var status = await fixture.Client.GetStatusAsync();
        Assert.Equal(Enum.GetValues<Sensor>().Length, status.Sensors.Count);
        Assert.True(status.Sensors.Single(x => x.Sensor == Sensor.Accelerometer).Supported);
        Assert.False(status.Sensors.Single(x => x.Sensor == Sensor.Barometer).Supported);
        Assert.DoesNotContain(status.Sensors, x => x.Running);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(Sensor.Barometer, new SensorStartRequest()));
        Assert.True(refused.IsNotSupported);

        var tooFast = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest(MinIntervalMs: 1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooFast.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.WebView.PostAsync("/_bridge/sensors/thermometer", null)).StatusCode);
    }

    [Fact]
    public async Task Starts_restarts_for_a_new_speed_and_stops()
    {
        await using var fixture = await SensorsFixture.StartAsync();
        var compass = fixture.Source(Sensor.Compass);

        var started = await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest(Contracts.SensorSpeed.Game, 50));
        Assert.Equal((true, Contracts.SensorSpeed.Game, 50), (started.Running, started.Speed!.Value, started.MinIntervalMs!.Value));
        Assert.Equal([EssentialsSpeed.Game], compass.Starts);

        // The same speed again only changes the interval; a new speed restarts the platform listener.
        await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest(Contracts.SensorSpeed.Game, 100));
        await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest(Contracts.SensorSpeed.Fastest, 100));
        Assert.Equal([EssentialsSpeed.Game, EssentialsSpeed.Fastest], compass.Starts);

        await fixture.Client.StopAsync(Sensor.Compass);
        Assert.False(compass.IsMonitoring);
        Assert.False((await fixture.Client.GetStatusAsync()).Sensors.Single(x => x.Sensor == Sensor.Compass).Running);

        // Stopping what is not running is not an error.
        await fixture.Client.StopAsync(Sensor.Gyroscope);
    }

    [Fact]
    public async Task Sends_readings_as_events_thinned_to_the_interval_but_never_a_shake()
    {
        await using var fixture = await SensorsFixture.StartAsync();
        await fixture.Client.StartAsync(Sensor.Accelerometer, new SensorStartRequest(MinIntervalMs: 60_000));

        await using var events = await fixture.OpenEventsAsync("sensors.accelerometer,sensors.shake");
        var accelerometer = fixture.Source(Sensor.Accelerometer);

        // The first reading goes out; the next ones fall inside the interval and are dropped. A shake always goes.
        accelerometer.Vector("sensors.accelerometer", 1, 2, 3);
        accelerometer.Vector("sensors.accelerometer", 4, 5, 6);
        accelerometer.Shake();

        Assert.Equal(("sensors.accelerometer", """{"x":1,"y":2,"z":3"""), Trim(await events.NextAsync(fixture.Timeout)));
        Assert.Equal("sensors.shake", (await events.NextAsync(fixture.Timeout)).Name);
    }

    [Fact]
    public async Task Readings_from_a_stopped_sensor_go_nowhere()
    {
        await using var fixture = await SensorsFixture.StartAsync();
        await using var events = await fixture.OpenEventsAsync("sensors.gyroscope,sensors.magnetometer");

        fixture.Source(Sensor.Gyroscope).Vector("sensors.gyroscope", 9, 9, 9);
        await fixture.Client.StartAsync(Sensor.Magnetometer, new SensorStartRequest(MinIntervalMs: 5));
        fixture.Source(Sensor.Magnetometer).Vector("sensors.magnetometer", 1, 1, 1);

        Assert.Equal("sensors.magnetometer", (await events.NextAsync(fixture.Timeout)).Name);
    }

    /// <summary>A page that closes never says so; its event stream closing is the signal.</summary>
    [Fact]
    public async Task Stops_every_sensor_when_the_page_leaves()
    {
        await using var fixture = await SensorsFixture.StartAsync();

        var events = await fixture.OpenEventsAsync("sensors.compass,sensors.orientation");
        await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest());
        await fixture.Client.StartAsync(Sensor.Orientation, new SensorStartRequest());

        await events.DisposeAsync();

        // The server learns the stream is gone when it next writes to it, which a running sensor makes happen.
        while (fixture.Source(Sensor.Compass).IsMonitoring || fixture.Source(Sensor.Orientation).IsMonitoring)
        {
            fixture.Source(Sensor.Compass).Vector("sensors.compass", 0, 0, 0);
            await Task.Delay(20, fixture.Timeout);
        }

        Assert.DoesNotContain((await fixture.Client.GetStatusAsync()).Sensors, x => x.Running);
    }

    [Fact]
    public async Task Stops_a_sensor_once_nothing_listens_to_it_and_keeps_the_rest()
    {
        await using var fixture = await SensorsFixture.StartAsync();

        await using var events = await fixture.OpenEventsAsync("sensors.compass,sensors.accelerometer,sensors.shake");
        await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest());
        await fixture.Client.StartAsync(Sensor.Accelerometer, new SensorStartRequest());

        // The page stops listening to the compass and to raw acceleration, but still wants shakes.
        await events.SetTopicsAsync(fixture.Timeout, "sensors.shake");

        while (fixture.Source(Sensor.Compass).IsMonitoring)
            await Task.Delay(10, fixture.Timeout);

        Assert.True(fixture.Source(Sensor.Accelerometer).IsMonitoring);

        fixture.Source(Sensor.Accelerometer).Shake();
        Assert.Equal("sensors.shake", (await events.NextAsync(fixture.Timeout)).Name);

        await events.SetTopicsAsync(fixture.Timeout);
        while (fixture.Source(Sensor.Accelerometer).IsMonitoring)
            await Task.Delay(10, fixture.Timeout);
    }

    [Fact]
    public void Maps_every_speed_onto_essentials()
    {
        foreach (var speed in Enum.GetValues<Contracts.SensorSpeed>())
            BridgeEnum.Convert<Contracts.SensorSpeed, EssentialsSpeed>(speed);
    }

    static (string Name, string Data) Trim((string Name, string Data) e) => (e.Name, e.Data[..e.Data.IndexOf(",\"timestamp\"", StringComparison.Ordinal)]);

    sealed class FakeSensor(Sensor sensor, bool supported) : SensorSource(sensor)
    {
        bool monitoring;

        public List<EssentialsSpeed> Starts { get; } = [];

        public override bool IsSupported => supported;

        public override bool IsMonitoring => this.monitoring;

        public override void Start(EssentialsSpeed speed)
        {
            this.Starts.Add(speed);
            this.monitoring = true;
        }

        public override void Stop() => this.monitoring = false;

        public void Vector(string name, double x, double y, double z)
            => this.Raise(new SensorSample<VectorReading>(this.Sensor, name, new(x, y, z, DateTimeOffset.UtcNow)));

        public void Shake()
            => this.Raise(new SensorSample<ShakeDetected>(this.Sensor, "sensors.shake", new(DateTimeOffset.UtcNow), throttled: false));
    }

    sealed class SensorsFixture(BuiltInClientTests.HostFixture host, HttpClient webView, IReadOnlyList<FakeSensor> sensors) : IAsyncDisposable
    {
        readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        public SensorsBridgeClient Client { get; } = new(host.Transport);

        public HttpClient WebView => webView;

        public CancellationToken Timeout => this.timeout.Token;

        public FakeSensor Source(Sensor sensor) => sensors.Single(x => x.Sensor == sensor);

        public static async Task<SensorsFixture> StartAsync()
        {
            var hub = new WebAppEventHub();
            FakeSensor[] sensors = [.. Enum.GetValues<Sensor>().Select(x => new FakeSensor(x, x != Sensor.Barometer))];
            HttpClient? webView = null;

            var host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new SensorsBridge(sensors)], hub, client => webView = client);
            return new SensorsFixture(host, webView!, sensors);
        }

        /// <summary>The page's event stream, its sources listening before this returns.</summary>
        public Task<TestEventStream> OpenEventsAsync(string topics) => TestEventStream.OpenAsync(webView, topics, this.Timeout);

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            this.timeout.Dispose();
        }
    }
}
