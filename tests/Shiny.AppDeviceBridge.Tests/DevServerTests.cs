using System.Net;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

public class DevServerTests
{
    [Fact]
    public async Task ProxiesPagesButKeepsBridgeAndSessionOnTheDevice()
    {
        // Stands in for dotnet watch on the development machine.
        await using var dev = new HttpServer(new HttpServerOptions { Port = 0 });
        dev.MapGet("/", ctx => Results.Text("<h1>from the dev server</h1>", "text/html").ExecuteAsync(ctx));
        dev.MapGet("/_framework/aspnetcore-browser-refresh.js", ctx => Results.Text("connect('ws://localhost:40123,wss://localhost:40124');", "text/javascript").ExecuteAsync(ctx));
        dev.MapGet("/echo-cookie", ctx => Results.Text("cookie=" + ctx.Request.Headers["Cookie"], "text/plain").ExecuteAsync(ctx));
        await dev.StartAsync();

        var devUri = new Uri(dev.ListenUrl!);

        await using var app = new TestApp();
        var options = app.Options(() => new StubHandler(_ => throw new HttpRequestException("no release server")));
        options.DevServer = devUri;

        await using var host = app.CreateHost(options);
        var start = await host.StartAsync();

        Assert.Equal(devUri, host.DevServer);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });

        Assert.Contains("from the dev server", await webView.GetStringAsync(start));

        // The hot reload socket is advertised at the dev server's host, not the device's localhost.
        var script = await webView.GetStringAsync(new Uri(host.Origin!, "/_framework/aspnetcore-browser-refresh.js"));
        Assert.Contains($"ws://{devUri.Host}:40123", script);
        Assert.DoesNotContain("localhost", script);

        // The session cookie never leaves the device.
        Assert.Equal("cookie=", await webView.GetStringAsync(new Uri(host.Origin!, "/echo-cookie")));

        // The bridge is still answered on the device.
        Assert.Contains("\"devServer\":", await webView.GetStringAsync(new Uri(host.Origin!, "/_bridge/host")));
    }

    [Fact]
    public async Task UnreachableDevServerFallsBackToTheInstalledBuild()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var options = app.Options();

        // The discard port: nothing listens there.
        options.DevServer = new Uri("http://127.0.0.1:9/");
        options.DevServerProbeTimeout = TimeSpan.FromMilliseconds(500);

        await using var host = app.CreateHost(options);
        var start = await host.StartAsync();

        Assert.Null(host.DevServer);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("version 1.0.0", await webView.GetStringAsync(start));
    }
}
