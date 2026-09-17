using System.Net;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Client;
using Shiny.Extensions.Stores;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The built-in bridges' generated clients, against a running host — what a Blazor page actually calls.</summary>
public class BuiltInClientTests
{
    [Fact]
    public async Task SettingsReadAndWriteTheAppsOwnTypes()
    {
        await using var fixture = await HostFixture.StartAsync(
            app => [new WebAppSettingsBridge(new MemoryKeyValueStore(), new MemoryKeyValueStore(), TestApp.AppId)]
        );
        var settings = new SettingsBridgeClient(fixture.Transport);

        Assert.Null(await settings.GetAsync(SettingsScope.Local, "theme", ClientTestJson.Default.Theme));
        Assert.Equal(new Theme(false, 3), await settings.GetAsync(SettingsScope.Local, "theme", ClientTestJson.Default.Theme, new Theme(false, 3)));

        await settings.SetAsync(SettingsScope.Local, "theme", new Theme(true, 14), ClientTestJson.Default.Theme);
        Assert.Equal(new Theme(true, 14), await settings.GetAsync(SettingsScope.Local, "theme", ClientTestJson.Default.Theme));

        // The scope travels as the enum's name; the host takes it in any case.
        var listing = await settings.ListAsync(SettingsScope.Local);
        Assert.Equal(SettingsScope.Local, listing.Scope);
        Assert.Equal(["theme"], listing.Keys);
        Assert.Empty((await settings.ListAsync(SettingsScope.Secure)).Keys);

        await settings.RemoveAsync(SettingsScope.Local, "theme");
        var missing = await Assert.ThrowsAsync<BridgeException>(() => settings.RemoveAsync(SettingsScope.Local, "theme"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task FilesRoundTripTextAndReportConflictsAsBridgeExceptions()
    {
        await using var fixture = await HostFixture.StartAsync(app => [FilesBridge(app.BridgeOptions())]);
        var files = new FilesBridgeClient(fixture.Transport);

        Assert.Equal(["cache", "data"], await files.GetRootsAsync());

        var written = await files.WriteTextAsync("data", "notes/today.txt", "hello");
        Assert.Equal(new FileEntry("today.txt", "notes/today.txt", false, 5, written.Modified), written);

        await files.AppendTextAsync("data", "notes/today.txt", ", world");
        Assert.Equal("hello, world", await files.ReadTextAsync("data", "notes/today.txt"));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => files.WriteTextAsync("data", "notes/today.txt", "nope", overwrite: false));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("exists", refused.Code);

        var moved = await files.MoveAsync("data", new FileTransfer("notes", "archive/notes"));
        Assert.Equal("archive/notes", moved.Path);
        Assert.Equal(["archive"], (await files.ListAsync("data")).Select(x => x.Name));

        await files.DeleteAsync("data", "archive", recursive: true);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => files.GetInfoAsync("data", "archive"))).StatusCode);
    }

    [Fact]
    public async Task LinksAnswerNullWhenNothingIsPending()
    {
        var links = new WebAppLinks(new WebAppLinkOptions { Schemes = { "sample" } });
        var events = new WebAppEventHub();

        await using var fixture = await HostFixture.StartAsync(app => [new WebAppLinksBridge(links, events)], events);
        var client = new LinksBridgeClient(fixture.Transport);

        Assert.Null(await client.GetPendingAsync());

        links.Receive(new Uri("sample://orders/42"));

        Assert.Equal("/orders/42", (await client.GetPendingAsync())?.Route);
        Assert.Equal("/orders/42", (await client.ConsumeAsync())?.Route);
        Assert.Null(await client.ConsumeAsync());
    }

    [Fact]
    public async Task HostDescribesItselfAndItsBridges()
    {
        await using var fixture = await HostFixture.StartAsync(app => [FilesBridge(app.BridgeOptions())]);
        var info = await new HostBridgeClient(fixture.Transport).GetInfoAsync();

        Assert.Equal(TestApp.AppId, info.AppId);
        Assert.Contains(info.Bridges, x => x is { Name: "files", IsSupported: true });
    }

    [Fact]
    public async Task A_root_added_at_runtime_is_served_like_the_configured_ones()
    {
        WebAppFileRoots? registry = null;
        await using var fixture = await HostFixture.StartAsync(app =>
        {
            var options = app.BridgeOptions();
            registry = new WebAppFileRoots(options);
            return [new WebAppFilesBridge(registry, options, new WebAppEventHub())];
        });
        var files = new FilesBridgeClient(fixture.Transport);

        var picked = new MemoryFileStore("picked");
        registry!.Add(picked);

        Assert.Equal(["cache", "data", "picked"], await files.GetRootsAsync());
        await files.WriteTextAsync("picked", "notes/a.txt", "from the page");
        Assert.Equal("from the page", await files.ReadTextAsync("picked", "notes/a.txt"));
        Assert.Equal(["a.txt"], (await files.ListAsync("picked", "notes")).Select(x => x.Name));

        // The page's paths are checked before any store sees them.
        var refused = await Assert.ThrowsAsync<BridgeException>(() => files.ReadTextAsync("picked", "../data/secret.txt"));
        Assert.Equal("invalid_path", refused.Code);

        // A store with no paths on disk cannot be handed to a bridge that needs one.
        Assert.False(registry.TryResolve("picked", "notes/a.txt", out _));

        Assert.True(registry.Remove("picked"));
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => files.ListAsync("picked"))).StatusCode);
    }

    [Fact]
    public async Task The_apps_own_roots_cannot_be_replaced_or_removed()
    {
        await using var app = new TestApp();
        var registry = new WebAppFileRoots(app.BridgeOptions());

        Assert.Throws<InvalidOperationException>(() => registry.Add(new MemoryFileStore("data")));
        Assert.False(registry.Remove("data"));
        Assert.True(registry.IsConfigured("cache"));
    }

    [Theory]
    [InlineData("a?b.txt")]
    [InlineData("a|b")]
    [InlineData("a\u0001b")]
    [InlineData("x/../y")]
    public void Paths_mean_the_same_on_every_platform(string path)
        => Assert.Null(WebAppFilePath.Normalize(path));

    static WebAppFilesBridge FilesBridge(AppDeviceBridgeOptions options) => new(new WebAppFileRoots(options), options, new WebAppEventHub());

    /// <summary>A store with no disk behind it, as a picked Android folder has none.</summary>
    sealed class MemoryFileStore(string name) : WebAppFileStore(name)
    {
        readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(this.files.TryGetValue(path, out var bytes)
                ? new FileEntry(WebAppFilePath.NameOf(path), path, false, bytes.Length, DateTimeOffset.UtcNow)
                : null);

        public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileEntry>>(
            [
                .. this.files
                    .Where(x => WebAppFilePath.ParentOf(x.Key) == path)
                    .Select(x => new FileEntry(WebAppFilePath.NameOf(x.Key), x.Key, false, x.Value.Length, DateTimeOffset.UtcNow))
            ]);

        public override Task<Shiny.Net.HttpServer.IResult> ReadAsync(string path, string? downloadName, CancellationToken cancellationToken)
            => this.files.TryGetValue(path, out var bytes)
                ? Task.FromResult<Shiny.Net.HttpServer.IResult>(Shiny.Net.HttpServer.Files.FileDownloadResult.FromBytes(bytes, "text/plain", downloadName))
                : throw WebAppFileException.NotFound();

        public override async Task<FileWriteResult> WriteAsync(string path, Stream content, FileWriteMode mode, long maxBytes, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var created = this.files.TryAdd(path, buffer.ToArray());
            this.files[path] = buffer.ToArray();
            return new FileWriteResult((await this.GetEntryAsync(path, cancellationToken))!, created);
        }

        public override Task<FileWriteResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<FileEntry> TransferAsync(string from, string to, bool overwrite, bool move, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A host with the WebView's session, and the page's transport over it.</summary>
    internal sealed class HostFixture(TestApp app, WebAppHost host, HttpClient webView) : IAsyncDisposable
    {
        public IBridgeTransport Transport { get; } = new HttpTransport(webView);

        public static async Task<HostFixture> StartAsync(Func<TestApp, IWebAppBridge[]> bridges, WebAppEventHub? events = null, Action<HttpClient>? onStarted = null)
        {
            var app = new TestApp();
            app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await app.StartReleaseServerAsync();

            var host = app.CreateHost(app.Options(), null, events, bridges(app));
            var start = await host.StartAsync();

            var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
            await webView.GetStringAsync(start);
            onStarted?.Invoke(webView);

            return new HostFixture(app, host, webView);
        }

        public async ValueTask DisposeAsync()
        {
            webView.Dispose();
            await host.DisposeAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>What <c>BlazorBridgeTransport</c> does, minus the WebAssembly: relative paths under the bridge prefix.</summary>
    internal sealed class HttpTransport(HttpClient http) : IBridgeTransport
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri("/_bridge/" + request.RequestUri!.OriginalString, UriKind.Relative);
            return http.SendAsync(request, cancellationToken);
        }

        public Task<IAsyncDisposable> SubscribeAsync(string eventName, Func<string, Task> handler) => throw new NotSupportedException();
    }
}

public sealed record Theme(bool Dark, int FontSize);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Theme))]
public partial class ClientTestJson : JsonSerializerContext;

