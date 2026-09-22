using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AspNetCore;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>The signature over map pack parts, and the release server's store and endpoints that publish them.</summary>
public class MapPackCatalogTests
{
    static readonly double[] Colorado = [-109.06, 36.99, -102.04, 41.0];

    static MapPackPart Part(MapPackPartKind kind = MapPackPartKind.Map) => new()
    {
        Kind = kind,
        Url = "files/colorado.pmtiles",
        Version = "abc123",
        Sha256 = new string('a', 64),
        Size = 1234
    };

    [Fact]
    public void A_signed_part_verifies_with_the_public_key()
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var part = Part() with { Signature = MapPackSignature.Sign("colorado", Colorado, Part(), privateKey) };

        Assert.True(MapPackSignature.Verify("colorado", Colorado, part, publicKey));
    }

    [Fact]
    public void Changing_anything_signed_breaks_the_signature()
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);
        var signed = Part() with { Signature = MapPackSignature.Sign("colorado", Colorado, Part(), privateKey) };

        Assert.False(MapPackSignature.Verify("utah", Colorado, signed, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", [-110, 36.99, -102.04, 41.0], signed, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Sha256 = new string('b', 64) }, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Size = 1 }, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Version = "newer" }, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Kind = MapPackPartKind.Directions }, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Signature = "not base64!" }, publicKey));
        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed with { Signature = null }, publicKey));

        // Not a signed field: where the file is downloaded from. The hash is what makes the bytes trustworthy.
        Assert.True(MapPackSignature.Verify("colorado", Colorado, signed with { Url = "https://cdn.example.com/c.pmtiles" }, publicKey));
    }

    [Fact]
    public void Another_key_does_not_verify()
    {
        var (_, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        var (otherPublic, _) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(otherPublic);

        var signed = Part() with { Signature = MapPackSignature.Sign("colorado", Colorado, Part(), privateKey) };

        Assert.False(MapPackSignature.Verify("colorado", Colorado, signed, publicKey));
    }

    [Fact]
    public void A_web_app_release_signature_never_verifies_as_a_map_pack()
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var release = new WebAppRelease { AppId = "colorado", Version = "1.0.0", Sha256 = new string('a', 64), Size = 1234 };
        var part = Part() with { Signature = WebAppReleaseSignature.Sign(release, privateKey) };

        Assert.False(MapPackSignature.Verify("colorado", Colorado, part, publicKey));
    }

    [Theory]
    [InlineData("colorado", true)]
    [InlineData("new-york_2", true)]
    [InlineData("", false)]
    [InlineData("../etc", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    public void Region_ids_are_path_safe(string id, bool valid) => Assert.Equal(valid, MapPackSignature.IsValidRegionId(id));

    [Fact]
    public void Bounds_must_be_west_south_east_north()
    {
        Assert.True(MapPackSignature.IsValidBounds(Colorado));
        Assert.False(MapPackSignature.IsValidBounds([-102, 36, -109, 41]));   // west of east
        Assert.False(MapPackSignature.IsValidBounds([-109, 41, -102, 36]));   // south of north
        Assert.False(MapPackSignature.IsValidBounds([-190, 36, -102, 41]));
        Assert.False(MapPackSignature.IsValidBounds([1, 2, 3]));
        Assert.False(MapPackSignature.IsValidBounds(null));
    }

    [Fact]
    public async Task The_file_system_store_describes_each_part_from_the_files()
    {
        using var packs = PackDirectory.Create();

        var catalog = await new FileSystemMapPackStore(packs.Path).GetCatalogAsync(CancellationToken.None);

        var region = Assert.Single(catalog.Regions);
        Assert.Equal("boulder", region.Id);
        Assert.Equal("Boulder", region.Name);
        Assert.Equal(13, region.MaxZoom);
        Assert.Equal("files/boulder.pmtiles", region.Map.Url);
        Assert.Equal(packs.MapSha256, region.Map.Sha256);
        Assert.Equal(packs.MapSha256[..12], region.Map.Version);
        Assert.Equal(new FileInfo(Path.Combine(packs.Path, "boulder.pmtiles")).Length, region.Map.Size);
        Assert.Equal(MapPackPartKind.Directions, region.Directions!.Kind);
        Assert.Equal(MapPackPartKind.Assets, catalog.Assets!.Kind);
    }

    [Fact]
    public async Task A_region_without_its_map_file_is_left_out()
    {
        using var packs = PackDirectory.Create();
        File.Delete(Path.Combine(packs.Path, "boulder.pmtiles"));

        var catalog = await new FileSystemMapPackStore(packs.Path).GetCatalogAsync(CancellationToken.None);

        Assert.Empty(catalog.Regions);
    }

    [Fact]
    public async Task The_endpoints_serve_a_signed_catalog_and_resumable_files()
    {
        using var packs = PackDirectory.Create();
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        await using var server = await PackServer.StartAsync(packs.Path, privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var catalog = await server.Client.GetFromJsonAsync("/maps/catalog", MapPackJsonContext.Default.MapPackCatalog);
        var region = Assert.Single(catalog!.Regions);
        Assert.True(MapPackSignature.Verify(region.Id, region.Bounds, region.Map, publicKey));
        Assert.True(MapPackSignature.Verify(region.Id, region.Bounds, region.Directions, publicKey));
        Assert.True(MapPackSignature.Verify(MapPackSignature.AssetsRegionId, null, catalog.Assets, publicKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/maps/" + region.Map.Url);
        request.Headers.Range = new RangeHeaderValue(0, 6);
        using var partial = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("PMTiles", await partial.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/maps/files/..%2Fregions.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/maps/files/nope.pmtiles")).StatusCode);
    }

    [Fact]
    public void The_store_needs_a_signing_key()
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMapPacks(o => o.PacksDirectory = "/tmp"));

    /// <summary>A pack directory as shiny-map-packs writes it: the Boulder fixture, a stand-in road network and assets.</summary>
    internal sealed class PackDirectory : IDisposable
    {
        public required string Path { get; init; }
        public required string MapSha256 { get; init; }

        public static PackDirectory Create(string directionsContent = "valhalla tiles")
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appdevicebridge-maps", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(path);

            var map = System.IO.Path.Combine(path, "boulder.pmtiles");
            File.Copy(PmTilesTests.Fixture("boulder.pmtiles"), map);
            File.WriteAllText(System.IO.Path.Combine(path, "boulder.valhalla.tar"), directionsContent);
            File.WriteAllText(System.IO.Path.Combine(path, "regions.json"), """
                [ { "id": "boulder", "name": "Boulder", "bounds": [-105.285, 40.005, -105.265, 40.02], "maxZoom": 13 } ]
                """);

            using (var zip = System.IO.Compression.ZipFile.Open(System.IO.Path.Combine(path, "assets.zip"), System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("fonts/Noto Sans Regular/0-255.pbf").Open()))
                    w.Write("glyphs 0-255");
                using (var w = new StreamWriter(zip.CreateEntry("sprites/v4/light.json").Open()))
                    w.Write("""{"icon":{}}""");
            }

            return new PackDirectory { Path = path, MapSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(map))) };
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The release server's map pack endpoints on TestServer.</summary>
    internal sealed class PackServer : IAsyncDisposable
    {
        WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public HttpMessageHandler CreateHandler() => this.app.GetTestServer().CreateHandler();

        public static async Task<PackServer> StartAsync(string directory, string signingKey)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddMapPacks(o =>
            {
                o.SigningKey = signingKey;
                o.PacksDirectory = directory;
            });

            var server = new PackServer { app = builder.Build() };
            server.app.MapMapPacks();
            await server.app.StartAsync();
            server.Client = server.app.GetTestClient();
            return server;
        }

        public async ValueTask DisposeAsync() => await this.app.DisposeAsync();
    }
}
