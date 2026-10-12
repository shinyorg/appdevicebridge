using System.Text.Json;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.RegionGeofencing.Client;

public sealed record RegionGeofenceAccessResult(AccessState Access);

/// <param name="IsStarted">Whether monitoring is running. It survives an app restart.</param>
/// <param name="RegionSets">The region sets the app registered, in registration order.</param>
public sealed record RegionGeofenceStatus(bool IsStarted, IReadOnlyList<RegionSetInfo> RegionSets);

/// <summary>A layer of regions the app registered in C# — states, cities, delivery zones.</summary>
/// <param name="Name">The name every change in the set carries.</param>
public sealed record RegionSetInfo(string Name);

/// <summary>The device entered or left one region of one set.</summary>
/// <param name="RegionSet">The set the region belongs to.</param>
/// <param name="RegionId">The region's id.</param>
/// <param name="RegionName">Its display name, when the set's source has one.</param>
/// <param name="Entered">True for an entry, false for an exit.</param>
/// <param name="Latitude">The GPS position that produced the change.</param>
/// <param name="Longitude">The GPS position that produced the change.</param>
/// <param name="Region">
/// The region object — a document, or the placemark of a geocoder set. Null when its type has no JSON metadata in
/// the bridge's serializer options, or for an exit the source can no longer describe.
/// </param>
public sealed record RegionChange(
    string RegionSet,
    string RegionId,
    string? RegionName,
    bool Entered,
    double Latitude,
    double Longitude,
    JsonElement? Region = null
);

/// <summary>Where the device is now in one set.</summary>
/// <param name="RegionSet">The set that was queried.</param>
/// <param name="RegionId">The region the device is in, or null when it is outside every region of the set.</param>
/// <param name="RegionName">Its display name, when the set's source has one.</param>
/// <param name="DistanceMeters">Distance to the region — 0 when the device is inside it.</param>
/// <param name="Region">The region object, as on <see cref="RegionChange.Region"/>.</param>
public sealed record CurrentRegion(
    string RegionSet,
    string? RegionId,
    string? RegionName,
    double DistanceMeters,
    JsonElement? Region = null
);

/// <summary>Serialization for every region geofencing contract, shared by the page's clients and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RegionGeofenceAccessResult))]
[JsonSerializable(typeof(RegionGeofenceStatus))]
[JsonSerializable(typeof(RegionChange))]
[JsonSerializable(typeof(IReadOnlyList<CurrentRegion>))]
public partial class RegionGeofencingJsonContext : JsonSerializerContext;
