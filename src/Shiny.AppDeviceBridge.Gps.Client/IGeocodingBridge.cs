using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Gps.Client;

/// <summary>
/// Reverse geocoding: the addresses at a position, from the platform geocoder (MapKit / CoreLocation on Apple,
/// <c>android.location.Geocoder</c> on Android), which needs network access but no location permission. Android, iOS and
/// Mac Catalyst; elsewhere, and on Android devices without a geocoding backend, every call fails with 501.
/// </summary>
[BridgeClient("geocoding", typeof(GpsJsonContext))]
public interface IGeocodingBridge
{
    /// <summary>
    /// The placemarks at a position, most relevant first — empty when the geocoder found nothing. Fails with 400 for a
    /// position off the map and 503 when the platform geocoder could not answer (typically no network).
    /// </summary>
    [BridgeGet("reverse")]
    Task<IReadOnlyList<Placemark>> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
