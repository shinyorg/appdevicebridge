using System.Net;

namespace Shiny.AppDeviceBridge.Tests;

public class BaselineTests
{
    const string Resource = "Shiny.AppDeviceBridge.Tests.baseline.zip";

    /// <summary>
    /// The whole setup for an app that never updates itself: an app id and a zip compiled into the assembly.
    /// No update server, no manifest, no signing key, and no network at any point.
    /// </summary>
    [Fact]
    public async Task ServesAnEmbeddedZipWithNoUpdateProvider()
    {
        await using var app = new TestApp();
        var options = new WebAppHostOptions
        {
            InstallDirectory = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"))
        };

        options.UseBaseline(typeof(BaselineTests).Assembly, Resource);

        Assert.Null(options.UpdateProvider);
        Assert.Equal("1.0.0", options.Baseline!.Version);

        await using var host = app.CreateHost(options);
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

    /// <summary>A baseline alone satisfies Validate, and so does an update provider alone.</summary>
    [Fact]
    public void ABaselineIsEnoughOnItsOwn()
    {
        var options = new WebAppHostOptions();

        var nothing = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Set a Baseline", nothing.Message);

        options.UseBaseline(typeof(BaselineTests).Assembly, Resource);
        options.Validate();

        using var provider = new GitHubReleasesUpdateProvider("https://github.com/acme/field-app");
        var remoteOnly = new WebAppHostOptions { UpdateProvider = provider };
        remoteOnly.Validate();
    }
}
