using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Maps.Client;

public enum TravelMode
{
    Car,
    Bicycle,
    Walking,
    Truck
}

/// <summary>Where a route is computed.</summary>
public enum DirectionsSource
{
    /// <summary>On the device when a downloaded road network covers every stop, online otherwise.</summary>
    Auto,

    /// <summary>On the device only; fails when no downloaded road network covers every stop.</summary>
    Device,

    /// <summary>Online only.</summary>
    Online
}

/// <summary>The units spoken and written instructions use. Distances in the response are always metres.</summary>
public enum DistanceUnits
{
    Kilometers,
    Miles
}

public sealed record RouteStop(double Latitude, double Longitude, string? Name = null);

public sealed record RouteAvoid(bool Tolls = false, bool Highways = false, bool Ferries = false);

/// <param name="Stops">Two or more, in order: where the route starts, any stops along the way, where it ends.</param>
/// <param name="Language">An IETF language tag for the instructions, such as <c>en-US</c> or <c>fr</c>. English when null or unsupported.</param>
public sealed record DirectionsRequest(
    IReadOnlyList<RouteStop> Stops,
    TravelMode Mode = TravelMode.Car,
    DistanceUnits Units = DistanceUnits.Kilometers,
    string? Language = null,
    DirectionsSource Source = DirectionsSource.Auto,
    RouteAvoid? Avoid = null
);

/// <summary>What a maneuver asks the traveller to do. Enough to pick an arrow icon.</summary>
public enum ManeuverKind
{
    Depart,
    Arrive,
    Continue,
    SlightRight,
    Right,
    SharpRight,
    UTurn,
    SharpLeft,
    Left,
    SlightLeft,
    RampStraight,
    RampRight,
    RampLeft,
    ExitRight,
    ExitLeft,
    KeepStraight,
    KeepRight,
    KeepLeft,
    Merge,
    EnterRoundabout,
    ExitRoundabout,
    Ferry,
    Other
}

/// <param name="Instruction">Written for reading: "Turn right onto North Broadway."</param>
/// <param name="VerbalInstruction">Written for speaking before the maneuver, when the router provides it.</param>
/// <param name="Distance">Metres from this maneuver to the next.</param>
/// <param name="Duration">Seconds from this maneuver to the next.</param>
/// <param name="ShapeIndex">Where the maneuver is: an index into <see cref="DirectionsRoute.Shape"/>.</param>
/// <param name="RoundaboutExit">Which exit to take, for <see cref="ManeuverKind.EnterRoundabout"/>.</param>
public sealed record RouteManeuver(
    ManeuverKind Kind,
    string Instruction,
    string? VerbalInstruction,
    double Distance,
    double Duration,
    IReadOnlyList<string> StreetNames,
    int ShapeIndex,
    int? RoundaboutExit = null
);

/// <summary>The part of a route between two consecutive stops.</summary>
public sealed record RouteLeg(double Distance, double Duration, IReadOnlyList<RouteManeuver> Maneuvers);

/// <param name="Source">Where the route was computed: <see cref="DirectionsSource.Device"/> or <see cref="DirectionsSource.Online"/>.</param>
/// <param name="Distance">Metres.</param>
/// <param name="Duration">Seconds.</param>
/// <param name="Shape">The line to draw, as <c>[longitude, latitude]</c> pairs — GeoJSON's order, so it drops into a LineString as it is.</param>
/// <param name="Bounds"><c>[west, south, east, north]</c> around <see cref="Shape"/>, for fitting the map to the route.</param>
public sealed record DirectionsRoute(
    DirectionsSource Source,
    double Distance,
    double Duration,
    IReadOnlyList<double[]> Shape,
    double[] Bounds,
    IReadOnlyList<RouteLeg> Legs
);

/// <param name="Online">Whether the app configured an online router.</param>
/// <param name="OnDevice">Whether this platform can compute routes on the device. It still needs a downloaded road network to do so.</param>
/// <param name="OfflineRegions">The regions whose road network is on the device.</param>
/// <param name="Geocoding">Whether the app configured a geocoder, so <c>geocode</c> can turn an address into a stop.</param>
public sealed record DirectionsInfo(bool Online, bool OnDevice, IReadOnlyList<string> OfflineRegions, bool Geocoding);

/// <summary>A place a geocoder found for an address or a name.</summary>
/// <param name="Name">What the place is called: "Union Station", "1701 Wynkoop Street".</param>
/// <param name="Address">The full address or description, which tells apart places with the same name.</param>
/// <param name="Bounds"><c>[west, south, east, north]</c> around a place with an extent — a city, a park — for fitting the map to it. Null for a point.</param>
public sealed record GeocodedPlace(string Name, string Address, double Latitude, double Longitude, double[]? Bounds = null);

/// <param name="Places">Best match first. Empty when nothing matched.</param>
/// <param name="Attribution">The credit the geocoder's terms require, as HTML.</param>
public sealed record GeocodeResult(IReadOnlyList<GeocodedPlace> Places, string Attribution);

/// <summary>Serialization for every directions contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DirectionsRequest))]
[JsonSerializable(typeof(DirectionsRoute))]
[JsonSerializable(typeof(DirectionsInfo))]
[JsonSerializable(typeof(GeocodeResult))]
public partial class DirectionsJsonContext : JsonSerializerContext;
