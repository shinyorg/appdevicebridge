using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.AspNetCore;
using Shiny.AppDeviceBridge.AutoUpdate;
using Shiny.AppDeviceBridge.HttpTransfers;
using Shiny.Net.Http;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The native app's own updates, localized release notes, and the planner rules behind them.</summary>
public class HostUpdatePlannerTests
{
    static WebAppPolicy Policy(string? minimum, string? latest, string? storeUrl = "https://apps.apple.com/app/id1", string platform = "ios") => new()
    {
        Hosts = new Dictionary<string, WebAppHostPolicy>
        {
            [platform] = new() { MinimumVersion = minimum, LatestVersion = latest, StoreUrl = storeUrl, ReleaseNotes = new WebAppLocalizedText([new("*", "Faster"), new("fr", "Plus rapide")]) }
        }
    };

    [Theory]
    [InlineData("1.0", WebAppUpdateKind.Required)]
    [InlineData("2.0", WebAppUpdateKind.Optional)]
    [InlineData("2.4", WebAppUpdateKind.Optional)]
    [InlineData("2.5", null)]
    [InlineData("3.0", null)]
    public void MinimumRequiresAndLatestOffers(string host, WebAppUpdateKind? expected)
        => Assert.Equal(expected, WebAppUpdatePlanner.PlanHost(Policy("2.0", "2.5"), "ios", WebAppVersion.Parse(host), null)?.Kind);

    [Fact]
    public void FollowsThePlatformThenTheWildcard()
    {
        var policy = new WebAppPolicy
        {
            Hosts = new Dictionary<string, WebAppHostPolicy>
            {
                ["*"] = new() { MinimumVersion = "5.0" },
                ["ANDROID"] = new() { MinimumVersion = "1.0" }
            }
        };

        Assert.Null(WebAppUpdatePlanner.PlanHost(policy, "android", WebAppVersion.Parse("2.0"), null));
        Assert.Equal(WebAppUpdateKind.Required, WebAppUpdatePlanner.PlanHost(policy, "ios", WebAppVersion.Parse("2.0"), null)!.Kind);
    }

    [Fact]
    public void NoRuleNoVersionOrATypoLocksNobodyOut()
    {
        Assert.Null(WebAppUpdatePlanner.PlanHost(null, "ios", WebAppVersion.Parse("1.0"), null));
        Assert.Null(WebAppUpdatePlanner.PlanHost(Policy("2.0", null), "ios", null, null));
        Assert.Null(WebAppUpdatePlanner.PlanHost(Policy("two", "three"), "ios", WebAppVersion.Parse("1.0"), null));
        Assert.Null(WebAppUpdatePlanner.PlanHost(Policy("2.0", null), "android", WebAppVersion.Parse("1.0"), null));
    }

    [Fact]
    public void LocalizesNotesAndDropsAnUnsafeStoreUrl()
    {
        var required = WebAppUpdatePlanner.PlanHost(Policy("2.0", null), "ios", WebAppVersion.Parse("1.0"), "fr-CA")!;
        Assert.Equal("Plus rapide", required.ReleaseNotes);
        Assert.Equal("https://apps.apple.com/app/id1", required.StoreUrl);

        var unsafeUrl = WebAppUpdatePlanner.PlanHost(Policy("2.0", null, "javascript:alert(1)"), "ios", WebAppVersion.Parse("1.0"), "de")!;
        Assert.Null(unsafeUrl.StoreUrl);
        Assert.Equal("Faster", unsafeUrl.ReleaseNotes);
    }
}

public class LocalizedTextTests
{
    static readonly WebAppLocalizedText Notes = new([new("en", "Hello"), new("fr", "Bonjour"), new("fr-CA", "Allô"), new("*", "Hi")]);

    [Theory]
    [InlineData("fr-CA", "Allô")]
    [InlineData("FR-ca", "Allô")]
    [InlineData("fr-FR", "Bonjour")]
    [InlineData("en-US", "Hello")]
    [InlineData("de", "Hi")]
    [InlineData(null, "Hi")]
    [InlineData("", "Hi")]
    public void FallsBackThroughParentsToTheWildcard(string? culture, string expected)
        => Assert.Equal(expected, Notes.Resolve(culture));

