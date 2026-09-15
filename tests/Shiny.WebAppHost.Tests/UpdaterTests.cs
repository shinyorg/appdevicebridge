using System.Net;
using System.Net.Http.Json;

namespace Shiny.WebAppHost.Tests;

public class UpdaterTests
{
    [Fact]
    public async Task InstallsASignedRelease()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();
        await using var host = app.CreateHost();

        var check = await host.Updater.CheckAsync(null);

        Assert.Equal(WebAppUpdateStatus.Available, check.Status);
        Assert.Equal(WebAppUpdateKind.Required, check.Kind);

        var package = await host.Updater.InstallAsync(check);

        Assert.Equal(WebAppPackageOrigin.Installed, package.Origin);
        Assert.True(File.Exists(package.ZipPath));
        Assert.Equal(WebAppUpdateStatus.UpToDate, (await host.Updater.CheckAsync(package.Version)).Status);
    }

    [Fact]
    public async Task RejectsAReleaseSignedWithAnotherKey()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync(signingKey: WebAppReleaseSignature.CreateKeyPair().PrivateKeyPem);
        await using var host = app.CreateHost();

        Assert.Equal(WebAppUpdateStatus.Rejected, (await host.Updater.CheckAsync(null)).Status);
    }

    [Fact]
    public async Task RejectsAReplayedOlderRelease()
    {
        await using var app = new TestApp();
        var zip = TestApp.Zip("1.0.0");
        var release = app.Store.Add("1.0.0", zip);

        // A validly signed old release, served by something in the path as if it were an update.
        var response = new WebAppUpdateResponse
        {
            Kind = WebAppUpdateKind.Required,
            Release = release,
            Signature = app.Sign(release),
            DownloadUrl = "releases/1.0.0/download"
        };

        var options = app.Options(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response, WebAppJsonContext.Default.WebAppUpdateResponse)
        }));

        await using var host = app.CreateHost(options);

        var check = await host.Updater.CheckAsync(WebAppVersion.Parse("2.0.0"));
        Assert.Equal(WebAppUpdateStatus.Rejected, check.Status);
    }

    [Fact]
    public async Task RejectsADownloadThatDoesNotMatchItsHash()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"), sha256: new string('0', 64));
        await app.StartReleaseServerAsync();
        await using var host = app.CreateHost();

        var check = await host.Updater.CheckAsync(null);

        await Assert.ThrowsAsync<InvalidDataException>(() => host.Updater.InstallAsync(check));
        Assert.Empty(Directory.EnumerateFiles(app.InstallDirectory, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(app.InstallDirectory, "*.download", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsAnArchiveWithoutTheEntryDocument()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0", withIndex: false));
        await app.StartReleaseServerAsync();
        await using var host = app.CreateHost();

        var check = await host.Updater.CheckAsync(null);

        await Assert.ThrowsAsync<InvalidDataException>(() => host.Updater.InstallAsync(check));
    }

    [Fact]
    public async Task OfflineIsUnavailable()
    {
        await using var app = new TestApp();
        await using var host = app.CreateHost(app.Options(() => new StubHandler(_ => throw new HttpRequestException("offline"))));

        Assert.Equal(WebAppUpdateStatus.Unavailable, (await host.Updater.CheckAsync(null)).Status);
    }
}
