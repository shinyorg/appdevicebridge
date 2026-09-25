using System.Diagnostics;
using System.Net;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>/_bridge/directions/geocode</c>: addresses into stops through the app's geocoder — Nominatim, checked against a real
/// answer for "Union Station, Denver" — with its address and key kept from the page.
/// </summary>
public class GeocodingTests
{
    static string RealSearch() => File.ReadAllText(PmTilesTests.Fixture("nominatim-search.json"));

    static NominatimGeocoder Nominatim() => new("Shiny.AppDeviceBridge tests (https://shinylib.net/appdevicebridge)")
    {
        BaseAddress = new Uri("https://nominatim.example.com/"),
        MinimumInterval = TimeSpan.Zero
    };

    [Fact]
    public async Task Finds_an_address_through_nominatim()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());
        fixture.Network.Stub("nominatim.example.com", _ => Network.Json(RealSearch()));

        var result = await fixture.Directions.GeocodeAsync("Union Station, Denver", 2, "en-US");

        var request = fixture.Network.Requests.Single();
        Assert.Equal("?format=jsonv2&limit=2&q=Union%20Station%2C%20Denver", request.RequestUri!.Query);
        Assert.Contains("Shiny.AppDeviceBridge tests", request.Headers.UserAgent.ToString());
        Assert.Equal("en-US", request.Headers.AcceptLanguage.ToString());

        Assert.Equal(2, result.Places.Count);
        var station = result.Places[0];
        Assert.Equal("Union Station", station.Name);
        Assert.StartsWith("Union Station, 1701, Wynkoop Street", station.Address);
        Assert.Equal(39.7532277, station.Latitude, 7);
        Assert.Equal(-105.0000944, station.Longitude, 7);

        // Nominatim's [south, north, west, east], turned into the contracts' [west, south, east, north].
        Assert.Equal([-105.0010122, 39.7526608, -104.9995423, 39.7538007], station.Bounds!);
        Assert.Contains("OpenStreetMap", result.Attribution);

        Assert.True((await fixture.Directions.GetInfoAsync()).Geocoding);
        Assert.DoesNotContain("nominatim.example.com", await fixture.WebView.GetStringAsync("/_bridge/directions/geocode?query=Union%20Station"));
    }

    [Fact]
    public void Names_a_place_from_its_address_when_nominatim_gives_none()
    {
        var places = NominatimGeocoder.Parse(
            """[{ "lat": "39.75", "lon": "-105.0", "name": "", "display_name": "1701, Wynkoop Street, Denver", "boundingbox": ["39.75", "39.75", "-105.0", "-105.0"] }]""",
            5
        );

        var place = Assert.Single(places);
        Assert.Equal("1701", place.Name);
        Assert.Null(place.Bounds);
        Assert.Empty(NominatimGeocoder.Parse("[]", 5));
        Assert.Throws<FormatException>(() => NominatimGeocoder.Parse("""{ "error": "nope" }""", 5));
    }

    [Fact]
    public async Task Keeps_to_nominatims_one_request_a_second()
    {
        var geocoder = Nominatim();
        geocoder.MinimumInterval = TimeSpan.FromMilliseconds(300);
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = geocoder);
        fixture.Network.Stub("nominatim.example.com", _ => Network.Json("[]"));

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(fixture.Directions.GeocodeAsync("Denver"), fixture.Directions.GeocodeAsync("Boulder"));

        Assert.Equal(2, fixture.Network.Requests.Count);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(280), $"Two searches took {clock.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Without_a_geocoder_it_is_not_supported()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.GeocodeAsync("Denver"));

        Assert.True(refused.IsNotSupported);
        Assert.False((await fixture.Directions.GetInfoAsync()).Geocoding);
    }

    [Fact]
    public async Task A_geocoder_alone_finds_places_but_does_not_route()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());
        fixture.Network.Stub("nominatim.example.com", _ => Network.Json(RealSearch()));

        Assert.NotEmpty((await fixture.Directions.GeocodeAsync("Union Station")).Places);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "directions").IsSupported);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([new(39.7, -104.9), new(40, -105)])));
        Assert.True(refused.IsNotSupported);
    }

    [Fact]
    public async Task Uses_a_geocoder_swapped_while_the_app_runs()
    {
        var options = (MapsOptions?)null;
        await using var fixture = await MapsFixture.StartAsync(o => options = o);

        options!.Directions.Geocoder = new FixedGeocoder(new GeocodedPlace("Here", "Right here", 1, 2));

        Assert.Equal("Here", Assert.Single((await fixture.Directions.GeocodeAsync("anything")).Places).Name);
    }

    [Theory]
    [InlineData("query=")]
    [InlineData("query=%20%20")]
    [InlineData("limit=5")]
    [InlineData("query=Denver&limit=0")]
    [InlineData("query=Denver&limit=21")]
    [InlineData("query=Denver&limit=five")]
    [InlineData("query=Denver&language=en%22%2C%22x")]
    public async Task Refuses_a_search_it_cannot_make(string query)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());

        using var response = await fixture.WebView.GetAsync($"/_bridge/directions/geocode?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Refuses_a_query_longer_than_200_characters()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.GeocodeAsync(new string('a', 201)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Offline_the_geocoder_is_unavailable()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());
        fixture.Network.Offline = true;

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.GeocodeAsync("Denver"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("geocoder_unavailable", refused.Code);
    }

    [Theory]
    [InlineData("<html>busy</html>", HttpStatusCode.OK, HttpStatusCode.BadGateway)]
    [InlineData("""{ "error": "Bad request" }""", HttpStatusCode.OK, HttpStatusCode.BadGateway)]
    [InlineData("rate limited", HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable)]
    public async Task A_geocoder_that_answers_badly_is_an_error(string body, HttpStatusCode answered, HttpStatusCode expected)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = Nominatim());
        fixture.Network.Stub("nominatim.example.com", _ => Network.Json(body, answered));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.GeocodeAsync("Denver"));

        Assert.Equal(expected, refused.StatusCode);
    }

    sealed class FixedGeocoder(params GeocodedPlace[] places) : IGeocoder
    {
        public string Attribution => "fixed";

        public Task<IReadOnlyList<GeocodedPlace>> SearchAsync(GeocodeQuery query, HttpClient http, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GeocodedPlace>>(places);
    }
}
