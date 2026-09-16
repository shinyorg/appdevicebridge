using System.Net;
using System.Text.Json;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Tests;

public class AppLinkTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/orders/42?tab=items#top")]
    [InlineData("/_bridgework")]
    [InlineData("/a%2F%2Fb")]
    public void AcceptsLocalRoutes(string route) => Assert.True(WebAppLinks.IsLocalRoute(route));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("orders/42")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/orders\\42")]
    [InlineData("https://evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/orders\n42")]
    [InlineData("/orders\t42")]
    [InlineData("/_bridge/gps/current")]
    [InlineData("/_BRIDGE?x=1")]
    [InlineData("/_host/start?token=x")]
    public void RefusesAnythingButALocalPageRoute(string? route) => Assert.False(WebAppLinks.IsLocalRoute(route));

    [Theory]
    [InlineData("sample://orders/42?x=1#top", "/orders/42?x=1#top")]
    [InlineData("sample://orders", "/orders")]
    [InlineData("sample:///orders/42", "/orders/42")]
    [InlineData("https://app.example.com/orders/42?x=1", "/orders/42?x=1")]
    [InlineData("https://APP.example.com", "/")]
    public void MapsAcceptedLinksToRoutes(string url, string route)
    {
        var links = Create();

        Assert.True(links.Receive(new Uri(url)));
        Assert.Equal(route, links.Pending!.Route);
        Assert.Equal(url, links.Pending.Url);
    }

    [Theory]
    [InlineData("other://orders/42")]
    [InlineData("https://evil.example/orders")]
    [InlineData("http://app.example.com.evil.example/")]
    [InlineData("file:///etc/passwd")]
    public void LeavesOtherLinksToTheNativeApp(string url)
    {
        var links = Create();

        Assert.False(links.Receive(new Uri(url)));
        Assert.Null(links.Pending);
    }

    [Fact]
    public void RefusesRoutesTheMapperGetsWrong()
    {
        var links = Create();

        links.Options.MapRoute = uri => uri.Query.TrimStart('?');
        Assert.False(links.Receive(new Uri("sample://go?//evil.example")));

        links.Options.MapRoute = _ => throw new FormatException();
        Assert.False(links.Receive(new Uri("sample://go")));

        links.Options.MapRoute = _ => null;
        Assert.False(links.Receive(new Uri("sample://go")));

        Assert.Null(links.Pending);
    }

    [Fact]
    public void KeepsOnlyTheLatestAndConsumesOnce()
    {
        var links = Create();
        var received = new List<AppLink>();
        links.Received += received.Add;

        links.Receive(new Uri("sample://first"));
        links.Receive(new Uri("sample://second"));

        Assert.Equal(2, received.Count);
        Assert.Equal("/second", links.Consume()!.Route);
        Assert.Null(links.Consume());
    }

    [Fact]
    public async Task ColdStartLinkLoadsTheWebAppAtItsRoute()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var links = Create();
        links.Options.NavigateOnColdStart = true;
        links.Receive(new Uri("sample://orders/42?x=1"));

        var events = new WebAppEventHub();
        await using var host = new WebAppHost(app.Options(), new WebAppSession(), events, [new WebAppLinksBridge(links, events)]);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        using var response = await webView.GetAsync(start);

        Assert.Equal("/orders/42", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("?x=1", response.RequestMessage.RequestUri.Query);

        // Consumed by the navigation, so the page does not act on it again.
        Assert.Null(links.Pending);
        Assert.DoesNotContain("path=", (await host.StartAsync()).Query);
    }

    [Fact]
    public async Task PageReadsThenConsumesThePendingLink()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var links = Create();
        var events = new WebAppEventHub();
        await using var host = new WebAppHost(app.Options(), new WebAppSession(), events, [new WebAppLinksBridge(links, events)]);
        var start = await host.StartAsync();

        // Without NavigateOnColdStart the WebView starts at the root and the link waits for the page.
        Assert.DoesNotContain("path=", start.Query);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        var pending = new Uri(host.Origin!, "/_bridge/links/pending");
        using (var none = await webView.GetAsync(pending))
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);

        links.Receive(new Uri("https://app.example.com/orders/7"));

        using (var peek = JsonDocument.Parse(await webView.GetStringAsync(pending)))
            Assert.Equal("/orders/7", peek.RootElement.GetProperty("route").GetString());

        using (var consumed = await webView.DeleteAsync(pending))
        {
            Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
            using var body = JsonDocument.Parse(await consumed.Content.ReadAsStringAsync());
            Assert.Equal("https://app.example.com/orders/7", body.RootElement.GetProperty("url").GetString());
        }

        using (var gone = await webView.DeleteAsync(pending))
            Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
    }

    [Fact]
    public async Task StartRedirectRefusesForeignPaths()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using var host = app.CreateHost();
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false });
        using var response = await webView.GetAsync(new Uri(start.AbsoluteUri + "&path=" + Uri.EscapeDataString("/_bridge/host")));

        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    static WebAppLinks Create()
    {
        var links = new WebAppLinks(new WebAppLinkOptions());
        links.Options.Schemes.Add("sample");
        links.Options.Hosts.Add("app.example.com");
        return links;
    }
}
