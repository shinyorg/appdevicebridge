using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Serialization.Metadata;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;
using EssentialsSpeed = Microsoft.Maui.Devices.Sensors.SensorSpeed;

namespace Shiny.AppDeviceBridge.AppSupport;

/// <summary>
/// <c>/_bridge/sensors</c> over the .NET MAUI Essentials sensors.
/// <code>
/// GET    /_bridge/sensors
/// POST   /_bridge/sensors/{sensor}    { "speed": "UI", "minIntervalMs": 16 }
/// DELETE /_bridge/sensors/{sensor}
/// DELETE /_bridge/sensors
///
/// events: sensors.accelerometer, sensors.gyroscope, sensors.magnetometer, sensors.compass, sensors.barometer,
///         sensors.orientation, sensors.shake
/// </code>
/// <para>
/// Sensors cost battery for as long as they run, and a page that closes never says so. So readings go out only as events,
/// each sensor on its own topic, and a sensor stops when the last listener of its topic leaves. The accelerometer keeps
/// running while <c>sensors.shake</c> is heard too: shakes are detected from its readings.
/// </para>
/// </summary>
public sealed class SensorsBridge : IWebAppBridge, IDisposable
{
    const int MinimumIntervalMs = 5;

    readonly IReadOnlyDictionary<Contracts.Sensor, SensorSource> sources;
    readonly IReadOnlyDictionary<string, SensorTopic> topics;
    readonly Lock gate = new();
    readonly Dictionary<Contracts.Sensor, Running> running = [];

    public SensorsBridge()
        : this(EssentialsSensors.Create())
    {
    }

