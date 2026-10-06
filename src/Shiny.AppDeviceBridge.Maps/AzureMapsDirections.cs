using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Azure Maps routes, from the Route service's <c>Post Route Directions</c> (2025-01-01): cars, trucks and walking, with live
/// traffic when <see cref="UseTraffic"/> is on. Authenticates with an <see cref="AzureMapsCredential"/>, which stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Router = new AzureMapsRouteProvider(azureMapsKey));
/// </code>
/// <para>
/// Azure Maps has no bicycle routing; the page sees which modes there are in <c>onlineModes</c>. Instructions are written in
/// <see cref="DirectionsRequest.Language"/> and Azure Maps' own choice of units.
/// </para>
/// </summary>
public sealed class AzureMapsRouteProvider : IRouteProvider
{
    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsRouteProvider(string subscriptionKey) : this(new AzureMapsCredential(subscriptionKey))
    {
    }

    /// <summary>Authenticates with a shared key or Microsoft Entra ID, as <paramref name="credential"/> says.</summary>
    public AzureMapsRouteProvider(AzureMapsCredential credential)
        => this.Credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public AzureMapsCredential Credential { get; }

    /// <summary><c>https://atlas.microsoft.com/</c>, or a geography's own host such as <c>https://us.atlas.microsoft.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://atlas.microsoft.com/");

    /// <summary>Routes driving and trucks around current traffic, with durations that include it. True by default.</summary>
    public bool UseTraffic { get; set; } = true;

    public string Name => "Azure Maps";

    public string Attribution => "© Microsoft Azure Maps, © TomTom";

    public IReadOnlyCollection<TravelMode> Modes { get; } = [TravelMode.Car, TravelMode.Walking, TravelMode.Truck];

    public async Task<DirectionsRoute> RouteAsync(DirectionsRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        var authorize = await this.Credential.AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(this.BaseAddress, "route/directions?api-version=2025-01-01"))
        {
            Content = new StringContent(this.ToRequest(request), System.Text.Encoding.UTF8, "application/geo+json")
        };
        if (!String.IsNullOrWhiteSpace(request.Language))
            message.Headers.AcceptLanguage.ParseAdd(request.Language);
        authorize(message);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw ProviderErrors.ToException("Azure Maps", response.StatusCode, body);

