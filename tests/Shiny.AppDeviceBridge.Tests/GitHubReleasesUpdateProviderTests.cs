using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.AppDeviceBridge.Tests;

public class GitHubReleasesUpdateProviderTests
{
    static readonly Version Host = new(1, 0);

    [Theory]
    [InlineData("https://github.com/acme/field-app", "acme", "field-app", "https://api.github.com/")]
    [InlineData("https://github.com/acme/field-app.git", "acme", "field-app", "https://api.github.com/")]
    [InlineData("https://github.com/acme/field-app/releases", "acme", "field-app", "https://api.github.com/")]
    [InlineData("https://git.example.com/acme/field-app/", "acme", "field-app", "https://git.example.com/api/v3/")]
    public void ReadsTheRepositoryUrl(string url, string owner, string repository, string api)
    {
        using var provider = new GitHubReleasesUpdateProvider(url);

        Assert.Equal(owner, provider.Owner);
        Assert.Equal(repository, provider.Repository);
        Assert.Equal(new Uri(api), provider.ApiBaseAddress);
    }

    [Theory]
    [InlineData("github.com/acme/field-app")]
    [InlineData("https://github.com/acme")]
    [InlineData("ftp://github.com/acme/field-app")]
    public void RefusesSomethingThatIsNotARepository(string url)
        => Assert.Throws<ArgumentException>(() => new GitHubReleasesUpdateProvider(url));

