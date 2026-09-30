using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Gps.Client;

/// <summary>Which kind of GPS use a permission check is for.</summary>
public enum GpsAccessMode { Foreground, Background, Realtime }

/// <summary>How a listener keeps running with the app in the background.</summary>
public enum GpsBackgroundMode
{
    /// <summary>Foreground only.</summary>
    None,

    /// <summary>Battery-friendly background readings.</summary>
    Standard,

    /// <summary>Frequent background readings, at a battery cost.</summary>
    Realtime
}

public enum MotionActivityType { Unknown, Stationary, Walking, Running, Cycling, Automotive }

public enum MotionActivityConfidence { Low, Medium, High }

public sealed record GpsAccessResult(AccessState Access);

/// <param name="RequestPreciseAccuracy">Ask for precise rather than approximate location where the platform distinguishes.</param>
/// <param name="AutoRestart">Restart the listener after the app is relaunched.</param>
public sealed record GpsListenerSettings(
    GpsBackgroundMode BackgroundMode = GpsBackgroundMode.None,
    bool RequestPreciseAccuracy = false,
    bool AutoRestart = true
);

/// <summary>A position.</summary>
/// <param name="PositionAccuracy">Meters.</param>
/// <param name="Heading">Degrees from north.</param>
/// <param name="Altitude">Meters.</param>
/// <param name="Speed">Meters per second.</param>
public sealed record GpsReading(
    double Latitude,
    double Longitude,
    double PositionAccuracy,
    DateTimeOffset Timestamp,
    double Heading,
    double HeadingAccuracy,
    double Altitude,
    double Speed,
    double SpeedAccuracy,
    int Floor,
    bool IsStationary
);

public sealed record MotionAccessResult(AccessState Access);

public sealed record MotionActivity(MotionActivityType Activity, MotionActivityConfidence Confidence, DateTimeOffset Timestamp);

public sealed record MotionListener(bool IsListening);

/// <summary>Serialization for every GPS and motion activity contract, shared by the page's clients and the native bridges.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GpsAccessResult))]
[JsonSerializable(typeof(GpsListenerSettings))]
[JsonSerializable(typeof(GpsReading))]
[JsonSerializable(typeof(MotionAccessResult))]
[JsonSerializable(typeof(MotionActivity))]
[JsonSerializable(typeof(MotionListener))]
public partial class GpsJsonContext : JsonSerializerContext;
