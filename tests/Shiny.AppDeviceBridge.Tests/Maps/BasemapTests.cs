using System.Net;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>MapsOptions.Basemap</c>: the online map, whichever provider draws it — Protomaps' vector tiles served with downloaded
/// regions, Azure Maps' and Google's images served on their own for the page to lay over them — with every key kept native.
/// </summary>
public class BasemapTests
{
    static readonly double[] Boulder = [-105.30, 39.95, -105.20, 40.05];
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    static HttpResponseMessage Image(byte[] bytes)
    {
        var response = Network.Bytes(bytes);
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

    [Fact]
    public async Task Without_a_basemap_only_downloaded_regions_draw()
    {
        await using var fixture = await MapsFixture.StartAsync();

        var info = await fixture.Maps.GetInfoAsync();

        Assert.Null(info.Basemap);
        Assert.Equal(15, info.MaxZoom);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/tiles/12/850/1550")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await fixture.WebView.GetAsync("/_bridge/maps/basemap/12/850/1550")).StatusCode);
    }

    [Fact]
    public async Task A_vector_basemap_has_no_raster_route()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Basemap = new ProtomapsBasemapProvider("https://tiles.example.com/{z}/{x}/{y}"));

        Assert.Equal(HttpStatusCode.NotImplemented, (await fixture.WebView.GetAsync("/_bridge/maps/basemap/12/850/1550")).StatusCode);
    }

    [Fact]
    public async Task Google_is_described_as_a_raster_layer_without_its_key()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Basemap = new GoogleMapsBasemapProvider("google-secret"));

        var info = await fixture.Maps.GetInfoAsync();

        var basemap = Assert.IsType<BasemapInfo>(info.Basemap);
        Assert.Equal("Google Maps", basemap.Provider);
        Assert.Equal(TileFormat.Raster, basemap.Format);
        Assert.Equal("/_bridge/maps/basemap/{z}/{x}/{y}", basemap.TilesUrl);
        Assert.Equal(256, basemap.TileSize);
        Assert.Contains("Google", basemap.Attribution);
        Assert.Equal("/_bridge/maps/tiles/{z}/{x}/{y}", info.TilesUrl);
        Assert.DoesNotContain("google-secret", await fixture.WebView.GetStringAsync("/_bridge/maps"));
    }

    [Fact]
    public async Task Google_tiles_share_one_session_and_are_not_kept_for_offline()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
            o.Basemap = new GoogleMapsBasemapProvider("google-secret") { Style = GoogleMapsBasemapStyle.Satellite, Language = "fr-FR", Region = "FR" });
        var expiry = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeSeconds();
        string? sessionBody = null;
        fixture.Network.Stub("tile.googleapis.com", async r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/createSession")
            {
                sessionBody = await r.Content!.ReadAsStringAsync();
                return Network.Json($$"""{ "session": "s-1", "expiry": "{{expiry}}", "tileWidth": 512, "tileHeight": 512, "imageFormat": "jpeg" }""");
            }

            return Image(Png);
        });

        Assert.Equal(Png, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/basemap/12/850/1550"));
        using var second = await fixture.WebView.GetAsync("/_bridge/maps/basemap/12/851/1550");
        Assert.Equal("image/png", second.Content.Headers.ContentType?.MediaType);

        var requests = fixture.Network.Requests.ToList();
        Assert.Equal(3, requests.Count);
        Assert.Equal("?key=google-secret", requests[0].RequestUri!.Query);
        var session = JsonNode.Parse(sessionBody!)!;
        Assert.Equal("satellite", (string?)session["mapType"]);
        Assert.Equal("fr-FR", (string?)session["language"]);
        Assert.Equal("FR", (string?)session["region"]);
        Assert.Equal("scaleFactor2x", (string?)session["scale"]);
        Assert.Equal("/v1/2dtiles/12/850/1550", requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("?session=s-1&key=google-secret", requests[1].RequestUri!.Query);

        // Google's terms forbid keeping tiles: offline the image is gone and downloaded regions show through.
        fixture.Network.Offline = true;
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/basemap/12/850/1550")).StatusCode);
    }

    [Fact]
    public async Task Google_starts_a_new_session_when_the_old_one_is_refused()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Basemap = new GoogleMapsBasemapProvider("google-secret"));
        var sessions = 0;
        fixture.Network.Stub("tile.googleapis.com", r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/createSession")
                return Network.Json($$"""{ "session": "s-{{++sessions}}", "expiry": "{{DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}}" }""");

            return r.RequestUri.Query.Contains("session=s-1") ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Image(Png);
        });

        Assert.Equal(Png, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/basemap/3/1/2"));
        Assert.Equal(2, sessions);
    }

    [Fact]
    public async Task Azure_Maps_draws_its_render_tilesets_with_the_key_in_a_header()
    {
        await using var fixture = await MapsFixture.StartAsync(o =>
            o.Basemap = new AzureMapsBasemapProvider("azure-secret") { Style = AzureMapsBasemapStyle.DarkGrey, Language = "de-DE", View = "Auto" });
        fixture.Network.Stub("atlas.microsoft.com", _ => Image(Png));

        var info = await fixture.Maps.GetInfoAsync();
        Assert.Equal(new BasemapInfo("Azure Maps", TileFormat.Raster, "/_bridge/maps/basemap/{z}/{x}/{y}", 0, 22, 512, "© Microsoft Azure Maps, © TomTom"), info.Basemap);
        Assert.Equal(Png, await fixture.WebView.GetByteArrayAsync("/_bridge/maps/basemap/12/850/1550.png"));

        var request = Assert.Single(fixture.Network.Requests);
        Assert.Equal("azure-secret", request.Headers.GetValues("subscription-key").Single());
        Assert.Equal(
            "?api-version=2024-04-01&tilesetId=microsoft.base.darkgrey&zoom=12&x=850&y=1550&tileSize=512&language=de-DE&view=Auto",
            request.RequestUri!.Query
        );
        Assert.DoesNotContain("azure-secret", await fixture.WebView.GetStringAsync("/_bridge/maps"));
    }

    [Fact]
    public async Task One_azure_credential_serves_every_azure_provider_through_Entra_ID()
    {
        var tokens = 0;
        var credential = new AzureMapsCredential("client-id", _ => Task.FromResult($"token-{++tokens}"));
        await using var fixture = await MapsFixture.StartAsync(o =>
        {
            o.Basemap = new AzureMapsBasemapProvider(credential);
            o.Traffic = new AzureMapsTrafficProvider(credential);
        });
        fixture.Network.Stub("atlas.microsoft.com", _ => Image(Png));

        await fixture.WebView.GetByteArrayAsync("/_bridge/maps/basemap/12/850/1550");
        await fixture.WebView.GetByteArrayAsync("/_bridge/maps/traffic/12/850/1550");

        Assert.Collection(
            fixture.Network.Requests,
            r => Assert.Equal("Bearer token-1", r.Headers.Authorization!.ToString()),
            r => Assert.Equal("Bearer token-2", r.Headers.Authorization!.ToString())
        );
        Assert.All(fixture.Network.Requests, r => Assert.Equal("client-id", r.Headers.GetValues("x-ms-client-id").Single()));
    }

    [Fact]
    public async Task Under_a_raster_basemap_downloaded_regions_still_draw()
    {
        await using var fixture = await MapsFixture.StartAsync(o => o.Basemap = new GoogleMapsBasemapProvider("google-secret"));
        fixture.InstallDirectly("boulder", Boulder, PmTilesTests.Fixture("boulder.pmtiles"));

        using var tile = await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/1700/3100");

        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        Assert.Equal(14, (await fixture.Maps.GetInfoAsync()).MaxZoom);

        // The vector tiles never go to a raster provider.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync("/_bridge/maps/tiles/13/100/100")).StatusCode);
        Assert.Empty(fixture.Network.Requests);
    }

    [Fact]
    public async Task A_basemap_of_the_apps_own_can_be_swapped_while_the_app_runs()
    {
        var own = new OwnBasemap();
        await using var fixture = await MapsFixture.StartAsync(o => o.Basemap = new ProtomapsBasemapProvider("https://tiles.example.com/{z}/{x}/{y}"));
        Assert.Equal(TileFormat.Vector, (await fixture.Maps.GetInfoAsync()).Basemap!.Format);

        fixture.Service.Options.Basemap = own;

        var info = await fixture.Maps.GetInfoAsync();
        Assert.Equal("Own", info.Basemap!.Provider);
        Assert.Equal(5, info.Basemap.MinZoom);

        using var below = await fixture.WebView.GetAsync("/_bridge/maps/basemap/4/1/1");
        Assert.Equal(HttpStatusCode.NoContent, below.StatusCode);

        using var image = await fixture.WebView.GetAsync("/_bridge/maps/basemap/6/1/1");
        Assert.Equal("image/webp", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal([(6, 1, 1)], own.Asked);
    }

    sealed class OwnBasemap : IBasemapProvider
    {
        public List<(int, int, int)> Asked { get; } = [];

        public BasemapLayer Layer => new("Own", TileFormat.Raster, 5, 18, "© Own");

        public Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
        {
            this.Asked.Add((z, x, y));
            return Task.FromResult<ProviderTile?>(new ProviderTile([1, 2], "image/webp"));
        }
    }

    [Fact]
    public void Every_provider_needs_a_key()
    {
        Assert.Throws<ArgumentException>(() => new GoogleMapsBasemapProvider(" "));
        Assert.Throws<ArgumentException>(() => new AzureMapsBasemapProvider(""));
        Assert.Throws<ArgumentException>(() => new ProtomapsBasemapProvider(""));
        Assert.Throws<ArgumentException>(() => new AzureMapsCredential(""));
    }
}
