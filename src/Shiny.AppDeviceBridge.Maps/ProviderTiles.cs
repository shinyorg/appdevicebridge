using System.Net;
using System.Net.Http.Headers;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>One tile from a provider — a basemap, traffic flow or incidents — as the bridge passes it to the page.</summary>
/// <param name="ContentType"><c>application/vnd.mapbox-vector-tile</c> for vector tiles, the image's type for raster.</param>
/// <param name="ContentEncoding"><c>gzip</c> when <paramref name="Data"/> is still compressed, as vector tiles often are.</param>
public sealed record ProviderTile(byte[] Data, string ContentType, string? ContentEncoding = null);

/// <summary>The request every built-in tile provider makes: one GET, "nothing here" answers as null, compression passed through.</summary>
static class ProviderTiles
{
    public const string VectorTile = "application/vnd.mapbox-vector-tile";

    public static async Task<ProviderTile?> GetAsync(
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

        return new ProviderTile(data, type, gzip ? "gzip" : null);
    }
}
