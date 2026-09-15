using System.Net;
using System.Net.Http.Json;
using System.Text;
using Shiny.Extensions.Stores;

namespace Shiny.WebAppHost.Tests;

public class WebAppFileRootTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    [InlineData("c:/windows")]
    [InlineData("a/b:stream")]
    [InlineData("nul\0l")]
    public void RefusesUnsafePaths(string path)
    {
        var root = new WebAppFileRoot("data", Path.Combine(Path.GetTempPath(), "webapphost-tests", "root"));
        Assert.Null(root.Resolve(path));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("a.txt", "a.txt")]
    [InlineData("/photos//2026/x.jpg", "photos/2026/x.jpg")]
    [InlineData(".hidden", ".hidden")]
    public void ResolvesInsideTheRoot(string path, string relative)
    {
        var root = new WebAppFileRoot("data", Path.Combine(Path.GetTempPath(), "webapphost-tests", "root"));

        var full = root.Resolve(path);

        Assert.NotNull(full);
        Assert.Equal(relative, root.ToRelative(full));
    }

    [Fact]
    public void RefusesLinksThatLeaveTheRoot()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "webapphost-tests", Guid.NewGuid().ToString("n"));
        var rootPath = Path.Combine(scratch, "root");
        var outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(rootPath, "escape"), outside);
            Directory.CreateSymbolicLink(Path.Combine(rootPath, "inside"), Path.Combine(rootPath));

            var root = new WebAppFileRoot("data", rootPath);

            Assert.Null(root.Resolve("escape"));
            Assert.Null(root.Resolve("escape/new-file.txt"));
            Assert.NotNull(root.Resolve("inside/file.txt"));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}

public class StorageBridgeTests
{
    static async Task<(WebAppHost Host, HttpClient WebView)> StartAsync(TestApp app, WebAppHostOptions options, params IWebAppBridge[] bridges)
    {
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var host = app.CreateHost(options, bridges);
        var start = await host.StartAsync();

        var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
        await webView.GetStringAsync(start);

        return (host, webView);
    }

