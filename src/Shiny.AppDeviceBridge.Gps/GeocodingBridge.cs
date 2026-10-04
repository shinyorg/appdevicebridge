using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Gps.Client;
using static Shiny.AppDeviceBridge.Gps.GpsContractMapping;

namespace Shiny.AppDeviceBridge.Gps;

/// <summary>
/// <c>/_bridge/geocoding</c> over <see cref="IGeocoder"/>: the addresses at a position, from the platform geocoder.
/// It needs network access but no location permission.
/// <code>
/// GET /_bridge/geocoding/reverse?latitude=&amp;longitude=    → [{ latitude, longitude, name, thoroughfare, locality, … }]
/// </code>
/// Unsupported — 501 — where Shiny registers no geocoder, and on Android devices without a geocoding backend
/// (typically no Google Play Services).
/// </summary>
public sealed class GeocodingBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IGeocoder? geocoder = services.GetOptionalService<IGeocoder>();

    public string Name => "geocoding";

    public bool IsSupported => this.geocoder is { IsSupported: true };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/reverse", this.ReverseAsync);

    async ValueTask ReverseAsync(HttpContext context)
    {
        if (this.geocoder is not { IsSupported: true } g)
        {
            await WebAppBridgeResults.NotSupported(context, "Geocoding");
            return;
        }

        if (!TryReadCoordinate(context, "latitude", 90, out var latitude) || !TryReadCoordinate(context, "longitude", 180, out var longitude))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected ?latitude=&longitude= on the map: latitude -90 to 90, longitude -180 to 180.");
            return;
        }

        IReadOnlyList<Placemark> placemarks;
        try
        {
            placemarks = await g.ReverseGeocode(new Position(latitude, longitude), context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            // The platform geocoders fail with their own exception types (an IOException on Android, an NSError on Apple),
            // almost always because the network or the geocoding service is unreachable.
            await WebAppBridgeResults.Error(context, StatusCodes.Status503ServiceUnavailable, "geocoder_unavailable", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json<IReadOnlyList<Contracts.Placemark>>(
            context,
            [.. placemarks.Select(ToContract)],
            Contracts.GpsJsonContext.Default.IReadOnlyListPlacemark
        );
    }

    static bool TryReadCoordinate(HttpContext context, string name, double limit, out double value)
        => Double.TryParse(context.Request.Query[name].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && value >= -limit
           && value <= limit;
}

public static class GeocodingBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/geocoding</c> and registers Shiny's platform geocoder. Not part of
    /// <see cref="GpsBridgeExtensions.AddGpsBridge"/>: it needs no location permission, but every lookup goes to the
    /// platform's geocoding service over the network. Android, iOS and Mac Catalyst; other platforms answer 501.
    /// </summary>
    public static TBuilder AddGeocodingBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        // Registers nothing on the platforms Shiny has no geocoder for, which leaves the bridge answering 501.
        bridge.Services.AddGeocoding();
        bridge.AddBridge<GeocodingBridge>();
        return bridge;
    }
}
