using System.Net;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Blazor;
using Shiny.AppDeviceBridge.Maps.Client;
using Shiny.AppDeviceBridge.Maps.Valhalla;
using DistanceUnits = Shiny.AppDeviceBridge.Maps.Client.DistanceUnits;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>/_bridge/directions</c>: the translation to and from Valhalla — checked against a real response from Valhalla run
/// over Colorado — and which router answers: the device inside a downloaded road network, online otherwise.
/// </summary>
public class DirectionsBridgeTests
{
    static readonly double[] Colorado = [-109.06, 36.99, -102.04, 41.0];
    static readonly RouteStop Denver = new(39.7392, -104.9903, "Denver");
    static readonly RouteStop Boulder = new(40.0150, -105.2705);
    static readonly RouteStop Chicago = new(41.8781, -87.6298);

    static string RealRoute() => File.ReadAllText(PmTilesTests.Fixture("valhalla-route.json"));

    [Fact]
    public void Translates_a_request_into_valhallas_route_api()
    {
        var json = JsonNode.Parse(ValhallaTranslation.ToRequest(new DirectionsRequest(
            [Denver, Boulder],
            TravelMode.Bicycle,
            DistanceUnits.Miles,
            "fr-CA",
            Avoid: new RouteAvoid(Tolls: true, Ferries: true)
        )))!;

        Assert.Equal("bicycle", (string?)json["costing"]);
        Assert.Equal("miles", (string?)json["units"]);
        Assert.Equal("fr-CA", (string?)json["language"]);
        Assert.Equal("polyline6", (string?)json["shape_format"]);
        Assert.Equal(0, (int)json["costing_options"]!["bicycle"]!["use_tolls"]!);
        Assert.Equal(0, (int)json["costing_options"]!["bicycle"]!["use_ferry"]!);
        Assert.Null(json["costing_options"]!["bicycle"]!["use_highways"]);
        Assert.Equal(39.7392, (double)json["locations"]![0]!["lat"]!);
        Assert.Equal(-104.9903, (double)json["locations"]![0]!["lon"]!);
        Assert.Equal("Denver", (string?)json["locations"]![0]!["name"]);
        Assert.Null(json["locations"]![1]!["name"]);
    }

    [Fact]
    public void Reads_a_real_valhalla_route()
    {
        // Denver → Westminster → Boulder, computed by Valhalla over Colorado's OSM extract.
        var route = ValhallaTranslation.FromResponse(RealRoute(), DirectionsSource.Device);

        Assert.Equal(DirectionsSource.Device, route.Source);
        Assert.Equal(49316, route.Distance, 0);   // Valhalla said 49.316 km
        Assert.Equal(2169.5, route.Duration, 1);
        Assert.Equal(2, route.Legs.Count);
        Assert.Equal(13, route.Legs[0].Maneuvers.Count);
        Assert.Equal(8, route.Legs[1].Maneuvers.Count);
        Assert.Equal(route.Distance, route.Legs.Sum(l => l.Distance), 0);

        var first = route.Legs[0].Maneuvers[0];
        Assert.Equal(ManeuverKind.Depart, first.Kind);
        Assert.Equal("Drive east on West 14th Avenue Parkway.", first.Instruction);
        Assert.Equal(0, first.ShapeIndex);
        Assert.Equal(ManeuverKind.Arrive, route.Legs[^1].Maneuvers[^1].Kind);

        // One line through both legs, from the start to the end — each snapped to the nearest road, so within a few
        // hundred metres of the stop rather than on it.
        Assert.InRange(route.Shape[0][0], -104.995, -104.985);
        Assert.InRange(route.Shape[0][1], 39.734, 39.744);
        Assert.InRange(route.Shape[^1][0], -105.275, -105.265);
        Assert.InRange(route.Shape[^1][1], 40.010, 40.020);
        Assert.Equal(route.Shape.Count - 1, route.Legs[^1].Maneuvers[^1].ShapeIndex);
        Assert.All(route.Legs[1].Maneuvers, m => Assert.InRange(m.ShapeIndex, route.Legs[0].Maneuvers[^1].ShapeIndex, route.Shape.Count - 1));

        Assert.Equal(route.Shape.Min(p => p[0]), route.Bounds[0]);
        Assert.Equal(route.Shape.Max(p => p[1]), route.Bounds[3]);
    }

