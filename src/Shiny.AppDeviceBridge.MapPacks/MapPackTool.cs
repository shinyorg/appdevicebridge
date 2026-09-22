using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.AspNetCore;

namespace Shiny.AppDeviceBridge.MapPacks;

sealed class MapPackToolException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// <c>shiny-map-packs</c>: builds and lists the directory <see cref="FileSystemMapPackStore"/> serves.
/// </summary>
static class MapPackTool
{
    public const string Usage = """
        shiny-map-packs — builds downloadable regions for Shiny.AppDeviceBridge.Maps

          shiny-map-packs region <id> --name <name> (--bbox <w,s,e,n> | --polygon <file.geojson>) --planet <url|file>
                                      [--maxzoom 14] [--osm <url|file.osm.pbf>] [--valhalla local|docker] [--out <dir>]
              Cuts the region's map from a PMTiles planet (pmtiles CLI) and, with --osm, builds its road network for
              on-device directions with Valhalla 3.6.3 — the version the phone runs: its tools on PATH, or its Docker image.

          shiny-map-packs assets [--source <url>] [--out <dir>]
              Packs the glyphs and sprites the Protomaps style draws with into assets.zip.

          shiny-map-packs remove <id> [--out <dir>]
          shiny-map-packs list [--out <dir>]

        --out defaults to the current directory. Serve it with AddMapPacks(o => o.PacksDirectory = …) and MapMapPacks().
        """;

    const string DefaultAssets = "https://github.com/protomaps/basemaps-assets/archive/refs/heads/main.zip";
    // The Valhalla that Shiny.AppDeviceBridge.Maps.Valhalla runs on the phone (valhalla-mobile 0.6.3). It only reads road
    // networks built by the same version, so the tool builds with it; bump the two together.
    public const string ValhallaVersion = "3.6.3";
    const string ValhallaImage = "ghcr.io/valhalla/valhalla:" + ValhallaVersion;

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var options = Parse(args.Skip(1));
        var directory = Path.GetFullPath(options.GetValueOrDefault("out") ?? ".");
        Directory.CreateDirectory(directory);