    internal SensorsBridge(IEnumerable<SensorSource> sources)
    {
        this.sources = sources.ToDictionary(x => x.Sensor);

        var json = Contracts.SensorsJsonContext.Default;
        SensorTopic[] topics =
        [
            new SensorTopic<Contracts.VectorReading>("sensors.accelerometer", Contracts.Sensor.Accelerometer, json.VectorReading),
            new SensorTopic<Contracts.VectorReading>("sensors.gyroscope", Contracts.Sensor.Gyroscope, json.VectorReading),
            new SensorTopic<Contracts.VectorReading>("sensors.magnetometer", Contracts.Sensor.Magnetometer, json.VectorReading),
            new SensorTopic<Contracts.CompassReading>("sensors.compass", Contracts.Sensor.Compass, json.CompassReading),
            new SensorTopic<Contracts.BarometerReading>("sensors.barometer", Contracts.Sensor.Barometer, json.BarometerReading),
            new SensorTopic<Contracts.OrientationReading>("sensors.orientation", Contracts.Sensor.Orientation, json.OrientationReading),
            new SensorTopic<Contracts.ShakeDetected>("sensors.shake", Contracts.Sensor.Accelerometer, json.ShakeDetected)
        ];
        this.topics = topics.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var source in this.sources.Values)
            source.Sampled += this.OnSampled;
    }

    public string Name => "sensors";

    public bool IsSupported => this.sources.Values.Any(x => x.IsSupported);

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/{sensor}", this.StartAsync)
            .MapDelete("/{sensor}", this.StopAsync)
            .MapDelete("", this.StopAllAsync);

        foreach (var topic in this.topics.Values)
            topic.Map(routes, this.OnListenerStopped);
    }

    ValueTask StatusAsync(HttpContext context)
        => WebAppBridgeResults.Json(
            context,
            new Contracts.SensorStatus([.. this.sources.Keys.Order().Select(this.Describe)]),
            Contracts.SensorsJsonContext.Default.SensorStatus
        );

    async ValueTask StartAsync(HttpContext context)
    {
        if (!this.TryReadSensor(context, out var source))
        {
            await UnknownSensor(context);
            return;
        }

        if (!source.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, $"The {source.Sensor.ToString().ToLowerInvariant()} sensor");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.SensorsJsonContext.Default.SensorStartRequest) ?? new Contracts.SensorStartRequest();
        if (!Enum.IsDefined(body.Speed) || body.MinIntervalMs is < MinimumIntervalMs or > 60_000)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected a speed of Default, UI, Game or Fastest, and minIntervalMs from {MinimumIntervalMs} to 60000.");
            return;
        }

        try
        {
            await OnMainThread(() =>
            {
                lock (this.gate)
                {
                    // A new speed needs the platform listener restarted; a new interval only changes what is let through.
                    if (this.running.TryGetValue(source.Sensor, out var current) && current.Speed != body.Speed && source.IsMonitoring)
                        source.Stop();

                    if (!source.IsMonitoring)
                        source.Start(BridgeEnum.Convert<Contracts.SensorSpeed, EssentialsSpeed>(body.Speed));

                    this.running[source.Sensor] = new Running(body.Speed, body.MinIntervalMs);
                }
            });
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException)
        {
            await WebAppBridgeResults.NotSupported(context, $"The {source.Sensor.ToString().ToLowerInvariant()} sensor");
            return;
        }

        await WebAppBridgeResults.Json(context, this.Describe(source.Sensor), Contracts.SensorsJsonContext.Default.SensorInfo);
    }

    async ValueTask StopAsync(HttpContext context)
    {
        if (!this.TryReadSensor(context, out var source))
        {
            await UnknownSensor(context);
            return;
        }

        await OnMainThread(() => this.Stop(source));
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopAllAsync(HttpContext context)
    {
        await OnMainThread(this.StopAll);
        await WebAppBridgeResults.NoContent(context);
    }

    Contracts.SensorInfo Describe(Contracts.Sensor sensor)
    {
        var source = this.sources[sensor];

        lock (this.gate)
        {
            var state = this.running.TryGetValue(sensor, out var current) ? current : null;
            return new Contracts.SensorInfo(sensor, source.IsSupported, state is not null, state?.Speed, state?.MinIntervalMs);
        }
    }

    bool TryReadSensor(HttpContext context, out SensorSource source)
    {
        source = null!;
        return Enum.TryParse<Contracts.Sensor>(context.Request.RouteValues["sensor"], true, out var sensor)
               && Enum.IsDefined(sensor)
               && this.sources.TryGetValue(sensor, out source!);
    }

    static ValueTask UnknownSensor(HttpContext context)
        => WebAppBridgeResults.NotFound(context, "Expected a sensor: accelerometer, gyroscope, magnetometer, compass, barometer or orientation.");

    void OnSampled(SensorSample sample)
    {
        lock (this.gate)
        {
            if (!this.running.TryGetValue(sample.Sensor, out var state))
                return;

            // A shake is an occasion, not a reading; it is never thinned out.
            if (sample.Throttled)
            {
                var now = Stopwatch.GetTimestamp();
                if (state.LastPublished != 0 && Stopwatch.GetElapsedTime(state.LastPublished, now).TotalMilliseconds < state.MinIntervalMs)
                    return;

                state.LastPublished = now;
            }
        }

        sample.PublishTo(this.topics);
    }

    /// <summary>A sensor stops once no topic it feeds has a listener left.</summary>
    void OnListenerStopped(Contracts.Sensor sensor, int remaining)
    {
        if (remaining > 0 || this.IsHeard(sensor) || !this.sources.TryGetValue(sensor, out var source))
            return;

        // Read again on the main thread, so a listener that arrived in the meantime keeps the sensor running.
        _ = OnMainThread(() =>
        {
            if (!this.IsHeard(sensor))
                this.Stop(source);
        });
    }

    bool IsHeard(Contracts.Sensor sensor) => this.topics.Values.Any(x => x.Sensor == sensor && x.HasListeners);

    void Stop(SensorSource source)
    {
        lock (this.gate)
        {
            this.running.Remove(source.Sensor);

            try
            {
                if (source.IsMonitoring)
                    source.Stop();
            }
            catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException)
            {
            }
        }
    }

    void StopAll()
    {
        foreach (var source in this.sources.Values)
            this.Stop(source);
    }

    static Task OnMainThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            return dispatcher.DispatchAsync(action);

        action();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var source in this.sources.Values)
            source.Sampled -= this.OnSampled;

        this.StopAll();
    }

    sealed class Running(Contracts.SensorSpeed speed, int minIntervalMs)
    {
        public Contracts.SensorSpeed Speed { get; } = speed;

        public int MinIntervalMs { get; } = minIntervalMs;

        public long LastPublished { get; set; }
    }
}

