using System.Text.RegularExpressions;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>What <c>bridgemap.js</c> needs from the files packed beside it. The drawing itself is checked in the sample app.</summary>
public class BridgeMapScriptTests
{
    static readonly string Root = Path.Combine(RepositoryRoot(), "src", "Shiny.AppDeviceBridge.Maps.Blazor", "wwwroot");

    /// <summary>
    /// Without MapLibre's stylesheet a marker is not positioned absolutely, so a pin dropped on a click sits in the page's flow
    /// under the map instead of where the click was. The component loads it itself, before the map is created, so no page has
    /// to remember a link tag.
    /// </summary>
    [Fact]
    public void LoadsMapLibresStylesheetBeforeCreatingTheMap()
    {
        var script = File.ReadAllText(Path.Combine(Root, "bridgemap.js"));

        var href = Regex.Match(script, """new URL\("\./(?<path>[^"]+\.css)", import\.meta\.url\)""");
        Assert.True(href.Success, "bridgemap.js does not load a stylesheet relative to itself.");
        Assert.True(File.Exists(Path.Combine(Root, href.Groups["path"].Value)), $"{href.Groups["path"].Value} is not in the package.");

        var create = script[script.IndexOf("export async function create", StringComparison.Ordinal)..];
        Assert.True(
            create.IndexOf("await loadStylesheet()", StringComparison.Ordinal) < create.IndexOf("new maplibregl.Map", StringComparison.Ordinal),
            "create builds the map before the stylesheet has loaded."
        );
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shiny.AppDeviceBridge.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Not inside the repository.");
    }
}
