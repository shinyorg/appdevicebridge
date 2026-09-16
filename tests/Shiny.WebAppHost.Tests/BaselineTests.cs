using System.Net;

namespace Shiny.WebAppHost.Tests;

public class BaselineTests
{
    const string Resource = "Shiny.WebAppHost.Tests.baseline.zip";

    /// <summary>
    /// The whole setup for an app that never updates itself: an app id and a zip compiled into the assembly.
    /// No update server, no manifest, no signing key, and no network at any point.
    /// </summary>
    [Fact]
    public async Task ServesAnEmbeddedZipWithNoUpdateServer()
    {
        var options = new WebAppHostOptions
        {
            AppId = TestApp.AppId,
            InstallDirectory = Path.Combine(Path.GetTempPath(), "webapphost-tests", Guid.NewGuid().ToString("n")),
            Port = 0
        };

        options.UseBaseline(typeof(BaselineTests).Assembly, Resource);

        Assert.Null(options.UpdateServer);
        Assert.Null(options.PublicKey);
        Assert.Equal("1.0.0", options.Baseline!.Version);

        await using var host = new WebAppHost(options, new WebAppSession(), new WebAppEventHub(), []);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("baseline", await webView.GetStringAsync(start));

        // Served from the zip, not from anything downloaded.
        Assert.Equal(WebAppPackageOrigin.Baseline, host.Package!.Origin);
        Assert.Null(host.LastCheck);

        Assert.Contains("console.log", await webView.GetStringAsync(new Uri(host.Origin!, "/app.js")));

        // Nothing was downloaded, so nothing was written: the install directory is never even created.
        Assert.False(Directory.Exists(options.InstallDirectory));
    }

    /// <summary>A baseline alone satisfies Validate; only an UpdateServer drags the signing key in with it.</summary>
    [Fact]
    public void ABaselineIsEnoughOnItsOwn()
    {
        var options = new WebAppHostOptions { AppId = TestApp.AppId };

        var nothing = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Set a Baseline", nothing.Message);

        options.UseBaseline(typeof(BaselineTests).Assembly, Resource);
        options.Validate();

        options.UpdateServer = new Uri("https://example.com/webapps");
        Assert.Contains("PublicKey is required", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}
