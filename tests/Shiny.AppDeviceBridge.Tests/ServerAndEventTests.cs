using System.Net;
using System.Security.Cryptography;
using Shiny.AppDeviceBridge.AspNetCore;

namespace Shiny.AppDeviceBridge.Tests;

public class FileSystemReleaseStoreTests
{
    [Fact]
    public async Task ReadsReleasesSidecarsAndPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));
        var app = Path.Combine(root, "demo");
        Directory.CreateDirectory(app);

        try
        {
            var stable = TestApp.Zip("1.0.0");
            File.WriteAllBytes(Path.Combine(app, "1.0.0.zip"), stable);
            File.WriteAllBytes(Path.Combine(app, "1.1.0-beta.1.zip"), TestApp.Zip("1.1.0-beta.1"));
            File.WriteAllText(Path.Combine(app, "notes.zip"), "not a version, so not a release");

            File.WriteAllText(Path.Combine(app, "1.1.0-beta.1.json"), """
                {
                  // comments and trailing commas are allowed: people edit these by hand
                  "channel": "beta",
                  "minimumHostVersion": "2.0",
                  "platforms": ["ios"],
                }
                """);

            File.WriteAllText(Path.Combine(app, "app.json"), """{ "minimumVersion": "1.0.0" }""");

            var store = new FileSystemWebAppReleaseStore(root);
            var releases = await store.GetReleasesAsync("demo", CancellationToken.None);

            Assert.NotNull(releases);
            Assert.Equal(2, releases.Count);

            var beta = releases.Single(x => x.Channel == "beta");
            Assert.Equal("2.0", beta.Release.MinimumHostVersion);
            Assert.Equal(["ios"], beta.Platforms!);

            var release = releases.Single(x => x.Release.Version == "1.0.0").Release;
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stable)), release.Sha256);
            Assert.Equal(stable.Length, release.Size);

            Assert.Equal("1.0.0", (await store.GetPolicyAsync("demo", CancellationToken.None))!.MinimumVersion);

            Assert.Null(await store.GetReleasesAsync("unknown", CancellationToken.None));
            Assert.Null(await store.GetReleasesAsync("../demo", CancellationToken.None));
            Assert.Null(await store.OpenReleaseAsync("demo", "../demo/1.0.0", CancellationToken.None));

            await using var stream = await store.OpenReleaseAsync("demo", "1.0.0", CancellationToken.None);
            Assert.NotNull(stream);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class EventAndInstallTests
{
    [Fact]
    public async Task StreamsEventsToTheWebView()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var events = new WebAppEventHub();
        await using var host = new WebAppHost(app.Options(), new WebAppSession(), events, []);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The page subscribes when the handler runs, which the test cannot observe, so publish until
        // something arrives rather than guessing at a delay.
        var publishing = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                events.Publish("test.ping", new WebAppBridgeError("pong", "hello"), WebAppBridgeJsonContext.Default.WebAppBridgeError);
                await Task.Delay(50);
            }
        });

        using var response = await webView.GetAsync(new Uri(host.Origin!, "/_bridge/events"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && !line.StartsWith("event:", StringComparison.Ordinal))
        {
        }

        Assert.NotNull(line);
        Assert.Contains("test.ping", line);
        Assert.Contains("\"code\":\"pong\"", await reader.ReadLineAsync(timeout.Token));

        await timeout.CancelAsync();
        await publishing;
    }

    [Fact]
    public async Task InstallsANewVersionWithIdenticalContent()
    {
        await using var app = new TestApp();
        var zip = TestApp.Zip("same bytes");
        app.Store.Add("1.0.0", zip);
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        // Same bytes, same hash, same file name — and the file is the one being served.
        app.Store.Add("1.0.1", zip);

        await using var host = app.CreateHost();
        await host.StartAsync();

        var check = await host.Updater.CheckAsync(host.Package!.Version);
        var package = await host.Updater.InstallAsync(check);

        Assert.Equal("1.0.1", package.Version.ToString());
        Assert.True(File.Exists(package.ZipPath));
    }
}
