using System.Globalization;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Computes routes online — <see cref="ValhallaRouteProvider"/>, <see cref="AzureMapsRouteProvider"/>,
/// <see cref="GoogleMapsRouteProvider"/>, or a class of the app's own over any routing service. Set it on
/// <see cref="DirectionsOptions.Router"/>. The page posts to <c>/_bridge/directions/route</c> and never learns the service's
/// address or key; routes inside a downloaded road network are computed on the device instead, whatever the provider.
/// </summary>
public interface IRouteProvider
{
    /// <summary>Who computes the routes, for showing the user: <c>Valhalla</c>, <c>Azure Maps</c>, <c>Google Maps</c>.</summary>
    string Name { get; }

    /// <summary>The credit the service's terms require, as HTML. Sent to the page with every route.</summary>
    string Attribution { get; }

    /// <summary>The travel modes the service routes. A request for another is refused before the provider is asked.</summary>
    IReadOnlyCollection<TravelMode> Modes { get; }

    /// <summary>
    /// Computes the route: 2 to 20 stops on the map, in a mode from <see cref="Modes"/>. Distances are metres and durations
    /// seconds whatever <see cref="DirectionsRequest.Units"/> says, which only shapes the instructions; the bridge sets the
    /// route's source and attribution. Throw <see cref="DirectionsException"/> for a failure the service describes,
    /// <see cref="HttpRequestException"/> when it cannot be reached, and <see cref="System.Text.Json.JsonException"/> or
    /// <see cref="FormatException"/> for an answer that is not a route. The bridge applies <see cref="DirectionsOptions.Timeout"/>.
    /// </summary>
    /// <param name="http">The maps bridge's client, built from <see cref="MapsOptions.HttpMessageHandlerFactory"/>.</param>
    Task<DirectionsRoute> RouteAsync(DirectionsRequest request, HttpClient http, CancellationToken cancellationToken);
}

/// <summary>What went wrong computing a route, and so what the page is told.</summary>
public enum DirectionsError
{
    /// <summary>No route connects the stops, or a stop is nowhere near a road: 404 <c>no_route</c>.</summary>
    NoRoute,

    /// <summary>The service refused the request as it was asked: 400 <c>bad_request</c>.</summary>
    InvalidRequest,

    /// <summary>The service failed, or refused the app's key: 502 <c>router_error</c>.</summary>
    RouterFailed
}

/// <summary>A failure a router described. <see cref="Error"/> decides what the page is told; the message goes with it.</summary>
public class DirectionsException(DirectionsError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public DirectionsError Error { get; } = error;
}

/// <summary>
/// An online Valhalla server — your own, or a hosted one such as Stadia Maps — over OpenStreetMap's roads. The same engine and
/// translation as on-device directions, so a route reads the same either way.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Router = new ValhallaRouteProvider(new Uri("https://valhalla.example.com/route")));
/// </code>
/// </summary>
/// <param name="routeUrl"><c>https://valhalla.example.com/route</c> for your own, or <c>https://api.stadiamaps.com/route/v1</c> for Stadia Maps.</param>
public sealed class ValhallaRouteProvider(Uri routeUrl) : IRouteProvider
{
    public Uri RouteUrl { get; } = routeUrl ?? throw new ArgumentNullException(nameof(routeUrl));

    /// <summary>Sent as the <c>api_key</c> query parameter, which is how hosted Valhalla services take it. Never reaches the page.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Adds headers to every route request.</summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    public string Name => "Valhalla";

    public string Attribution { get; set; } = "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    public IReadOnlyCollection<TravelMode> Modes { get; } = [TravelMode.Car, TravelMode.Bicycle, TravelMode.Walking, TravelMode.Truck];

    public async Task<DirectionsRoute> RouteAsync(DirectionsRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        var url = this.RouteUrl;
        if (!String.IsNullOrWhiteSpace(this.ApiKey))
            url = new UriBuilder(url) { Query = AppendQuery(url.Query, "api_key", this.ApiKey) }.Uri;

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(ValhallaTranslation.ToRequest(request), System.Text.Encoding.UTF8, "application/json")
        };
        this.ConfigureRequest?.Invoke(message);

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw (Exception?)ValhallaException.TryParse(body, (int)response.StatusCode)
                  ?? new HttpRequestException($"The online router answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return ValhallaTranslation.FromResponse(body, DirectionsSource.Online);
    }

    static string AppendQuery(string query, string name, string value)
    {
        var pair = $"{name}={Uri.EscapeDataString(value)}";
        return query.Length <= 1 ? pair : $"{query[1..]}&{pair}";
    }
}

/// <summary>What every route translation shares: polylines, bounds, durations.</summary>
static class RouteShapes
{
    /// <summary>
    /// Google's encoded polyline — at five decimal places as Google writes them, six as Valhalla does. Returns
    /// <c>[longitude, latitude]</c> pairs.
    /// </summary>
    public static List<double[]> DecodePolyline(string encoded, int precision)
    {
        var factor = Math.Pow(10, precision);
        var points = new List<double[]>();
        int index = 0, lat = 0, lon = 0;

        while (index < encoded.Length)
        {
            lat += Next(encoded, ref index);
            lon += Next(encoded, ref index);
            points.Add([lon / factor, lat / factor]);
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

    /// <summary>Adds a leg's or a step's points to the route's, keeping a point shared with the end of the last once. Returns where they start.</summary>
    public static int Append(List<double[]> shape, IReadOnlyList<double[]> points)
    {
        if (shape.Count == 0)
        {
            shape.AddRange(points);
            return 0;
        }

        var last = shape[^1];
        if (points.Count > 0 && points[0][0] == last[0] && points[0][1] == last[1])
        {
            shape.AddRange(points.Skip(1));
            return shape.Count - points.Count;
        }

        var offset = shape.Count;
        shape.AddRange(points);
        return offset;
    }

    public static double[] Bounds(IReadOnlyList<double[]> shape)
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

    /// <summary>A protobuf JSON duration — <c>"123s"</c>, <c>"1.5s"</c> — in seconds.</summary>
    public static double Seconds(string? duration)
        => duration is { Length: > 1 } d && d[^1] == 's' && Double.TryParse(d[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
}