/// <summary>A reading on its way to the page, carrying the event it goes out as.</summary>
internal abstract class SensorSample(Contracts.Sensor sensor, bool throttled)
{
    public Contracts.Sensor Sensor => sensor;

    public bool Throttled => throttled;

    public abstract void PublishTo(IReadOnlyDictionary<string, SensorTopic> topics);
}

internal sealed class SensorSample<T>(Contracts.Sensor sensor, string eventName, T reading, bool throttled = true)
    : SensorSample(sensor, throttled)
{
    public T Reading => reading;

    public override void PublishTo(IReadOnlyDictionary<string, SensorTopic> topics)
    {
        if (topics.TryGetValue(eventName, out var topic) && topic is SensorTopic<T> typed)
            typed.Publish(reading);
    }
}

/// <summary>One event name, and the sensor that has to run for it to carry anything.</summary>
internal abstract class SensorTopic(string name, Contracts.Sensor sensor)
{
    public string Name => name;

    public Contracts.Sensor Sensor => sensor;

    public abstract bool HasListeners { get; }

    public abstract void Map(WebAppBridgeRoutes routes, Action<Contracts.Sensor, int> stopped);
}

internal sealed class SensorTopic<T>(string name, Contracts.Sensor sensor, JsonTypeInfo<T> typeInfo) : SensorTopic(name, sensor)
{
    readonly WebAppEventSource<T> readings = new();

    public override bool HasListeners => this.readings.HasListeners;

    public void Publish(T reading) => this.readings.Publish(reading);

    public override void Map(WebAppBridgeRoutes routes, Action<Contracts.Sensor, int> stopped)
        => routes.MapEvent(this.Name, ct => this.readings.ListenAsync(remaining => stopped(this.Sensor, remaining), ct), typeInfo);
}

/// <summary>One sensor. Abstract so the bridge can be tested without a device.</summary>
internal abstract class SensorSource(Contracts.Sensor sensor)
{
    public Contracts.Sensor Sensor => sensor;

    public abstract bool IsSupported { get; }

    public abstract bool IsMonitoring { get; }

    public abstract void Start(EssentialsSpeed speed);

    public abstract void Stop();

    public event Action<SensorSample>? Sampled;

    protected void Raise(SensorSample sample) => this.Sampled?.Invoke(sample);

