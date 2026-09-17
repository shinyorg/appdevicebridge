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

        using var events = await fixture.OpenEventsAsync();
        var accelerometer = fixture.Source(Sensor.Accelerometer);

        // The first reading goes out; the next ones fall inside the interval and are dropped. A shake always goes.
        accelerometer.Vector("sensors.accelerometer", 1, 2, 3);
        accelerometer.Vector("sensors.accelerometer", 4, 5, 6);
        accelerometer.Shake();

        Assert.Equal(("sensors.accelerometer", """{"x":1,"y":2,"z":3"""), Trim(await events.NextAsync()));
        Assert.Equal("sensors.shake", (await events.NextAsync()).Name);
    }

    [Fact]
    public async Task Readings_from_a_stopped_sensor_go_nowhere()
    {
        await using var fixture = await SensorsFixture.StartAsync();
        using var events = await fixture.OpenEventsAsync();

        fixture.Source(Sensor.Gyroscope).Vector("sensors.gyroscope", 9, 9, 9);
        await fixture.Client.StartAsync(Sensor.Magnetometer, new SensorStartRequest(MinIntervalMs: 5));
        fixture.Source(Sensor.Magnetometer).Vector("sensors.magnetometer", 1, 1, 1);

        Assert.Equal("sensors.magnetometer", (await events.NextAsync()).Name);
    }

    /// <summary>A page that closes never says so; its last event stream closing is the signal.</summary>
    [Fact]
    public async Task Stops_every_sensor_when_the_last_listener_leaves()
    {
        await using var fixture = await SensorsFixture.StartAsync();

        var events = await fixture.OpenEventsAsync();
        await fixture.Client.StartAsync(Sensor.Compass, new SensorStartRequest());
        await fixture.Client.StartAsync(Sensor.Orientation, new SensorStartRequest());

        events.Dispose();

        // The server learns the stream is gone when it next writes to it, which a running sensor makes happen.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Source(Sensor.Compass).IsMonitoring || fixture.Source(Sensor.Orientation).IsMonitoring)
        {
            fixture.Source(Sensor.Compass).Vector("sensors.compass", 0, 0, 0);
            await Task.Delay(20, timeout.Token);
        }

        Assert.DoesNotContain((await fixture.Client.GetStatusAsync()).Sensors, x => x.Running);
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
            => this.Raise(new SensorSample<VectorReading>(this.Sensor, name, new(x, y, z, DateTimeOffset.UtcNow), SensorsJsonContext.Default.VectorReading));

        public void Shake()
            => this.Raise(new SensorSample<ShakeDetected>(this.Sensor, "sensors.shake", new(DateTimeOffset.UtcNow), SensorsJsonContext.Default.ShakeDetected, throttled: false));
    }

    sealed class SensorsFixture(BuiltInClientTests.HostFixture host, HttpClient webView, IReadOnlyList<FakeSensor> sensors, WebAppEventHub hub) : IAsyncDisposable
    {
        public SensorsBridgeClient Client { get; } = new(host.Transport);

        public HttpClient WebView => webView;

        public FakeSensor Source(Sensor sensor) => sensors.Single(x => x.Sensor == sensor);

        public static async Task<SensorsFixture> StartAsync()
        {
            var hub = new WebAppEventHub();
            FakeSensor[] sensors = [.. Enum.GetValues<Sensor>().Select(x => new FakeSensor(x, x != Sensor.Barometer))];
            HttpClient? webView = null;

            var host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new SensorsBridge(hub, sensors)], hub, client => webView = client);
            return new SensorsFixture(host, webView!, sensors, hub);
        }

        /// <summary>The page's event stream, registered with the hub before this returns.</summary>
        public async Task<EventStream> OpenEventsAsync()
        {
            var before = hub.SubscriberCount;
            var response = await webView.GetAsync("/_bridge/events", HttpCompletionOption.ResponseHeadersRead);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (hub.SubscriberCount == before)
                await Task.Delay(10, timeout.Token);

            return new EventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    sealed class EventStream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public async Task<(string Name, string Data)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? name = null;

            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                    name = line["event:".Length..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal) && name is not null)
                    return (name, line["data:".Length..].Trim());
            }

            throw new EndOfStreamException();
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }
}
