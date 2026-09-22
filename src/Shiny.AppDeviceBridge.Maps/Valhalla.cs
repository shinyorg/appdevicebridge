using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Answers a Valhalla <c>route</c> request. Online and on the device the request and the response are the same JSON,
/// so everything above this — the contract, source selection, errors — is shared.
/// </summary>
public interface IValhallaRouter : IDisposable
{
    /// <summary>Valhalla's response JSON. A failure Valhalla describes is thrown as <see cref="ValhallaException"/>.</summary>
    Task<string> RouteAsync(string requestJson, CancellationToken cancellationToken);
}

/// <summary>Opens on-device routers over downloaded road networks. Registered by Shiny.AppDeviceBridge.Maps.Valhalla where the platform has one.</summary>
public interface IOnDeviceRouterFactory
{
    /// <summary>A router over one region's Valhalla tile extract (<c>.tar</c>).</summary>
    IValhallaRouter Open(string tileExtractPath);
}

/// <summary>A failure Valhalla reported: its numeric error code and message.</summary>
public sealed class ValhallaException(int errorCode, string message, int httpStatus = 400) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;

    public int HttpStatus { get; } = httpStatus;

    /// <summary>Valhalla's codes for "these places are not connected": no path, or no road near a stop.</summary>
    public bool IsNoRoute => this.ErrorCode is 170 or 171 or 442 or 443;

    /// <summary>
    /// Reads a failure: the Valhalla service's body, <c>{ "error_code": 442, "error": "No path could be found for input" }</c>,
    /// or valhalla-mobile's envelope, <c>{ "code": 442, "message": "…" }</c> — with <c>-1</c> for a failure that is not
    /// Valhalla's own. Null for anything else, a route included.
    /// </summary>
    public static ValhallaException? TryParse(string json, int httpStatus)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o.ContainsKey("trip"))
                return null;

            if (o["error_code"] is { } code)
                return new ValhallaException((int)code, (string?)o["error"] ?? "Valhalla could not compute the route.", httpStatus);

            if (o["code"] is { } mobileCode && o["message"] is { } message)
            {
                var value = (int)mobileCode;
                return new ValhallaException(value, (string?)message ?? "Valhalla could not compute the route.", value < 0 ? 500 : 400);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }
}

/// <summary>The translation between the bridge's directions contract and Valhalla's route API.</summary>
static class ValhallaTranslation
{
    public static string ToRequest(DirectionsRequest request)
    {
        var costing = request.Mode switch
        {
            TravelMode.Bicycle => "bicycle",
            TravelMode.Walking => "pedestrian",
            TravelMode.Truck => "truck",
            _ => "auto"
        };

        var options = new JsonObject();
        if (request.Avoid is { } avoid)
        {
            // Valhalla takes preferences from 0 (avoid) to 1 (prefer); 0.5 is its default.
            if (avoid.Tolls)
                options["use_tolls"] = 0;
            if (avoid.Highways)
                options["use_highways"] = 0;
            if (avoid.Ferries)
                options["use_ferry"] = 0;
        }

        var locations = new JsonArray();
        foreach (var stop in request.Stops)
        {
            var location = new JsonObject { ["lat"] = stop.Latitude, ["lon"] = stop.Longitude };
            if (!String.IsNullOrWhiteSpace(stop.Name))
                location["name"] = stop.Name;
            locations.Add((JsonNode)location);
        }

        var body = new JsonObject
        {
            ["locations"] = locations,
            ["costing"] = costing,
            ["costing_options"] = new JsonObject { [costing] = options },
            ["units"] = request.Units == DistanceUnits.Miles ? "miles" : "kilometers",
            ["directions_type"] = "instructions",
            ["shape_format"] = "polyline6"
        };

        if (!String.IsNullOrWhiteSpace(request.Language))
            body["language"] = request.Language;

        return body.ToJsonString();
    }

