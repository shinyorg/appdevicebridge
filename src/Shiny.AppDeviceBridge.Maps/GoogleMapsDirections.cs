using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Google Maps routes, from the Routes API's <c>computeRoutes</c>: cars, bicycles and walking. The key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Router = new GoogleMapsRouteProvider(googleMapsKey));
/// </code>
/// <para>
/// The key needs the Routes API enabled. Google has no truck routing; the page sees which modes there are in
/// <c>onlineModes</c>. Google gives no instruction for arriving, so each leg ends with an <see cref="ManeuverKind.Arrive"/>
/// maneuver written here, in English, naming the stop when it has a name. Google's terms require its routes to be shown on a
/// Google map: pair this with <see cref="GoogleMapsBasemapProvider"/>.
/// </para>
/// </summary>
/// <param name="apiKey">A Google Maps Platform key. Sent in a header, never to the page.</param>
public sealed class GoogleMapsRouteProvider(string apiKey) : IRouteProvider
{
    const string FieldMask = "routes.distanceMeters,routes.duration,routes.legs.distanceMeters,routes.legs.duration,"
                             + "routes.legs.steps.distanceMeters,routes.legs.steps.staticDuration,"
                             + "routes.legs.steps.polyline.encodedPolyline,routes.legs.steps.navigationInstruction";

    public string ApiKey { get; } = String.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("A Google Maps API key is required.", nameof(apiKey)) : apiKey;

    /// <summary><c>https://routes.googleapis.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://routes.googleapis.com/");

    /// <summary>
    /// Routes cars around current traffic, with durations that include it. Off by default: Google bills traffic-aware routes
    /// at a higher rate.
    /// </summary>
    public bool UseTraffic { get; set; }

    public string Name => "Google Maps";

    public string Attribution => "Routes © Google";

    public IReadOnlyCollection<TravelMode> Modes { get; } = [TravelMode.Car, TravelMode.Bicycle, TravelMode.Walking];

