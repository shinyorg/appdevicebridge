using System.Net;

namespace Shiny.AppDeviceBridge.Tests;

public class WebAppHostTests
{
    [Fact]
    public async Task ServesOnlyItsOwnWebView()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();
        await using var host = app.CreateHost(bridges: new EchoBridge());

        var start = await host.StartAsync();
        var origin = host.Origin!;

        // Another app on the device: no cookie.
        using (var stranger = new HttpClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(origin)).StatusCode);
            // Refused by the bridge policy: anonymous, so 401 rather than the page's 403.
            Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(new Uri(origin, "/_bridge/echo/ping"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(new Uri(origin, "/_host/start?token=guess"))).StatusCode);
        }

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });

        Assert.Contains("version 1.0.0", await webView.GetStringAsync(start));
        Assert.Contains("version 1.0.0", await webView.GetStringAsync(new Uri(origin, "/client/side/route")));
        Assert.Equal(HttpStatusCode.NoContent, (await webView.GetAsync(new Uri(origin, "/_bridge/echo/ping"))).StatusCode);

        // An unknown bridge route is a 404, not the SPA fallback answering with index.html.
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync(new Uri(origin, "/_bridge/echo/missing"))).StatusCode);

        var boom = await webView.GetAsync(new Uri(origin, "/_bridge/echo/boom"));
        Assert.Equal(HttpStatusCode.InternalServerError, boom.StatusCode);
        Assert.Contains("bridge_failed", await boom.Content.ReadAsStringAsync());

        using (var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/_bridge/echo/ping")))
        {
            foreignOrigin.Headers.Add("Origin", "http://evil.test");
            Assert.Equal(HttpStatusCode.Forbidden, (await webView.SendAsync(foreignOrigin)).StatusCode);
        }

        // DNS rebinding: the right socket, the wrong name.
        using (var rebound = new HttpRequestMessage(HttpMethod.Get, origin))
        {
            rebound.Headers.Host = "evil.test";
            Assert.Equal(HttpStatusCode.MisdirectedRequest, (await webView.SendAsync(rebound)).StatusCode);
        }

        var info = await webView.GetStringAsync(new Uri(origin, "/_bridge/host"));
        Assert.Contains("\"webAppVersion\":\"1.0.0\"", info);
        Assert.Contains("\"name\":\"echo\"", info);
    }

    [Fact]
    public async Task OnlyTheEntryDocumentIsMarkedNoCache()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();
        await using var host = app.CreateHost();

        var start = await host.StartAsync();
        var origin = host.Origin!;

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        foreach (var path in new[] { "/", "/index.html", "/client/side/route" })
        {
            using var entry = await webView.GetAsync(new Uri(origin, path));
            Assert.True(entry.Headers.CacheControl?.NoCache, path);
        }

        using var asset = await webView.GetAsync(new Uri(origin, "/app.js"));
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Null(asset.Headers.CacheControl);
    }

    [Fact]
    public async Task TheAppDecidesTheRestOfTheCachePolicy()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.OnPrepareResponse = x =>
        {
            if (x.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                x.HttpContext.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        };
        await using var host = app.CreateHost(options);

        var start = await host.StartAsync();
        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        using var asset = await webView.GetAsync(new Uri(host.Origin!, "/app.js"));
        Assert.True(asset.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), asset.Headers.CacheControl?.MaxAge);

        // The host's own rule for the entry document still applies when the app's policy leaves it alone.
        using var entry = await webView.GetAsync(new Uri(host.Origin!, "/"));
        Assert.True(entry.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task ContentTypesCanBeOverridden()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();
        options.ContentTypeOverrides[".js"] = "text/x-test";
        await using var host = app.CreateHost(options);

        var start = await host.StartAsync();
        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        using var asset = await webView.GetAsync(new Uri(host.Origin!, "/app.js"));
        Assert.Equal("text/x-test", asset.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A browser on the same machine, without the WebView's launch session.</summary>
    [Fact]
    public async Task ServesALocalBrowserOnlyWhenAsked()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using (var closed = app.CreateHost(bridges: new EchoBridge()))
        {
            await closed.StartAsync();
            using var browser = new HttpClient();
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(closed.Origin!)).StatusCode);
        }

        var options = app.Options();
        options.ServeWebAppLocally = true;
        await using var open = app.CreateHost(options, new EchoBridge());
        await open.StartAsync();

        using (var browser = new HttpClient())
        {
            Assert.Contains("version 1.0.0", await browser.GetStringAsync(open.Origin!));
            Assert.Contains("version 1.0.0", await browser.GetStringAsync(new Uri(open.Origin!, "/client/side/route")));

            // Opening the pages is not opening the bridges.
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(new Uri(open.Origin!, "/_bridge/echo/ping"))).StatusCode);
        }
    }

    [Fact]
    public async Task FailsWhenThereIsNothingToServe()
    {
        await using var app = new TestApp();
        await using var host = app.CreateHost(app.Options(() => new StubHandler(_ => throw new HttpRequestException("offline"))));

        await Assert.ThrowsAsync<WebAppUnavailableException>(() => host.StartAsync());
        Assert.Equal(WebAppHostState.Failed, host.Status.State);
    }

    [Fact]
    public async Task OptionalUpdateInstallsInTheBackgroundAndServesNextLaunch()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        app.Store.Add("1.1.0", TestApp.Zip("1.1.0"));

        await using (var second = app.CreateHost())
        {
            var installed = new TaskCompletionSource<WebAppPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
            second.UpdateInstalled += (_, package) => installed.TrySetResult(package);

            await second.StartAsync();
            Assert.Equal("1.0.0", second.Package!.Version.ToString());

            var pending = await installed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("1.1.0", pending.Version.ToString());

            Assert.True(second.ApplyPendingUpdate());
            Assert.Equal("1.1.0", second.Package!.Version.ToString());
        }

        await using var third = app.CreateHost();
        await third.StartAsync();

        Assert.Equal("1.1.0", third.Package!.Version.ToString());
        Assert.Equal(WebAppUpdateStatus.UpToDate, third.LastCheck!.Status);
    }

    [Fact]
    public async Task BelowMinimumInstallsBeforeReturning()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        app.Store.Add("1.2.0", TestApp.Zip("1.2.0", folder: "wwwroot"));
        app.Store.Policy = new AspNetCore.WebAppPolicy { MinimumVersion = "1.1.0" };

        await using var second = app.CreateHost();
        var start = await second.StartAsync();

        Assert.Equal("1.2.0", second.Package!.Version.ToString());

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("version 1.2.0", await webView.GetStringAsync(start));
    }

    [Fact]
    public async Task OfflineServesWhatIsInstalled()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        await using var offline = app.CreateHost(app.Options(() => new StubHandler(_ => throw new HttpRequestException("offline"))));
        await offline.StartAsync();

        Assert.Equal("1.0.0", offline.Package!.Version.ToString());
        Assert.Equal(WebAppUpdateStatus.Unavailable, offline.LastCheck!.Status);
    }
}
