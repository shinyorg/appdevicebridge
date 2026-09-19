using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Wearables;
using Shiny.AppDeviceBridge.Wearables.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Wearables;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The wearables bridge over a fake <see cref="Native.IWearableManager"/>: the routes, the JSON-to-bytes wire, the
/// error mapping, the file roots a file must come from and is filed into, and the delegate that turns what the watch
/// sends into handler calls whose return value is the reply. WatchConnectivity and the Data Layer themselves are
/// Shiny.Wearables' and need a paired device.
/// </summary>
public class WearablesBridgeTests
{
    [Fact]
    public async Task Answers_501_without_a_wearable_manager()
    {
        await using var fixture = await WearablesFixture.StartAsync(manager: null);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync());
        Assert.True(refused.IsNotSupported);
        await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendMessageAsync(new WearableMessageRequest("sync")));
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "wearables").IsSupported);
    }

    [Fact]
    public async Task Reports_the_status()
    {
        var manager = new FakeWearableManager
        {
            Status = new Native.WearableStatus(true, true, true, false, [new Native.WearableNode("n1", "Pixel Watch", true, true)])
        };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var status = await fixture.Client.GetStatusAsync();

        Assert.True(status.Supported);
        Assert.True(status.Paired);
        Assert.True(status.AppInstalled);
        Assert.False(status.Reachable);
        Assert.Equal(new WearableNode("n1", "Pixel Watch", true, true), Assert.Single(status.Nodes));
    }

    [Fact]
    public async Task Sends_a_message_as_json_bytes_and_returns_the_reply_as_json()
    {
        var manager = new FakeWearableManager { Reply = Encoding.UTF8.GetBytes("""{"steps":1200}""") };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var reply = await fixture.Client.SendMessageAsync(new WearableMessageRequest("/workout/start/", Json("""{"kind":"run"}"""), "n1"));

        var sent = Assert.Single(manager.Messages);
        Assert.Equal("/workout/start/", sent.Path); // the manager normalizes; the bridge passes it through
        Assert.Equal("""{"kind":"run"}""", Encoding.UTF8.GetString(sent.Data));
        Assert.Equal("n1", sent.NodeId);

        Assert.False(reply.Binary);
        Assert.Equal(1200, reply.Data.GetProperty("steps").GetInt32());
    }

    [Fact]
    public async Task A_message_without_data_sends_no_bytes_and_an_empty_reply_is_null()
    {
        var manager = new FakeWearableManager { Reply = [] };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var reply = await fixture.Client.SendMessageAsync(new WearableMessageRequest("ping"));

        Assert.Empty(Assert.Single(manager.Messages).Data);
        Assert.Equal(JsonValueKind.Null, reply.Data.ValueKind);
        Assert.False(reply.Binary);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_arrives_as_base64()
    {
        var manager = new FakeWearableManager { Reply = [0xFF, 0x00, 0x7F] };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var reply = await fixture.Client.SendMessageAsync(new WearableMessageRequest("raw"));

        Assert.True(reply.Binary);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x7F }, Convert.FromBase64String(reply.Data.GetString()!));
    }

    [Theory]
    [InlineData(Native.WearableErrorCode.NotReachable, HttpStatusCode.Conflict, "not_reachable")]
    [InlineData(Native.WearableErrorCode.NotSupported, HttpStatusCode.NotImplemented, "not_supported")]
    [InlineData(Native.WearableErrorCode.Failed, HttpStatusCode.BadGateway, "wearable_failed")]
    public async Task Maps_the_managers_failures(Native.WearableErrorCode code, HttpStatusCode status, string errorCode)
    {
        var manager = new FakeWearableManager { Throw = new Native.WearableException(code, "nope") };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendMessageAsync(new WearableMessageRequest("sync")));

        Assert.Equal(status, refused.StatusCode);
        Assert.Equal(errorCode, refused.Code);
    }

    [Fact]
    public async Task A_bad_path_is_a_400()
    {
        var manager = new FakeWearableManager { Throw = new ArgumentException("bad path") };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.TransferAsync(new WearableTransferRequest("a b", Json("1"))));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Theory]
    [InlineData("messages", "{}")]
    [InlineData("messages", "not json")]
    [InlineData("transfers", "{ \"data\": 1 }")]
    [InlineData("files", "{ \"path\": \"maps\" }")]
    public async Task Refuses_bodies_it_cannot_read(string route, string body)
    {
        var manager = new FakeWearableManager();
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync($"/_bridge/wearables/{route}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.Messages);
        Assert.Empty(manager.Transfers);
    }

    [Fact]
    public async Task Shares_and_reads_context()
    {
        var manager = new FakeWearableManager { ReceivedContext = new Native.WearableContext(Encoding.UTF8.GetBytes("""{"hr":61}"""), "n1") };
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var before = await fixture.Client.GetContextAsync();
        Assert.Null(before.Sent);
        Assert.Equal(61, before.Received!.Data.GetProperty("hr").GetInt32());
        Assert.Equal("n1", before.Received.NodeId);

        await fixture.Client.UpdateContextAsync(new WearableContextUpdate(Json("""{"plan":"5k"}""")));

        Assert.Equal("""{"plan":"5k"}""", Encoding.UTF8.GetString(manager.Context!));
        var after = await fixture.Client.GetContextAsync();
        Assert.Equal("5k", after.Sent!.Data.GetProperty("plan").GetString());
        Assert.Null(after.Sent.NodeId);
    }

    [Fact]
    public async Task Queues_lists_and_cancels_transfers()
    {
        var manager = new FakeWearableManager();
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var ticket = await fixture.Client.TransferAsync(new WearableTransferRequest("log", Json("""[1,2,3]""")));

        var queued = Assert.Single(manager.Transfers);
        Assert.Equal(ticket.Id, queued.Id);
        Assert.Equal("[1,2,3]", Encoding.UTF8.GetString(queued.Data));

        var pending = Assert.Single(await fixture.Client.GetPendingTransfersAsync());
        Assert.Equal(new WearablePendingTransfer(ticket.Id, "log", WearableTransferKind.Data, null), pending);

        await fixture.Client.CancelTransferAsync(ticket.Id);
        Assert.Empty(await fixture.Client.GetPendingTransfersAsync());

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CancelTransferAsync(ticket.Id));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Sends_a_file_from_a_file_root()
    {
        var manager = new FakeWearableManager();
        await using var fixture = await WearablesFixture.StartAsync(manager);
        Assert.True(fixture.Roots.TryResolve("data", "maps/city.bin", out var full));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, [1, 2, 3]);

        var ticket = await fixture.Client.SendFileAsync(new WearableFileRequest(
            "maps/offline",
            new BridgeFile("data", "maps/city.bin"),
            new Dictionary<string, string> { ["zoom"] = "14" }
        ));

        var sent = Assert.Single(manager.Files);
        Assert.Equal(ticket.Id, sent.Id);
        Assert.Equal("maps/offline", sent.Path);
        Assert.Equal(full, sent.FilePath);
        Assert.Equal("14", sent.Metadata!["zoom"]);
        Assert.Equal(WearableTransferKind.File, Assert.Single(await fixture.Client.GetPendingTransfersAsync()).Kind);
    }

    [Theory]
    [InlineData("data", "missing.bin")]
    [InlineData("data", "../../etc/passwd")]
    [InlineData("nope", "a.bin")]
    public async Task Refuses_a_file_it_cannot_resolve(string root, string path)
    {
        var manager = new FakeWearableManager();
        await using var fixture = await WearablesFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendFileAsync(new WearableFileRequest("x", new BridgeFile(root, path))));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Empty(manager.Files);
    }

    [Fact]
    public async Task A_watch_message_is_answered_by_the_web_apps_handler()
    {
        await using var app = new TestApp();
        var (host, invoker) = await BackgroundHostAsync(app, """
            appdevicebridge.on("wearables.message", ({ path, data, expectsReply }) => ({ path, echo: data, expectsReply }));
            """);
        await using var _ = host;

        var events = new WebAppEventHub();
        var d = CreateDelegate(events, invoker, new WebAppFileRoots(app.BridgeOptions()));

        var reply = await d.OnMessageReceived(new Native.WearableMessage("workout/start", Encoding.UTF8.GetBytes("""{"kind":"run"}"""), "n1", true));

        using var doc = JsonDocument.Parse(reply!);
        Assert.Equal("workout/start", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("run", doc.RootElement.GetProperty("echo").GetProperty("kind").GetString());
        Assert.True(doc.RootElement.GetProperty("expectsReply").GetBoolean());
    }

    [Fact]
    public async Task A_watch_message_nobody_handles_gets_no_reply_from_the_bridge()
    {
        await using var app = new TestApp();
        var d = CreateDelegate(new WebAppEventHub(), new WebAppInvoker(app.BridgeOptions()), new WebAppFileRoots(app.BridgeOptions()));

        // Null lets another IWearableDelegate answer; Shiny sends an empty reply when none does.
        Assert.Null(await d.OnMessageReceived(new Native.WearableMessage("sync", [], "n1", true)));
    }

    [Fact]
    public async Task A_file_from_the_watch_is_filed_into_the_root_and_the_original_removed()
    {
        await using var app = new TestApp();
        var roots = new WebAppFileRoots(app.BridgeOptions());
        var d = CreateDelegate(new WebAppEventHub(), new WebAppInvoker(app.BridgeOptions()), roots);

        var inbox = Path.Combine(app.InstallDirectory, "Shiny.Wearables", "t1");
        Directory.CreateDirectory(inbox);
        var original = Path.Combine(inbox, "run.gpx");
        await File.WriteAllTextAsync(original, "<gpx/>");

        var filed = await d.FileAsync(new Native.WearableFile("t1", "workouts", "run.gpx", original, new Dictionary<string, string> { ["km"] = "5" }, "n1"));

        Assert.NotNull(filed);
        Assert.Equal(new BridgeFile("data", "wearables/t1/run.gpx"), filed.File);
        Assert.Equal(6, filed.Size);
        Assert.Equal("5", filed.Metadata["km"]);
        Assert.True(roots.TryResolve("data", filed.File.Path, out var full));
        Assert.Equal("<gpx/>", await File.ReadAllTextAsync(full));
        Assert.False(File.Exists(original));
        Assert.False(Directory.Exists(inbox));
    }

    [Theory]
    [InlineData("../../escape.txt", "t2", "wearables/t2/escape.txt")]
    [InlineData("..", "..", null)] // both replaced; checked by shape below
    [InlineData("a/b\\c.txt", "../t3", "wearables/t3/c.txt")]
    public async Task A_senders_names_cannot_leave_the_folder(string fileName, string id, string? expected)
    {
        await using var app = new TestApp();
        var roots = new WebAppFileRoots(app.BridgeOptions());
        var d = CreateDelegate(new WebAppEventHub(), new WebAppInvoker(app.BridgeOptions()), roots);

        var original = Path.Combine(app.InstallDirectory, "incoming.tmp");
        Directory.CreateDirectory(app.InstallDirectory);
        await File.WriteAllTextAsync(original, "x");

        var filed = await d.FileAsync(new Native.WearableFile(id, "p", fileName, original, new Dictionary<string, string>(), null));

        Assert.NotNull(filed);
        if (expected is not null)
            Assert.Equal(expected, filed.File.Path);

        var segments = filed.File.Path.Split('/');
        Assert.Equal(3, segments.Length);
        Assert.Equal("wearables", segments[0]);
        Assert.DoesNotContain("..", segments);
    }

    [Fact]
    public async Task A_file_with_no_root_to_go_to_is_left_in_place()
    {
        await using var app = new TestApp();
        var roots = new WebAppFileRoots(app.BridgeOptions());
        var d = new WebAppWearableDelegate(new WebAppEventHub(), new WebAppInvoker(app.BridgeOptions()), roots, new WearablesBridgeOptions { Root = "elsewhere" });

        var original = Path.Combine(app.InstallDirectory, "keep.bin");
        Directory.CreateDirectory(app.InstallDirectory);
        await File.WriteAllTextAsync(original, "x");

        Assert.Null(await d.FileAsync(new Native.WearableFile("t", "p", "keep.bin", original, new Dictionary<string, string>(), null)));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void Registers_once_with_shinys_service_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
        {
            bridge.AddWearablesBridge(o => o.Folder = "watch");
            bridge.AddWearablesBridge();
        }), autoStart: false);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<WebAppWearableDelegate>(Assert.Single(provider.GetServices<Native.IWearableDelegate>()));
        Assert.Single(provider.GetServices<IWebAppBridge>().OfType<WearablesBridge>());
        Assert.Equal("watch", Assert.Single(services, x => x.ServiceType == typeof(WearablesBridgeOptions)).ImplementationInstance is WearablesBridgeOptions o ? o.Folder : null);

        var bad = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => bad.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
            bridge.AddWearablesBridge(o => o.Folder = "../out")), autoStart: false));
    }

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static WebAppWearableDelegate CreateDelegate(WebAppEventHub events, WebAppInvoker invoker, WebAppFileRoots roots)
        => new(events, invoker, roots, new WearablesBridgeOptions());

    /// <summary>The web app's background.js with nothing else: the host a background wake-up from the watch gets.</summary>
    static async Task<(WebAppHost Host, WebAppInvoker Invoker)> BackgroundHostAsync(TestApp app, string script)
    {
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0", backgroundScript: script));
        await app.StartReleaseServerAsync();

        await using (var first = app.CreateHost())
            await first.StartAsync();

        var options = app.Options();
        var bridgeOptions = app.BridgeOptions();
        WebAppHost host = null!;
        var engine = new WebAppScriptEngine(options, () => host, NullLogger.Instance);
        var invoker = new WebAppInvoker(bridgeOptions, () => engine);
        host = app.CreateHost(options, bridgeOptions, new WebAppEventHub(), [invoker]);
        return (host, invoker);
    }

    sealed class WearablesFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public WearablesBridgeClient Client { get; private set; } = null!;
        public WebAppFileRoots Roots { get; private set; } = null!;

        public static async Task<WearablesFixture> StartAsync(Native.IWearableManager? manager)
        {
            var fixture = new WearablesFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    fixture.Roots = new WebAppFileRoots(app.BridgeOptions());
                    return [new WearablesBridge(provider, fixture.Roots)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new WearablesBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class FakeWearableManager : Native.IWearableManager
    {
        public Native.WearableStatus Status { get; init; } = new(true, true, true, true, []);
        public byte[] Reply { get; init; } = [];
        public Exception? Throw { get; init; }
        public byte[]? Context { get; private set; }
        public Native.WearableContext? ReceivedContext { get; init; }

        public List<(string Path, byte[] Data, string? NodeId)> Messages { get; } = [];
        public List<(string Id, string Path, byte[] Data)> Transfers { get; } = [];
        public List<(string Id, string Path, string FilePath, IReadOnlyDictionary<string, string>? Metadata)> Files { get; } = [];

        void Check()
        {
            if (this.Throw is not null)
                throw this.Throw;
        }

        public Task<Native.WearableStatus> GetStatus(CancellationToken cancelToken = default)
        {
            this.Check();
            return Task.FromResult(this.Status);
        }

        public Task<byte[]> SendMessage(string path, byte[] data, string? nodeId = null, CancellationToken cancelToken = default)
        {
            this.Check();
            this.Messages.Add((path, data, nodeId));
            return Task.FromResult(this.Reply);
        }

        public Task UpdateContext(byte[] data, CancellationToken cancelToken = default)
        {
            this.Check();
            this.Context = data;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetContext(CancellationToken cancelToken = default) => Task.FromResult(this.Context);

        public Task<Native.WearableContext?> GetReceivedContext(CancellationToken cancelToken = default) => Task.FromResult(this.ReceivedContext);

        public Task<string> Transfer(string path, byte[] data, CancellationToken cancelToken = default)
        {
            this.Check();
            var id = Guid.NewGuid().ToString("N");
            this.Transfers.Add((id, path, data));
            return Task.FromResult(id);
        }

        public Task<string> TransferFile(string path, string filePath, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancelToken = default)
        {
            this.Check();
            var id = Guid.NewGuid().ToString("N");
            this.Files.Add((id, path, filePath, metadata));
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<Native.WearableTransferInfo>> GetPendingTransfers(CancellationToken cancelToken = default)
        {
            IReadOnlyList<Native.WearableTransferInfo> list =
            [
                .. this.Transfers.Select(x => new Native.WearableTransferInfo(x.Id, x.Path, Native.WearableTransferKind.Data, null)),
                .. this.Files.Select(x => new Native.WearableTransferInfo(x.Id, x.Path, Native.WearableTransferKind.File, null))
            ];
            return Task.FromResult(list);
        }

        public Task<bool> CancelTransfer(string id, CancellationToken cancelToken = default)
            => Task.FromResult(this.Transfers.RemoveAll(x => x.Id == id) + this.Files.RemoveAll(x => x.Id == id) > 0);
    }
}
