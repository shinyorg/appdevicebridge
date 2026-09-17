using System.Net;
using System.Net.Http.Json;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Folders;
using Shiny.AppDeviceBridge.Folders.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>Folders the app maps while it runs — a share, a folder picked in its own dialog — and the files it may take.</summary>
public class FileRootAndUploadTests
{
    [Fact]
    public void Reports_every_change_to_the_roots()
    {
        using var scratch = new Scratch();
        var roots = new WebAppFileRoots(new AppDeviceBridgeOptions { AppId = TestApp.AppId, DataDirectory = scratch.Path });
        var changes = new List<(string, FileRootChange)>();
        roots.Changed += (_, e) => changes.Add((e.Name, e.Change));

        roots.Add(new WebAppFileRoot("share", scratch.Directory("a")));
        roots.Add(new WebAppFileRoot("share", scratch.Directory("b")));
        Assert.True(roots.Remove("share"));

        // The app's own roots are not the app's to take away at runtime, and nothing happened.
        Assert.False(roots.Remove("data"));
        Assert.False(roots.Remove("share"));

        Assert.Equal([("share", FileRootChange.Added), ("share", FileRootChange.Replaced), ("share", FileRootChange.Removed)], changes);
    }

    [Fact]
    public void A_directory_root_is_an_absolute_path()
    {
        Assert.Throws<ArgumentException>(() => new WebAppFileRoot("share", "relative/path"));
        Assert.Throws<ArgumentException>(() => new WebAppFileRoot("share", "../elsewhere"));
    }

    /// <summary>
    /// The case that was missing: a folder the app decides on, by path, remembered across launches — on every platform, not
    /// only where the platform has a folder picker. This test host is one without.
    /// </summary>
    [Fact]
    public void Keeps_a_folder_added_by_path_across_a_restart()
    {
        using var scratch = new Scratch();
        var work = scratch.Directory("Work");
        File.WriteAllText(Path.Combine(work, "notes.txt"), "hello");

        var options = scratch.Options();
        var first = new FolderRoots(new WebAppFileRoots(options), options);
        var added = first.Add("work", work, "Work stuff");

        Assert.Equal(new PickedFolder("work", "Work stuff"), added);

        // A fresh launch: new registry, new service, the same data directory.
        var roots = new WebAppFileRoots(options);
        var restarted = new FolderRoots(roots, options);

        Assert.Equal([new PickedFolder("work", "Work stuff", true)], restarted.All);
        Assert.True(roots.TryResolve("work", "notes.txt", out var resolved));
        Assert.Equal("hello", File.ReadAllText(resolved));
    }

    [Fact]
    public void Names_a_folder_after_its_directory_by_default()
    {
        using var scratch = new Scratch();
        var options = scratch.Options();
        var folders = new FolderRoots(new WebAppFileRoots(options), options);

        Assert.Equal("Team Docs", folders.Add("team-docs", scratch.Directory("Team Docs")).DisplayName);
    }

