using System.Net;
using System.Net.Http.Headers;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// Where live traffic flow comes from — <see cref="TomTomTrafficProvider"/>, <see cref="HereTrafficProvider"/>,
/// <see cref="AzureMapsTrafficProvider"/>, or a class of the app's own. Set it on <see cref="MapsOptions.Traffic"/>.
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

/// <summary>
/// Where live traffic incidents come from — <see cref="TomTomIncidentProvider"/>, or a class of the app's own. Set it on
/// <see cref="MapsOptions.TrafficIncidents"/>, independently of <see cref="MapsOptions.Traffic"/>: one provider's flow and
/// another's incidents work together. Served at <c>/_bridge/maps/incidents/{z}/{x}/{y}</c> under the same rules as flow.
/// </summary>
public interface ITrafficIncidentProvider
{
    TrafficIncidentLayer Layer { get; }

    /// <summary>One vector tile, or null when the provider has nothing there.</summary>
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

/// <summary>What an incident provider's vector tiles hold. See <see cref="TrafficIncidentInfo"/>, which the page receives.</summary>
/// <param name="KindProperty">The property holding the provider's incident type.</param>
/// <param name="Kinds">The provider's values of <paramref name="KindProperty"/>, as text, and what each is.</param>
public sealed record TrafficIncidentLayer(
    int MinZoom,
    int MaxZoom,
    TimeSpan Refresh,
    string Attribution,
    string KindProperty,
    IReadOnlyDictionary<string, TrafficIncidentKind> Kinds
)
{
    /// <summary>The layer holding the affected stretches of road, as lines.</summary>
    public string? LineSourceLayer { get; init; }

    /// <summary>The layer holding incident locations, as points.</summary>
    public string? PointSourceLayer { get; init; }

    public string? DescriptionProperty { get; init; }

    /// <summary>The property holding the delay the incident causes, in seconds.</summary>
    public string? DelayProperty { get; init; }

    /// <summary>The property holding how many incidents a point stands for, where the provider groups them.</summary>
    public string? ClusterSizeProperty { get; init; }
}

/// <param name="ContentType"><c>application/vnd.mapbox-vector-tile</c> for vector tiles, the image's type for raster.</param>
/// <param name="ContentEncoding"><c>gzip</c> when <paramref name="Data"/> is still compressed, as vector tiles often are.</param>
public sealed record TrafficTile(byte[] Data, string ContentType, string? ContentEncoding = null);

/// <summary>The request every built-in provider makes: one GET, "nothing here" answers as null, compression passed through.</summary>
static class TrafficTiles
{
    public const string VectorTile = "application/vnd.mapbox-vector-tile";

    public static async Task<TrafficTile?> GetAsync(
        HttpClient http,
        Uri uri,
        string contentType,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        configure?.Invoke(request);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Providers answer 400 for a tile outside their range and 404 where they have none.
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;

        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (data.Length == 0)
            return null;

        // Some handlers decompress on their own and drop the header, and NSURLSession does; look at the bytes, not the header.
        var gzip = data is [0x1f, 0x8b, ..];

        // An image is served as the type the provider says it is — png, or jpeg where it chose to; vector tiles have one type.
        var type = contentType != VectorTile && response.Content.Headers.ContentType?.MediaType is { } served && served.StartsWith("image/", StringComparison.Ordinal)
            ? served
            : contentType;

        return new TrafficTile(data, type, gzip ? "gzip" : null);
    }
}
