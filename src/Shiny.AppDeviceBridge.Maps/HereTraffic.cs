using System.Globalization;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// HERE's Traffic Raster Tile API: images of traffic flow, green to red to black, drawn over the map as they are. Needs a HERE
/// API key with access to traffic tiles; the key stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Traffic = new HereTrafficProvider(hereKey));
/// </code>
/// <para>
/// The colours are HERE's, not the map's, and each tile is a request against the key's quota.
/// </para>
/// </summary>
public sealed class HereTrafficProvider(string apiKey) : ITrafficProvider
{
    readonly string apiKey = String.IsNullOrWhiteSpace(apiKey)
        ? throw new ArgumentException("A HERE API key is required.", nameof(apiKey))
        : apiKey;

    /// <summary>HERE's traffic tile host. Change it only for a test server.</summary>
    public Uri BaseAddress { get; set; } = new("https://traffic.maps.hereapi.com/");

    /// <summary>The lowest zoom tiles are fetched at. 6 by default.</summary>
    public int MinZoom { get; set; } = 6;

    /// <summary>The highest zoom HERE is asked for; the map scales the images past it. 20 by default.</summary>
    public int MaxZoom { get; set; } = 20;

    /// <summary>Two minutes by default.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Image size, 256 or 512 pixels. 512 by default, which draws sharply on high-density screens.</summary>
    public int TileSize { get; set; } = 512;

    /// <summary>
    /// The least congested traffic drawn: <c>free</c> (every road HERE covers, the default), <c>heavy</c>, <c>queuing</c> or
    /// <c>blocked</c> — so only trouble is drawn over a busy map.
    /// </summary>
    public string MinTrafficCongestion { get; set; } = "free";

    public TrafficLayer Layer => new(TrafficTileFormat.Raster, this.MinZoom, this.MaxZoom, this.Refresh, "<a href=\"https://www.here.com/\">© HERE</a>")
    {
        TileSize = this.TileSize
    };

    public Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        // Column then row: x then y.
        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"v3/flow/mc/{z}/{x}/{y}/png?size={this.TileSize}&minTrafficCongestion={Uri.EscapeDataString(this.MinTrafficCongestion)}&apiKey={Uri.EscapeDataString(this.apiKey)}"
        );
        return TrafficTiles.GetAsync(http, new Uri(this.BaseAddress, path), "image/png", null, cancellationToken);
    }
}
