using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Gps;
using Shiny.AppDeviceBridge.Gps.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Native = Shiny.Locations;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The geocoding bridge over a fake Shiny.Gps geocoder: 501 without one or with one that reports itself unsupported, positions
/// checked before they reach it, its placemarks mapped to the contract, and its failures answered as 503. MapKit and
/// Android's Geocoder themselves are Shiny.Gps' and need a device; its Nominatim fallback needs the network.
/// </summary>
public class GeocodingBridgeTests
{
    [Fact]
    public async Task Answers_501_without_a_geocoder()
    {
        await using var fixture = await GeocodingFixture.StartAsync(null);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ReverseGeocodeAsync(43.6532, -79.3832))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "geocoding").IsSupported);
    }

    [Fact]
    public async Task Answers_501_when_the_geocoder_reports_itself_unsupported()
    {
        // An app's own IGeocoder that cannot answer here (Shiny.Gps' own fall back to Nominatim instead).
        var geocoder = new FakeGeocoder { IsSupported = false };
        await using var fixture = await GeocodingFixture.StartAsync(geocoder);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ReverseGeocodeAsync(43.6532, -79.3832))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "geocoding").IsSupported);
        Assert.Null(geocoder.Asked);
    }

    [Fact]
    public async Task Reverse_geocodes_a_position_into_placemarks_most_relevant_first()
    {
        var geocoder = new FakeGeocoder
        {
            Placemarks =
            [
                new Native.Placemark(
                    new Position(43.6425662, -79.3870568),
                    "CN Tower", "290", "Bremner Blvd", "Entertainment District", "Toronto", "Toronto Division", "ON",
                    "M5V 3L9", "CA", "Canada", "290 Bremner Blvd, Toronto, ON M5V 3L9, Canada"
                ),
                new Native.Placemark(new Position(43.6426, -79.3871), null, null, null, null, "Toronto", null, "ON", null, "CA", "Canada", null)
            ]
        };
        await using var fixture = await GeocodingFixture.StartAsync(geocoder);

        var placemarks = await fixture.Client.ReverseGeocodeAsync(43.6425662, -79.3870568);

        // The position crosses the query string exactly.
        Assert.Equal(new Position(43.6425662, -79.3870568), geocoder.Asked);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "geocoding").IsSupported);
        Assert.Equal(
            [
                new Gps.Client.Placemark(
                    43.6425662, -79.3870568,
                    "CN Tower", "290", "Bremner Blvd", "Entertainment District", "Toronto", "Toronto Division", "ON",
                    "M5V 3L9", "CA", "Canada", "290 Bremner Blvd, Toronto, ON M5V 3L9, Canada"
                ),
                new Gps.Client.Placemark(43.6426, -79.3871, Locality: "Toronto", AdministrativeArea: "ON", CountryCode: "CA", CountryName: "Canada")
            ],
            placemarks
        );
    }

    [Fact]
    public async Task Answers_an_empty_list_when_nothing_is_there()
    {
        await using var fixture = await GeocodingFixture.StartAsync(new FakeGeocoder());

        Assert.Empty(await fixture.Client.ReverseGeocodeAsync(0, -140));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?latitude=43.65")]
    [InlineData("?longitude=-79.38")]
    [InlineData("?latitude=90.5&longitude=0")]
    [InlineData("?latitude=0&longitude=-180.5")]
    [InlineData("?latitude=NaN&longitude=0")]
    [InlineData("?latitude=north&longitude=0")]
    public async Task Refuses_a_position_off_the_map_with_400(string query)
    {
        var geocoder = new FakeGeocoder();
        await using var fixture = await GeocodingFixture.StartAsync(geocoder);

        using var response = await fixture.WebView.GetAsync("/_bridge/geocoding/reverse" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(geocoder.Asked);
    }

    [Fact]
    public async Task Answers_503_when_the_platform_geocoder_fails()
    {
        // What Android's Geocoder does with no network.
        var geocoder = new FakeGeocoder { Failure = new IOException("grpc failed") };
        await using var fixture = await GeocodingFixture.StartAsync(geocoder);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ReverseGeocodeAsync(43.6532, -79.3832));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("geocoder_unavailable", failed.Code);
    }

    [Fact]
    public async Task AddGeocodingBridge_registers_the_bridge()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddGeocodingBridge()),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        // Plain net10.0 has no platform geocoder: Shiny.Gps falls back to OpenStreetMap's Nominatim, so it is supported.
        var bridge = Assert.Single(server.Bridges.OfType<GeocodingBridge>());
        Assert.True(bridge.IsSupported);
    }

    sealed class GeocodingFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public GeocodingBridgeClient Client { get; private set; } = null!;

        public static async Task<GeocodingFixture> StartAsync(IGeocoder? geocoder)
        {
            var fixture = new GeocodingFixture();
            var services = new ServiceCollection();
            if (geocoder is not null)
                services.AddSingleton(geocoder);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new GeocodingBridge(provider)],
                onStarted: client => fixture.WebView = client
            );

            fixture.Client = new GeocodingBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class FakeGeocoder : IGeocoder
    {
        public bool IsSupported { get; init; } = true;
        public IReadOnlyList<Native.Placemark> Placemarks { get; init; } = [];
        public Exception? Failure { get; init; }
        public Position? Asked { get; private set; }

        public Task<IReadOnlyList<Native.Placemark>> ReverseGeocode(Position position, CancellationToken cancelToken = default)
        {
            this.Asked = position;
            return this.Failure is { } failure
                ? Task.FromException<IReadOnlyList<Native.Placemark>>(failure)
                : Task.FromResult(this.Placemarks);
        }
    }
}
