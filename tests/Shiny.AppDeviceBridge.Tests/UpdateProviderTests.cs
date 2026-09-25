using System.Net;
using System.Security.Cryptography;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>What the host does with any <see cref="IUpdateProvider"/>, whatever it reads releases from.</summary>
public class UpdateProviderTests
{
    [Fact]
    public async Task InstallsFromAProviderOfItsOwn()
    {
        await using var app = new TestApp();
        var provider = new MemoryUpdateProvider { Offer = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0")) };
        await using var host = app.CreateHost(new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory });

        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("version 1.0.0", await webView.GetStringAsync(start));
        Assert.Equal(WebAppPackageOrigin.Installed, host.Package!.Origin);

        // Nothing installed yet: the app version is null, and the host version arrives as System.Version.
        Assert.Null(provider.LastAppVersion);
        Assert.Equal(new Version(1, 0, 0), provider.LastHostVersion);
    }

    [Fact]
    public async Task PassesTheRunningVersionAndPrereleaseFreeHostVersion()
    {
        await using var app = new TestApp();
        var provider = new MemoryUpdateProvider();
        var options = new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory };
        options.UseBaseline(typeof(BaselineTests).Assembly, "Shiny.AppDeviceBridge.Tests.baseline.zip", "1.4.0");

        await using var host = app.CreateHost(options, app.BridgeOptions(o => o.HostVersion = "2.3.1-beta.2"), null, []);
        await host.StartAsync();

