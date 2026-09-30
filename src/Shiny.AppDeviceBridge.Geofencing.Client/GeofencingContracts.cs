using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Geofencing.Client;

public enum GeofenceState { Unknown, Entered, Exited }

public sealed record GeofenceAccessResult(AccessState Access);

/// <summary>A circular region.</summary>
/// <param name="Identifier">Names the region; monitoring one with an identifier already in use replaces it.</param>
/// <param name="RadiusMeters">Must be positive.</param>
/// <param name="SingleUse">Stop monitoring after the first transition.</param>
public sealed record GeofenceRegion(
    string Identifier,
    double Latitude,
    double Longitude,
    double RadiusMeters,
    bool SingleUse = false,
    bool NotifyOnEntry = true,
    bool NotifyOnExit = true
);

public sealed record GeofenceStatus(string Identifier, GeofenceState State);

/// <summary>Serialization for every geofencing contract, shared by the page's clients and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GeofenceAccessResult))]
[JsonSerializable(typeof(GeofenceRegion))]
[JsonSerializable(typeof(IReadOnlyList<GeofenceRegion>))]
[JsonSerializable(typeof(GeofenceStatus))]
public partial class GeofencingJsonContext : JsonSerializerContext;