    [Fact]
    public async Task SettingsRoundTripAndStayInTheirLane()
    {
        await using var app = new TestApp();
        var local = new MemoryKeyValueStore();
        var secure = new MemoryKeyValueStore();

        // A value the native app owns. The page must neither see it nor clear it.
        local.Set("native-setting", "keep me");

        var (host, webView) = await StartAsync(app, app.Options(), new WebAppSettingsBridge(local, secure, TestApp.AppId));
        await using var _ = host;
        using var __ = webView;

        var put = await webView.PutAsync("/_bridge/settings/local/theme", new StringContent("""{ "dark": true }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        Assert.Equal("""{ "dark": true }""", await webView.GetStringAsync("/_bridge/settings/local/theme"));
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync("/_bridge/settings/secure/theme")).StatusCode);

        await webView.PutAsync("/_bridge/settings/secure/token", new StringContent("\"s3cret\""));
        Assert.Equal("\"s3cret\"", await webView.GetStringAsync("/_bridge/settings/secure/token"));

        var listing = await webView.GetFromJsonAsync<WebAppSettingsList>("/_bridge/settings/local");
        Assert.Equal(["theme"], listing!.Keys);

        Assert.Equal(HttpStatusCode.BadRequest, (await webView.PutAsync("/_bridge/settings/local/theme", new StringContent("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.PutAsync("/_bridge/settings/local/bad%20key", new StringContent("1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync("/_bridge/settings/elsewhere")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await webView.DeleteAsync("/_bridge/settings/local")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync("/_bridge/settings/local/theme")).StatusCode);
        Assert.Empty((await webView.GetFromJsonAsync<WebAppSettingsList>("/_bridge/settings/local"))!.Keys);

        Assert.Equal("keep me", local.Get<string>("native-setting"));
        Assert.Equal("\"s3cret\"", await webView.GetStringAsync("/_bridge/settings/secure/token"));

        Assert.Equal(HttpStatusCode.NoContent, (await webView.DeleteAsync("/_bridge/settings/secure/token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.DeleteAsync("/_bridge/settings/secure/token")).StatusCode);
    }

    [Fact]
    public async Task FilesCoverTheLifecycle()
    {
        await using var app = new TestApp();
        var options = app.Options();

        var (host, webView) = await StartAsync(app, options, new WebAppFilesBridge(options));
        await using var _ = host;
        using var __ = webView;

        Assert.Equal(["cache", "data"], await webView.GetFromJsonAsync<List<string>>("/_bridge/files"));

        // write, read, overwrite rules
        var created = await webView.PutAsync("/_bridge/files/data/content?path=notes/today.txt", new StringContent("hello"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("hello", await webView.GetStringAsync("/_bridge/files/data/content?path=notes/today.txt"));

        var refused = await webView.PutAsync("/_bridge/files/data/content?path=notes/today.txt&overwrite=false", new StringContent("nope"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await webView.PostAsync("/_bridge/files/data/append?path=notes/today.txt", new StringContent(", world"))).StatusCode);
        Assert.Equal("hello, world", await webView.GetStringAsync("/_bridge/files/data/content?path=notes/today.txt"));

        // ranges come from the server's file download result
        using (var range = new HttpRequestMessage(HttpMethod.Get, "/_bridge/files/data/content?path=notes/today.txt"))
        {
            range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(7, 11);
            var partial = await webView.SendAsync(range);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal("world", await partial.Content.ReadAsStringAsync());
        }

        // directories, listing, info
        Assert.Equal(HttpStatusCode.Created, (await webView.PostAsync("/_bridge/files/data/directory?path=archive/2026", null)).StatusCode);

        var root = await webView.GetFromJsonAsync<List<WebAppFileEntry>>("/_bridge/files/data/list");
        Assert.NotNull(root);
        Assert.Equal(["archive", "notes"], root.Select(x => x.Name));
        Assert.All(root, x => Assert.True(x.IsDirectory));

        var info = await webView.GetFromJsonAsync<WebAppFileEntry>("/_bridge/files/data/info?path=notes/today.txt");
        Assert.Equal(new WebAppFileEntry("today.txt", "notes/today.txt", false, 12, info!.Modified), info);

        // rename, move, copy
        var renamed = await webView.PostAsJsonAsync("/_bridge/files/data/move", new WebAppFileTransfer("notes/today.txt", "notes/yesterday.txt"));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var moved = await webView.PostAsJsonAsync("/_bridge/files/data/move", new WebAppFileTransfer("notes", "archive/2026/notes"));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("hello, world", await webView.GetStringAsync("/_bridge/files/data/content?path=archive/2026/notes/yesterday.txt"));

        var copied = await webView.PostAsJsonAsync("/_bridge/files/data/copy", new WebAppFileTransfer("archive", "backup"));
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        Assert.Equal("hello, world", await webView.GetStringAsync("/_bridge/files/data/content?path=backup/2026/notes/yesterday.txt"));

        Assert.Equal(HttpStatusCode.Conflict, (await webView.PostAsJsonAsync("/_bridge/files/data/copy", new WebAppFileTransfer("archive", "backup"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.PostAsJsonAsync("/_bridge/files/data/move", new WebAppFileTransfer("archive", "archive/inner"))).StatusCode);

        // delete
        Assert.Equal(HttpStatusCode.Conflict, (await webView.DeleteAsync("/_bridge/files/data/entry?path=backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await webView.DeleteAsync("/_bridge/files/data/entry?path=backup&recursive=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync("/_bridge/files/data/info?path=backup")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.DeleteAsync("/_bridge/files/data/entry?path=")).StatusCode);

        // the sandbox
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.GetAsync("/_bridge/files/data/content?path=../current.json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.PostAsJsonAsync("/_bridge/files/data/copy", new WebAppFileTransfer("archive", "../stolen"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.GetAsync("/_bridge/files/elsewhere/list")).StatusCode);
    }

    [Fact]
    public async Task FilesRefuseWritesOverTheLimit()
    {
        await using var app = new TestApp();
        var options = app.Options();
        options.MaxFileWriteBytes = 10;

        var (host, webView) = await StartAsync(app, options, new WebAppFilesBridge(options));
        await using var _ = host;
        using var __ = webView;

        Assert.Equal(HttpStatusCode.Created, (await webView.PutAsync("/_bridge/files/data/content?path=small.txt", new StringContent("0123456789"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await webView.PutAsync("/_bridge/files/data/content?path=big.txt", new StringContent("0123456789A"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await webView.PostAsync("/_bridge/files/data/append?path=small.txt", new StringContent("A"))).StatusCode);

        var listing = await webView.GetFromJsonAsync<List<WebAppFileEntry>>("/_bridge/files/data/list");
        Assert.Equal(["small.txt"], listing!.Select(x => x.Name));
    }
}
