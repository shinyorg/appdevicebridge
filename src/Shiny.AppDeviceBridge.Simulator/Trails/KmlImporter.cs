using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>
/// Turns a KML file — or a <c>.kmz</c>, the same zipped — into a GPS walk, exactly as <see cref="GpxImporter"/> does a GPX:
/// a <c>gx:Track</c> plays at its recorded pace, a <c>LineString</c> (a path drawn in Google Earth or My Maps) or a list of
/// <c>Point</c> placemarks one point per interval.
/// </summary>
public static class KmlImporter
{
    static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    /// <inheritdoc cref="GpxImporter.Import(string, TimeSpan?, bool)"/>
    public static Trail Import(string path, TimeSpan? interval = null, bool useTimestamps = true)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        if (!Path.GetExtension(path).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return Import(stream, name, interval, useTimestamps);
        }

        using var archive = ZipFile.OpenRead(path);

        // doc.kml by convention; any .kml at all otherwise.
        var entry = archive.Entries.FirstOrDefault(x => x.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{path} holds no .kml file.");

        using var kml = entry.Open();
        return Import(kml, name, interval, useTimestamps);
    }

    /// <param name="name">The trail's name when the file's document or placemark has none of its own.</param>
    public static Trail Import(Stream kml, string name, TimeSpan? interval = null, bool useTimestamps = true)
    {
        var document = XDocument.Load(kml);
        var own = document.Descendants()
            .Where(x => x.Name.LocalName is "Document" or "Folder" or "Placemark")
            .Select(x => x.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value.Trim())
            .FirstOrDefault(x => !String.IsNullOrEmpty(x));

        return GpxImporter.FromPoints(Read(document), own ?? name, interval ?? TimeSpan.FromSeconds(1), useTimestamps);
    }

    /// <summary>Track points, else line strings, else point placemarks — whatever the file has, in order.</summary>
    public static IReadOnlyList<TrackPoint> Read(Stream kml) => Read(XDocument.Load(kml));

    static IReadOnlyList<TrackPoint> Read(XDocument document)
    {
        var tracks = document.Descendants().Where(x => x.Name.LocalName == "Track").SelectMany(Track).ToList();
        if (tracks.Count > 0)
            return tracks;

        var lines = Coordinates(document, "LineString");
        if (lines.Count > 0)
            return lines;

        var points = Coordinates(document, "Point");
        if (points.Count > 0)
            return points;

        throw new InvalidDataException("The KML file has no gx:Track, LineString or Point.");
    }

    /// <summary>A <c>gx:Track</c>: <c>when</c> and <c>gx:coord</c> elements, paired in order. Its coordinates are space-separated.</summary>
    static IEnumerable<TrackPoint> Track(XElement track)
    {
        var times = track.Elements().Where(x => x.Name.LocalName == "when").Select(x => Time(x.Value)).ToList();
        var coords = track.Elements().Where(x => x.Name.LocalName == "coord").ToList();

        return coords.Select((coord, i) => Point(coord.Value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries), i < times.Count ? times[i] : null));
    }

    /// <summary>Every <c>coordinates</c> under the named geometry: <c>lon,lat[,alt]</c> tuples separated by whitespace.</summary>
    static List<TrackPoint> Coordinates(XDocument document, string geometry) => document.Descendants()
        .Where(x => x.Name.LocalName == geometry)
        .SelectMany(x => x.Elements().Where(e => e.Name.LocalName == "coordinates"))
        .SelectMany(x => x.Value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries))
        .Select(x => Point(x.Split(','), null))
        .ToList();

    static TrackPoint Point(string[] parts, DateTimeOffset? time)
    {
        if (parts.Length < 2)
            throw new InvalidDataException($"'{String.Join(" ", parts)}' is not a longitude and latitude.");

        var lon = Double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
        var lat = Double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
        double? elevation = parts.Length > 2 && Double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null;

        return new TrackPoint(lat, lon, elevation, time);
    }

    static DateTimeOffset? Time(string value)
        => DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
