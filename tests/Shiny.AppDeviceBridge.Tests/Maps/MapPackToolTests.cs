using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.MapPacks;
using static Shiny.AppDeviceBridge.Tests.Maps.MapPackCatalogTests;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// <c>shiny-map-packs</c>, for what runs without the pmtiles CLI or Valhalla: reading a region's area, listing and removing
/// regions, and refusing bad arguments. Cutting and building were run by hand against Colorado.
/// </summary>
public class MapPackToolTests
{
    [Fact]
    public void Bounds_come_from_every_coordinate_of_a_geojson_file()
    {
        var geojson = JsonNode.Parse("""
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "properties": { "name": "a" }, "geometry": { "type": "Polygon", "coordinates": [[[-109, 37], [-102, 37], [-102, 41], [-109, 41], [-109, 37]]] } },
              { "type": "Feature", "geometry": { "type": "MultiPolygon", "coordinates": [[[[-110.5, 36.5], [-110, 36.5], [-110, 36.9], [-110.5, 36.5]]]] } }
            ] }
            """);

        Assert.Equal([-110.5, 36.5, -102, 41], MapPackTool.BoundsOf(geojson));
    }

    [Fact]
    public void A_geojson_file_without_coordinates_is_refused()
        => Assert.Throws<MapPackToolException>(() => MapPackTool.BoundsOf(JsonNode.Parse("""{ "type": "FeatureCollection", "features": [] }""")));

    [Fact]
    public async Task Lists_and_removes_regions()
    {
        using var packs = PackDirectory.Create();
        var output = new StringWriter();

        Assert.Equal(0, await MapPackTool.RunAsync(["list", "--out", packs.Path], output, CancellationToken.None));
        Assert.Contains("boulder", output.ToString());
        Assert.Contains("directions", output.ToString());
        Assert.Contains("assets", output.ToString());

        Assert.Equal(0, await MapPackTool.RunAsync(["remove", "boulder", $"--out={packs.Path}"], output, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(packs.Path, "boulder.pmtiles")));
        Assert.False(File.Exists(Path.Combine(packs.Path, "boulder.valhalla.tar")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(packs.Path, "regions.json")).Trim());
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("region")]
    [InlineData("region", "bad id!", "--planet", "p.pmtiles", "--bbox=-1,-1,1,1")]
    [InlineData("region", "ok", "--bbox=-1,-1,1,1")]
    [InlineData("region", "ok", "--planet", "p.pmtiles")]
    [InlineData("region", "ok", "--planet", "p.pmtiles", "--bbox=1,1,-1,-1")]
    public async Task Refuses_bad_arguments_before_doing_anything(params string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), "appdevicebridge-maps", Guid.NewGuid().ToString("n"));

        var error = await Assert.ThrowsAsync<MapPackToolException>(() => MapPackTool.RunAsync([.. args, "--out", directory], TextWriter.Null, CancellationToken.None));

        Assert.Equal(2, error.ExitCode);
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any());
    }
}