        switch (args[0])
        {
            case "region":
                await RegionAsync(options, directory, output, cancellationToken);
                return 0;

            case "assets":
                await AssetsAsync(options.GetValueOrDefault("source") ?? DefaultAssets, directory, output, cancellationToken);
                return 0;

            case "remove":
                Remove(Positional(options, "an id"), directory, output);
                return 0;

            case "list":
                await ListAsync(directory, output, cancellationToken);
                return 0;

            default:
                throw new MapPackToolException($"Unknown command '{args[0]}'.\n\n{Usage}", 2);
        }
    }

    static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        using var e = args.GetEnumerator();

        while (e.MoveNext())
        {
            if (e.Current.StartsWith("--", StringComparison.Ordinal))
            {
                var name = e.Current[2..];

                // --bbox=-109,36,-102,41: the = form keeps a value that starts with '-' from reading as an option.
                if (name.IndexOf('=') is > 0 and var at)
                {
                    options[name[..at]] = name[(at + 1)..];
                    continue;
                }

                if (!e.MoveNext())
                    throw new MapPackToolException($"--{name} needs a value.", 2);

                options[name] = e.Current;
            }
            else if (!options.TryAdd("", e.Current))
            {
                throw new MapPackToolException($"Unexpected '{e.Current}'.", 2);
            }
        }

        return options;
    }

    static string Positional(Dictionary<string, string> options, string what)
        => options.GetValueOrDefault("") ?? throw new MapPackToolException($"Expected {what}.\n\n{Usage}", 2);

    static async Task RegionAsync(Dictionary<string, string> options, string directory, TextWriter output, CancellationToken cancellationToken)
    {
        var id = Positional(options, "a region id");
        if (!MapPackSignature.IsValidRegionId(id))
            throw new MapPackToolException($"'{id}' is not a valid region id: letters, digits, '-' and '_'.", 2);

        var planet = options.GetValueOrDefault("planet") ?? throw new MapPackToolException("--planet is required: a PMTiles planet URL or file.", 2);
        var maxZoom = int.Parse(options.GetValueOrDefault("maxzoom") ?? "14", CultureInfo.InvariantCulture);

        double[] bounds;
        string area;
        if (options.TryGetValue("bbox", out var bbox))
        {
            bounds = [.. bbox.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture))];
            area = $"--bbox={bbox}";
        }
        else if (options.TryGetValue("polygon", out var polygon))
        {
            bounds = BoundsOf(JsonNode.Parse(await File.ReadAllTextAsync(polygon, cancellationToken)));
            area = $"--region={Path.GetFullPath(polygon)}";
        }
        else
        {
            throw new MapPackToolException("Give the region's area with --bbox <w,s,e,n> or --polygon <file.geojson>.", 2);
        }

        if (!MapPackSignature.IsValidBounds(bounds))
            throw new MapPackToolException("The area must be west,south,east,north in degrees.", 2);

        var map = Path.Combine(directory, $"{id}.pmtiles");
        output.WriteLine($"Cutting {id}'s map to zoom {maxZoom} from {planet}…");
        await RunAsync(output, Tool("pmtiles"), cancellationToken, "extract", planet, map + ".tmp", area, $"--maxzoom={maxZoom}");
        File.Move(map + ".tmp", map, true);
        output.WriteLine($"  {id}.pmtiles  {Megabytes(new FileInfo(map).Length)}");

        if (options.TryGetValue("osm", out var osm))
            await DirectionsAsync(id, osm, options.GetValueOrDefault("valhalla"), directory, output, cancellationToken);

        var regions = ReadRegions(directory).Where(x => x.Id != id).ToList();
        regions.Add(new RegionEntry(id, options.GetValueOrDefault("name") ?? id, bounds, maxZoom));
        WriteRegions(directory, regions);
        output.WriteLine($"regions.json lists {regions.Count} region(s).");
    }

    static async Task DirectionsAsync(string id, string osm, string? valhalla, string directory, TextWriter output, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "shiny-map-packs", id + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            var pbf = Path.Combine(work, "region.osm.pbf");
            if (Uri.TryCreate(osm, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                output.WriteLine($"Downloading {url}…");
                using var http = new HttpClient();
                await using var input = await http.GetStreamAsync(url, cancellationToken);
                await using var file = File.Create(pbf);
                await input.CopyToAsync(file, cancellationToken);
            }
            else
            {
                File.Copy(osm, pbf);
            }

            var mode = valhalla ?? (FindOnPath("valhalla_build_tiles") is null ? "docker" : "local");
            output.WriteLine($"Building {id}'s road network with Valhalla ({mode})…");

            if (mode == "docker")
            {
                await RunAsync(output, "docker", cancellationToken,
                    "run", "--rm", "-v", $"{work}:/data", "--entrypoint", "/bin/sh", ValhallaImage, "-c",
                    "valhalla_build_config --mjolnir-tile-dir /data/tiles --mjolnir-tile-extract /data/tiles.tar > /data/valhalla.json"
                    + " && valhalla_build_tiles -c /data/valhalla.json /data/region.osm.pbf"
                    + " && valhalla_build_extract -c /data/valhalla.json -v");
            }
            else
            {
                var version = await CaptureAsync(Tool("valhalla_build_tiles"), cancellationToken, "--version");
                if (!version.Contains(ValhallaVersion, StringComparison.Ordinal))
                    output.WriteLine($"  warning: valhalla_build_tiles is {version.Trim()}; on-device directions read road networks built by Valhalla {ValhallaVersion}. Use --valhalla docker.");

                var config = Path.Combine(work, "valhalla.json");
                var configText = await CaptureAsync(Tool("valhalla_build_config"), cancellationToken,
                    "--mjolnir-tile-dir", Path.Combine(work, "tiles"), "--mjolnir-tile-extract", Path.Combine(work, "tiles.tar"));
                await File.WriteAllTextAsync(config, configText, cancellationToken);
                await RunAsync(output, Tool("valhalla_build_tiles"), cancellationToken, "-c", config, pbf);
                await RunAsync(output, Tool("valhalla_build_extract"), cancellationToken, "-c", config, "-v");
            }

            var tar = Path.Combine(directory, $"{id}.valhalla.tar");
            File.Move(Path.Combine(work, "tiles.tar"), tar, true);
            output.WriteLine($"  {id}.valhalla.tar  {Megabytes(new FileInfo(tar).Length)}");
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The basemaps-assets repository's fonts and v4 sprites, repacked as <c>fonts/…</c> and <c>sprites/v4/…</c>.</summary>
    static async Task AssetsAsync(string source, string directory, TextWriter output, CancellationToken cancellationToken)
    {
        output.WriteLine($"Downloading {source}…");
        using var http = new HttpClient();
        await using var download = new MemoryStream();
        await using (var input = await http.GetStreamAsync(source, cancellationToken))
            await input.CopyToAsync(download, cancellationToken);

        download.Position = 0;
        using var archive = new ZipArchive(download, ZipArchiveMode.Read);

        var target = Path.Combine(directory, FileSystemMapPackStore.AssetsFile);
        await using (var file = File.Create(target + ".tmp"))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                // "basemaps-assets-main/fonts/Noto Sans Regular/0-255.pbf" → "fonts/Noto Sans Regular/0-255.pbf"
                var slash = entry.FullName.IndexOf('/');
                var path = slash < 0 ? entry.FullName : entry.FullName[(slash + 1)..];

                if (entry.Length == 0 || !(path.StartsWith("fonts/", StringComparison.Ordinal) || path.StartsWith("sprites/v4/", StringComparison.Ordinal)))
                    continue;

                var copy = zip.CreateEntry(path, CompressionLevel.Optimal);
                await using var from = entry.Open();
                await using var to = copy.Open();
                await from.CopyToAsync(to, cancellationToken);
                count++;
            }

            if (count == 0)
                throw new MapPackToolException($"{source} has no fonts/ or sprites/v4/ files.");
        }

        File.Move(target + ".tmp", target, true);
        output.WriteLine($"  {FileSystemMapPackStore.AssetsFile}  {Megabytes(new FileInfo(target).Length)}");
    }

    static void Remove(string id, string directory, TextWriter output)
    {
        foreach (var name in new[] { $"{id}.pmtiles", $"{id}.valhalla.tar" })
            File.Delete(Path.Combine(directory, name));

        WriteRegions(directory, [.. ReadRegions(directory).Where(x => x.Id != id)]);
        output.WriteLine($"Removed {id}.");
    }

    static async Task ListAsync(string directory, TextWriter output, CancellationToken cancellationToken)
    {
        var catalog = await new FileSystemMapPackStore(directory).GetCatalogAsync(cancellationToken);
        foreach (var region in catalog.Regions)
        {
            var directions = region.Directions is { } d ? $"directions {Megabytes(d.Size)}" : "no directions";
            output.WriteLine($"{region.Id,-24} {region.Name,-24} map {Megabytes(region.Map.Size),-10} {directions}");
        }

        output.WriteLine(catalog.Assets is { } a ? $"assets {Megabytes(a.Size)}" : "no assets.zip — run shiny-map-packs assets");
    }

    static List<RegionEntry> ReadRegions(string directory)
    {
        var path = Path.Combine(directory, FileSystemMapPackStore.RegionsFile);
        return File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(path), ToolJsonContext.Default.ListRegionEntry) ?? []
            : [];
    }

    static void WriteRegions(string directory, List<RegionEntry> regions)
    {
        var path = Path.Combine(directory, FileSystemMapPackStore.RegionsFile);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(regions.OrderBy(x => x.Id).ToList(), ToolJsonContext.Default.ListRegionEntry));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>The bounds of every coordinate in a GeoJSON geometry, feature or feature collection.</summary>
    internal static double[] BoundsOf(JsonNode? geojson)
    {
        double west = 180, south = 90, east = -180, north = -90;
        var any = false;

        void Walk(JsonNode? node)
        {
            if (node is not JsonArray array)
            {
                if (node is JsonObject o)
                {
                    foreach (var (_, value) in o)
                        Walk(value);
                }

                return;
            }

            if (array is [JsonValue x, JsonValue y, ..] && x.TryGetValue<double>(out var lon) && y.TryGetValue<double>(out var lat))
            {
                west = Math.Min(west, lon);
                east = Math.Max(east, lon);
                south = Math.Min(south, lat);
                north = Math.Max(north, lat);
                any = true;
                return;
            }

            foreach (var item in array)
                Walk(item);
        }

        Walk(geojson);
        return any ? [west, south, east, north] : throw new MapPackToolException("The polygon file has no coordinates.", 2);
    }

    static string Tool(string name)
        => FindOnPath(name) ?? throw new MapPackToolException(name switch
        {
            "pmtiles" => "The pmtiles CLI is not on PATH. Install it from https://github.com/protomaps/go-pmtiles/releases.",
            _ => $"{name} is not on PATH. Install Valhalla, or pass --valhalla docker."
        });

    static string? FindOnPath(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name + ".cmd", name } : [name];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(n => Path.Combine(dir, n)))
            .FirstOrDefault(File.Exists);
    }

    static async Task RunAsync(TextWriter output, string file, CancellationToken cancellationToken, params string[] args)
    {
        using var process = Start(file, args, redirect: false);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new MapPackToolException($"{Path.GetFileName(file)} exited with {process.ExitCode}.");
    }

    static async Task<string> CaptureAsync(string file, CancellationToken cancellationToken, params string[] args)
    {
        using var process = Start(file, args, redirect: true);
        var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new MapPackToolException($"{Path.GetFileName(file)} exited with {process.ExitCode}.");

        return text;
    }

    static Process Start(string file, string[] args, bool redirect)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = redirect, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        return Process.Start(info) ?? throw new MapPackToolException($"{file} could not be started.");
    }

    static string Megabytes(long bytes) => $"{bytes / 1048576d:0.#} MB";
}

sealed record RegionEntry(string Id, string? Name, double[] Bounds, int? MaxZoom);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(List<RegionEntry>))]
partial class ToolJsonContext : JsonSerializerContext;
