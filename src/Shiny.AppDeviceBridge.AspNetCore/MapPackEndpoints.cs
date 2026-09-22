using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AppDeviceBridge.AspNetCore;

/// <summary>Where a map pack server's regions and files come from.</summary>
public interface IMapPackStore
{
    /// <summary>The catalog, unsigned — the endpoint signs it. File URLs are relative to the catalog's URL.</summary>
    Task<MapPackCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>A file the catalog names, or null.</summary>
    Task<Stream?> OpenFileAsync(string name, CancellationToken cancellationToken);
}

public sealed class MapPackServerOptions
{
    /// <summary>The ECDSA P-256 private key the catalog is signed with, as PEM. Usually the web app release key.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Serves regions from this directory with <see cref="FileSystemMapPackStore"/>. Leave null to register a store of your own.</summary>
    public string? PacksDirectory { get; set; }
}

/// <summary>Signs catalogs with one key, safely from concurrent requests.</summary>
public sealed class MapPackSigner(string privateKeyPem) : IDisposable
{
    readonly ECDsa key = WebAppReleaseSignature.ImportPrivateKey(privateKeyPem);
    readonly Lock gate = new();

    public MapPackCatalog Sign(MapPackCatalog catalog)
    {
        lock (this.gate)
            return MapPackSignature.SignCatalog(catalog, this.key);
    }

    public void Dispose() => this.key.Dispose();
}

public static class MapPackExtensions
{
    /// <summary>
    /// Registers the catalog signer and, when <see cref="MapPackServerOptions.PacksDirectory"/> is set, the file system store.
    /// <code>
    /// builder.Services.AddMapPacks(o =>
    /// {
    ///     o.SigningKey = builder.Configuration["WebApps:SigningKey"];
    ///     o.PacksDirectory = "/srv/maps";
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddMapPacks(this IServiceCollection services, Action<MapPackServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MapPackServerOptions();
        configure(options);

        if (String.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException("MapPackServerOptions.SigningKey is required. Apps only install regions whose signature checks out.");

        services.TryAddSingleton(new MapPackSigner(options.SigningKey));
        if (!String.IsNullOrWhiteSpace(options.PacksDirectory))
            services.TryAddSingleton<IMapPackStore>(new FileSystemMapPackStore(options.PacksDirectory));

        return services;
    }

    /// <summary>
    /// Maps <c>GET {prefix}/catalog</c> — the signed catalog an app's <c>MapsOptions.Catalog</c> points at — and
    /// <c>GET {prefix}/files/{name}</c>, with range support so an interrupted region download resumes. Returns the group.
    /// </summary>
    public static RouteGroupBuilder MapMapPacks(this IEndpointRouteBuilder endpoints, string prefix = "/maps")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IMapPackStore>() is null)
            throw new InvalidOperationException("No IMapPackStore is registered. Set PacksDirectory in AddMapPacks, or register a store of your own.");

        var group = endpoints.MapGroup(prefix);
        group.MapGet("/catalog", CatalogAsync);
        group.MapGet("/files/{name}", FileAsync);
        return group;
    }

    static async Task CatalogAsync(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<IMapPackStore>();
        var signed = context.RequestServices.GetRequiredService<MapPackSigner>().Sign(await store.GetCatalogAsync(context.RequestAborted));

        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsJsonAsync(signed, MapPackJsonContext.Default.MapPackCatalog, cancellationToken: context.RequestAborted);
    }

    static async Task FileAsync(HttpContext context)
    {
        var name = context.Request.RouteValues["name"] as string;
        if (!FileSystemMapPackStore.IsValidFileName(name))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var stream = await context.RequestServices.GetRequiredService<IMapPackStore>().OpenFileAsync(name!, context.RequestAborted);
        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await Results.Stream(stream, "application/octet-stream", enableRangeProcessing: stream.CanSeek).ExecuteAsync(context);
    }
}

/// <summary>
/// Regions laid out in a directory:
/// <code>
/// maps/
///   regions.json            [ { "id": "colorado", "name": "Colorado", "bounds": [-109.06, 36.99, -102.04, 41.0], "maxZoom": 14 } ]
///   colorado.pmtiles        the map, required
///   colorado.valhalla.tar   the road network, optional
///   assets.zip              glyphs and sprites, optional
/// </code>
/// Publishing is copying files: a part's hash and size are computed on first sight and cached until the file changes,
/// and its version is the start of its hash, so a changed file is an update to every app that has the region.
/// <c>shiny-map-packs</c> writes this layout.
/// </summary>
public sealed class FileSystemMapPackStore : IMapPackStore
{
    public const string RegionsFile = "regions.json";
    public const string AssetsFile = "assets.zip";

    readonly string root;
    readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Sha256)> hashes = new(StringComparer.Ordinal);

    public FileSystemMapPackStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.root = Path.GetFullPath(rootDirectory);
    }

    public async Task<MapPackCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var regionsPath = Path.Combine(this.root, RegionsFile);
        var entries = File.Exists(regionsPath)
            ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(regionsPath, cancellationToken), MapPackStoreJsonContext.Default.ListRegionEntry) ?? []
            : [];

        var regions = new List<MapPackRegion>();
        foreach (var entry in entries)
        {
            if (!MapPackSignature.IsValidRegionId(entry.Id) || !MapPackSignature.IsValidBounds(entry.Bounds))
                continue;

            if (await this.DescribeAsync($"{entry.Id}.pmtiles", MapPackPartKind.Map, cancellationToken) is not { } map)
                continue;

            regions.Add(new MapPackRegion
            {
                Id = entry.Id,
                Name = String.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                Bounds = entry.Bounds,
                MaxZoom = entry.MaxZoom ?? 14,
                Map = map,
                Directions = await this.DescribeAsync($"{entry.Id}.valhalla.tar", MapPackPartKind.Directions, cancellationToken)
            });
        }

        return new MapPackCatalog
        {
            Regions = regions,
            Assets = await this.DescribeAsync(AssetsFile, MapPackPartKind.Assets, cancellationToken),
            PublishedAt = File.Exists(regionsPath) ? File.GetLastWriteTimeUtc(regionsPath) : null
        };
    }

    public Task<Stream?> OpenFileAsync(string name, CancellationToken cancellationToken)
    {
        if (!IsValidFileName(name))
            return Task.FromResult<Stream?>(null);

        var path = Path.Combine(this.root, name);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    async Task<MapPackPart?> DescribeAsync(string name, MapPackPartKind kind, CancellationToken cancellationToken)
    {
        var path = Path.Combine(this.root, name);
        var file = new FileInfo(path);
        if (!file.Exists)
            return null;

        if (!this.hashes.TryGetValue(name, out var known) || known.Length != file.Length || known.Modified != file.LastWriteTimeUtc)
        {
            await using var stream = file.OpenRead();
            known = (file.Length, file.LastWriteTimeUtc, await WebAppReleaseSignature.ComputeSha256Async(stream, cancellationToken));
            this.hashes[name] = known;
        }

        return new MapPackPart
        {
            Kind = kind,
            Url = $"files/{Uri.EscapeDataString(name)}",
            Version = known.Sha256[..12],
            Sha256 = known.Sha256,
            Size = known.Length
        };
    }

    /// <summary>A file name in the directory itself: no separators, no dots at the front.</summary>
    public static bool IsValidFileName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 128
           && name[0] != '.'
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

sealed record RegionEntry(string Id, string? Name, double[] Bounds, int? MaxZoom);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(List<RegionEntry>))]
partial class MapPackStoreJsonContext : JsonSerializerContext;
