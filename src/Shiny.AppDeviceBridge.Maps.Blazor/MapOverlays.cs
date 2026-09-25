using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps.Blazor;

public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>A marker on the map.</summary>
/// <param name="Id">Unique on the map. Adding a pin with an id already there replaces it.</param>
/// <param name="Label">Shown in a popup when the pin is tapped.</param>
/// <param name="Draggable">Whether the user can drag it; the map raises <c>OnPinMoved</c> where it is dropped.</param>
public sealed record MapPin(string Id, GeoPoint Position, string? Label = null, string Color = "#e11d48", bool Draggable = false);

public enum MapShapeKind
{
    Line,
    Polygon
}

/// <summary>A line or an area drawn above the roads and below the labels.</summary>
/// <param name="Id">Unique on the map. Adding a shape with an id already there replaces it.</param>
/// <param name="Points">A line's points in order, or an area's outline — not closed; the map closes it.</param>
/// <param name="Color">A CSS colour for the line or the outline and fill.</param>
/// <param name="Width">Line width in pixels.</param>
/// <param name="FillOpacity">An area's fill, 0 to 1.</param>
/// <param name="CasingColor">A wider line drawn under this one — the outline navigation apps give a route. None when null.</param>
public sealed record MapShape(
    string Id,
    MapShapeKind Kind,
    IReadOnlyList<GeoPoint> Points,
    string Color = "#3b82f6",
    double Width = 4,
    double FillOpacity = 0.25,
    string? CasingColor = null,
    string? Label = null
);

/// <summary>What a click on the map does.</summary>
public enum MapDrawMode
{
    /// <summary>Pans and zooms; clicks are reported.</summary>
    None,

    /// <summary>Each click is reported as a place to drop a pin.</summary>
    Pin,

    /// <summary>Clicks add points; a double-click finishes the line.</summary>
    Line,

    /// <summary>Clicks add points; a double-click closes the area.</summary>
    Polygon
}

/// <param name="IsPinMode">Whether the map was in <see cref="MapDrawMode.Pin"/> — the page drops a pin there.</param>
public sealed record MapClick(GeoPoint Position, bool IsPinMode);

public sealed record MapDrawn(MapShapeKind Kind, IReadOnlyList<GeoPoint> Points);

public sealed record MapPinMoved(string Id, GeoPoint Position);

public sealed record MapView(GeoPoint Center, double Zoom);

/// <summary>What the page sends the script when a map is created.</summary>
sealed record MapCreateOptions(
    string Id,
    string TilesUrl,
    string GlyphsUrl,
    string SpritesUrl,
    int MaxZoom,
    string Attribution,
    string Flavor,
    string Language,
    double Latitude,
    double Longitude,
    double Zoom,
    bool Navigation,
    TrafficInfo? Traffic,
    bool ShowTraffic
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MapPin))]
[JsonSerializable(typeof(MapShape))]
[JsonSerializable(typeof(GeoPoint))]
[JsonSerializable(typeof(List<GeoPoint>))]
[JsonSerializable(typeof(MapView))]
[JsonSerializable(typeof(MapCreateOptions))]
partial class MapOverlayJsonContext : JsonSerializerContext;

public static class BridgeMapsExtensions
{
    /// <summary>
    /// Registers what <see cref="BridgeMap"/> needs: the maps and directions bridge clients. Call after
    /// <c>AddWebAppHostClient()</c>.
    /// </summary>
    public static IServiceCollection AddBridgeMaps(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddMapsBridgeClient().AddDirectionsBridgeClient();
    }

    /// <summary>The route as a line to draw: blue with a dark casing, the way navigation apps show one.</summary>
    public static MapShape ToShape(this DirectionsRoute route, string id = "route", string color = "#3b82f6", string casingColor = "#1e3a8a", double width = 5)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new MapShape(id, MapShapeKind.Line, [.. route.Shape.Select(p => new GeoPoint(p[1], p[0]))], color, width, 0, casingColor);
    }
}