    [Fact]
    public void WithoutAWildcardTheFirstLanguageIsTheLastResort()
        => Assert.Equal("Hola", new WebAppLocalizedText([new("es", "Hola"), new("it", "Ciao")]).Resolve("de"));

    [Fact]
    public void ReadsAStringOrAnObjectAndWritesThemBack()
    {
        var options = new JsonSerializerOptions();

        var single = JsonSerializer.Deserialize<WebAppLocalizedText>("\"Faster\"", options)!;
        Assert.Equal("Faster", single.Resolve("fr"));
        Assert.Equal("\"Faster\"", JsonSerializer.Serialize(single, options));

        var many = JsonSerializer.Deserialize<WebAppLocalizedText>("""{ "*": "Faster", "fr": "Plus rapide" }""", options)!;
        Assert.Equal("Plus rapide", many.Resolve("fr-CA"));
        Assert.Equal("""{"*":"Faster","fr":"Plus rapide"}""", JsonSerializer.Serialize(many, options));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebAppLocalizedText>("""{ "fr": 1 }""", options));
    }

    [Fact]
    public async Task TheFileSystemStoreReadsLocalizedNotesAndHostRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));
        var app = Path.Combine(root, "demo");
        Directory.CreateDirectory(app);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(app, "1.0.0.zip"), TestApp.Zip("1.0.0"));
            await File.WriteAllTextAsync(Path.Combine(app, "1.0.0.json"), """{ "releaseNotes": { "*": "Faster", "fr": "Plus rapide" } }""");
            await File.WriteAllTextAsync(Path.Combine(app, "app.json"), """
                { "hosts": { "ios": { "minimumVersion": "2.0", "storeUrl": "https://apps.apple.com/app/id1", "releaseNotes": "Required" } } }
                """);

            var store = new FileSystemWebAppReleaseStore(root);
            var entry = Assert.Single((await store.GetReleasesAsync("demo", CancellationToken.None))!);
            Assert.Equal("Plus rapide", entry.ReleaseNotes!.Resolve("fr"));
            Assert.Equal("Faster", entry.Release.ReleaseNotes);

            var policy = (await store.GetPolicyAsync("demo", CancellationToken.None))!;
            Assert.Equal("2.0", policy.Hosts!["ios"].MinimumVersion);
            Assert.Equal("Required", policy.Hosts["ios"].ReleaseNotes!.Resolve("fr"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>The splash screen's rules: a required update of either app stops it with a message.</summary>
public class HostUpdateFlowTests
{
    static WebAppPolicy HostPolicy(string minimum, string? latest = null) => new()
    {
        Hosts = new Dictionary<string, WebAppHostPolicy>
        {
            ["*"] = new()
            {
                MinimumVersion = minimum,
                LatestVersion = latest,
                StoreUrl = "https://apps.apple.com/app/id1",
                ReleaseNotes = new WebAppLocalizedText([new("*", "New native features"), new("fr", "Nouvelles fonctions")])
            }
        }
    };

    static WebAppHostOptions French(WebAppHostOptions options)
    {
        options.Culture = () => CultureInfo.GetCultureInfo("fr-CA");
        return options;
    }

    [Fact]
    public async Task ARequiredNativeUpdateStopsStartupWithTheWayToTheStore()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        app.Store.Policy = HostPolicy("2.0");
        await app.StartReleaseServerAsync();

        await using var host = app.CreateHost(French(app.Options()), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []);
        var statuses = new List<WebAppHostStatus>();
        host.StatusChanged += (_, s) => statuses.Add(s);

        var ex = await Assert.ThrowsAsync<WebAppHostUpdateRequiredException>(() => host.StartAsync());

        Assert.True(ex.Update.IsRequired);
        Assert.Equal("2.0", ex.Update.MinimumVersion);
        Assert.Equal("Nouvelles fonctions", ex.Update.WhatsNew);
        Assert.Equal(new Uri("https://apps.apple.com/app/id1"), ex.Update.StoreUri);
        Assert.Equal(WebAppHostState.HostUpdateRequired, host.Status.State);
        Assert.Same(ex.Update, host.Status.HostUpdate);

        // Nothing downloaded for a native app that cannot run it, and no Failed status over the stop.
        Assert.DoesNotContain(statuses, s => s.State is WebAppHostState.Downloading or WebAppHostState.Failed);
        Assert.Null(host.Package);
    }

    [Fact]
    public async Task TheRequirementSurvivesGoingOfflineUntilTheStoreUpdateArrives()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        // Installed while the server still accepted this native app.
        await using (var first = app.CreateHost(app.Options(), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []))
            await first.StartAsync();

        app.Store.Policy = HostPolicy("2.0");
        await using (var told = app.CreateHost(app.Options(), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []))
            await Assert.ThrowsAsync<WebAppHostUpdateRequiredException>(() => told.StartAsync());

        var offline = () => app.Options(() => new StubHandler(_ => throw new HttpRequestException("offline")));

        await using (var airplane = app.CreateHost(offline(), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []))
        {
            var ex = await Assert.ThrowsAsync<WebAppHostUpdateRequiredException>(() => airplane.StartAsync());
            Assert.Equal(WebAppUpdateStatus.Unavailable, airplane.LastCheck!.Status);
            Assert.Equal(new Uri("https://apps.apple.com/app/id1"), ex.Update.StoreUri);
        }

        // Updated from the store: the remembered minimum is met, offline or not.
        await using (var updated = app.CreateHost(offline(), app.BridgeOptions(o => o.HostVersion = "2.0.0"), null, []))
        {
            await updated.StartAsync();
            Assert.Equal("1.0.0", updated.Package!.Version.ToString());
            Assert.Null(updated.HostUpdate);
        }

        // And the server lifting the rule forgets it.
        app.Store.Policy = null;
        await using (var lifted = app.CreateHost(app.Options(), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []))
            await lifted.StartAsync();

        await using (var afterwards = app.CreateHost(offline(), app.BridgeOptions(o => o.HostVersion = "1.5.0"), null, []))
            await afterwards.StartAsync();
    }

    [Fact]
    public async Task AnOptionalNativeUpdateIsOfferedToThePage()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        app.Store.Policy = HostPolicy("1.0", latest: "3.0");
        await app.StartReleaseServerAsync();

        await using var host = app.CreateHost(app.Options(), app.BridgeOptions(o => o.HostVersion = "2.0.0"), null, []);
        var start = await host.StartAsync();

        Assert.False(host.HostUpdate!.IsRequired);
        Assert.Equal(WebAppHostState.Ready, host.Status.State);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetAsync(start);

        var info = JsonSerializer.Deserialize(
            await webView.GetStringAsync(new Uri(start, "/_bridge/host")),
            Client.AppDeviceBridgeJsonContext.Default.HostInfo
        )!;

        Assert.Equal(new Client.StoreUpdate(false, "1.0", "3.0", "New native features"), info.StoreUpdate);
    }

    [Fact]
    public async Task TheCheckCarriesTheCultureAndNotesComeBackInIt()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"), releaseNotes: new WebAppLocalizedText([new("*", "Faster"), new("fr", "Plus rapide")]));
        await app.StartReleaseServerAsync();

        string? query = null;
        var options = French(app.Options(() => new SpyHandler(r => query = r.RequestUri!.Query, app)));

        await using var host = app.CreateHost(options);
        var statuses = new List<WebAppHostStatus>();
        host.StatusChanged += (_, s) => statuses.Add(s);
        await host.StartAsync();

        Assert.Contains("culture=fr-CA", query);

        // The required first install shows on the splash with its notes, in French, and its progress.
        var downloading = statuses.Where(s => s.State == WebAppHostState.Downloading).ToList();
        Assert.NotEmpty(downloading);
        Assert.All(downloading, s => Assert.Equal("Plus rapide", s.Update!.WhatsNew));
        Assert.Contains(downloading, s => s.Progress is { Fraction: 1 });
    }

    [Fact]
    public async Task ARequiredWebAppUpdateThatFailsStopsByDefault()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        // Required — the installed build is below the minimum — and broken: a hash that never matches.
        app.Store.Add("1.2.0", TestApp.Zip("1.2.0"), sha256: new string('a', 64));
        app.Store.Policy = new WebAppPolicy { MinimumVersion = "1.1.0" };

        await using (var strict = app.CreateHost())
        {
            var ex = await Assert.ThrowsAsync<WebAppUnavailableException>(() => strict.StartAsync());
            Assert.Contains("1.2.0", ex.Message);
            Assert.Equal(WebAppHostState.Failed, strict.Status.State);
        }

        var lenient = app.Options();
        lenient.BlockOnRequiredUpdateFailure = false;

        await using var fallback = app.CreateHost(lenient);
        await fallback.StartAsync();
        Assert.Equal("1.0.0", fallback.Package!.Version.ToString());
    }

    [Fact]
    public async Task ThePageSignalsReadyOnce()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using var host = app.CreateHost();
        var start = await host.StartAsync();
        Assert.False(host.PageReady.IsCompleted);

        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync(new Uri(start, "/_bridge/host/ready"), null)).StatusCode);
        Assert.False(host.PageReady.IsCompleted);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetAsync(start);

        Assert.Equal(HttpStatusCode.NoContent, (await webView.PostAsync(new Uri(start, "/_bridge/host/ready"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await webView.PostAsync(new Uri(start, "/_bridge/host/ready"), null)).StatusCode);
        Assert.True(host.PageReady.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OpenStoreGoesThroughTheAppsChoice()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        app.Store.Policy = HostPolicy("1.0", latest: "3.0");
        await app.StartReleaseServerAsync();

        HostUpdateInfo? asked = null;
        var options = app.Options();
        options.OpenStore = update =>
        {
            asked = update;
            return Task.FromResult(true);
        };

        await using var host = app.CreateHost(options, app.BridgeOptions(o => o.HostVersion = "2.0.0"), null, []);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetAsync(start);

        using var response = await webView.PostAsync(new Uri(start, "/_bridge/host/open-store"), null);
        var result = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), Client.AppDeviceBridgeJsonContext.Default.OpenStoreResult)!;

        Assert.True(result.Opened);
        Assert.Equal("3.0", asked!.LatestVersion);
    }

    [Fact]
    public async Task OpenStoreWithNothingToOpenSaysSo()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        await using var host = app.CreateHost();
        await host.StartAsync();

        Assert.Null(host.HostUpdate);
        Assert.False(await host.OpenStoreAsync());
    }

    /// <summary>Passes everything to the release server, noting each request.</summary>
    sealed class SpyHandler(Action<HttpRequestMessage> spy, TestApp app) : DelegatingHandler(app.ReleaseServerHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/check", StringComparison.Ordinal))
                spy(request);

            return base.SendAsync(request, cancellationToken);
        }
    }
}