    public async Task<DirectionsRoute> RouteAsync(DirectionsRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(this.BaseAddress, "directions/v2:computeRoutes"))
        {
            Content = new StringContent(this.ToRequest(request), System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-Goog-Api-Key", this.ApiKey);
        message.Headers.Add("X-Goog-FieldMask", FieldMask);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw ProviderErrors.ToException("Google Maps", response.StatusCode, body);

        return Parse(body, request.Stops);
    }

    internal string ToRequest(DirectionsRequest request)
    {
        static JsonObject Waypoint(RouteStop stop) => new()
        {
            ["location"] = new JsonObject
            {
                ["latLng"] = new JsonObject { ["latitude"] = stop.Latitude, ["longitude"] = stop.Longitude }
            }
        };

        var car = request.Mode == TravelMode.Car;
        var body = new JsonObject
        {
            ["origin"] = Waypoint(request.Stops[0]),
            ["destination"] = Waypoint(request.Stops[^1]),
            ["travelMode"] = request.Mode switch
            {
                TravelMode.Bicycle => "BICYCLE",
                TravelMode.Walking => "WALK",
                _ => "DRIVE"
            },
            ["polylineQuality"] = "HIGH_QUALITY",
            ["units"] = request.Units == DistanceUnits.Miles ? "IMPERIAL" : "METRIC"
        };

        if (request.Stops.Count > 2)
            body["intermediates"] = new JsonArray([.. request.Stops.Skip(1).Take(request.Stops.Count - 2).Select(s => (JsonNode)Waypoint(s))]);

        if (!String.IsNullOrWhiteSpace(request.Language))
            body["languageCode"] = request.Language;

        // Google takes a routing preference, and avoidances, only for driving.
        if (car)
        {
            body["routingPreference"] = this.UseTraffic ? "TRAFFIC_AWARE" : "TRAFFIC_UNAWARE";

            if (request.Avoid is { } avoid && (avoid.Tolls || avoid.Highways || avoid.Ferries))
            {
                body["routeModifiers"] = new JsonObject
                {
                    ["avoidTolls"] = avoid.Tolls,
                    ["avoidHighways"] = avoid.Highways,
                    ["avoidFerries"] = avoid.Ferries
                };
            }
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The response: legs of steps, each with its own polyline at five decimal places. The route's shape is the steps' lines
    /// joined, so each maneuver sits at the start of its step's.
    /// </summary>
    internal static DirectionsRoute Parse(string json, IReadOnlyList<RouteStop> stops)
    {
        // An empty object, or no routes, is Google's "no route".
        if (JsonNode.Parse(json)?["routes"]?.AsArray() is not [JsonObject route, ..])
            throw new DirectionsException(DirectionsError.NoRoute, "Google Maps found no route between the stops.");

        var shape = new List<double[]>();
        var legs = new List<RouteLeg>();
        var legNodes = route["legs"]?.AsArray() ?? [];

        for (var l = 0; l < legNodes.Count; l++)
        {
            var leg = legNodes[l];
            var maneuvers = new List<RouteManeuver>();

            foreach (var step in leg?["steps"]?.AsArray() ?? [])
            {
                var points = RouteShapes.DecodePolyline((string?)step?["polyline"]?["encodedPolyline"] ?? String.Empty, 5);
                var index = RouteShapes.Append(shape, points);
                var instruction = step?["navigationInstruction"];

                var kind = ToKind((string?)instruction?["maneuver"]);
                if (maneuvers.Count == 0 && kind is ManeuverKind.Other or ManeuverKind.Continue)
                    kind = ManeuverKind.Depart;

                maneuvers.Add(new RouteManeuver(
                    kind,
                    (string?)instruction?["instructions"] ?? String.Empty,
                    null,
                    Math.Round((double?)step?["distanceMeters"] ?? 0, 1),
                    Math.Round(RouteShapes.Seconds((string?)step?["staticDuration"]), 1),
                    [],
                    index
                ));
            }

            var stop = l + 1 < stops.Count ? stops[l + 1] : null;
            var final = l == legNodes.Count - 1;
            maneuvers.Add(new RouteManeuver(
                ManeuverKind.Arrive,
                stop?.Name is { Length: > 0 } name
                    ? $"Arrive at {name}."
                    : final ? "Arrive at your destination." : $"Arrive at stop {l + 1}.",
                null,
                0,
                0,
                [],
                Math.Max(0, shape.Count - 1)
            ));

            legs.Add(new RouteLeg(
                Math.Round((double?)leg?["distanceMeters"] ?? 0, 1),
                Math.Round(RouteShapes.Seconds((string?)leg?["duration"]), 1),
                maneuvers
            ));
        }

        return new DirectionsRoute(
            DirectionsSource.Online,
            Math.Round((double?)route["distanceMeters"] ?? 0, 1),
            Math.Round(RouteShapes.Seconds((string?)route["duration"]), 1),
            shape,
            RouteShapes.Bounds(shape),
            legs
        );
    }

    /// <summary>Google's maneuvers folded into what a page draws an arrow for.</summary>
    internal static ManeuverKind ToKind(string? maneuver) => maneuver switch
    {
        "DEPART" => ManeuverKind.Depart,
        "STRAIGHT" or "NAME_CHANGE" => ManeuverKind.Continue,
        "TURN_SLIGHT_LEFT" => ManeuverKind.SlightLeft,
        "TURN_SHARP_LEFT" => ManeuverKind.SharpLeft,
        "TURN_LEFT" => ManeuverKind.Left,
        "TURN_SLIGHT_RIGHT" => ManeuverKind.SlightRight,
        "TURN_SHARP_RIGHT" => ManeuverKind.SharpRight,
        "TURN_RIGHT" => ManeuverKind.Right,
        "UTURN_LEFT" or "UTURN_RIGHT" => ManeuverKind.UTurn,
        "RAMP_LEFT" => ManeuverKind.RampLeft,
        "RAMP_RIGHT" => ManeuverKind.RampRight,
        "MERGE" => ManeuverKind.Merge,
        "FORK_LEFT" => ManeuverKind.KeepLeft,
        "FORK_RIGHT" => ManeuverKind.KeepRight,
        "FERRY" or "FERRY_TRAIN" => ManeuverKind.Ferry,
        "ROUNDABOUT_LEFT" or "ROUNDABOUT_RIGHT" => ManeuverKind.EnterRoundabout,
        _ => ManeuverKind.Other
    };
}

/// <summary>
/// Google's Geocoding API: addresses and places worldwide. The key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Geocoder = new GoogleMapsGeocoder(googleMapsKey));
/// </code>
/// <para>The key needs the Geocoding API enabled.</para>
/// </summary>
/// <param name="apiKey">A Google Maps Platform key. Never sent to the page.</param>
public sealed class GoogleMapsGeocoder(string apiKey) : IGeocoder
{
    public string ApiKey { get; } = String.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("A Google Maps API key is required.", nameof(apiKey)) : apiKey;

    /// <summary><c>https://maps.googleapis.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://maps.googleapis.com/");

    /// <summary>Prefers places in this region, as a ccTLD code such as <c>us</c> or <c>uk</c>. Everywhere alike when null.</summary>
    public string? Region { get; set; }

    public string Attribution => "© Google";

    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(GeocodeQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);

        var path = $"maps/api/geocode/json?address={Uri.EscapeDataString(query.Text)}&key={Uri.EscapeDataString(this.ApiKey)}";
        if (query.Language is { } language)
            path += "&language=" + Uri.EscapeDataString(language);
        if (!String.IsNullOrWhiteSpace(this.Region))
            path += "&region=" + Uri.EscapeDataString(this.Region);

        using var response = await http.GetAsync(new Uri(this.BaseAddress, path), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), query.Limit);
    }

    /// <summary>
    /// <c>{ status, results: [ { formatted_address, geometry: { location, viewport } } ] }</c>. A refused key or a spent quota
    /// is a status, not an HTTP error.
    /// </summary>
    internal static IReadOnlyList<GeocodedPlace> Parse(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

        switch (status)
        {
            case "OK":
                break;
            case "ZERO_RESULTS":
                return [];
            case "OVER_QUERY_LIMIT" or "UNKNOWN_ERROR":
                throw new HttpRequestException($"Google's geocoder answered {status}.");
            default:
                var detail = root.TryGetProperty("error_message", out var e) ? e.GetString() : null;
                throw new InvalidOperationException($"Google's geocoder answered {status ?? "nothing"}{(detail is null ? "" : ": " + detail)}");
        }

        var places = new List<GeocodedPlace>();
        foreach (var result in root.GetProperty("results").EnumerateArray())
        {
            if (places.Count == limit)
                break;

            var address = result.TryGetProperty("formatted_address", out var f) ? f.GetString() ?? String.Empty : String.Empty;
            var geometry = result.GetProperty("geometry");
            var location = geometry.GetProperty("location");

            double[]? bounds = null;
            if (geometry.TryGetProperty("viewport", out var viewport))
            {
                var ne = viewport.GetProperty("northeast");
                var sw = viewport.GetProperty("southwest");
                bounds = [sw.GetProperty("lng").GetDouble(), sw.GetProperty("lat").GetDouble(), ne.GetProperty("lng").GetDouble(), ne.GetProperty("lat").GetDouble()];
            }

            places.Add(new GeocodedPlace(
                address.Split(',', 2)[0].Trim(),
                address,
                location.GetProperty("lat").GetDouble(),
                location.GetProperty("lng").GetDouble(),
                bounds
            ));
        }

        return places;
    }
}
