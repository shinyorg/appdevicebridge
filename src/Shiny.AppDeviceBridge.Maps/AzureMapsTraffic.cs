using System.Globalization;
using System.Net.Http.Headers;
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
/// retires in March 2028. Raster images drawn over the map as they are. Authenticates with the account's shared key, or with
/// Microsoft Entra ID through <see cref="ClientId"/> and <see cref="GetAccessToken"/>; either stays in the app.
/// <code>
/// bridge.AddMapsBridge(o => o.Traffic = new AzureMapsTrafficProvider(azureMapsKey));
/// </code>
/// <para>
/// Azure Maps requires its attribution wherever its tiles are drawn; the layer reports it and the map shows it.
/// </para>
/// </summary>
public sealed class AzureMapsTrafficProvider : ITrafficProvider
{
    readonly string? subscriptionKey;

    /// <summary>Authenticates with the Azure Maps account's shared key.</summary>
    public AzureMapsTrafficProvider(string subscriptionKey)
    {
        if (String.IsNullOrWhiteSpace(subscriptionKey))
            throw new ArgumentException("An Azure Maps key is required.", nameof(subscriptionKey));

        this.subscriptionKey = subscriptionKey;
    }

    /// <summary>
    /// Authenticates with Microsoft Entra ID: <paramref name="clientId"/> is the Azure Maps account's client id, and
    /// <paramref name="getAccessToken"/> returns a token for <c>https://atlas.microsoft.com/.default</c>, cached as the app sees fit.
    /// </summary>
    public AzureMapsTrafficProvider(string clientId, Func<CancellationToken, Task<string>> getAccessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(getAccessToken);

        this.ClientId = clientId;
        this.GetAccessToken = getAccessToken;
    }

    /// <summary>The Azure Maps account's client id, for Entra ID authentication.</summary>
    public string? ClientId { get; }

    /// <summary>Returns an Entra ID access token for each request.</summary>
    public Func<CancellationToken, Task<string>>? GetAccessToken { get; }

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

    public TrafficLayer Layer => new(TrafficTileFormat.Raster, this.MinZoom, this.MaxZoom, this.Refresh, "© Microsoft Azure Maps, © TomTom")
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

    public async Task<TrafficTile?> GetTileAsync(int z, int x, int y, HttpClient http, CancellationToken cancellationToken)
    {
        var path = String.Create(
            CultureInfo.InvariantCulture,
            $"map/tile?api-version=2024-04-01&tilesetId={this.TilesetId}&zoom={z}&x={x}&y={y}&tileSize={this.TileSize}"
        );

        string? token = null;
        if (this.GetAccessToken is { } getToken)
            token = await getToken(cancellationToken).ConfigureAwait(false);

        return await TrafficTiles.GetAsync(http, new Uri(this.BaseAddress, path), "image/png", request =>
        {
            if (token is null)
            {
                request.Headers.Add("subscription-key", this.subscriptionKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("x-ms-client-id", this.ClientId);
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
