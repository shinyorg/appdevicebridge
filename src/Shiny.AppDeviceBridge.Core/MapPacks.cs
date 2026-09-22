using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge;

/// <summary>What a map pack file holds.</summary>
public enum MapPackPartKind
{
    /// <summary>Vector map tiles: a PMTiles archive.</summary>
    Map,

    /// <summary>Road network for on-device directions: a Valhalla tile extract (<c>.tar</c>).</summary>
    Directions,

    /// <summary>The fonts (glyphs) and sprites the map style draws labels and icons with: a zip.</summary>
    Assets
}

/// <summary>
/// One downloadable file of a region. <see cref="Kind"/>, <see cref="Version"/>, <see cref="Sha256"/> and <see cref="Size"/>
/// are signed together with the region's id and bounds — see <see cref="MapPackSignature"/> — so a file is only used when
/// the catalog's server vouched for exactly those bytes covering exactly that area.
/// </summary>
public sealed record MapPackPart
{
    public required MapPackPartKind Kind { get; init; }

    /// <summary>Where to download it. Relative URLs resolve against the catalog's URL.</summary>
    public required string Url { get; init; }

    /// <summary>A build identifier, such as the date of the OpenStreetMap data. A newer version replaces an installed one.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 of the file, as 64 lowercase hex characters.</summary>
    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    /// <summary>The signature over this part — see <see cref="MapPackSignature"/>. Set by the server that publishes the catalog.</summary>
    public string? Signature { get; init; }
}

/// <summary>A downloadable area: a province, a state, a city.</summary>
public sealed record MapPackRegion
{
    /// <summary>Letters, digits, <c>-</c> and <c>_</c>; unique within the catalog.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>[west, south, east, north]</c> in degrees. The area the region's files cover.</summary>
    public required double[] Bounds { get; init; }

    /// <summary>The map tiles. Required.</summary>
    public required MapPackPart Map { get; init; }

    /// <summary>The road network for on-device directions, when the region offers it.</summary>
    public MapPackPart? Directions { get; init; }

    /// <summary>The highest zoom the map tiles carry; the map draws closer zooms from these.</summary>
    public int MaxZoom { get; init; } = 14;
}

/// <summary>Every region a server offers, and the style assets they share.</summary>
public sealed record MapPackCatalog
{
    public required IReadOnlyList<MapPackRegion> Regions { get; init; }

    /// <summary>Glyphs and sprites, installed with the first region so labels and icons draw offline.</summary>
    public MapPackPart? Assets { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// The ECDSA P-256 signature over one map pack part, with the same keys as <see cref="WebAppReleaseSignature"/>: an app
/// that trusts a release server for its web app can trust it for its maps, and a key signs both without either signature
/// ever verifying as the other.
/// </summary>
public static class MapPackSignature
{
    /// <summary>Prefixed so a signature made for anything else — a web app release included — can never verify as a map pack.</summary>
    const string Scheme = "shiny-map-pack-v1";

    /// <summary>The id <see cref="MapPackCatalog.Assets"/> is signed under, since the assets belong to no region.</summary>
    public const string AssetsRegionId = "_assets";

    /// <summary>The exact bytes that are signed. One field per line, in a fixed order.</summary>
    public static byte[] GetPayload(string regionId, double[]? bounds, MapPackPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (regionId != AssetsRegionId && !IsValidRegionId(regionId))
            throw new ArgumentException($"'{regionId}' is not a valid region id.", nameof(regionId));

        if (bounds is not null && !IsValidBounds(bounds))
            throw new ArgumentException("Bounds must be [west, south, east, north] in degrees.", nameof(bounds));

        if (!WebAppReleaseSignature.IsSha256Hex(part.Sha256))
            throw new ArgumentException("Sha256 must be 64 hex characters.", nameof(part));

        if (String.IsNullOrWhiteSpace(part.Version) || part.Version.Contains('\n') || part.Version.Contains('\r'))
            throw new ArgumentException("Version must be a single line.", nameof(part));

        var text = String.Join(
            '\n',
            Scheme,
            regionId,
            bounds is null ? String.Empty : String.Join(',', bounds.Select(x => x.ToString("R", CultureInfo.InvariantCulture))),
            part.Kind.ToString(),
            part.Version.Trim(),
            part.Sha256.ToLowerInvariant(),
            part.Size.ToString(CultureInfo.InvariantCulture)
        );

        return Encoding.UTF8.GetBytes(text);
    }

    public static string Sign(string regionId, double[]? bounds, MapPackPart part, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        return Convert.ToBase64String(privateKey.SignData(
            GetPayload(regionId, bounds, part),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        ));
    }

    /// <summary>False for a missing, malformed or non-matching signature. Never throws for bad input.</summary>
    public static bool Verify(string regionId, double[]? bounds, MapPackPart? part, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (part is null || String.IsNullOrWhiteSpace(part.Signature))
            return false;

        try
        {
            return publicKey.VerifyData(
                GetPayload(regionId, bounds, part),
                Convert.FromBase64String(part.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            );
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Returns the catalog with every part signed.</summary>
    public static MapPackCatalog SignCatalog(MapPackCatalog catalog, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog with
        {
            Assets = catalog.Assets is { } assets
                ? assets with { Signature = Sign(AssetsRegionId, null, assets, privateKey) }
                : null,
            Regions =
            [
                .. catalog.Regions.Select(r => r with
                {
                    Map = r.Map with { Signature = Sign(r.Id, r.Bounds, r.Map, privateKey) },
                    Directions = r.Directions is { } d ? d with { Signature = Sign(r.Id, r.Bounds, d, privateKey) } : null
                })
            ]
        };
    }

    public static bool IsValidRegionId(string? id)
        => !String.IsNullOrEmpty(id)
           && id.Length <= 64
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsValidBounds(double[]? bounds)
        => bounds is [var west, var south, var east, var north]
           && west is >= -180 and <= 180
           && east is >= -180 and <= 180
           && south is >= -90 and <= 90
           && north is >= -90 and <= 90
           && south < north
           && west < east;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(MapPackCatalog))]
public partial class MapPackJsonContext : JsonSerializerContext;