        Assert.Equal(WebAppVersion.Parse("1.4.0"), provider.LastAppVersion);
        Assert.Equal(new Version(2, 3, 1), provider.LastHostVersion);
        Assert.Equal(WebAppUpdateStatus.UpToDate, host.LastCheck!.Status);
    }

    [Theory]
    [InlineData("3", 3, 0, -1)]
    [InlineData("3.1", 3, 1, -1)]
    [InlineData("3.1.4+abc", 3, 1, 4)]
    [InlineData("3.1.4-rc.1", 3, 1, 4)]
    public void HostVersionDropsTheLabel(string text, int major, int minor, int build)
    {
        var version = WebAppUpdater.ToHostVersion(text);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Fact]
    public async Task WorksWithoutASizeOrHash()
    {
        await using var app = new TestApp();
        var update = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0"));
        update.FileSize = null;
        update.Sha256 = null;

        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Offer = update },
            InstallDirectory = app.InstallDirectory
        });

        var check = await host.Updater.CheckAsync(null);
        var package = await host.Updater.InstallAsync(check);

        Assert.Equal(WebAppVersion.Parse("1.0.0"), package.Version);
        Assert.True(File.Exists(package.ZipPath));
    }

    [Fact]
    public async Task RefusesAnOfferThatIsNotNewer()
    {
        await using var app = new TestApp();
        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Offer = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0")) },
            InstallDirectory = app.InstallDirectory
        });

        Assert.Equal(WebAppUpdateStatus.Rejected, (await host.Updater.CheckAsync(WebAppVersion.Parse("1.0.0"))).Status);
    }

    [Fact]
    public async Task RefusesADownloadOfTheWrongSize()
    {
        await using var app = new TestApp();
        var update = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0"));
        update.FileSize -= 1;

        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Offer = update },
            InstallDirectory = app.InstallDirectory
        });

        var check = await host.Updater.CheckAsync(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Updater.InstallAsync(check));
        Assert.Empty(Directory.EnumerateFiles(app.InstallDirectory, "*.download", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RefusesADownloadThatDoesNotMatchItsHash()
    {
        await using var app = new TestApp();
        var update = MemoryUpdateProvider.Release("1.0.0", TestApp.Zip("1.0.0"));
        update.Sha256 = new string('a', 64);

        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Offer = update },
            InstallDirectory = app.InstallDirectory
        });

        var check = await host.Updater.CheckAsync(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Updater.InstallAsync(check));
        Assert.Empty(Directory.EnumerateFiles(app.InstallDirectory, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AFailingProviderIsOfflineAndAnInvalidOneIsRejected()
    {
        await using var app = new TestApp();

        await using var offline = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Failure = new HttpRequestException("offline") },
            InstallDirectory = app.InstallDirectory
        });
        Assert.Equal(WebAppUpdateStatus.Unavailable, (await offline.Updater.CheckAsync(null)).Status);

        await using var invalid = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Failure = new InvalidDataException("bad manifest") },
            InstallDirectory = app.InstallDirectory
        });
        var rejected = await invalid.Updater.CheckAsync(null);
        Assert.Equal(WebAppUpdateStatus.Rejected, rejected.Status);
        Assert.Equal("bad manifest", rejected.Error);
    }

    [Fact]
    public async Task ASlowProviderTimesOut()
    {
        await using var app = new TestApp();
        await using var host = app.CreateHost(new WebAppHostOptions
        {
            UpdateProvider = new MemoryUpdateProvider { Delay = TimeSpan.FromSeconds(30) },
            InstallDirectory = app.InstallDirectory,
            CheckTimeout = TimeSpan.FromMilliseconds(100)
        });

        Assert.Equal(WebAppUpdateStatus.Unavailable, (await host.Updater.CheckAsync(null)).Status);
    }

    [Fact]
    public async Task AnOptionalUpdateInstallsInTheBackground()
    {
        await using var app = new TestApp();
        var options = new WebAppHostOptions { InstallDirectory = app.InstallDirectory };
        options.UseBaseline(typeof(BaselineTests).Assembly, "Shiny.AppDeviceBridge.Tests.baseline.zip", "1.0.0");

        var update = MemoryUpdateProvider.Release("1.1.0", TestApp.Zip("1.1.0"));
        update.IsOptional = true;
        options.UpdateProvider = new MemoryUpdateProvider { Offer = update };

        await using var host = app.CreateHost(options);
        var installed = new TaskCompletionSource<WebAppPackage>();
        host.UpdateInstalled += (_, package) => installed.TrySetResult(package);

        await host.StartAsync();

        // Still serving the baseline; the update waits for the next launch.
        Assert.Equal(WebAppPackageOrigin.Baseline, host.Package!.Origin);
        Assert.Equal(WebAppVersion.Parse("1.1.0"), (await installed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Version);
    }

    [Fact]
    public async Task TheHostDisposesTheProvider()
    {
        await using var app = new TestApp();
        var provider = new MemoryUpdateProvider();
        var host = app.CreateHost(new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory });

        await host.DisposeAsync();
        Assert.True(provider.Disposed);
    }
}

sealed class MemoryUpdateProvider : IUpdateProvider, IDisposable
{
    public MemoryUpdateInfo? Offer { get; set; }
    public Exception? Failure { get; set; }
    public TimeSpan Delay { get; set; }

    public Version? LastHostVersion { get; private set; }
    public WebAppVersion? LastAppVersion { get; private set; }
    public bool Disposed { get; private set; }

    public static MemoryUpdateInfo Release(string version, byte[] zip) => new()
    {
        Version = WebAppVersion.Parse(version),
        Zip = zip,
        FileSize = zip.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(zip))
    };

    public async Task<UpdateInfo?> GetUpdateInfoAsync(Version currentHostVersion, WebAppVersion? currentAppVersion, CancellationToken cancellationToken)
    {
        this.LastHostVersion = currentHostVersion;
        this.LastAppVersion = currentAppVersion;

        if (this.Delay > TimeSpan.Zero)
            await Task.Delay(this.Delay, cancellationToken);

        if (this.Failure is { } failure)
            throw failure;

        return this.Offer;
    }

    public Task<Stream> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new MemoryStream(((MemoryUpdateInfo)update).Zip));

    public void Dispose() => this.Disposed = true;
}

sealed class MemoryUpdateInfo : UpdateInfo
{
    public required byte[] Zip { get; init; }
}
