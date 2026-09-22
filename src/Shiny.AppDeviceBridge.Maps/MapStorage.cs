using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>A region on the device: which of its parts are installed, and which builds.</summary>
sealed record InstalledRegion
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double[] Bounds { get; init; }
    public int MaxZoom { get; init; }
    public string? MapVersion { get; init; }
    public long MapSize { get; init; }
    public string? DirectionsVersion { get; init; }
    public long DirectionsSize { get; init; }
}

sealed record InstalledAssets(string Version, long Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(InstalledRegion))]
[JsonSerializable(typeof(InstalledAssets))]
partial class MapStorageJsonContext : JsonSerializerContext;

/// <summary>
/// Where maps live on the device:
/// <code>
/// {root}/packs/{region}/map.pmtiles
/// {root}/packs/{region}/directions.tar
/// {root}/packs/{region}/region.json
/// {root}/assets/fonts/…, sprites/…     the installed asset pack
/// {root}/assets/assets.json
/// {root}/fetched/fonts/…, sprites/…    assets fetched online, kept so labels draw offline
/// {root}/catalog.json                  the last catalog fetched
/// {root}/.downloads/                   partial downloads, resumed on the next install
/// </code>
/// </summary>
sealed class MapStorage
{
    public MapStorage(string root, string cacheRoot)
    {
        this.Root = root;
        this.TileCache = cacheRoot;
        Directory.CreateDirectory(this.Packs);
        Directory.CreateDirectory(this.Downloads);
    }

    public string Root { get; }
    public string TileCache { get; }
    public string Packs => Path.Combine(this.Root, "packs");
    public string Assets => Path.Combine(this.Root, "assets");
    public string FetchedAssets => Path.Combine(this.Root, "fetched");
    public string Downloads => Path.Combine(this.Root, ".downloads");
    public string CatalogFile => Path.Combine(this.Root, "catalog.json");

    public string RegionDirectory(string id) => Path.Combine(this.Packs, id);
    public string MapFile(string id) => Path.Combine(this.RegionDirectory(id), "map.pmtiles");
    public string DirectionsFile(string id) => Path.Combine(this.RegionDirectory(id), "directions.tar");
    string RegionFile(string id) => Path.Combine(this.RegionDirectory(id), "region.json");
    string AssetsFile => Path.Combine(this.Assets, "assets.json");

    public IReadOnlyList<InstalledRegion> ReadRegions()
    {
        var regions = new List<InstalledRegion>();

        foreach (var directory in Directory.EnumerateDirectories(this.Packs))
        {
            var id = Path.GetFileName(directory);
            if (!MapPackSignature.IsValidRegionId(id) || this.ReadRegion(id) is not { } region)
                continue;

            // A part whose file went missing is not installed, whatever the record says.
            if (region.MapVersion is not null && !File.Exists(this.MapFile(id)))
                region = region with { MapVersion = null, MapSize = 0 };

            if (region.DirectionsVersion is not null && !File.Exists(this.DirectionsFile(id)))
                region = region with { DirectionsVersion = null, DirectionsSize = 0 };

            regions.Add(region);
        }

        return regions;
    }

    public InstalledRegion? ReadRegion(string id)
    {
        try
        {
            return File.Exists(this.RegionFile(id))
                ? JsonSerializer.Deserialize(File.ReadAllBytes(this.RegionFile(id)), MapStorageJsonContext.Default.InstalledRegion)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void WriteRegion(InstalledRegion region)
    {
        Directory.CreateDirectory(this.RegionDirectory(region.Id));
        WriteAtomically(this.RegionFile(region.Id), JsonSerializer.SerializeToUtf8Bytes(region, MapStorageJsonContext.Default.InstalledRegion));
    }

    public InstalledAssets? ReadAssets()
    {
        try
        {
            return File.Exists(this.AssetsFile)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(this.AssetsFile), MapStorageJsonContext.Default.InstalledAssets)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void WriteAssets(InstalledAssets assets)
        => WriteAtomically(this.AssetsFile, JsonSerializer.SerializeToUtf8Bytes(assets, MapStorageJsonContext.Default.InstalledAssets));

    public static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Keeps a downloaded region out of iCloud and iTunes backups: it can be downloaded again, and Apple rejects apps that
    /// back up hundreds of megabytes of it. Everywhere else, nothing to do.
    /// </summary>
    public static void ExcludeFromBackup(string path)
    {
        if (!(OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()))
            return;

        // The attribute NSURLIsExcludedFromBackupKey sets, written directly so this net10.0 build needs no Apple bindings.
        var value = "com.apple.backupd"u8.ToArray();
        _ = NativeMethods.setxattr(path, "com.apple.metadata:com_apple_backup_excludeItem", value, (nuint)value.Length, 0, 0);
    }
}

static partial class NativeMethods
{
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int setxattr(string path, string name, byte[] value, nuint size, uint position, int options);
}