    [Fact]
    public async Task OffersTheNewestStableReleaseWithAZip()
    {
        var api = new FakeGitHub();
        api.Release("v1.0.0", "webapp.zip");
        api.Release("v1.2.0", "webapp.zip", body: "Faster maps", published: "2026-09-01T10:00:00Z");
        api.Release("v1.3.0", "webapp.zip", draft: true);
        api.Release("v1.4.0-beta.1", "webapp.zip", prerelease: true);
        api.Release("v1.5.0", "notes.txt");
        api.Release("not-a-version", "webapp.zip");

        using var provider = api.CreateProvider();
        var update = Assert.IsType<GitHubUpdateInfo>(await provider.GetUpdateInfoAsync(Host, WebAppVersion.Parse("1.0.0"), CancellationToken.None));

        Assert.Equal(WebAppVersion.Parse("1.2.0"), update.Version);
        Assert.Equal("v1.2.0", update.TagName);
        Assert.Equal("Faster maps", update.WhatsNew);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), update.ReleaseDate);
        Assert.Equal(api.Zips["v1.2.0"].Length, update.FileSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(api.Zips["v1.2.0"])), update.Sha256);
        Assert.True(update.IsOptional);

        Assert.Equal("/repos/acme/field-app/releases", api.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("application/vnd.github+json", api.Requests[0].Headers.Accept.Single().MediaType);
        Assert.NotEmpty(api.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task NothingNewerIsNull()
    {
        var api = new FakeGitHub();
        api.Release("v1.2.0", "webapp.zip");

        using var provider = api.CreateProvider();
        Assert.Null(await provider.GetUpdateInfoAsync(Host, WebAppVersion.Parse("1.2.0"), CancellationToken.None));
    }

    [Fact]
    public async Task PrereleasesAreOptIn()
    {
        var api = new FakeGitHub();
        api.Release("v1.0.0", "webapp.zip");
        api.Release("v1.1.0-beta.1", "webapp.zip", prerelease: true);

        using var provider = api.CreateProvider();
        provider.IncludePrereleases = true;

        var update = await provider.GetUpdateInfoAsync(Host, null, CancellationToken.None);
        Assert.Equal(WebAppVersion.Parse("1.1.0-beta.1"), update!.Version);
    }

    [Fact]
    public async Task TagPrefixAndAssetPatternPickTheWebAppOut()
    {
        var api = new FakeGitHub();
        api.Release("webapp-v2.0.0", "field-app-2.0.0.zip", extraAsset: "symbols.zip");
        api.Release("server-v9.0.0", "server.zip");

        using var provider = api.CreateProvider();
        provider.TagPrefix = "webapp-";
        provider.AssetName = "field-app-*.zip";

        var update = Assert.IsType<GitHubUpdateInfo>(await provider.GetUpdateInfoAsync(Host, null, CancellationToken.None));
        Assert.Equal(WebAppVersion.Parse("2.0.0"), update.Version);
        Assert.Equal("field-app-2.0.0.zip", update.AssetName);
    }

    [Fact]
    public async Task RequiredWhenAnySkippedReleaseIsRequired()
    {
        var api = new FakeGitHub();
        api.Release("v1.0.0", "webapp.zip");
        api.Release("v1.1.0", "webapp.zip", body: "[required] new bridge contract");
        api.Release("v1.2.0", "webapp.zip", body: "polish");

        using var provider = api.CreateProvider();
        provider.RequiredWhen = x => x.WhatsNew?.Contains("[required]") == true;

        Assert.False((await provider.GetUpdateInfoAsync(Host, WebAppVersion.Parse("1.0.0"), CancellationToken.None))!.IsOptional);
        Assert.True((await provider.GetUpdateInfoAsync(Host, WebAppVersion.Parse("1.1.0"), CancellationToken.None))!.IsOptional);
    }

    [Fact]
    public async Task DownloadsTheAssetThroughTheApiWithTheToken()
    {
        var api = new FakeGitHub();
        api.Release("v1.0.0", "webapp.zip");

        using var provider = api.CreateProvider();
        provider.Token = "secret";

        var update = await provider.GetUpdateInfoAsync(Host, null, CancellationToken.None);
        await using var stream = await provider.DownloadAsync(update!, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(api.Zips["v1.0.0"], buffer.ToArray());

        var download = api.Requests.Last();
        Assert.Equal("application/octet-stream", download.Headers.Accept.Single().MediaType);
        Assert.Equal("Bearer secret", download.Headers.Authorization!.ToString());
        Assert.All(api.Requests, x => Assert.Equal("secret", x.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task AnApiErrorIsOffline()
    {
        await using var app = new TestApp();
        var provider = new GitHubReleasesUpdateProvider(
            "https://github.com/acme/field-app",
            () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))
        );

        await using var host = app.CreateHost(new WebAppHostOptions { UpdateProvider = provider, InstallDirectory = app.InstallDirectory });
        Assert.Equal(WebAppUpdateStatus.Unavailable, (await host.Updater.CheckAsync(null)).Status);
    }

    [Fact]
    public async Task TheHostInstallsARelease()
    {
        await using var app = new TestApp();
        var api = new FakeGitHub();
        api.Release("v1.0.0", "webapp.zip");

        await using var host = app.CreateHost(new WebAppHostOptions { UpdateProvider = api.CreateProvider(), InstallDirectory = app.InstallDirectory });
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Contains("version 1.0.0", await webView.GetStringAsync(start));
        Assert.Equal(WebAppPackageOrigin.Installed, host.Package!.Origin);
    }

    /// <summary>The releases API and asset downloads for acme/field-app.</summary>
    sealed class FakeGitHub
    {
        readonly List<string> releases = [];

        public Dictionary<string, byte[]> Zips { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Release(
            string tag,
            string asset,
            string? body = null,
            bool draft = false,
            bool prerelease = false,
            string published = "2026-01-01T00:00:00Z",
            string? extraAsset = null
        )
        {
            var id = this.releases.Count + 1;
            var zip = TestApp.Zip(tag.TrimStart('v'));
            this.Zips[tag] = zip;

            var assets = new List<string>();
            if (extraAsset is not null)
                assets.Add(Asset(id * 10 + 1, extraAsset, [1, 2, 3]));
            assets.Add(Asset(id * 10, asset, zip));

            this.releases.Add($$"""
                {
                  "tag_name": "{{tag}}",
                  "name": "{{tag}}",
                  "body": {{(body is null ? "null" : $"\"{body}\"")}},
                  "html_url": "https://github.com/acme/field-app/releases/tag/{{tag}}",
                  "draft": {{(draft ? "true" : "false")}},
                  "prerelease": {{(prerelease ? "true" : "false")}},
                  "published_at": "{{published}}",
                  "assets": [{{String.Join(",", assets)}}]
                }
                """);
        }

        static string Asset(int id, string name, byte[] content) => $$"""
            {
              "id": {{id}},
              "name": "{{name}}",
              "size": {{content.Length}},
              "url": "https://api.github.com/repos/acme/field-app/releases/assets/{{id}}",
              "browser_download_url": "https://github.com/acme/field-app/releases/download/x/{{name}}",
              "digest": "sha256:{{Convert.ToHexStringLower(SHA256.HashData(content))}}"
            }
            """;

        public GitHubReleasesUpdateProvider CreateProvider()
            => new("https://github.com/acme/field-app", () => new StubHandler(this.Respond));

        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            lock (this.Requests)
                this.Requests.Add(request);

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/acme/field-app/releases")
            {
                // Newest first, as GitHub lists them.
                var json = "[" + String.Join(",", Enumerable.Reverse(this.releases)) + "]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (path.StartsWith("/repos/acme/field-app/releases/assets/", StringComparison.Ordinal))
            {
                var id = Int32.Parse(path[(path.LastIndexOf('/') + 1)..]);
                var tag = this.Zips.Keys.ElementAt(id / 10 - 1);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(this.Zips[tag]) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
