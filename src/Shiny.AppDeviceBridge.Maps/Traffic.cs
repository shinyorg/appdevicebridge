using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Where live traffic comes from — <see cref="TomTomTrafficProvider"/>, or a class of the app's own. Set it on
/// <see cref="MapsOptions.Traffic"/>.
/// <para>
/// The provider describes its tiles once, through <see cref="Layer"/>, and fetches them one at a time. The page asks the bridge
/// for <c>/_bridge/maps/traffic/{z}/{x}/{y}</c> and never learns the provider's address or key. The bridge keeps each tile for
/// <see cref="TrafficLayer.Refresh"/>, and answers 204 for a tile outside the layer's zooms, for a null, or for an exception —
/// traffic is a live layer, and offline it is simply empty.
/// </para>
/// </summary>
public interface ITrafficProvider
{
    TrafficLayer Layer { get; }

    /// <summary>One tile, or null when the provider has nothing there.</summary>
    /// <param name="http">The maps bridge's client, built from <see cref="MapsOptions.HttpMessageHandlerFactory"/>.</param>
    Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken);
}

/// <summary>What a traffic provider's tiles are and how to draw them. See <see cref="TrafficInfo"/>, which the page receives.</summary>
/// <param name="Refresh">How often the provider's data changes: tiles are kept this long, and the page fetches them again after it.</param>
/// <param name="Attribution">The credit the provider's terms require, as HTML.</param>
public sealed record TrafficLayer(TrafficTileFormat Format, int MinZoom, int MaxZoom, TimeSpan Refresh, string Attribution)
{
    /// <summary>Vector: the layer inside each tile holding the road segments.</summary>
    public string? SourceLayer { get; init; }

    /// <summary>Vector: the property holding current speed over free-flow speed, 0 to 1.</summary>
    public string? SpeedRatioProperty { get; init; }

    /// <summary>Vector: the property that is true on a closed road.</summary>
    public string? ClosedProperty { get; init; }

    /// <summary>Raster: the images' size in pixels.</summary>
    public int TileSize { get; init; } = 256;
}

/// <param name="ContentType"><c>application/vnd.mapbox-vector-tile</c> for vector tiles, the image's type for raster.</param>
/// <param name="ContentEncoding"><c>gzip</c> when <paramref name="Data"/> is still compressed, as vector tiles often are.</param>
public sealed record TrafficTile(byte[] Data, string ContentType, string? ContentEncoding = null);

/// <summary>
/// TomTom's Traffic Flow vector tiles: every road TomTom covers, with its current speed relative to free flow and whether it is
/// closed. Needs a TomTom API key with the Traffic API; the key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Traffic = new TomTomTrafficProvider(tomTomKey));
/// </code>
/// <para>
/// Each tile is a request against the key's quota, and every tile on screen is fetched again after <see cref="Refresh"/>.
/// <see cref="MinZoom"/> keeps the continent-wide zooms, where traffic is unreadable anyway, from spending it.
/// </para>
/// </summary>
public sealed class TomTomTrafficProvider(string apiKey) : ITrafficProvider
{
    readonly string apiKey = String.IsNullOrWhiteSpace(apiKey)
        ? throw new ArgumentException("A TomTom API key is required.", nameof(apiKey))
        : apiKey;

    /// <summary>TomTom's API host. Change it only for a regional endpoint or a test server.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.tomtom.com/");

    /// <summary>The lowest zoom tiles are fetched at. 6 by default: regions and the roads between cities.</summary>
    public int MinZoom { get; set; } = 6;

    /// <summary>TomTom's flow data changes about once a minute. Two minutes by default, to halve the quota spent.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromMinutes(2);

    public TrafficLayer Layer => new(TrafficTileFormat.Vector, this.MinZoom, 22, this.Refresh, "<a href=\"https://www.tomtom.com/\">© TomTom</a>")
    {
        SourceLayer = "Traffic flow",
        SpeedRatioProperty = "traffic_level",
        ClosedProperty = "road_closure"
    };

    public async Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        // The "relative" style: traffic_level is current speed over free-flow speed, which is what the layer colours by.
        var path = String.Create(CultureInfo.InvariantCulture, $"traffic/map/4/tile/flow/relative/{z}/{x}/{y}.pbf?key={Uri.EscapeDataString(this.apiKey)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(this.BaseAddress, path));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // TomTom answers 400 for a tile outside its range and 404 where it has none.
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;

        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (data.Length == 0)
            return null;

        // Some handlers decompress on their own and drop the header, and NSURLSession does; look at the bytes, not the header.
        var gzip = data is [0x1f, 0x8b, ..];
        return new TrafficTile(data, "application/vnd.mapbox-vector-tile", gzip ? "gzip" : null);
    }
}
