using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.AppSupport.Client;

public enum Sensor
{
    /// <summary>Acceleration in g, including gravity. Also raises <c>sensors.shake</c>.</summary>
    Accelerometer,

    /// <summary>Rotation rate in radians per second.</summary>
    Gyroscope,

    /// <summary>The magnetic field in microteslas.</summary>
    Magnetometer,

    /// <summary>The heading from magnetic north, in degrees.</summary>
    Compass,

    /// <summary>Air pressure in hectopascals.</summary>
    Barometer,

    /// <summary>The device's orientation in space, as a quaternion.</summary>
    Orientation
}

/// <summary>How often the platform delivers readings. <see cref="Fastest"/> can be hundreds a second; see <see cref="SensorStartRequest.MinIntervalMs"/>.</summary>
public enum SensorSpeed
{
    Default,

    /// <summary>Suited to updating a user interface.</summary>
    UI,

    /// <summary>Suited to games.</summary>
    Game,

    Fastest
}

/// <param name="Supported">False when the device has no such sensor, or the platform offers none; starting it fails with 501.</param>
/// <param name="Running">True while it is started and reporting.</param>
/// <param name="MinIntervalMs">The least time between readings the page is sent, while running.</param>
public sealed record SensorInfo(Sensor Sensor, bool Supported, bool Running, SensorSpeed? Speed, int? MinIntervalMs);

/// <summary>Every sensor and what it is doing.</summary>
public sealed record SensorStatus(IReadOnlyList<SensorInfo> Sensors);

/// <param name="Speed">How often the platform samples.</param>
/// <param name="MinIntervalMs">
/// Readings closer together than this are dropped before they reach the page, which is what keeps a fast sensor from
/// flooding the event stream. 16 (about 60 a second) by default; never less than 5.
/// </param>
public sealed record SensorStartRequest(SensorSpeed Speed = SensorSpeed.UI, int MinIntervalMs = 16);

/// <summary>An accelerometer, gyroscope or magnetometer reading.</summary>
public sealed record VectorReading(double X, double Y, double Z, DateTimeOffset Timestamp);

/// <param name="Heading">Degrees clockwise from magnetic north, 0 to 360.</param>
public sealed record CompassReading(double Heading, DateTimeOffset Timestamp);

public sealed record BarometerReading(double PressureHPa, DateTimeOffset Timestamp);

/// <summary>The rotation from the reference frame to the device, as a unit quaternion.</summary>
public sealed record OrientationReading(double X, double Y, double Z, double W, DateTimeOffset Timestamp);

/// <summary>The device was shaken, while the accelerometer is running.</summary>
public sealed record ShakeDetected(DateTimeOffset Timestamp);

/// <summary>Serialization for every sensor contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SensorStatus))]
[JsonSerializable(typeof(SensorInfo))]
[JsonSerializable(typeof(SensorStartRequest))]
[JsonSerializable(typeof(VectorReading))]
[JsonSerializable(typeof(CompassReading))]
[JsonSerializable(typeof(BarometerReading))]
[JsonSerializable(typeof(OrientationReading))]
[JsonSerializable(typeof(ShakeDetected))]
public partial class SensorsJsonContext : JsonSerializerContext;
