using System.Text.RegularExpressions;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;
using Shiny.AppDeviceBridge.Simulator.Tui;
using Shiny.AppDeviceBridge.Wifi.Client;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The simulator's screens, rendered off-screen: what a person at the terminal would see.</summary>
public partial class SimulatorTuiTests : IAsyncLifetime
{
    const int Width = 160;
    const int Height = 44;

    SimulatorHost host = null!;
    SimulatorShell shell = null!;
    Visual root = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Platform = "android" });
        this.http = new HttpClient { BaseAddress = await this.host.StartAsync(TestContext.Current.CancellationToken) };
        this.shell = new SimulatorShell(this.host);
        this.root = this.shell.Build();
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Tags();

    string Render(string name)
    {
        // What the loop does each turn.
        this.shell.Tick();
        var buffer = VisualSnapshotRenderer.Render(this.root, Width, Height, XenoAtom.Terminal.UI.Styling.Theme.Default);
        var markup = String.Join("\n", buffer.ToMarkupLines());
        var text = Tags().Replace(markup, "").Replace("[[", "[").Replace("]]", "]");

        if (Environment.GetEnvironmentVariable("TUI_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, name + ".txt"), text);
        }

        return text;
    }

    /// <summary>
    /// The editor the Bridges tab shows for <paramref name="data"/>, rendered on its own. Off-screen there is no loop to
    /// rebuild a computed visual after the first frame, so an editor is drawn directly rather than by moving the tree.
    /// </summary>
    string RenderEditor(object data, string name)
    {
        var buffer = VisualSnapshotRenderer.Render(this.shell.Bridges.BuildEditor(data), 110, 40, XenoAtom.Terminal.UI.Styling.Theme.Default);
        var text = Tags().Replace(String.Join("\n", buffer.ToMarkupLines()), "").Replace("[[", "[").Replace("]]", "]");

        if (Environment.GetEnvironmentVariable("TUI_DUMP") is { Length: > 0 } dump)
            File.WriteAllText(Path.Combine(dump, name + ".txt"), text);

        return text;
    }

    [Fact]
    public void Opens_on_the_host_with_every_bridge_in_the_tree()
    {
        var screen = this.Render("host");

        Assert.Contains("Shiny.AppDeviceBridge Simulator", screen);
        Assert.Contains(this.host.Origin!.AbsoluteUri, screen);
        Assert.Contains("The host", screen);
        Assert.Contains("android", screen);
        foreach (var name in new[] { "wifi", "gps", "ble", "notifications" })
            Assert.Contains(name, screen);

        Assert.Contains("Ctrl+Q quit", screen);
        Assert.DoesNotContain("\\u00", screen);
    }

    [Fact]
    public async Task A_route_shows_its_contract_its_value_and_how_often_it_was_called()
    {
        var route = this.host.State.FindRoute("wifi", "GET current")!;
        await new WifiBridgeClient(new BuiltInClientTests.HttpTransport(this.http)).GetCurrentNetworkAsync(TestContext.Current.CancellationToken);

        var screen = this.RenderEditor(route, "route");

        Assert.Contains("/_bridge/wifi/current", screen);
        Assert.Contains("WifiNetworkInfo", screen);
        Assert.Contains("\"interfaceName\"", screen);
        Assert.Contains("called ×1", screen);
        Assert.Contains("Apply", screen);
        Assert.Contains("GET    /_bridge/wifi/current", screen);    // the method's colour, not its markup
    }

    [Fact]
    public void An_event_shows_whether_a_page_is_listening()
    {
        var screen = this.RenderEditor(this.host.State.FindEvent("gps.reading")!, "event");

        Assert.Contains("gps.reading", screen);
        Assert.Contains("GpsReading", screen);
        Assert.Contains("no page stream is listening", screen);
        Assert.Contains("Fire", screen);
    }

    [Fact]
    public void A_bridge_summarises_what_each_route_answers()
    {
        this.host.State.SetBehavior("wifi", "POST connection", new RouteBehavior(ResponseMode.Error, "", 403, "access_denied", "no"));
        this.host.State.SetSupported("wifi", false);
        var screen = this.RenderEditor(this.host.State.Find("wifi")!, "bridge");

        Assert.Contains("403 access_denied", screen);
        Assert.Contains("Supported", screen);
        Assert.Contains("wifi.changed", screen);
        Assert.Contains("off", screen);
    }

    [Fact]
    public async Task The_traffic_tab_lists_requests_and_shows_the_whole_exchange()
    {
        await new WifiBridgeClient(new BuiltInClientTests.HttpTransport(this.http)).GetRadioAsync(TestContext.Current.CancellationToken);
        this.shell.Traffic.Refresh();
        this.shell.SelectTab(1);

        var screen = this.Render("traffic");

        Assert.Contains("1 request", screen);
        Assert.Contains("/_bridge/wifi/radio", screen);
        Assert.Contains("200 OK", screen);
        Assert.Contains("RESPONSE BODY", screen);
        Assert.Contains("\"enabled\"", screen);
    }

    [Fact]
    public void The_trails_tab_shows_a_loaded_trail_and_its_steps()
    {
        this.host.Trails.Add(GpxImporter.FromPoints([new(43.64, -79.38, 80, null), new(43.65, -79.38, 80, null)], "harbourfront", TimeSpan.FromSeconds(2)));
        this.shell.Trails.Sync();
        this.shell.SelectTab(2);

        var screen = this.Render("trails");

        Assert.Contains("harbourfront", screen);
        Assert.Contains("6 steps", screen);
        Assert.Contains("fire gps.reading", screen.Replace("point 1/2", "fire gps.reading"));
        Assert.Contains("Play", screen);
    }

    [Fact]
    public void The_activity_tab_shows_what_happened()
    {
        this.host.State.Fire("wifi.changed", """{ "current": null }""");
        this.shell.SelectTab(3);

        var screen = this.Render("activity");
        Assert.Contains("fired wifi.changed", screen);
    }
}
