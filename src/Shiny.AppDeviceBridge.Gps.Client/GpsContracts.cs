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

/// <summary>A human-readable description of a position from the platform geocoder. Any part it could not resolve is null.</summary>
/// <param name="Latitude">The latitude of the position the placemark describes.</param>
/// <param name="Longitude">The longitude of the position the placemark describes.</param>
/// <param name="Name">The name of the place: a landmark or business, or the street address.</param>
/// <param name="SubThoroughfare">The street number.</param>
/// <param name="Thoroughfare">The street name.</param>
/// <param name="SubLocality">The neighbourhood or district.</param>
/// <param name="Locality">The city or town.</param>
/// <param name="SubAdministrativeArea">The county or equivalent.</param>
/// <param name="AdministrativeArea">The state, province or equivalent.</param>
/// <param name="PostalCode">The postal or zip code.</param>
/// <param name="CountryCode">The ISO 3166-1 alpha-2 country code.</param>
/// <param name="CountryName">The localized country name.</param>
/// <param name="FormattedAddress">The full address on one line, formatted by the platform.</param>
public sealed record Placemark(
    double Latitude,
    double Longitude,
    string? Name = null,
    string? SubThoroughfare = null,
    string? Thoroughfare = null,
    string? SubLocality = null,
    string? Locality = null,
    string? SubAdministrativeArea = null,
    string? AdministrativeArea = null,
    string? PostalCode = null,
    string? CountryCode = null,
    string? CountryName = null,
    string? FormattedAddress = null
);

/// <summary>Serialization for every GPS, motion activity and geocoding contract, shared by the page's clients and the native bridges.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GpsAccessResult))]
[JsonSerializable(typeof(GpsListenerSettings))]
[JsonSerializable(typeof(GpsReading))]
[JsonSerializable(typeof(MotionAccessResult))]
[JsonSerializable(typeof(MotionActivity))]
[JsonSerializable(typeof(MotionListener))]
[JsonSerializable(typeof(Placemark))]
[JsonSerializable(typeof(IReadOnlyList<Placemark>))]
public partial class GpsJsonContext : JsonSerializerContext;
