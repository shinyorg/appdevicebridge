using System.Globalization;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>The Azure Maps traffic tilesets <see cref="AzureMapsTrafficProvider"/> draws from.</summary>
public enum AzureMapsTrafficStyle
{
    /// <summary>Speed relative to free flow: congestion stands out. <c>microsoft.traffic.relative.main</c>.</summary>
    Relative,

    /// <summary><see cref="Relative"/> in colours for a dark map. <c>microsoft.traffic.relative.dark</c>.</summary>
    RelativeDark,

    /// <summary>Colours only where traffic is slower than free flow. <c>microsoft.traffic.delay.main</c>.</summary>
    Delay,

    /// <summary>Relative, needing a larger slowdown to change colour. <c>microsoft.traffic.reduced.main</c>.</summary>
    ReducedSensitivity,

    /// <summary>The measured speed itself. <c>microsoft.traffic.absolute.main</c>.</summary>
    Absolute
}

/// <summary>
/// Azure Maps traffic flow, from the Render service's <c>Get Map Tile</c> — the replacement for the Traffic v1 tiles Microsoft
/// retires in March 2028. Raster images drawn over the map as they are. Authenticates with an
/// <see cref="AzureMapsCredential"/> — the account's shared key, or Microsoft Entra ID — which stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Traffic = new AzureMapsTrafficProvider(azureMapsKey));
/// </code>
/// <para>
/// Azure Maps requires its attribution wherever its tiles are drawn; the layer reports it and the map shows it.
/// </para>
/// </summary>
public sealed class AzureMapsTrafficProvider : ITrafficProvider
{
    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsTrafficProvider(string subscriptionKey) : this(new AzureMapsCredential(subscriptionKey))
    {
    }

    /// <summary>Authenticates with a shared key or Microsoft Entra ID, as <paramref name="credential"/> says.</summary>
    public AzureMapsTrafficProvider(AzureMapsCredential credential)
        => this.Credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public AzureMapsCredential Credential { get; }

    /// <summary><c>https://atlas.microsoft.com/</c>, or a geography's own host such as <c>https://us.atlas.microsoft.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://atlas.microsoft.com/");

    public AzureMapsTrafficStyle Style { get; set; } = AzureMapsTrafficStyle.Relative;

    /// <summary>The lowest zoom tiles are fetched at. 6 by default.</summary>
    public int MinZoom { get; set; } = 6;

    /// <summary>The highest zoom Azure Maps is asked for; the map scales the images past it. 18 by default.</summary>
    public int MaxZoom { get; set; } = 18;

    /// <summary>Two minutes by default.</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Image size, 256 or 512 pixels. 512 by default, which draws sharply on high-density screens.</summary>
    public int TileSize { get; set; } = 512;

    public TrafficLayer Layer => new(TileFormat.Raster, this.MinZoom, this.MaxZoom, this.Refresh, "© Microsoft Azure Maps, © TomTom")
    {
        TileSize = this.TileSize
    };

    string TilesetId => this.Style switch
    {
        AzureMapsTrafficStyle.RelativeDark => "microsoft.traffic.relative.dark",
        AzureMapsTrafficStyle.Delay => "microsoft.traffic.delay.main",
        AzureMapsTrafficStyle.ReducedSensitivity => "microsoft.traffic.reduced.main",
        AzureMapsTrafficStyle.Absolute => "microsoft.traffic.absolute.main",
        _ => "microsoft.traffic.relative.main"
    };

    public async Task<ProviderTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"map/tile?api-version=2024-04-01&tilesetId={this.TilesetId}&zoom={z}&x={x}&y={y}&tileSize={this.TileSize}"
        );

        var authorize = await this.Credential.AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        return await ProviderTiles.GetAsync(http, new Uri(this.BaseAddress, path), "image/png", authorize, cancellationToken).ConfigureAwait(false);
    }
}
