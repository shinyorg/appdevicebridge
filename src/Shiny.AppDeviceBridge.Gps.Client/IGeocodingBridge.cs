using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Gps.Client;

/// <summary>
/// Reverse geocoding: the addresses at a position, from the platform geocoder (MapKit / CoreLocation on iOS and Mac
/// Catalyst, <c>android.location.Geocoder</c> on Android) or, on Windows, Linux, macOS and Android devices without a
/// geocoding backend, OpenStreetMap's Nominatim. It needs network access but no location permission.
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