public class SplashContextTests
{
    static WebAppSplashContext Create() => new(new WebAppSplashText(), new Command(() => { }), new Command(() => { }));

    [Fact]
    public void ARequiredWebAppUpdateShowsItsVersionNotesAndProgress()
    {
        var splash = Create();
        var update = new UpdateInfo { Version = WebAppVersion.Parse("1.2.0"), WhatsNew = "Faster" };

        splash.Apply(new WebAppHostStatus(WebAppHostState.Downloading, null, new WebAppDownloadProgress(50, 200), Update: update));

        Assert.Equal("Installing required update 1.2.0…", splash.Message);
        Assert.Equal("Faster", splash.ReleaseNotes);
        Assert.True(splash.HasReleaseNotes);
        Assert.True(splash.IsProgressVisible);
        Assert.Equal(0.25, splash.Progress);
        Assert.False(splash.IsStopped);
    }

    [Fact]
    public void ARequiredNativeUpdateStopsWithTheStoreButton()
    {
        var splash = Create();
        splash.ShowHostUpdateRequired(new HostUpdateInfo { IsRequired = true, MinimumVersion = "2.0", WhatsNew = "New" });

        Assert.True(splash.IsStopped);
        Assert.True(splash.CanOpenStore);
        Assert.False(splash.IsBusy);
        Assert.Equal("New", splash.ReleaseNotes);
        Assert.Contains("2.0", splash.Message);
        Assert.Equal(WebAppHostState.HostUpdateRequired, splash.State);
    }

