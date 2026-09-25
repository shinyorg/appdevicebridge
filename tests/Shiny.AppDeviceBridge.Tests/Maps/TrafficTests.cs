using System.IO.Compression;
using System.Net;
using System.Web;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary><c>/_bridge/maps/traffic</c>: any <see cref="ITrafficProvider"/> through the bridge, and TomTom's in particular.</summary>
public class TrafficTests
{
    [Fact]
    public async Task Without_a_provider_there_is_no_layer_and_tiles_are_501()
    {
        await using var fixture = await MapsFixture.StartAsync();

        Assert.Null((await fixture.Maps.GetInfoAsync()).Traffic);
        Assert.Equal(HttpStatusCode.NotImplemented, (await fixture.WebView.GetAsync("/_bridge/maps/traffic/10/200/300")).StatusCode);
    }

    [Fact]
    public async Task TomTom_is_described_to_the_page_without_its_key()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Traffic = new TomTomTrafficProvider("tt-secret"));

        var traffic = (await fixture.Maps.GetInfoAsync()).Traffic!;

        Assert.Equal("/_bridge/maps/traffic/{z}/{x}/{y}", traffic.TilesUrl);
        Assert.Equal(TrafficTileFormat.Vector, traffic.Format);
        Assert.Equal(6, traffic.MinZoom);
        Assert.Equal(22, traffic.MaxZoom);
        Assert.Equal(120, traffic.RefreshSeconds);
        Assert.Equal("Traffic flow", traffic.SourceLayer);
        Assert.Equal("traffic_level", traffic.SpeedRatioProperty);
        Assert.Equal("road_closure", traffic.ClosedProperty);
        Assert.Contains("TomTom", traffic.Attribution);
        Assert.DoesNotContain("tt-secret", await fixture.WebView.GetStringAsync("/_bridge/maps"));
    }

    [Fact]
    public async Task TomTom_tiles_are_fetched_with_the_key_passed_through_compressed_and_kept()
    {
        var tile = Gzip("a traffic tile"u8.ToArray());
        await using var fixture = await MapsFixture.StartAsync(o => o.Traffic = new TomTomTrafficProvider("tt-secret"));
        fixture.Network.Stub("api.tomtom.com", _ => Network.Bytes(tile));

        using var response = await fixture.WebView.GetAsync("/_bridge/maps/traffic/12/850/1550");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.mapbox-vector-tile", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(TimeSpan.FromMinutes(2), response.Headers.CacheControl?.MaxAge);
        Assert.Equal(tile, await response.Content.ReadAsByteArrayAsync());

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("/traffic/map/4/tile/flow/relative/12/850/1550.pbf", request.RequestUri!.AbsolutePath);
        Assert.Equal("tt-secret", HttpUtility.ParseQueryString(request.RequestUri.Query)["key"]);

        // Within the refresh interval the same tile is not fetched again, whatever query string the page adds.
        Assert.Equal(tile, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/traffic/12/850/1550?t=2"));
        Assert.Single(fixture.Network.Requests);
    }

    [Fact]
    public async Task Below_the_layers_zoom_nothing_is_fetched()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Traffic = new TomTomTrafficProvider("tt-secret"));

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/traffic/4/3/5")).StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task Offline_or_nothing_there_is_204_and_not_kept()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Traffic = new TomTomTrafficProvider("tt-secret"));
        fixture.Network.Stub("api.tomtom.com", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/traffic/12/1/1")).StatusCode);

        fixture.Network.Offline = true;
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/traffic/12/2/2")).StatusCode);

        // Back online, the failed tile is asked for again rather than answered from the failure.
        fixture.Network.Offline = false;
        fixture.Network.Stub("api.tomtom.com", _ => Network.Bytes([1, 2, 3]));
        Assert.Equal([1, 2, 3], await fixture.WebView.GetByteArrayAsync("/_bridge/maps/traffic/12/2/2"));
    }

    [Fact]
    public async Task A_provider_of_the_apps_own_serves_images_and_expires_them()
    {
        var provider = new RasterProvider();
        await using var fixture = await MapsFixture.StartAsync(o => o.Traffic = provider);

        var traffic = (await fixture.Maps.GetInfoAsync()).Traffic!;
        Assert.Equal(TrafficTileFormat.Raster, traffic.Format);
        Assert.Equal(512, traffic.TileSize);
        Assert.Null(traffic.SourceLayer);
        Assert.Equal(1, traffic.RefreshSeconds);

        using var response = await fixture.WebView.GetAsync("/_bridge/maps/traffic/10/1/2.png");
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("10/1/2", System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()));

        await Task.Delay(provider.Layer.Refresh + TimeSpan.FromMilliseconds(50));
        await fixture.WebView.GetByteArrayAsync("/_bridge/maps/traffic/10/1/2.png");
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void TomTom_needs_a_key() => Assert.Throws<ArgumentException>(() => new TomTomTrafficProvider(" "));

    static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data);

        return buffer.ToArray();
    }

    sealed class RasterProvider : ITrafficProvider
    {
        public int Calls;

        public TrafficLayer Layer { get; } = new(TrafficTileFormat.Raster, 0, 18, TimeSpan.FromMilliseconds(200), "© Example")
        {
            TileSize = 512
        };

        public Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.Calls);
            return Task.FromResult<TrafficTile?>(new TrafficTile(System.Text.Encoding.UTF8.GetBytes($"{z}/{x}/{y}"), "image/png"));
        }
    }
}
