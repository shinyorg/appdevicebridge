using System.Net;
using System.Text.Json;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>Serving the app, and the bridges, somewhere other than where they land by default.</summary>
public class MountPointTests
{
    [Fact]
    public async Task ServesEverythingUnderTheBasePath()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BasePath = "/kiosk";

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), [new EchoBridge()]);
        var start = await host.StartAsync();

        Assert.StartsWith("/kiosk/_host/start", start.PathAndQuery);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("version 1.0.0", await webView.GetStringAsync(start));

        Assert.Equal(HttpStatusCode.NoContent, (await webView.GetAsync(new Uri(host.Origin!, "/kiosk/_bridge/echo/ping"))).StatusCode);
        Assert.Contains("console.log", await webView.GetStringAsync(new Uri(host.Origin!, "/kiosk/app.js")));

        // Nothing answers at the root any more: the app moved, it was not copied.
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync(new Uri(host.Origin!, "/app.js"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync(new Uri(host.Origin!, "/_bridge/echo/ping"))).StatusCode);
    }

    /// <summary>A Blazor publish ships &lt;base href="/" /&gt;, so the host has to correct it or nothing loads.</summary>
    [Fact]
    public async Task PointsTheEntryDocumentsBaseHrefAtTheMountPoint()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BasePath = "/kiosk";

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), []);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        Assert.Contains("""<base href="/kiosk/" />""", await webView.GetStringAsync(new Uri(host.Origin!, "/kiosk/")));

        // And on a client-side route, which the SPA fallback answers with the same document.
        Assert.Contains("""<base href="/kiosk/" />""", await webView.GetStringAsync(new Uri(host.Origin!, "/kiosk/settings/profile")));
    }

    // Inserted directly after <head>, not merely somewhere in it: <base> only governs the URLs that follow it.
    [Theory]
    [InlineData("<html><head><meta charset=\"utf-8\"></head><body>x</body></html>", "<html><head>")]
    [InlineData("<html><head><base href=\"/\" /><title>t</title></head></html>", "<html><head>")]
    [InlineData("<html><head><BASE HREF='/old/'></head></html>", "<html><head>")]
    [InlineData("just text", "")]
    public async Task RewritesOrInsertsTheBaseTag(string html, string expectedBefore)
    {
        var options = new WebAppHostOptions { AppId = TestApp.AppId, BasePath = "/kiosk", Port = 0 };
        options.UseBaseline(typeof(MountPointTests).Assembly, "Shiny.AppDeviceBridge.Tests.baseline.zip");

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), []);
        var rewritten = host.RewriteBaseHref(html);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(rewritten, "<base", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        Assert.Contains($"""{expectedBefore}<base href="/kiosk/" />""", rewritten);
    }

    [Fact]
    public async Task MovesTheBridgesWithoutMovingTheApp()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BridgePrefix = "/_native";

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), [new EchoBridge()]);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        Assert.Equal(HttpStatusCode.NoContent, (await webView.GetAsync(new Uri(host.Origin!, "/_native/echo/ping"))).StatusCode);

        // The default prefix is now just a client-side route, so the SPA fallback answers it.
        var vacated = await webView.GetAsync(new Uri(host.Origin!, "/_bridge/echo/ping"));
        Assert.Equal(HttpStatusCode.OK, vacated.StatusCode);
        Assert.Contains("version 1.0.0", await vacated.Content.ReadAsStringAsync());
    }

    /// <summary>What a web app that shipped separately reads to find the bridges.</summary>
    [Fact]
    public async Task TellsThePageWhereEverythingIs()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BasePath = "/kiosk";
        options.BridgePrefix = "/_native";

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), []);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        using var config = JsonDocument.Parse(await webView.GetStringAsync(new Uri(host.Origin!, "/kiosk/_host/config")));
        Assert.Equal("/kiosk/", config.RootElement.GetProperty("base").GetString());
        Assert.Equal("/kiosk/_native/", config.RootElement.GetProperty("bridge").GetString());
    }

    /// <summary>The script the page imports has to reach the bridges wherever they actually are.</summary>
    [Fact]
    public async Task BakesTheMountPointsIntoTheInvokeClientScript()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BasePath = "/kiosk";
        options.BridgePrefix = "/_native";

        var events = new WebAppEventHub();
        var invoker = new WebAppInvoker(options, events, () => null!);

        await using var host = new WebAppHost(options, new WebAppSession(), events, [invoker]);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        var script = await webView.GetStringAsync(new Uri(host.Origin!, "/kiosk/_native/invoke/client.js"));

        Assert.Contains("\"/kiosk/_native/events\"", script);
        Assert.Contains("\"/kiosk/_native/invoke/handlers\"", script);
        Assert.DoesNotContain("{bridge}", script);
    }

    [Fact]
    public async Task RedirectsTheMountPointToItsTrailingSlash()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.BasePath = "/kiosk";

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), []);
        await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false });
        var response = await webView.GetAsync(new Uri(host.Origin!, "/kiosk"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/kiosk/", response.Headers.Location?.OriginalString);
    }
}

public class WebAppPathsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("kiosk", "/kiosk")]
    [InlineData("/kiosk", "/kiosk")]
    [InlineData("/kiosk/", "/kiosk")]
    [InlineData("  /a/b/  ", "/a/b")]
    public void NormalizesWhateverItIsGiven(string? value, string expected)
        => Assert.Equal(expected, WebAppPaths.Normalize(value));

    [Theory]
    [InlineData("/", true)]
    [InlineData("/kiosk", true)]
    [InlineData("/a/b", true)]
    [InlineData("/a-b_c.d~e", true)]
    [InlineData("/../etc", false)]
    [InlineData("/a b", false)]
    [InlineData("/a?b", false)]
    [InlineData("/a%20b", false)]
    public void RefusesAPathItCannotMount(string value, bool expected)
        => Assert.Equal(expected, WebAppPaths.IsValid(value));

    [Fact]
    public void StripsTheBaseOffARequest()
    {
        var paths = WebAppPaths.From(new WebAppHostOptions { BasePath = "/kiosk" });

        Assert.True(paths.TryStripBase("/kiosk/app.js", out var relative));
        Assert.Equal("/app.js", relative);

        Assert.True(paths.TryStripBase("/kiosk", out relative));
        Assert.Equal("/", relative);

        Assert.False(paths.TryStripBase("/app.js", out _));
        Assert.False(paths.TryStripBase("/kiosketeria/app.js", out _));
    }

    [Fact]
    public void LeavesEverythingAloneAtTheRoot()
    {
        var paths = WebAppPaths.From(new WebAppHostOptions());

        Assert.Equal(String.Empty, paths.Base);
        Assert.Equal("/", paths.BaseWithSlash);
        Assert.Equal("/_bridge", paths.Bridge);
        Assert.Equal("/_host/start", paths.Start);
        Assert.Equal("/_host/config", paths.Config);

        Assert.True(paths.TryStripBase("/app.js", out var relative));
        Assert.Equal("/app.js", relative);
        Assert.False(paths.IsBaseWithoutSlash("/"));
    }

    [Theory]
    [InlineData("/_host", "cannot be '/_host'")]
    [InlineData("/_host/x", "cannot be '/_host'")]
    [InlineData("/", "cannot be the root")]
    [InlineData("/a b", "is not valid")]
    public void RefusesABridgePrefixThatWouldBreakTheHost(string prefix, string expected)
    {
        var options = new WebAppHostOptions { AppId = "demo", BridgePrefix = prefix };
        options.UseBaseline(typeof(WebAppPathsTests).Assembly, "Shiny.AppDeviceBridge.Tests.baseline.zip");

        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