    [Fact]
    public void AFailureStopsWithRetryAndARestartClearsIt()
    {
        var splash = Create();
        splash.Apply(new WebAppHostStatus(WebAppHostState.Failed, Error: "A required update (1.2.0) could not be installed."));

        Assert.True(splash.IsStopped);
        Assert.True(splash.CanRetry);
        Assert.False(splash.CanOpenStore);

        splash.Restart();
        Assert.False(splash.IsStopped);
        Assert.True(splash.IsBusy);
        Assert.False(splash.CanRetry);
    }

    [Fact]
    public void TheWordsAreTheApps()
    {
        var text = new WebAppSplashText { CheckingForUpdates = "Recherche de mises à jour…" };
        var splash = new WebAppSplashContext(text, new Command(() => { }), new Command(() => { }));

        splash.Apply(new WebAppHostStatus(WebAppHostState.CheckingForUpdate));
        Assert.Equal("Recherche de mises à jour…", splash.Message);
    }
}

public class BackgroundUpdateTests
{
    [Fact]
    public async Task AnInstalledDownloadMustVerify()
    {
        await using var app = new TestApp();
        var zip = TestApp.Zip("1.0.0");
        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider(),
            InstallDirectory = app.InstallDirectory
        });

        var wrong = MemoryUpdateProvider.Release("1.0.0", zip);
        wrong.Sha256 = new string('b', 64);
        var file = Write(app, zip);

        await Assert.ThrowsAsync<InvalidDataException>(() => host.InstallDownloadedAsync(wrong, file));
        Assert.False(File.Exists(file));

        file = Write(app, zip);
        var package = await host.InstallDownloadedAsync(MemoryUpdateProvider.Release("1.0.0", zip), file);
        Assert.Equal(WebAppPackageOrigin.Installed, package.Origin);
        Assert.False(File.Exists(file));

        // Not newer than what is now installed: refused, as a replayed release would be.
        file = Write(app, zip);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.InstallDownloadedAsync(MemoryUpdateProvider.Release("1.0.0", zip), file));
    }

    [Fact]
    public async Task WhileRunningADownloadBecomesThePendingUpdateWithItsNotes()
    {
        await using var app = new TestApp();
        var provider = new MemoryUpdateProvider { Offer = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0")) };
        await using var host = app.CreateHost(new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory });
        await host.StartAsync();

        WebAppPackage? raised = null;
        host.UpdateInstalled += (_, p) => raised = p;

        var zip = TestApp.Zip("1.1.0");
        var update = MemoryUpdateProvider.Release("1.1.0", zip);
        update.WhatsNew = "Shiny";

        await host.InstallDownloadedAsync(update, Write(app, zip));

        Assert.Equal("1.1.0", host.PendingPackage!.Version.ToString());
        Assert.Same(host.PendingPackage, raised);
        Assert.Equal("Shiny", ((IWebAppStatus)host).PendingReleaseNotes);
        Assert.Equal("1.0.0", host.Package!.Version.ToString());
    }

    [Fact]
    public async Task AnOptionalUpdateGoesToARegisteredDownloader()
    {
        await using var app = new TestApp();
        var downloader = new RecordingDownloader();
        var provider = new MemoryUpdateProvider { Offer = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0")) };

        await using (var first = app.CreateHost(new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory }))
            await first.StartAsync();

        var newer = MemoryUpdateProvider.Release("1.1.0", TestApp.Zip("1.1.0"));
        newer.IsOptional = true;

        await using var host = app.CreateHost(
            new WebAppHostOptions { UpdateProvider = new MemoryUpdateProvider { Offer = newer }, InstallDirectory = app.InstallDirectory },
            null,
            null,
            [],
            http => http.Services.AddSingleton<IWebAppUpdateDownloader>(downloader)
        );

        await host.StartAsync();
        var queued = await downloader.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("1.1.0", queued.Version.ToString());
        Assert.Null(host.PendingPackage);
    }

    [Fact]
    public async Task TheJobQueuesATransferOnceAndInstallsWhatLands()
    {
        await using var app = new TestApp();
        var zip = TestApp.Zip("1.1.0");
        var transfers = new FakeTransferManager();
        var provider = new MemoryUpdateProvider
        {
            Offer = MemoryUpdateProvider.Release("1.1.0", zip),
            DownloadRequest = new UpdateDownloadRequest(new Uri("https://cdn.test/1.1.0.zip"), new Dictionary<string, string> { ["Accept"] = "application/zip" })
        };

        var options = new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory };
        options.UseBaseline(typeof(BaselineTests).Assembly, "Shiny.AppDeviceBridge.Tests.baseline.zip", "1.0.0");

        await using var host = app.CreateHost(options, null, null, [], http => http.Services.AddSingleton<IHttpTransferManager>(transfers));
        var updater = new WebAppAutoUpdater(app.Services!, new WebAppAutoUpdateOptions { UseMeteredConnection = false }, NullLogger<WebAppAutoUpdater>.Instance);

        // Never started: a background wake-up.
        Assert.True(await updater.RunAsync(CancellationToken.None));
        Assert.True(await updater.RunAsync(CancellationToken.None));

        var request = Assert.Single(transfers.Queued);
        Assert.StartsWith(WebAppAutoUpdater.IdentifierPrefix, request.Identifier);
        Assert.Equal("https://cdn.test/1.1.0.zip", request.Uri);
        Assert.False(request.UseMeteredConnection);
        Assert.Equal("application/zip", request.Headers!["Accept"]);

        // The OS finishes the download — perhaps in another process — and the delegate installs it.
        await File.WriteAllBytesAsync(request.LocalFilePath, zip);
        await new WebAppUpdateTransferDelegate(updater).OnCompleted(request);

        Assert.False(File.Exists(request.LocalFilePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(request.LocalFilePath)!));

        // The next launch serves it, and a check finds nothing newer.
        var start = await host.StartAsync();
        Assert.Equal("1.1.0", host.Package!.Version.ToString());
        Assert.False(await updater.RunAsync(CancellationToken.None));
        Assert.NotNull(start);
    }

    [Fact]
    public async Task AFailedOrForeignTransferIsCleanedUpOrIgnored()
    {
        await using var app = new TestApp();
        var transfers = new FakeTransferManager();
        var provider = new MemoryUpdateProvider
        {
            Offer = MemoryUpdateProvider.Release("1.1.0", TestApp.Zip("1.1.0")),
            DownloadRequest = new UpdateDownloadRequest(new Uri("https://cdn.test/1.1.0.zip"))
        };

        await using var host = app.CreateHost(
            new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory },
            null,
            null,
            [],
            http => http.Services.AddSingleton<IHttpTransferManager>(transfers)
        );
        var updater = new WebAppAutoUpdater(app.Services!, new WebAppAutoUpdateOptions(), NullLogger<WebAppAutoUpdater>.Instance);
        var transferDelegate = new WebAppUpdateTransferDelegate(updater);

        await updater.RunAsync(CancellationToken.None);
        var request = Assert.Single(transfers.Queued);

        await transferDelegate.OnError(request, 500, new HttpRequestException("boom"));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(request.LocalFilePath)!));

        // Someone else's transfer, and one forged to escape the folder: neither is touched.
        await transferDelegate.OnCompleted(request with { Identifier = "page.1234" });
        await transferDelegate.OnCompleted(request with { Identifier = WebAppAutoUpdater.IdentifierPrefix + "../../current" });
        Assert.Null(host.PendingPackage);
    }

    [Fact]
    public async Task WithoutAPlainDownloadTheJobInstallsInProcess()
    {
        await using var app = new TestApp();
        var provider = new MemoryUpdateProvider { Offer = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0")) };

        await using var host = app.CreateHost(
            new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory },
            null,
            null,
            [],
            http => http.Services.AddSingleton<IHttpTransferManager>(new FakeTransferManager())
        );
        var updater = new WebAppAutoUpdater(app.Services!, new WebAppAutoUpdateOptions(), NullLogger<WebAppAutoUpdater>.Instance);

        Assert.True(await updater.RunAsync(CancellationToken.None));

        await host.StartAsync();
        Assert.Equal(WebAppPackageOrigin.Installed, host.Package!.Origin);
        Assert.Equal("1.0.0", host.Package.Version.ToString());
    }

    [Fact]
    public void TheTransferManagerIsRegisteredOnceWhicheverPackageComesFirst()
    {
        foreach (var autoUpdateFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHttpTransferManager>(new FakeTransferManager());
            var bridge = new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services));

            if (autoUpdateFirst)
                bridge.AddWebAppAutoUpdate().AddHttpTransfersBridge();
            else
                bridge.AddHttpTransfersBridge().AddWebAppAutoUpdate();

            Assert.Single(services, x => x.ServiceType == typeof(IHttpTransferManager));
            Assert.Contains(services, x => x.ServiceType == typeof(IHttpTransferDelegate) && x.ImplementationType == typeof(WebAppUpdateTransferDelegate));
            Assert.Contains(services, x => x.ServiceType == typeof(IWebAppUpdateDownloader));
        }
    }

    [Fact]
    public void RegisteringTwiceIsAMistake()
    {
        var bridge = new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(new ServiceCollection()));
        bridge.AddWebAppAutoUpdate();
        Assert.Throws<InvalidOperationException>(() => bridge.AddWebAppAutoUpdate());
    }

    static string Write(TestApp app, byte[] zip)
    {
        var path = Path.Combine(app.InstallDirectory, Guid.NewGuid().ToString("n") + ".zip");
        File.WriteAllBytes(path, zip);
        return path;
    }

    sealed class RecordingDownloader : IWebAppUpdateDownloader
    {
        public TaskCompletionSource<UpdateInfo> Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> QueueAsync(WebAppHost host, UpdateInfo update, CancellationToken cancellationToken)
        {
            this.Queued.TrySetResult(update);
            return Task.FromResult(true);
        }
    }

    sealed class FakeTransferManager : IHttpTransferManager
    {
        readonly List<HttpTransfer> transfers = [];

        public List<HttpTransferRequest> Queued { get; } = [];

        public Task<IList<HttpTransfer>> GetTransfers() => Task.FromResult<IList<HttpTransfer>>([.. this.transfers]);

        public Task<HttpTransfer> Queue(HttpTransferRequest request)
        {
            this.Queued.Add(request);
            var transfer = new HttpTransfer(request, null, 0, HttpTransferState.Pending, DateTimeOffset.UtcNow);
            this.transfers.Add(transfer);
            return Task.FromResult(transfer);
        }

        public Task Cancel(string identifier)
        {
            this.transfers.RemoveAll(x => x.Request.Identifier == identifier);
            return Task.CompletedTask;
        }

        public Task Pause(string identifier) => Task.CompletedTask;
        public Task Resume(string identifier) => Task.CompletedTask;

        public Task CancelAll()
        {
            this.transfers.Clear();
            return Task.CompletedTask;
        }

        public event EventHandler<int>? CountChanged { add { } remove { } }
        public event EventHandler<HttpTransferResult>? UpdateReceived { add { } remove { } }
    }
}