    [Fact]
    public void Replaces_a_folder_under_the_same_name()
    {
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Directory("old"), "which.txt"), "old");
        File.WriteAllText(Path.Combine(scratch.Directory("new"), "which.txt"), "new");

        var options = scratch.Options();
        var roots = new WebAppFileRoots(options);
        var folders = new FolderRoots(roots, options);

        folders.Add("share", Path.Combine(scratch.Path, "old"));
        folders.Add("share", Path.Combine(scratch.Path, "new"));

        Assert.Single(folders.All);
        Assert.True(roots.TryResolve("share", "which.txt", out var resolved));
        Assert.Equal("new", File.ReadAllText(resolved));
    }

    [Fact]
    public void Forgets_a_folder_for_good()
    {
        using var scratch = new Scratch();
        var options = scratch.Options();
        var roots = new WebAppFileRoots(options);
        var folders = new FolderRoots(roots, options);
        folders.Add("share", scratch.Directory("share"));

        Assert.True(folders.Forget("share"));
        Assert.False(roots.TryGet("share", out _));
        Assert.Empty(folders.All);
        Assert.False(folders.Forget("share"));

        // And not back after a restart.
        Assert.Empty(new FolderRoots(new WebAppFileRoots(options), options).All);
    }

    [Fact]
    public void Lists_a_folder_that_went_away_as_unavailable()
    {
        using var scratch = new Scratch();
        var options = scratch.Options();
        var gone = scratch.Directory("gone");
        new FolderRoots(new WebAppFileRoots(options), options).Add("gone", gone);

        Directory.Delete(gone);

        var roots = new WebAppFileRoots(options);
        var restarted = new FolderRoots(roots, options);

        Assert.Equal([new PickedFolder("gone", "gone", false)], restarted.All);
        Assert.False(roots.TryGet("gone", out _));
    }

    [Theory]
    [InlineData("work", "relative/path", "bad_request", 400)]
    [InlineData("not a name", "", "bad_request", 400)]
    [InlineData("data", "", "configured_root", 409)]
    [InlineData("work", "missing", "folder_unavailable", 409)]
    public void Refuses_a_folder_it_cannot_keep(string root, string path, string code, int status)
    {
        using var scratch = new Scratch();
        var options = scratch.Options();
        var roots = new WebAppFileRoots(options);
        var folders = new FolderRoots(roots, options);

        var target = path switch
        {
            "" => scratch.Directory("exists"),
            "missing" => System.IO.Path.Combine(scratch.Path, "missing"),
            _ => path
        };

        var failure = Assert.Throws<WebAppFileException>(() => folders.Add(root, target));
        Assert.Equal(code, failure.Code);
        Assert.Equal(status, failure.StatusCode);
        Assert.Empty(folders.All);
    }

    /// <summary>The page sees a folder the app added natively — in the folders list, in the files bridge, and as it happens.</summary>
    [Fact]
    public async Task Tells_the_page_about_a_folder_the_app_added()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Directory("Work"), "notes.txt"), "hello");

        var options = app.BridgeOptions();
        var events = new WebAppEventHub();
        var roots = new WebAppFileRoots(options);
        var folders = new FolderRoots(roots, options);

        await using var host = app.CreateHost(app.Options(), options, events, [new FoldersBridge(folders), new WebAppFilesBridge(roots, options, events)]);
        var start = await host.StartAsync();

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
        await webView.GetStringAsync(start);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await webView.GetAsync("/_bridge/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));

        while (!events.HasSubscribers)
            await Task.Delay(10, timeout.Token);

        folders.Add("work", Path.Combine(scratch.Path, "Work"), "Work");

        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && !line.StartsWith("event:", StringComparison.Ordinal))
        {
        }

        Assert.Equal("files.roots", line?["event:".Length..].Trim());
        Assert.Equal("""{"root":"work","change":"Added"}""", (await reader.ReadLineAsync(timeout.Token))?["data:".Length..].Trim());

        var listed = await webView.GetFromJsonAsync("/_bridge/folders", FoldersJsonContext.Default.FolderList, timeout.Token);
        Assert.Equal([new PickedFolder("work", "Work")], listed!.Folders);

        Assert.Contains("work", (await webView.GetFromJsonAsync<List<string>>("/_bridge/files", timeout.Token))!);
        Assert.Equal("hello", await webView.GetStringAsync("/_bridge/files/work/content?path=notes.txt", timeout.Token));
    }

    /// <summary>
    /// The files bridge is not the only thing that takes a file: with it switched off, the app's own upload endpoint still
    /// gets the size the app asked for, not the server's 30 MB default.
    /// </summary>
    [Fact]
    public async Task Raises_the_body_limit_without_the_files_bridge()
    {
        await using var app = new TestApp();
        var server = app.CreateServer(
            app.BridgeOptions(o =>
            {
                o.EnableFiles = false;
                o.MaxFileWriteBytes = 48 * 1024 * 1024;
            }),
            [],
            http => http.Configure(s => s.MapPost("/api/upload", async ctx =>
            {
                long total = 0;
                var buffer = new byte[81920];
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    total += read;

                await ctx.Response.WriteAsync(total.ToString());
            }))
        );
        var origin = await server.StartAsync();

        Assert.Equal(48L * 1024 * 1024, server.Http.Options.Limits.MaxRequestBodySize);

        using var client = new HttpClient();
        var size = 40 * 1024 * 1024;
        var response = await client.PostAsync(new Uri(origin, "/api/upload"), new ByteArrayContent(new byte[size]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(size.ToString(), await response.Content.ReadAsStringAsync());

        // Past it, the server refuses before the endpoint reads a byte — a 413, or the connection closed under a client still
        // sending, depending on how far the body got.
        try
        {
            var tooBig = await client.PostAsync(new Uri(origin, "/api/upload"), new ByteArrayContent(new byte[49 * 1024 * 1024]));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        }
        catch (HttpRequestException)
        {
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1024L * 1024 * 1024)]
    public async Task Leaves_a_limit_the_app_set_higher_alone(long? limit)
    {
        await using var app = new TestApp();
        var server = app.CreateServer(app.BridgeOptions(), [], http => http.Options.Limits.MaxRequestBodySize = limit);

        _ = server.Http;
        Assert.Equal(limit, server.Http.Options.Limits.MaxRequestBodySize);
    }

    sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));

        public AppDeviceBridgeOptions Options() => new() { AppId = TestApp.AppId, DataDirectory = System.IO.Path.Combine(this.Path, "data") };

        public string Directory(string name) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine(this.Path, name)).FullName;

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(this.Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
