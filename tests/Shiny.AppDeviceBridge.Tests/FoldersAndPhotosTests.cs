using System.Net;
using System.Net.Http.Json;
using Shiny.AppDeviceBridge.Folders;
using Shiny.AppDeviceBridge.Photos;
using Shiny.AppDeviceBridge.Photos.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The parts of the folders and photos bridges that do not need a device: what they remember, and what they refuse. The
/// pickers and libraries themselves are platform UI, checked in the sample app.
/// </summary>
public class FoldersAndPhotosTests
{
    [Fact]
    public void Picked_folders_survive_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"), "folders.json");

        var memory = new FolderMemory(file);
        memory.Save(new SavedFolder("docs", "Documents", "path:/tmp/docs"));
        memory.Save(new SavedFolder("photos", "Pictures", "bookmark:AAAA"));
        memory.Save(new SavedFolder("DOCS", "Documents 2", "path:/tmp/docs2"));    // same root, any case: replaced

        var reloaded = new FolderMemory(file);
        Assert.Equal(["photos", "DOCS"], reloaded.All.Select(x => x.Root));
        Assert.Equal("path:/tmp/docs2", reloaded.Find("docs")?.Token);

        reloaded.Remove("photos");
        Assert.Equal(["DOCS"], new FolderMemory(file).All.Select(x => x.Root));
    }

    [Fact]
    public void A_damaged_memory_forgets_rather_than_failing()
    {
        var file = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"), "folders.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");

        Assert.Empty(new FolderMemory(file).All);
    }

    [Fact]
    public async Task Without_a_folder_picker_the_bridge_says_so()
    {
        await using var app = new TestApp();
        var options = app.BridgeOptions();
        var (host, webView) = await StartAsync(app, new FoldersBridge(new FolderRoots(new WebAppFileRoots(options), options)));
        await using var _ = host;
        using var __ = webView;

        // This test host is macOS or Windows running the net10.0 build, which only has GTK's picker on Linux.
        if (OperatingSystem.IsLinux())
            return;

        var list = await webView.GetFromJsonAsync("/_bridge/folders", Folders.Client.FoldersJsonContext.Default.FolderList);
        Assert.False(list!.Supported);
        Assert.Equal(HttpStatusCode.NotImplemented, (await webView.PostAsJsonAsync("/_bridge/folders/pick", new { root = "docs" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.DeleteAsync("/_bridge/folders/docs")).StatusCode);
    }

    [Fact]
    public async Task Photos_refuse_what_they_cannot_do_before_showing_anything()
    {
        await using var app = new TestApp();
        var options = app.BridgeOptions();
        var (host, webView) = await StartAsync(app, new PhotosBridge(new WebAppFileRoots(options), options));
        await using var _ = host;
        using var __ = webView;

        // No photo library in the net10.0 build.
        var status = await webView.GetFromJsonAsync("/_bridge/photos", PhotosJsonContext.Default.PhotoStatus);
        Assert.False(status!.LibrarySupported);
        Assert.Equal(HttpStatusCode.NotImplemented, (await webView.GetAsync("/_bridge/photos/library")).StatusCode);

        // Checked before the picker opens.
        Assert.Equal(HttpStatusCode.BadRequest, (await webView.PostAsJsonAsync("/_bridge/photos/pick", new { limit = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webView.PostAsJsonAsync("/_bridge/photos/pick", new { root = "nowhere" })).StatusCode);
    }

    static async Task<(WebAppHost Host, HttpClient WebView)> StartAsync(TestApp app, IWebAppBridge bridge)
    {
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        var host = app.CreateHost(app.Options(), bridge);
        var start = await host.StartAsync();

        var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
        await webView.GetStringAsync(start);
        return (host, webView);
    }
}