    [Theory]
    [InlineData("""{ "error_code": 442, "error": "No path could be found for input", "status_code": 400 }""", 442, true)]
    [InlineData("""{ "code": 171, "message": "No suitable edges near location" }""", 171, true)]
    [InlineData("""{ "code": -1, "message": "the actor is closed" }""", -1, false)]
    public void Reads_valhallas_errors_from_the_service_and_from_valhalla_mobile(string json, int code, bool noRoute)
    {
        var error = ValhallaException.TryParse(json, 400);

        Assert.NotNull(error);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(noRoute, error.IsNoRoute);
        Assert.Null(ValhallaException.TryParse(RealRoute(), 200));
        Assert.Null(ValhallaException.TryParse("not json", 500));
    }

    [Fact]
    public async Task Routes_online_with_the_key_the_page_never_sees()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Directions.OnlineRouteUrl = new Uri("https://api.valhalla.example.com/route/v1?format=json");
            o.Directions.ApiKey = "secret key";
        });
        string? sent = null;
        fixture.Network.Stub("api.valhalla.example.com", async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return Network.Json(RealRoute());
        });

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder]));

        Assert.Equal(DirectionsSource.Online, route.Source);
        Assert.Equal(2, route.Legs.Count);
        Assert.Equal("?format=json&api_key=secret%20key", fixture.Network.Requests.Single().RequestUri!.Query);
        Assert.Equal("auto", (string?)JsonNode.Parse(sent!)!["costing"]);

        var info = await fixture.Directions.GetInfoAsync();
        Assert.True(info.Online);
        Assert.False(info.OnDevice);
        Assert.DoesNotContain("secret", await fixture.WebView.GetStringAsync("/_bridge/directions"));
    }

    [Fact]
    public async Task Routes_on_the_device_inside_a_downloaded_road_network()
    {
        var device = new FakeRouterFactory(RealRoute());
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"), device);
        fixture.InstallDirectly("colorado", Colorado, directions: true);

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder]));

        Assert.Equal(DirectionsSource.Device, route.Source);
        Assert.Equal(fixture.Service.Storage.DirectionsFile("colorado"), Assert.Single(device.Opened));
        Assert.Empty(fixture.Network.Requests);

        var info = await fixture.Directions.GetInfoAsync();
        Assert.True(info.OnDevice);
        Assert.Equal(["colorado"], info.OfflineRegions);
    }

    [Fact]
    public async Task Goes_online_when_a_stop_is_outside_every_downloaded_network()
    {
        var device = new FakeRouterFactory(RealRoute());
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"), device);
        fixture.InstallDirectly("colorado", Colorado, directions: true);
        fixture.Network.Stub("valhalla.example.com", _ => Network.Json(RealRoute()));

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Chicago]));

        Assert.Equal(DirectionsSource.Online, route.Source);
        Assert.Empty(device.Opened);
    }

    [Fact]
    public async Task Goes_online_when_the_device_cannot_connect_the_stops()
    {
        var device = new FakeRouterFactory("""{ "code": 442, "message": "No path could be found for input" }""");
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"), device);
        fixture.InstallDirectly("colorado", Colorado, directions: true);
        fixture.Network.Stub("valhalla.example.com", _ => Network.Json(RealRoute()));

        Assert.Equal(DirectionsSource.Online, (await fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder]))).Source);

        // Asked for the device only, the device's answer stands.
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder], Source: DirectionsSource.Device)));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("no_route", refused.Code);
    }

    [Fact]
    public async Task Device_only_outside_every_downloaded_network_is_unavailable()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"), new FakeRouterFactory(RealRoute()));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder], Source: DirectionsSource.Device)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("offline_unavailable", refused.Code);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Offline_with_no_downloaded_network_is_unavailable()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"));
        fixture.Network.Offline = true;

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder])));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("offline_unavailable", refused.Code);
    }

    [Fact]
    public async Task Valhallas_no_route_is_404_and_its_other_errors_400()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"));
        var answer = """{ "error_code": 442, "error": "No path could be found for input", "status_code": 400 }""";
        fixture.Network.Stub("valhalla.example.com", _ => Network.Json(answer, HttpStatusCode.BadRequest));

        var noRoute = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder])));
        Assert.Equal(HttpStatusCode.NotFound, noRoute.StatusCode);
        Assert.Equal("No path could be found for input", noRoute.Message);

        answer = """{ "error_code": 154, "error": "Path distance exceeds the max distance limit", "status_code": 400 }""";
        var tooFar = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder])));
        Assert.Equal(HttpStatusCode.BadRequest, tooFar.StatusCode);
    }

    [Fact]
    public async Task Without_any_router_directions_are_not_supported()
    {
        await using var fixture = await MapsFixture.StartAsync();

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Denver, Boulder])));

        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "directions").IsSupported);
    }

    [Theory]
    [InlineData("""{ "stops": [ { "latitude": 39.7, "longitude": -104.9 } ] }""")]
    [InlineData("""{ "stops": [ { "latitude": 139.7, "longitude": -104.9 }, { "latitude": 40, "longitude": -105 } ] }""")]
    [InlineData("""{ "stops": [ { "latitude": 39.7, "longitude": -104.9 }, { "latitude": 40, "longitude": -105 } ], "language": "en\"}, {\"x" }""")]
    [InlineData("""{ "stops": [ { "latitude": 39.7, "longitude": -104.9 }, { "latitude": 40, "longitude": -105 } ], "mode": "Rocket" }""")]
    [InlineData("""not json""")]
    public async Task Refuses_a_request_it_cannot_route(string body)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.OnlineRouteUrl = new Uri("https://valhalla.example.com/route"));

        using var response = await fixture.WebView.PostAsync("/_bridge/directions/route", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public void A_route_becomes_a_line_in_the_maps_point_order()
    {
        var route = ValhallaTranslation.FromResponse(RealRoute(), DirectionsSource.Online);

        var shape = route.ToShape();

        Assert.Equal(MapShapeKind.Line, shape.Kind);
        Assert.Equal(route.Shape.Count, shape.Points.Count);
        Assert.Equal(new GeoPoint(route.Shape[0][1], route.Shape[0][0]), shape.Points[0]);
        Assert.NotNull(shape.CasingColor);
    }

    [Fact]
    public void The_on_device_config_points_valhalla_at_the_regions_extract()
    {
        // A path the config's JSON has to escape: a quote where the file system allows one, and on Windows, which does not, its
        // own backslashes.
        var directory = Path.Combine(Path.GetTempPath(), "appdevicebridge-maps", Guid.NewGuid().ToString("n"), OperatingSystem.IsWindows() ? "escaped" : "quote\"d");
        Directory.CreateDirectory(directory);
        var extract = Path.Combine(directory, "directions.tar");

        var config = JsonNode.Parse(File.ReadAllText(ValhallaConfig.WriteFor(extract)))!;

        Assert.Equal(extract, (string?)config["mjolnir"]!["tile_extract"]);
        Assert.Equal("", (string?)config["mjolnir"]!["tile_dir"]);
        Assert.Equal("", (string?)config["mjolnir"]!["traffic_extract"]);
        Assert.NotNull(config["thor"]);
        Assert.NotNull(config["odin"]);
    }

    sealed class FakeRouterFactory(string answer) : IOnDeviceRouterFactory
    {
        public List<string> Opened { get; } = [];

        public IValhallaRouter Open(string tileExtractPath)
        {
            this.Opened.Add(tileExtractPath);
            return new Router(answer);
        }

        sealed class Router(string answer) : IValhallaRouter
        {
            public Task<string> RouteAsync(string requestJson, CancellationToken cancellationToken)
                => ValhallaException.TryParse(answer, 400) is { } error ? Task.FromException<string>(error) : Task.FromResult(answer);

            public void Dispose() { }
        }
    }
}
