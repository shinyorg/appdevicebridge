namespace Shiny.AppDeviceBridge.Tests;

public class FileRootOptionsTests
{
    static readonly string Scratch = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", "roots");

    [Fact]
    public void DefaultsToDataAndCache()
    {
        var options = new WebAppHostOptions { AppId = "demo", InstallDirectory = Scratch };

        var roots = options.ResolveFileRoots();

        Assert.Equal(["data", "cache"], roots.Select(x => x.Name));
        Assert.Equal(Path.Combine(Scratch, "files"), roots[0].FullPath);
    }

    [Fact]
    public void ConfiguredRootsReplaceTheDefaults()
    {
        var options = new WebAppHostOptions { AppId = "demo", InstallDirectory = Scratch };
        options.FileRoots["media"] = Path.Combine(Scratch, "media") + Path.DirectorySeparatorChar;

        var root = Assert.Single(options.ResolveFileRoots());

        Assert.Equal("media", root.Name);
        Assert.Equal(Path.Combine(Scratch, "media"), root.FullPath);
    }
}
