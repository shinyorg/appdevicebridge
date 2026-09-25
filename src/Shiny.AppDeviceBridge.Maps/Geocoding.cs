using System.Globalization;
using System.Text.Json;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Turns an address or a place name into coordinates — <see cref="NominatimGeocoder"/>, or a class of the app's own over any
/// geocoding service. Set it on <see cref="DirectionsOptions.Geocoder"/>. The page asks
/// <c>/_bridge/directions/geocode?query=</c> and never learns the service's address or key.
/// </summary>
public interface IGeocoder
{
    /// <summary>The credit the service's terms require, as HTML. Sent to the page with every answer.</summary>
    string Attribution { get; }

    /// <summary>
    /// The places matching the query, best first, at most <see cref="GeocodeQuery.Limit"/>. Empty when nothing matches. Throw
    /// <see cref="HttpRequestException"/> when the service cannot be reached, and <see cref="JsonException"/> or
    /// <see cref="FormatException"/> for an answer that is not a list of places.
    /// </summary>
    /// <param name="http">The maps bridge's client, built from <see cref="MapsOptions.HttpMessageHandlerFactory"/>.</param>
    Task<IReadOnlyList<GeocodedPlace>> SearchAsync(GeocodeQuery query, HttpClient http, CancellationToken cancellationToken);
}

/// <param name="Text">What the user typed, trimmed and at most 200 characters.</param>
/// <param name="Limit">1 to 20.</param>
/// <param name="Language">An IETF language tag for the names, or null for the service's default.</param>
public sealed record GeocodeQuery(string Text, int Limit, string? Language);

/// <summary>
/// OpenStreetMap's Nominatim: addresses and places worldwide from the same data as the map.
/// <code>
/// bridge.AddMapsBridge(o => o.Directions.Geocoder = new NominatimGeocoder("MyApp/1.0 (support@example.com)"));
/// </code>
/// <para>
/// The public server at nominatim.openstreetmap.org is for light use: its policy asks for an application that identifies
/// itself, at most one request a second, and no autocomplete as the user types. The geocoder sends
/// <paramref name="userAgent"/> and spaces its requests by <see cref="MinimumInterval"/>; search when the user asks rather than
/// on every keystroke. Point <see cref="BaseAddress"/> at your own Nominatim, or a hosted one, for more.
/// </para>
/// </summary>
/// <param name="userAgent">Who is asking — the app's name and a way to reach you — as Nominatim's policy requires.</param>
public sealed class NominatimGeocoder(string userAgent) : IGeocoder
{
    readonly string userAgent = String.IsNullOrWhiteSpace(userAgent)
        ? throw new ArgumentException("Nominatim's usage policy requires a User-Agent naming the application.", nameof(userAgent))
        : userAgent;

    readonly SemaphoreSlim gate = new(1, 1);
    DateTimeOffset nextRequest;

    /// <summary>The Nominatim server. The public OpenStreetMap one by default.</summary>
    public Uri BaseAddress { get; set; } = new("https://nominatim.openstreetmap.org/");

    /// <summary>The least time between requests. One second by default, as the public server's policy asks.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Limits results to these countries, as ISO 3166-1 alpha-2 codes: <c>["us", "ca"]</c>. Everywhere when empty.</summary>
    public IReadOnlyList<string> Countries { get; set; } = [];

    public string Attribution => "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(GeocodeQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);

        var uri = String.Create(
            CultureInfo.InvariantCulture,
            $"search?format=jsonv2&limit={query.Limit}&q={Uri.EscapeDataString(query.Text)}"
        );
        if (this.Countries.Count > 0)
            uri += "&countrycodes=" + Uri.EscapeDataString(String.Join(',', this.Countries));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(this.BaseAddress, uri));
        request.Headers.UserAgent.ParseAdd(this.userAgent);
        if (query.Language is { } language)
            request.Headers.AcceptLanguage.ParseAdd(language);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = this.nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            try
            {
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Parse(json, query.Limit);
            }
            finally
            {
                this.nextRequest = DateTimeOffset.UtcNow + this.MinimumInterval;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Nominatim's <c>jsonv2</c> answer: an array of places, with coordinates and bounding boxes as strings.</summary>
    internal static IReadOnlyList<GeocodedPlace> Parse(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("Nominatim answered with something that is not a list of places.");

        var places = new List<GeocodedPlace>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (places.Count == limit)
                break;

            var address = item.TryGetProperty("display_name", out var display) ? display.GetString() ?? String.Empty : String.Empty;
            var name = item.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } named
                ? named
                : address.Split(',', 2)[0].Trim();

            // boundingbox is [south, north, west, east]; the contracts use [west, south, east, north].
            double[]? bounds = null;
            if (item.TryGetProperty("boundingbox", out var box) && box.ValueKind == JsonValueKind.Array && box.GetArrayLength() == 4)
            {
                var b = box.EnumerateArray().Select(Number).ToArray();
                if (b[0] != b[1] || b[2] != b[3])
                    bounds = [b[2], b[0], b[3], b[1]];
            }

            places.Add(new GeocodedPlace(name, address, Number(item.GetProperty("lat")), Number(item.GetProperty("lon")), bounds));
        }

        return places;
    }

    static double Number(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? value.GetDouble()
        : Double.Parse(value.GetString() ?? throw new FormatException("A coordinate is missing."), NumberStyles.Float, CultureInfo.InvariantCulture);
}
