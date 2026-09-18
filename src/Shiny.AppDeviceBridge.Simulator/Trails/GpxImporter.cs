using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Shiny.AppDeviceBridge.Locations.Client;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>
/// Turns a GPX file — a track, a route or a list of waypoints — into a trail of <c>gps.reading</c> events. Each point also
/// becomes what <c>GET gps/current</c> and <c>GET gps/last</c> answer, so a page that polls sees the same walk as one that
/// listens. Readings are stamped <c>"$now"</c>, so they are current whenever the trail plays.
/// </summary>
public static class GpxImporter
{
    const double EarthRadiusMeters = 6_371_000;

    /// <summary>A point of the walk.</summary>
    public sealed record GpxPoint(double Latitude, double Longitude, double? Elevation, DateTimeOffset? Time);

    /// <summary>Named after the file's track or route, or the file itself when it names neither.</summary>
    /// <param name="interval">The gap between points that carry no time; and the gap used when <paramref name="useTimestamps"/> is off.</param>
    /// <param name="useTimestamps">Replays the recorded pace when the file has times; otherwise one point per <paramref name="interval"/>.</param>
    public static Trail Import(string path, TimeSpan? interval = null, bool useTimestamps = true)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, Path.GetFileNameWithoutExtension(path), interval, useTimestamps);
    }

    /// <param name="name">The trail's name when the file's track or route has none of its own.</param>
    public static Trail Import(Stream gpx, string name, TimeSpan? interval = null, bool useTimestamps = true)
    {
        var document = XDocument.Load(gpx);
        var own = document.Descendants()
            .Where(x => x.Name.LocalName is "trk" or "rte" or "metadata")
            .Select(x => x.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value.Trim())
            .FirstOrDefault(x => !String.IsNullOrEmpty(x));

        return FromPoints(Read(document), own ?? name, interval ?? TimeSpan.FromSeconds(1), useTimestamps);
    }

    /// <summary>Track points, else route points, else waypoints — whatever the file has, in order.</summary>
    public static IReadOnlyList<GpxPoint> Read(Stream gpx) => Read(XDocument.Load(gpx));

    static IReadOnlyList<GpxPoint> Read(XDocument document)
    {
        foreach (var kind in new[] { "trkpt", "rtept", "wpt" })
        {
            var points = document.Descendants().Where(x => x.Name.LocalName == kind).Select(Point).ToList();
            if (points.Count > 0)
                return points;
        }

        throw new InvalidDataException("The GPX file has no track points, route points or waypoints.");
    }

    public static Trail FromPoints(IReadOnlyList<GpxPoint> points, string name, TimeSpan interval, bool useTimestamps = true)
    {
        var trail = new Trail { Name = name };

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var previous = i > 0 ? points[i - 1] : null;
            var gap = previous is null ? TimeSpan.Zero : Gap(previous, point, interval, useTimestamps);

            var distance = previous is null ? 0 : Distance(previous, point);
            var speed = gap > TimeSpan.Zero ? distance / gap.TotalSeconds : 0;
            var heading = previous is null
                ? (i + 1 < points.Count ? Bearing(point, points[i + 1]) : 0)
                : Bearing(previous, point);

            var reading = new GpsReading(
                point.Latitude,
                point.Longitude,
                PositionAccuracy: 5,
                Timestamp: DateTimeOffset.UnixEpoch,
                Heading: heading,
                HeadingAccuracy: 10,
                Altitude: point.Elevation ?? 0,
                Speed: speed,
                SpeedAccuracy: 1,
                Floor: 0,
                IsStationary: speed < 0.2
            );

            var payload = JsonSerializer.SerializeToNode(reading, LocationsJsonContext.Default.GpsReading)!.AsObject();
            payload["timestamp"] = "$now";

            trail.Steps.Add(new TrailStep
            {
                DelayMs = (int)Math.Round(gap.TotalMilliseconds),
                Note = $"point {i + 1}/{points.Count}  {point.Latitude:F5}, {point.Longitude:F5}",
                Event = "gps.reading",
                Payload = payload
            });
            trail.Steps.Add(new TrailStep { Bridge = "gps", Route = "GET current", Value = payload.DeepClone() });
            trail.Steps.Add(new TrailStep { Bridge = "gps", Route = "GET last", Value = payload.DeepClone() });
        }

        return trail;
    }

    static GpxPoint Point(XElement element)
    {
        var lat = Double.Parse(element.Attribute("lat")?.Value ?? throw new InvalidDataException("A point has no lat."), CultureInfo.InvariantCulture);
        var lon = Double.Parse(element.Attribute("lon")?.Value ?? throw new InvalidDataException("A point has no lon."), CultureInfo.InvariantCulture);

        double? elevation = Child(element, "ele") is { } ele && Double.TryParse(ele, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null;
        DateTimeOffset? time = Child(element, "time") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

        return new GpxPoint(lat, lon, elevation, time);
    }

    static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

    static TimeSpan Gap(GpxPoint from, GpxPoint to, TimeSpan interval, bool useTimestamps)
        => useTimestamps && from.Time is { } a && to.Time is { } b && b > a ? b - a : interval;

    /// <summary>Great-circle distance in meters.</summary>
    public static double Distance(GpxPoint a, GpxPoint b)
    {
        var dLat = Radians(b.Latitude - a.Latitude);
        var dLon = Radians(b.Longitude - a.Longitude);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from <paramref name="a"/> to <paramref name="b"/>, degrees from north.</summary>
    public static double Bearing(GpxPoint a, GpxPoint b)
    {
        var lat1 = Radians(a.Latitude);
        var lat2 = Radians(b.Latitude);
        var dLon = Radians(b.Longitude - a.Longitude);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    static double Radians(double degrees) => degrees * Math.PI / 180;
}