    /// <summary>
    /// A backend without an implementation — the maui-labs heads, or a reference assembly — throws rather than answering
    /// false. Either way the sensor is not there.
    /// </summary>
    protected static bool Probe(Func<bool> isSupported)
    {
        try
        {
            return isSupported();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

static class EssentialsSensors
{
    public static IEnumerable<SensorSource> Create()
    {
        var accelerometer = new Source<AccelerometerChangedEventArgs>(
            Contracts.Sensor.Accelerometer,
            () => Accelerometer.Default.IsSupported,
            () => Accelerometer.Default.IsMonitoring,
            speed => Accelerometer.Default.Start(speed),
            () => Accelerometer.Default.Stop(),
            h => Accelerometer.Default.ReadingChanged += h,
            h => Accelerometer.Default.ReadingChanged -= h,
            e => Vector(Contracts.Sensor.Accelerometer, "sensors.accelerometer", e.Reading.Acceleration)
        );

        // Essentials detects a shake from the accelerometer's own readings, so it arrives only while that runs.
        EventHandler shake = (_, _) => accelerometer.Emit(new SensorSample<Contracts.ShakeDetected>(
            Contracts.Sensor.Accelerometer,
            "sensors.shake",
            new(DateTimeOffset.UtcNow),
            throttled: false
        ));
        accelerometer.OnSubscribe = () => Accelerometer.Default.ShakeDetected += shake;
        accelerometer.OnUnsubscribe = () => Accelerometer.Default.ShakeDetected -= shake;

        return
        [
            accelerometer,
            new Source<GyroscopeChangedEventArgs>(
                Contracts.Sensor.Gyroscope,
                () => Gyroscope.Default.IsSupported,
                () => Gyroscope.Default.IsMonitoring,
                speed => Gyroscope.Default.Start(speed),
                () => Gyroscope.Default.Stop(),
                h => Gyroscope.Default.ReadingChanged += h,
                h => Gyroscope.Default.ReadingChanged -= h,
                e => Vector(Contracts.Sensor.Gyroscope, "sensors.gyroscope", e.Reading.AngularVelocity)
            ),
            new Source<MagnetometerChangedEventArgs>(
                Contracts.Sensor.Magnetometer,
                () => Magnetometer.Default.IsSupported,
                () => Magnetometer.Default.IsMonitoring,
                speed => Magnetometer.Default.Start(speed),
                () => Magnetometer.Default.Stop(),
                h => Magnetometer.Default.ReadingChanged += h,
                h => Magnetometer.Default.ReadingChanged -= h,
                e => Vector(Contracts.Sensor.Magnetometer, "sensors.magnetometer", e.Reading.MagneticField)
            ),
            new Source<CompassChangedEventArgs>(
                Contracts.Sensor.Compass,
                () => Compass.Default.IsSupported,
                () => Compass.Default.IsMonitoring,
                speed => Compass.Default.Start(speed),
                () => Compass.Default.Stop(),
                h => Compass.Default.ReadingChanged += h,
                h => Compass.Default.ReadingChanged -= h,
                e => new SensorSample<Contracts.CompassReading>(Contracts.Sensor.Compass, "sensors.compass", new(e.Reading.HeadingMagneticNorth, DateTimeOffset.UtcNow))
            ),
            new Source<BarometerChangedEventArgs>(
                Contracts.Sensor.Barometer,
                () => Barometer.Default.IsSupported,
                () => Barometer.Default.IsMonitoring,
                speed => Barometer.Default.Start(speed),
                () => Barometer.Default.Stop(),
                h => Barometer.Default.ReadingChanged += h,
                h => Barometer.Default.ReadingChanged -= h,
                e => new SensorSample<Contracts.BarometerReading>(Contracts.Sensor.Barometer, "sensors.barometer", new(e.Reading.PressureInHectopascals, DateTimeOffset.UtcNow))
            ),
            new Source<OrientationSensorChangedEventArgs>(
                Contracts.Sensor.Orientation,
                () => OrientationSensor.Default.IsSupported,
                () => OrientationSensor.Default.IsMonitoring,
                speed => OrientationSensor.Default.Start(speed),
                () => OrientationSensor.Default.Stop(),
                h => OrientationSensor.Default.ReadingChanged += h,
                h => OrientationSensor.Default.ReadingChanged -= h,
                e => new SensorSample<Contracts.OrientationReading>(
                    Contracts.Sensor.Orientation,
                    "sensors.orientation",
                    new(e.Reading.Orientation.X, e.Reading.Orientation.Y, e.Reading.Orientation.Z, e.Reading.Orientation.W, DateTimeOffset.UtcNow)
                )
            )
        ];
    }

    static SensorSample Vector(Contracts.Sensor sensor, string name, Vector3 value)
        => new SensorSample<Contracts.VectorReading>(sensor, name, new(value.X, value.Y, value.Z, DateTimeOffset.UtcNow));

    sealed class Source<TArgs>(
        Contracts.Sensor sensor,
        Func<bool> isSupported,
        Func<bool> isMonitoring,
        Action<EssentialsSpeed> start,
        Action stop,
        Action<EventHandler<TArgs>> subscribe,
        Action<EventHandler<TArgs>> unsubscribe,
        Func<TArgs, SensorSample> map
    ) : SensorSource(sensor)
    {
        bool subscribed;

        public Action? OnSubscribe { get; set; }

        public Action? OnUnsubscribe { get; set; }

        public override bool IsSupported => Probe(isSupported);

        public override bool IsMonitoring => Probe(isMonitoring);

        public override void Start(EssentialsSpeed speed)
        {
            if (!this.subscribed)
            {
                subscribe(this.OnReading);
                this.OnSubscribe?.Invoke();
                this.subscribed = true;
            }

            start(speed);
        }

        public override void Stop()
        {
            stop();

            if (this.subscribed)
            {
                unsubscribe(this.OnReading);
                this.OnUnsubscribe?.Invoke();
                this.subscribed = false;
            }
        }

        public void Emit(SensorSample sample) => this.Raise(sample);

        void OnReading(object? sender, TArgs e) => this.Raise(map(e));
    }
}
