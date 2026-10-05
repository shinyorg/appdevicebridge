using System.Text.Json;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.DocumentGeofencing.Client;

public sealed record DocumentGeofenceAccessResult(AccessState Access);

/// <param name="IsStarted">Whether monitoring is running. It survives an app restart.</param>
/// <param name="RegionSets">The region sets the app registered, in registration order.</param>
public sealed record DocumentGeofenceStatus(bool IsStarted, IReadOnlyList<DocumentRegionSetInfo> RegionSets);

/// <summary>A layer of regions the app registered in C# — states, cities, delivery zones.</summary>
/// <param name="Name">The name every change in the set carries.</param>
/// <param name="WithinMeters">Set for a proximity set — inside means within this distance; null for containment.</param>
public sealed record DocumentRegionSetInfo(string Name, double? WithinMeters = null);

/// <summary>The device entered or left one region of one set.</summary>
/// <param name="RegionSet">The set the region belongs to.</param>
/// <param name="RegionId">The region document's id.</param>
/// <param name="RegionName">Its display name, when the set was registered with one.</param>
/// <param name="Entered">True for an entry, false for an exit.</param>
/// <param name="Latitude">The GPS position that produced the change.</param>
/// <param name="Longitude">The GPS position that produced the change.</param>
/// <param name="Region">
/// The region document as the store serializes it. Null when its type has no JSON metadata in the store's
/// serializer options, or for an exit from a region since deleted.
/// </param>
public sealed record DocumentRegionChange(
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
/// <param name="RegionName">Its display name, when the set was registered with one.</param>
/// <param name="DistanceMeters">Distance to the region — 0 in a containment set.</param>
/// <param name="Region">The region document as the store serializes it, as on <see cref="DocumentRegionChange.Region"/>.</param>
public sealed record DocumentCurrentRegion(
    string RegionSet,
    string? RegionId,
    string? RegionName,
    double DistanceMeters,
    JsonElement? Region = null
);

/// <summary>Serialization for every document geofencing contract, shared by the page's clients and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DocumentGeofenceAccessResult))]
[JsonSerializable(typeof(DocumentGeofenceStatus))]
[JsonSerializable(typeof(DocumentRegionChange))]
[JsonSerializable(typeof(IReadOnlyList<DocumentCurrentRegion>))]
public partial class DocumentGeofencingJsonContext : JsonSerializerContext;