    public static DirectionsRoute FromResponse(string json, DirectionsSource source)
    {
        var trip = JsonNode.Parse(json)?["trip"] as JsonObject
                   ?? throw new ValhallaException(0, "The router answered without a trip.", 502);

        var metres = (string?)trip["units"] == "miles" ? 1609.344 : 1000d;
        var shape = new List<double[]>();
        var legs = new List<RouteLeg>();

        foreach (var leg in trip["legs"]?.AsArray() ?? [])
        {
            // Each leg's shape starts where the last one ended; the shared point is kept once.
            var offset = shape.Count == 0 ? 0 : shape.Count - 1;
            var points = DecodePolyline6((string?)leg?["shape"] ?? String.Empty);
            shape.AddRange(offset == 0 ? points : points.Skip(1));

            var maneuvers = new List<RouteManeuver>();
            foreach (var m in leg?["maneuvers"]?.AsArray() ?? [])
            {
                if (m is not JsonObject maneuver)
                    continue;

                maneuvers.Add(new RouteManeuver(
                    ToKind((int?)maneuver["type"] ?? 0),
                    (string?)maneuver["instruction"] ?? String.Empty,
                    (string?)maneuver["verbal_pre_transition_instruction"],
                    Math.Round(((double?)maneuver["length"] ?? 0) * metres, 1),
                    Math.Round((double?)maneuver["time"] ?? 0, 1),
                    [.. (maneuver["street_names"]?.AsArray() ?? []).Select(x => (string?)x).OfType<string>()],
                    offset + ((int?)maneuver["begin_shape_index"] ?? 0),
                    (int?)maneuver["roundabout_exit_count"]
                ));
            }

            legs.Add(new RouteLeg(
                Math.Round(((double?)leg?["summary"]?["length"] ?? 0) * metres, 1),
                Math.Round((double?)leg?["summary"]?["time"] ?? 0, 1),
                maneuvers
            ));
        }

        var summary = trip["summary"];
        return new DirectionsRoute(
            source,
            Math.Round(((double?)summary?["length"] ?? 0) * metres, 1),
            Math.Round((double?)summary?["time"] ?? 0, 1),
            shape,
            Bounds(shape),
            legs
        );
    }

    /// <summary>Valhalla's maneuver types (see its <c>DirectionsLeg_Maneuver_Type</c>) folded into what a page draws an arrow for.</summary>
    static ManeuverKind ToKind(int type) => type switch
    {
        1 or 2 or 3 => ManeuverKind.Depart,
        4 or 5 or 6 => ManeuverKind.Arrive,
        7 or 8 => ManeuverKind.Continue,
        9 => ManeuverKind.SlightRight,
        10 => ManeuverKind.Right,
        11 => ManeuverKind.SharpRight,
        12 or 13 => ManeuverKind.UTurn,
        14 => ManeuverKind.SharpLeft,
        15 => ManeuverKind.Left,
        16 => ManeuverKind.SlightLeft,
        17 => ManeuverKind.RampStraight,
        18 => ManeuverKind.RampRight,
        19 => ManeuverKind.RampLeft,
        20 => ManeuverKind.ExitRight,
        21 => ManeuverKind.ExitLeft,
        22 => ManeuverKind.KeepStraight,
        23 => ManeuverKind.KeepRight,
        24 => ManeuverKind.KeepLeft,
        25 or 37 or 38 => ManeuverKind.Merge,
        26 => ManeuverKind.EnterRoundabout,
        27 => ManeuverKind.ExitRoundabout,
        28 or 29 => ManeuverKind.Ferry,
        _ => ManeuverKind.Other
    };

    /// <summary>Google's encoded polyline at six decimal places, as Valhalla writes shapes. Returns <c>[longitude, latitude]</c> pairs.</summary>
    internal static List<double[]> DecodePolyline6(string encoded)
    {
        var points = new List<double[]>();
        int index = 0, lat = 0, lon = 0;

        while (index < encoded.Length)
        {
            lat += Next(encoded, ref index);
            lon += Next(encoded, ref index);
            points.Add([lon / 1e6, lat / 1e6]);
        }

        return points;

        static int Next(string s, ref int i)
        {
            int result = 0, shift = 0, b;
            do
            {
                if (i >= s.Length)
                    throw new FormatException("The route's shape is truncated.");

                b = s[i++] - 63;
                result |= (b & 0x1F) << shift;
                shift += 5;
            } while (b >= 0x20);

            return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        }
    }

    static double[] Bounds(List<double[]> shape)
    {
        if (shape.Count == 0)
            return [0, 0, 0, 0];

        double west = double.MaxValue, south = double.MaxValue, east = double.MinValue, north = double.MinValue;
        foreach (var p in shape)
        {
            west = Math.Min(west, p[0]);
            east = Math.Max(east, p[0]);
            south = Math.Min(south, p[1]);
            north = Math.Max(north, p[1]);
        }

        return [west, south, east, north];
    }
}

/// <summary>An online Valhalla server: your own, or a hosted one such as Stadia Maps.</summary>
sealed class ValhallaHttpRouter(HttpClient http, DirectionsOptions options) : IValhallaRouter
{
    public async Task<string> RouteAsync(string requestJson, CancellationToken cancellationToken)
    {
        var url = options.OnlineRouteUrl ?? throw new InvalidOperationException("No online route URL is configured.");

        if (!String.IsNullOrWhiteSpace(options.ApiKey))
            url = new UriBuilder(url) { Query = AppendQuery(url.Query, "api_key", options.ApiKey) }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
        };
        options.ConfigureRequest?.Invoke(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return body;

        throw (Exception?)ValhallaException.TryParse(body, (int)response.StatusCode)
              ?? new HttpRequestException($"The online router answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    static string AppendQuery(string query, string name, string value)
    {
        var pair = $"{name}={Uri.EscapeDataString(value)}";
        return query.Length <= 1 ? pair : $"{query[1..]}&{pair}";
    }

    public void Dispose() { }
}