        return Parse(body);
    }

    internal string ToRequest(DirectionsRequest request)
    {
        var features = new JsonArray();
        for (var i = 0; i < request.Stops.Count; i++)
        {
            var stop = request.Stops[i];
            features.Add((JsonNode)new JsonObject
            {
                ["type"] = "Feature",
                ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(stop.Longitude, stop.Latitude) },
                ["properties"] = new JsonObject { ["pointIndex"] = i, ["pointType"] = "waypoint" }
            });
        }

        var driving = request.Mode != TravelMode.Walking;
        var body = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = features,
            ["travelMode"] = request.Mode switch
            {
                TravelMode.Walking => "walking",
                TravelMode.Truck => "truck",
                _ => "driving"
            },
            ["routeOutputOptions"] = new JsonArray("routePath", "itinerary")
        };

        if (driving && this.UseTraffic)
            body["optimizeRoute"] = "fastestWithTraffic";

        // Avoidances are only taken for driving and trucks.
        if (driving && request.Avoid is { } avoid)
        {
            var avoided = new JsonArray();
            if (avoid.Tolls)
                avoided.Add((JsonNode)"tollRoads");
            if (avoid.Highways)
                avoided.Add((JsonNode)"limitedAccessHighways");
            if (avoid.Ferries)
                avoided.Add((JsonNode)"ferries");
            if (avoided.Count > 0)
                body["avoid"] = avoided;
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The response: a GeoJSON feature collection holding a <c>RoutePath</c> — a MultiLineString, one line per leg — and a
    /// <c>ManeuverPoint</c> for each instruction, placed by its leg and its index into that leg's line.
    /// </summary>
    internal static DirectionsRoute Parse(string json)
    {
        var features = JsonNode.Parse(json)?["features"]?.AsArray() ?? throw new FormatException("Azure Maps answered without features.");
        var path = features.FirstOrDefault(x => (string?)x?["properties"]?["type"] == "RoutePath")
                   ?? throw new DirectionsException(DirectionsError.NoRoute, "Azure Maps found no route.");

        var shape = new List<double[]>();
        var offsets = new List<int>();
        foreach (var line in path["geometry"]?["coordinates"]?.AsArray() ?? [])
        {
            var points = (line?.AsArray() ?? []).Select(p => new[] { (double)p![0]!, (double)p[1]! }).ToList();
            offsets.Add(RouteShapes.Append(shape, points));
        }

        var maneuvers = features
            .Where(x => (string?)x?["properties"]?["type"] == "ManeuverPoint")
            .Select(x => x!["properties"]!)
            .Select(p => (
                Leg: (int?)p["routePathPoint"]?["legIndex"] ?? 0,
                Point: (int?)p["routePathPoint"]?["pointIndex"] ?? 0,
                Properties: p
            ))
            .OrderBy(x => x.Leg)
            .ThenBy(x => x.Point)
            .ToList();

        var properties = path["properties"];
        var legs = new List<RouteLeg>();
        var pathLegs = properties?["legs"]?.AsArray() ?? [];

        for (var i = 0; i < Math.Max(1, pathLegs.Count); i++)
        {
            var leg = i < pathLegs.Count ? pathLegs[i] : properties;
            var index = (int?)leg?["routePathRange"]?["legIndex"] ?? i;
            var offset = index < offsets.Count ? offsets[index] : 0;

            legs.Add(new RouteLeg(
                Math.Round((double?)leg?["distanceInMeters"] ?? 0, 1),
                Duration(leg),
                [.. maneuvers.Where(x => x.Leg == index || pathLegs.Count == 0).Select(x => ToManeuver(x.Properties, offset + x.Point))]
            ));
        }

        return new DirectionsRoute(
            DirectionsSource.Online,
            Math.Round((double?)properties?["distanceInMeters"] ?? 0, 1),
            Duration(properties),
            shape,
            RouteShapes.Bounds(shape),
            legs
        );

        static double Duration(JsonNode? node) => Math.Round((double?)node?["durationTrafficInSeconds"] ?? (double?)node?["durationInSeconds"] ?? 0, 1);
    }

    static RouteManeuver ToManeuver(JsonNode properties, int shapeIndex)
    {
        var instruction = properties["instruction"];
        var streets = (properties["steps"]?.AsArray() ?? [])
            .SelectMany(s => s?["names"]?.AsArray() ?? [])
            .Select(x => (string?)x)
            .OfType<string>()
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();

        return new RouteManeuver(
            ToKind((string?)instruction?["maneuverType"]),
            (string?)instruction?["text"] ?? String.Empty,
            null,
            Math.Round((double?)properties["distanceInMeters"] ?? 0, 1),
            Math.Round((double?)properties["durationInSeconds"] ?? 0, 1),
            streets,
            shapeIndex
        );
    }

    /// <summary>Azure Maps' maneuver types folded into what a page draws an arrow for. Combined maneuvers take their first part.</summary>
    internal static ManeuverKind ToKind(string? type) => type switch
    {
        "DepartStart" or "DepartIntermediateStop" or "DepartIntermediateStopReturning" => ManeuverKind.Depart,
        "ArriveFinish" or "ArriveIntermediate" => ManeuverKind.Arrive,
        "Continue" or "Follow" or "RoadNameChange" or "KeepToStayStraight" or "SwitchToMainRoad" or "SwitchToParallelRoad" => ManeuverKind.Continue,
        "TurnRightSharp" => ManeuverKind.SharpRight,
        "TurnLeftSharp" => ManeuverKind.SharpLeft,
        "UTurn" or "TurnBack" => ManeuverKind.UTurn,
        "TurnThenMerge" or "BearThenMerge" or "MergeFreeway" or "MergeHighway" or "MergeMotorway" => ManeuverKind.Merge,
        "TakeRampLeft" or "KeepOnRampLeft" or "RampThenHighwayLeft" => ManeuverKind.RampLeft,
        "TakeRampRight" or "KeepOnRampRight" or "RampThenHighwayRight" => ManeuverKind.RampRight,
        "TakeRamp" or "TakeRampStraight" or "KeepOnRampStraight" or "RampThenHighwayStraight" => ManeuverKind.RampStraight,
        "MotorwayExitLeft" => ManeuverKind.ExitLeft,
        "MotorwayExitRight" => ManeuverKind.ExitRight,
        "KeepLeft" or "KeepToStayLeft" => ManeuverKind.KeepLeft,
        "KeepRight" or "KeepToStayRight" => ManeuverKind.KeepRight,
        "KeepStraight" => ManeuverKind.KeepStraight,
        "EnterRoundabout" or "EnterThenExitRoundabout" or "GoAroundRoundabout" => ManeuverKind.EnterRoundabout,
        "ExitRoundabout" or "ExitRoundaboutLeft" or "ExitRoundaboutRight" => ManeuverKind.ExitRoundabout,
        "TakeFerry" or "Take" => ManeuverKind.Ferry,
        { } t when t.StartsWith("TurnRight", StringComparison.Ordinal) || t == "TurnToStayRight" => ManeuverKind.Right,
        { } t when t.StartsWith("TurnLeft", StringComparison.Ordinal) || t == "TurnToStayLeft" => ManeuverKind.Left,
        { } t when t.StartsWith("BearRight", StringComparison.Ordinal) => ManeuverKind.SlightRight,
        { } t when t.StartsWith("BearLeft", StringComparison.Ordinal) => ManeuverKind.SlightLeft,
        _ => ManeuverKind.Other
    };
}