/// <summary>Native calls reach C# handlers as contracts, and results go back as JSON the host can read.</summary>
public class NativeCallTests
{
    [Fact]
    public async Task A_typed_handler_receives_the_contract_and_returns_json()
    {
        await using var calls = new WebAppNativeCalls(new NoScriptRuntime());

        await calls.HandleAsync("job:sync", AppDeviceBridgeJsonContext.Default.JobRun, ClientTestJson.Default.Theme, job =>
            Task.FromResult(new Theme(job.Name == "sync", 12)));

        Assert.Equal("""{"dark":true,"fontSize":12}""", await calls.OnCall("job:sync", """{"name":"sync"}"""));
    }

    [Fact]
    public async Task A_handler_with_nothing_to_report_answers_null()
    {
        await using var calls = new WebAppNativeCalls(new NoScriptRuntime());
        AppLink? received = null;

        await calls.HandleAsync("app.link", AppDeviceBridgeJsonContext.Default.AppLink, link =>
        {
            received = link;
            return Task.CompletedTask;
        });

        Assert.Null(await calls.OnCall("app.link", """{"url":"sample://a","route":"/a","receivedAt":"2026-09-16T00:00:00Z"}"""));
        Assert.Equal("/a", received?.Route);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.OnCall("gps", "{}"));
    }

    /// <summary>Accepts every script call and does nothing: these tests drive <c>OnCall</c> as the script would.</summary>
    sealed class NoScriptRuntime : Microsoft.JSInterop.IJSRuntime, Microsoft.JSInterop.IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult(typeof(TValue) == typeof(Microsoft.JSInterop.IJSObjectReference) ? (TValue)(object)this : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => this.InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
