namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>A point of a walk, read from a GPX or KML file.</summary>
public sealed record TrackPoint(double Latitude, double Longitude, double? Elevation = null, DateTimeOffset? Time = null)
{
    const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle distance in meters.</summary>
    public double DistanceTo(TrackPoint other) => Distance(this.Latitude, this.Longitude, other.Latitude, other.Longitude);

    /// <summary>Initial bearing towards <paramref name="other"/>, degrees from north.</summary>
    public double BearingTo(TrackPoint other)
    {
        var lat1 = Radians(this.Latitude);
        var lat2 = Radians(other.Latitude);
        var dLon = Radians(other.Longitude - this.Longitude);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Great-circle distance in meters between two coordinates.</summary>
    public static double Distance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var dLat = Radians(latitude2 - latitude1);
        var dLon = Radians(longitude2 - longitude1);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Radians(latitude1)) * Math.Cos(Radians(latitude2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    static double Radians(double degrees) => degrees * Math.PI / 180;
}