/// <summary>
/// Azure Maps' geocoder, from the Search service's <c>Get Geocoding</c> (2025-01-01): addresses, places and landmarks
/// worldwide. Authenticates with an <see cref="AzureMapsCredential"/>, which stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Geocoder = new AzureMapsGeocoder(azureMapsKey));
/// </code>
/// </summary>
public sealed class AzureMapsGeocoder : IGeocoder
{
    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsGeocoder(string subscriptionKey) : this(new AzureMapsCredential(subscriptionKey))
    {
    }

    /// <summary>Authenticates with a shared key or Microsoft Entra ID, as <paramref name="credential"/> says.</summary>
    public AzureMapsGeocoder(AzureMapsCredential credential)
        => this.Credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public AzureMapsCredential Credential { get; }

    /// <summary><c>https://atlas.microsoft.com/</c>, or a geography's own host such as <c>https://us.atlas.microsoft.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://atlas.microsoft.com/");

    /// <summary>Which country's view of disputed places to answer with, as an ISO 3166-1 alpha-2 code. Azure Maps' <c>Auto</c> when null.</summary>
    public string? View { get; set; }

    public string Attribution => "© Microsoft Azure Maps, © TomTom";

    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(GeocodeQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);

        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"geocode?api-version=2025-01-01&top={query.Limit}&query={Uri.EscapeDataString(query.Text)}"
        );
        if (!String.IsNullOrWhiteSpace(this.View))
            path += "&view=" + Uri.EscapeDataString(this.View);

        var authorize = await this.Credential.AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(this.BaseAddress, path));
        if (query.Language is { } language)
            request.Headers.AcceptLanguage.ParseAdd(language);
        authorize(request);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), query.Limit);
    }

    /// <summary>A GeoJSON feature collection: a point and a bounding box for each place, and its address.</summary>
    internal static IReadOnlyList<GeocodedPlace> Parse(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            throw new FormatException("Azure Maps answered with something that is not a list of places.");

        var places = new List<GeocodedPlace>();
        foreach (var feature in features.EnumerateArray())
        {
            if (places.Count == limit)
                break;

            var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
            var properties = feature.TryGetProperty("properties", out var p) ? p : default;
            var address = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("address", out var a) ? a : default;

            var formatted = Text(address, "formattedAddress") ?? String.Empty;
            var name = Text(properties, "type") == "Address" && Text(address, "addressLine") is { } line
                ? line
                : formatted.Split(',', 2)[0].Trim();

            double[]? bounds = null;
            if (feature.TryGetProperty("bbox", out var box) && box.ValueKind == JsonValueKind.Array && box.GetArrayLength() == 4)
            {
                var b = box.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (b[0] != b[2] || b[1] != b[3])
                    bounds = b;
            }

            places.Add(new GeocodedPlace(name, formatted, coordinates[1].GetDouble(), coordinates[0].GetDouble(), bounds));
        }

        return places;

        static string? Text(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>How Azure Maps and Google answer a request they refuse: <c>{ "error": { "message": … } }</c>.</summary>
static class ProviderErrors
{
    public static Exception ToException(string provider, HttpStatusCode status, string body)
    {
        string? text = null;
        try
        {
            text = (string?)JsonNode.Parse(body)?["error"]?["message"];
        }
        catch (JsonException)
        {
        }

        var message = text is { Length: > 0 } ? $"{provider}: {text}" : $"{provider} answered {(int)status}.";
        return status switch
        {
            HttpStatusCode.NotFound => new DirectionsException(DirectionsError.NoRoute, message),
            HttpStatusCode.BadRequest when text?.Contains("route", StringComparison.OrdinalIgnoreCase) == true
                                           && (text.Contains("no route", StringComparison.OrdinalIgnoreCase) || text.Contains("NO_ROUTE", StringComparison.Ordinal) || text.Contains("not find", StringComparison.OrdinalIgnoreCase))
                => new DirectionsException(DirectionsError.NoRoute, message),
            HttpStatusCode.BadRequest => new DirectionsException(DirectionsError.InvalidRequest, message),

            // A key refused, a quota spent, the service down: the app's problem or the provider's, not the page's request.
            _ => new DirectionsException(DirectionsError.RouterFailed, message)
        };
    }
}
