using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>DirectionsOptions.Router</c> and <c>Geocoder</c> beyond Valhalla and Nominatim: Azure Maps and Google, translated to the
/// same contracts, with their keys kept native — and a router of the app's own.
/// </summary>
public class RouteProviderTests
{
    static readonly RouteStop Bellevue = new(47.608678, -122.201399, "Bellevue");
    static readonly RouteStop Downtown = new(47.612002, -122.20687);
    static readonly RouteStop Park = new(47.615076, -122.201669, "Downtown Park");

    // ------------------------------------------------------------------ Azure Maps

    // Two legs, one line each, as Route Directions 2025-01-01 answers with itinerary and routePath.
    const string AzureRoute = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.2014, 47.6087] },
              "properties": { "type": "ManeuverPoint", "distanceInMeters": 300, "durationInSeconds": 40,
                "instruction": { "text": "Head north on 108th Ave NE", "maneuverType": "DepartStart" },
                "routePathPoint": { "legIndex": 0, "pointIndex": 0 }, "steps": [ { "names": ["108th Ave NE"] } ] } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.2014, 47.6100] },
              "properties": { "type": "ManeuverPoint", "distanceInMeters": 200, "durationInSeconds": 30,
                "instruction": { "text": "Turn left onto NE 4th St", "maneuverType": "TurnLeftThenTurnRight" },
                "routePathPoint": { "legIndex": 0, "pointIndex": 1 }, "steps": [ { "names": ["NE 4th St"] }, { "names": ["NE 4th St"] } ] } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.2068, 47.6120] },
              "properties": { "type": "ManeuverPoint", "distanceInMeters": 0, "durationInSeconds": 0,
                "instruction": { "text": "Arrive at your stop", "maneuverType": "ArriveIntermediate" },
                "routePathPoint": { "legIndex": 0, "pointIndex": 2 } } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.2068, 47.6120] },
              "properties": { "type": "ManeuverPoint", "distanceInMeters": 500, "durationInSeconds": 60,
                "instruction": { "text": "Bear right", "maneuverType": "BearRight" },
                "routePathPoint": { "legIndex": 1, "pointIndex": 0 } } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.2016, 47.6151] },
              "properties": { "type": "ManeuverPoint", "distanceInMeters": 0, "durationInSeconds": 0,
                "instruction": { "text": "Arrive at Downtown Park", "maneuverType": "ArriveFinish" },
                "routePathPoint": { "legIndex": 1, "pointIndex": 1 } } },
            { "type": "Feature", "bbox": [-122.2068, 47.6087, -122.2014, 47.6151],
              "geometry": { "type": "MultiLineString", "coordinates": [
                [ [-122.2014, 47.6087], [-122.2014, 47.6100], [-122.2068, 47.6120] ],
                [ [-122.2068, 47.6120], [-122.2016, 47.6151] ] ] },
              "properties": { "type": "RoutePath", "distanceInMeters": 1000, "durationInSeconds": 130, "durationTrafficInSeconds": 150,
                "legs": [
                  { "distanceInMeters": 500, "durationInSeconds": 70, "durationTrafficInSeconds": 80, "routePathRange": { "legIndex": 0, "range": [0, 2] } },
                  { "distanceInMeters": 500, "durationInSeconds": 60, "routePathRange": { "legIndex": 1, "range": [0, 1] } }
                ] } }
          ]
        }
        """;

    [Fact]
    public async Task Azure_Maps_routes_with_the_key_the_page_never_sees()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new AzureMapsRouteProvider("azure-secret"));
        string? sent = null;
        fixture.Network.Stub("atlas.microsoft.com", async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return Network.Json(AzureRoute);
        });

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest(
            [Bellevue, Downtown, Park],
            TravelMode.Truck,
            Language: "en-GB",
            Avoid: new RouteAvoid(Tolls: true, Ferries: true)
        ));

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("/route/directions", request.RequestUri!.AbsolutePath);
        Assert.Equal("?api-version=2025-01-01", request.RequestUri.Query);
        Assert.Equal("azure-secret", request.Headers.GetValues("subscription-key").Single());
        Assert.Equal("en-GB", request.Headers.AcceptLanguage.ToString());

        var body = JsonNode.Parse(sent!)!;
        Assert.Equal("truck", (string?)body["travelMode"]);
        Assert.Equal("fastestWithTraffic", (string?)body["optimizeRoute"]);
        Assert.Equal(["tollRoads", "ferries"], body["avoid"]!.AsArray().Select(x => (string?)x));
        var features = body["features"]!.AsArray();
        Assert.Equal(3, features.Count);
        Assert.Equal(-122.20687, (double)features[1]!["geometry"]!["coordinates"]![0]!);
        Assert.Equal(1, (int)features[1]!["properties"]!["pointIndex"]!);

        Assert.Equal(DirectionsSource.Online, route.Source);
        Assert.Equal("© Microsoft Azure Maps, © TomTom", route.Attribution);
        Assert.Equal(1000, route.Distance);
        Assert.Equal(150, route.Duration);
        Assert.Equal(4, route.Shape.Count);
        Assert.Equal([-122.2068, 47.6087, -122.2014, 47.6151], route.Bounds);

        Assert.Collection(
            route.Legs[0].Maneuvers,
            m =>
            {
                Assert.Equal(ManeuverKind.Depart, m.Kind);
                Assert.Equal("Head north on 108th Ave NE", m.Instruction);
                Assert.Equal(["108th Ave NE"], m.StreetNames);
            },
            m =>
            {
                Assert.Equal(ManeuverKind.Left, m.Kind);
                Assert.Equal(["NE 4th St"], m.StreetNames);
                Assert.Equal(1, m.ShapeIndex);
            },
            m => Assert.Equal(ManeuverKind.Arrive, m.Kind)
        );
        Assert.Equal(80, route.Legs[0].Duration);

        // The second leg's line starts where the first ended; its maneuvers are placed on the joined shape.
        Assert.Equal([ManeuverKind.SlightRight, ManeuverKind.Arrive], route.Legs[1].Maneuvers.Select(x => x.Kind));
        Assert.Equal([2, 3], route.Legs[1].Maneuvers.Select(x => x.ShapeIndex));

        var info = await fixture.Directions.GetInfoAsync();
        Assert.Equal("Azure Maps", info.Router);
        Assert.Equal([TravelMode.Car, TravelMode.Walking, TravelMode.Truck], info.OnlineModes);
        Assert.DoesNotContain("azure-secret", await fixture.WebView.GetStringAsync("/_bridge/directions"));
    }

    [Fact]
    public void Azure_Maps_walks_without_traffic_or_avoidances()
    {
        var body = JsonNode.Parse(new AzureMapsRouteProvider("k").ToRequest(new DirectionsRequest([Bellevue, Park], TravelMode.Walking, Avoid: new RouteAvoid(Tolls: true))))!;

        Assert.Equal("walking", (string?)body["travelMode"]);
        Assert.Null(body["optimizeRoute"]);
        Assert.Null(body["avoid"]);
    }

    [Fact]
    public async Task A_mode_the_online_router_lacks_is_refused_without_asking_it()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new AzureMapsRouteProvider("azure-secret"));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park], TravelMode.Bicycle)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("mode_unsupported", refused.Code);
        Assert.Empty(fixture.Network.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": { "code": "BadRequest", "message": "Engine error while executing route request: NO_ROUTE_FOUND" } }""", HttpStatusCode.NotFound, "no_route")]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": { "code": "BadRequest", "message": "The provided coordinates are invalid." } }""", HttpStatusCode.BadRequest, "bad_request")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "error": { "code": "401", "message": "Access denied due to invalid subscription key." } }""", HttpStatusCode.BadGateway, "router_error")]
    [InlineData(HttpStatusCode.OK, """{ "type": "FeatureCollection", "features": [] }""", HttpStatusCode.NotFound, "no_route")]
    [InlineData(HttpStatusCode.OK, """{ "unexpected": true }""", HttpStatusCode.BadGateway, "router_error")]
    public async Task Azure_Maps_failures_are_told_apart(HttpStatusCode answered, string body, HttpStatusCode expected, string code)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new AzureMapsRouteProvider("azure-secret"));
        fixture.Network.Stub("atlas.microsoft.com", _ => Network.Json(body, answered));

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park])));

        Assert.Equal(expected, failed.StatusCode);
        Assert.Equal(code, failed.Code);
        Assert.DoesNotContain("azure-secret", failed.Message);
    }

    [Theory]
    [InlineData("TurnRightSharp", ManeuverKind.SharpRight)]
    [InlineData("TurnRightThenBearLeft", ManeuverKind.Right)]
    [InlineData("BearLeftThenTurnLeft", ManeuverKind.SlightLeft)]
    [InlineData("RampThenHighwayLeft", ManeuverKind.RampLeft)]
    [InlineData("MotorwayExitRight", ManeuverKind.ExitRight)]
    [InlineData("KeepToStayLeft", ManeuverKind.KeepLeft)]
    [InlineData("EnterThenExitRoundabout", ManeuverKind.EnterRoundabout)]
    [InlineData("TakeFerry", ManeuverKind.Ferry)]
    [InlineData("MergeMotorway", ManeuverKind.Merge)]
    [InlineData("Walk", ManeuverKind.Other)]
    public void Azure_Maps_maneuvers_become_arrows(string type, ManeuverKind kind)
        => Assert.Equal(kind, AzureMapsRouteProvider.ToKind(type));

    [Fact]
    public async Task Azure_Maps_geocodes_addresses_and_landmarks()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = new AzureMapsGeocoder("azure-secret") { View = "US" });
        fixture.Network.Stub("atlas.microsoft.com", _ => Network.Json("""
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-122.138681, 47.630358] },
                "bbox": [-122.14632, 47.62649, -122.13103, 47.63422],
                "properties": { "type": "Address", "address": { "formattedAddress": "15127 NE 24th St, Redmond, WA 98052", "addressLine": "15127 NE 24th St" } } },
              { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-73.9858, 40.7484] },
                "properties": { "type": "PointOfInterest", "address": { "formattedAddress": "Empire State Building, NY" } } }
            ] }
            """));

        var result = await fixture.Directions.GeocodeAsync("15127 NE 24th Street, Redmond", 5, "en-US");

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("?api-version=2025-01-01&top=5&query=15127%20NE%2024th%20Street%2C%20Redmond&view=US", request.RequestUri!.Query);
        Assert.Equal("azure-secret", request.Headers.GetValues("subscription-key").Single());
        Assert.Equal("en-US", request.Headers.AcceptLanguage.ToString());

        Assert.Collection(
            result.Places,
            p =>
            {
                Assert.Equal("15127 NE 24th St", p.Name);
                Assert.Equal("15127 NE 24th St, Redmond, WA 98052", p.Address);
                Assert.Equal(47.630358, p.Latitude);
                Assert.Equal(-122.138681, p.Longitude);
                Assert.Equal([-122.14632, 47.62649, -122.13103, 47.63422], p.Bounds!);
            },
            p =>
            {
                Assert.Equal("Empire State Building", p.Name);
                Assert.Null(p.Bounds);
            }
        );
        Assert.Contains("Azure Maps", result.Attribution);
    }

    // ------------------------------------------------------------------ Google

    /// <summary>Encodes <c>[longitude, latitude]</c> pairs as Google does, at five decimal places.</summary>
    static string Encode(params double[][] points)
    {
        var text = new StringBuilder();
        int lastLat = 0, lastLon = 0;
        foreach (var p in points)
        {
            int lat = (int)Math.Round(p[1] * 1e5), lon = (int)Math.Round(p[0] * 1e5);
            Write(lat - lastLat);
            Write(lon - lastLon);
            (lastLat, lastLon) = (lat, lon);
        }

        return text.ToString();

        void Write(int value)
        {
            var v = value < 0 ? ~(value << 1) : value << 1;
            while (v >= 0x20)
            {
                text.Append((char)((0x20 | (v & 0x1f)) + 63));
                v >>= 5;
            }

            text.Append((char)(v + 63));
        }
    }

    static string GoogleRoute() => new JsonObject
    {
        ["routes"] = new JsonArray(new JsonObject
        {
            ["distanceMeters"] = 900,
            ["duration"] = "125s",
            ["legs"] = new JsonArray(
                new JsonObject
                {
                    ["distanceMeters"] = 500,
                    ["duration"] = "70s",
                    ["steps"] = new JsonArray(
                        Step(300, "40s", null, "Head north on 108th Ave NE", [-122.2014, 47.6087], [-122.2014, 47.6100]),
                        Step(200, "30s", "TURN_LEFT", "Turn left onto NE 4th St", [-122.2014, 47.6100], [-122.2068, 47.6120]))
                },
                new JsonObject
                {
                    ["distanceMeters"] = 400,
                    ["duration"] = "55.5s",
                    ["steps"] = new JsonArray(Step(400, "55.5s", "ROUNDABOUT_RIGHT", "At the roundabout, take the 2nd exit", [-122.2068, 47.6120], [-122.2016, 47.6151]))
                })
        })
    }.ToJsonString();

    static JsonObject Step(int metres, string duration, string? maneuver, string instruction, params double[][] points)
    {
        var navigation = new JsonObject { ["instructions"] = instruction };
        if (maneuver is not null)
            navigation["maneuver"] = maneuver;

        return new JsonObject
        {
            ["distanceMeters"] = metres,
            ["staticDuration"] = duration,
            ["polyline"] = new JsonObject { ["encodedPolyline"] = Encode(points) },
            ["navigationInstruction"] = navigation
        };
    }

    [Fact]
    public async Task Google_routes_with_the_key_in_a_header_and_a_field_mask()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new GoogleMapsRouteProvider("google-secret"));
        string? sent = null;
        fixture.Network.Stub("routes.googleapis.com", async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return Network.Json(GoogleRoute());
        });

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest(
            [Bellevue, Downtown, Park],
            Units: Shiny.AppDeviceBridge.Maps.Client.DistanceUnits.Miles,
            Language: "en-US",
            Avoid: new RouteAvoid(Highways: true)
        ));

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("/directions/v2:computeRoutes", request.RequestUri!.AbsolutePath);
        Assert.Equal("google-secret", request.Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.Contains("routes.legs.steps.navigationInstruction", request.Headers.GetValues("X-Goog-FieldMask").Single());
        Assert.DoesNotContain("google-secret", request.RequestUri.ToString());

        var body = JsonNode.Parse(sent!)!;
        Assert.Equal("DRIVE", (string?)body["travelMode"]);
        Assert.Equal("TRAFFIC_UNAWARE", (string?)body["routingPreference"]);
        Assert.Equal("IMPERIAL", (string?)body["units"]);
        Assert.Equal("en-US", (string?)body["languageCode"]);
        Assert.True((bool)body["routeModifiers"]!["avoidHighways"]!);
        Assert.Equal(47.608678, (double)body["origin"]!["location"]!["latLng"]!["latitude"]!);
        Assert.Equal(-122.20687, (double)body["intermediates"]![0]!["location"]!["latLng"]!["longitude"]!);
        Assert.Equal(47.615076, (double)body["destination"]!["location"]!["latLng"]!["latitude"]!);

        Assert.Equal("Routes © Google", route.Attribution);
        Assert.Equal(900, route.Distance);
        Assert.Equal(125, route.Duration);
        Assert.Equal(4, route.Shape.Count);
        Assert.Equal([-122.2014, 47.6087], route.Shape[0]);
        Assert.Equal(55.5, route.Legs[1].Duration);

        Assert.Equal([ManeuverKind.Depart, ManeuverKind.Left, ManeuverKind.Arrive], route.Legs[0].Maneuvers.Select(x => x.Kind));
        Assert.Equal([0, 1, 2], route.Legs[0].Maneuvers.Select(x => x.ShapeIndex));
        Assert.Equal("Arrive at stop 1.", route.Legs[0].Maneuvers[^1].Instruction);
        Assert.Equal([ManeuverKind.EnterRoundabout, ManeuverKind.Arrive], route.Legs[1].Maneuvers.Select(x => x.Kind));
        Assert.Equal([2, 3], route.Legs[1].Maneuvers.Select(x => x.ShapeIndex));
        Assert.Equal("Arrive at Downtown Park.", route.Legs[1].Maneuvers[^1].Instruction);
        Assert.Equal(30, route.Legs[0].Maneuvers[1].Duration);

        Assert.Equal([TravelMode.Car, TravelMode.Bicycle, TravelMode.Walking], (await fixture.Directions.GetInfoAsync()).OnlineModes);
    }

    [Fact]
    public void Google_walks_and_cycles_without_a_routing_preference()
    {
        var walk = JsonNode.Parse(new GoogleMapsRouteProvider("k").ToRequest(new DirectionsRequest([Bellevue, Park], TravelMode.Walking, Avoid: new RouteAvoid(Ferries: true))))!;
        var cycle = JsonNode.Parse(new GoogleMapsRouteProvider("k") { UseTraffic = true }.ToRequest(new DirectionsRequest([Bellevue, Park], TravelMode.Bicycle)))!;
        var drive = JsonNode.Parse(new GoogleMapsRouteProvider("k") { UseTraffic = true }.ToRequest(new DirectionsRequest([Bellevue, Park])))!;

        Assert.Equal("WALK", (string?)walk["travelMode"]);
        Assert.Null(walk["routingPreference"]);
        Assert.Null(walk["routeModifiers"]);
        Assert.Null(walk["intermediates"]);
        Assert.Equal("BICYCLE", (string?)cycle["travelMode"]);
        Assert.Equal("TRAFFIC_AWARE", (string?)drive["routingPreference"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}", HttpStatusCode.NotFound, "no_route")]
    [InlineData(HttpStatusCode.OK, """{ "routes": [] }""", HttpStatusCode.NotFound, "no_route")]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": { "code": 400, "message": "Invalid request.", "status": "INVALID_ARGUMENT" } }""", HttpStatusCode.BadRequest, "bad_request")]
    [InlineData(HttpStatusCode.Forbidden, """{ "error": { "code": 403, "message": "API key not valid.", "status": "PERMISSION_DENIED" } }""", HttpStatusCode.BadGateway, "router_error")]
    public async Task Google_failures_are_told_apart(HttpStatusCode answered, string body, HttpStatusCode expected, string code)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new GoogleMapsRouteProvider("google-secret"));
        fixture.Network.Stub("routes.googleapis.com", _ => Network.Json(body, answered));

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park])));

        Assert.Equal(expected, failed.StatusCode);
        Assert.Equal(code, failed.Code);
    }

    [Fact]
    public async Task Google_has_no_truck_routes()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = new GoogleMapsRouteProvider("google-secret"));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park], TravelMode.Truck)));

        Assert.Equal("mode_unsupported", refused.Code);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Google_geocodes_with_the_key_kept_native()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = new GoogleMapsGeocoder("google-secret") { Region = "us" });
        fixture.Network.Stub("maps.googleapis.com", _ => Network.Json("""
            { "status": "OK", "results": [
              { "formatted_address": "1701 Wynkoop St, Denver, CO 80202, USA",
                "geometry": { "location": { "lat": 39.7532, "lng": -105.0001 },
                              "viewport": { "northeast": { "lat": 39.7545, "lng": -104.9987 }, "southwest": { "lat": 39.7518, "lng": -105.0014 } } } },
              { "formatted_address": "Denver, CO, USA", "geometry": { "location": { "lat": 39.7392, "lng": -104.9903 } } }
            ] }
            """));

        var result = await fixture.Directions.GeocodeAsync("1701 Wynkoop St", 1, "en");

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("/maps/api/geocode/json", request.RequestUri!.AbsolutePath);
        Assert.Equal("?address=1701%20Wynkoop%20St&key=google-secret&language=en&region=us", request.RequestUri.Query);

        var place = Assert.Single(result.Places);
        Assert.Equal("1701 Wynkoop St", place.Name);
        Assert.Equal("1701 Wynkoop St, Denver, CO 80202, USA", place.Address);
        Assert.Equal([-105.0014, 39.7518, -104.9987, 39.7545], place.Bounds!);
        Assert.Equal("© Google", result.Attribution);
    }

    [Theory]
    [InlineData("""{ "status": "ZERO_RESULTS", "results": [] }""", HttpStatusCode.OK, null)]
    [InlineData("""{ "status": "OVER_QUERY_LIMIT", "results": [] }""", HttpStatusCode.ServiceUnavailable, "geocoder_unavailable")]
    [InlineData("""{ "status": "REQUEST_DENIED", "error_message": "The provided API key is invalid.", "results": [] }""", HttpStatusCode.BadGateway, "geocoder_error")]
    public async Task Google_geocoder_statuses_are_told_apart(string body, HttpStatusCode expected, string? code)
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Geocoder = new GoogleMapsGeocoder("google-secret"));
        fixture.Network.Stub("maps.googleapis.com", _ => Network.Json(body));

        if (code is null)
        {
            Assert.Empty((await fixture.Directions.GeocodeAsync("nowhere")).Places);
            return;
        }

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.GeocodeAsync("somewhere"));
        Assert.Equal(expected, failed.StatusCode);
        Assert.Equal(code, failed.Code);
    }

    // ------------------------------------------------------------------ the app's own

    [Fact]
    public async Task A_router_of_the_apps_own_is_given_the_request_and_the_bridges_client()
    {
        var own = new OwnRouter();
        await using var fixture = await MapsFixture.StartAsync(o => o.Directions.Router = own);

        var route = await fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park], TravelMode.Walking, Source: DirectionsSource.Auto));

        Assert.Equal(DirectionsSource.Online, route.Source);
        Assert.Equal("© Own", route.Attribution);
        Assert.Equal(TravelMode.Walking, own.Asked!.Mode);
        Assert.Equal("Own", (await fixture.Directions.GetInfoAsync()).Router);
    }

    [Fact]
    public async Task A_router_that_takes_too_long_is_unavailable()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Directions.Router = new OwnRouter { Delay = TimeSpan.FromSeconds(10) };
            o.Directions.Timeout = TimeSpan.FromMilliseconds(100);
        });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Directions.RouteAsync(new DirectionsRequest([Bellevue, Park])));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("offline_unavailable", failed.Code);
    }

    sealed class OwnRouter : IRouteProvider
    {
        public DirectionsRequest? Asked { get; private set; }
        public TimeSpan Delay { get; init; }

        public string Name => "Own";
        public string Attribution => "© Own";
        public IReadOnlyCollection<TravelMode> Modes => [TravelMode.Car, TravelMode.Walking];

        public async Task<DirectionsRoute> RouteAsync(DirectionsRequest request, HttpClient http, CancellationToken cancellationToken)
        {
            Assert.NotNull(http);
            this.Asked = request;
            await Task.Delay(this.Delay, cancellationToken);

            // The bridge sets the source and the attribution, whatever the provider put there.
            return new DirectionsRoute(DirectionsSource.Device, 10, 5, [[-122.2, 47.6], [-122.21, 47.61]], [-122.21, 47.6, -122.2, 47.61], [], "wrong");
        }
    }
}
