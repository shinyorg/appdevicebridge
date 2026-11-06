using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Hubs;
using Shiny.AppDeviceBridge.Hubs.Client;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.UniversalHubs;
using HubDisconnect = Shiny.BluetoothLE.Hubs.HubDisconnect;
using NativeReason = Shiny.BluetoothLE.Hubs.HubDisconnectReason;

namespace Shiny.AppDeviceBridge.Tests;

[BleHubClient]
public interface IWebGameHub
{
    Task<MoveResult> MakeMove(int cell);
    Task Fail();
    Task Kick(string reason);
    Task Missing();
    IAsyncEnumerable<int> Count(int to, CancellationToken cancellationToken);

    event Action<string> Chat;
}

public sealed record MoveResult(bool Accepted, int Cell);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MoveResult))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
public partial class WebGameJson : JsonSerializerContext;

/// <summary>
/// Universal Hubs answered by the web app: the real bridge server and WebView host, a page that answers native calls the
/// way client.js does, and a real Universal Hubs client reaching the hub over Wi-Fi on the bridge's own server.
/// </summary>
public class HubsBridgeTests
{
    const string Characteristic = "6e400010-b5a3-f393-e0a9-e50e24dcca9e";
    const string ServiceType = "_hubtest._tcp";

    static readonly HubDefinition Game = new(
        "game",
        Characteristic,
        ServiceType,
        [new HubMethod("MakeMove"), new HubMethod("Fail"), new HubMethod("Kick"), new HubMethod("Count", HubMethodKind.Stream)],
        MaxClients: 3
    );

    static HubsBridgeTests() => Json.AddContext(WebGameJson.Default);

    [Fact]
    public async Task Calls_reach_the_web_app_and_its_replies_reach_the_client()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);

        await using var client = await fixture.ConnectAsync("Alice");
        Assert.Equal(new MoveResult(true, 4), await client.Hub.MakeMove(4));

        var connected = fixture.Page.Calls.First(x => x.Kind == HubCallKind.Connected);
        Assert.Equal("Alice", connected.Connection.Name);
        Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http, connected.Connection.Transport);

        var move = fixture.Page.Calls.Single(x => x.Kind == HubCallKind.Invoke);
        Assert.Equal("MakeMove", move.Method);
        Assert.Equal(4, move.Arguments!.Single().GetInt32());

        var info = (await fixture.Bridge.GetHubsAsync()).Single();
        Assert.Equal(1, info.Connections);
        Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http, info.RunningTransports);
    }

    [Fact]
    public async Task The_web_app_chooses_the_errors_clients_see()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);
        await using var client = await fixture.ConnectAsync("Alice");

        var rule = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Hub.Fail());
        Assert.Equal("GameRule", rule.RemoteErrorType);
        Assert.Equal("Not your turn", rule.Message);

        // not in the definition: answered without reaching the web app
        var missing = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Hub.Missing());
        Assert.Equal("HubMethodNotFound", missing.RemoteErrorType);
        Assert.DoesNotContain(fixture.Page.Calls, x => x.Method == "Missing");
    }

    [Fact]
    public async Task The_web_app_can_refuse_a_client_and_end_a_session()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);

        var refused = await Assert.ThrowsAsync<BleHubException>(() => fixture.ConnectAsync("Mallory"));
        Assert.Contains("No Mallory", refused.Message);

        await using var client = await fixture.ConnectAsync("Alice");
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await client.Hub.Kick("kicked");

        await WaitFor(() => disconnect is not null);
        Assert.Equal(new HubDisconnect(NativeReason.ServerDisconnect, "kicked"), disconnect);
        await WaitFor(() => fixture.Page.Calls.Any(x => x.Kind == HubCallKind.Disconnected && x.Reason == Shiny.AppDeviceBridge.Hubs.Client.HubDisconnectReason.ServerDisconnect));
    }

    [Fact]
    public async Task Pushes_and_groups_reach_clients()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);
        await using var alice = await fixture.ConnectAsync("Alice");
        await using var bob = await fixture.ConnectAsync("Bob");
        var aliceChat = new ConcurrentQueue<string>();
        var bobChat = new ConcurrentQueue<string>();
        alice.Hub.Chat += aliceChat.Enqueue;
        bob.Hub.Chat += bobChat.Enqueue;

        await fixture.Bridge.PushAsync("game", new HubPushRequest("Chat", [JsonSerializer.SerializeToElement("hello all", WebGameJson.Default.String)]));

        var aliceId = (await fixture.Bridge.GetConnectionsAsync("game")).Single(x => x.Name == "Alice").Id;
        await fixture.Bridge.AddToGroupAsync("game", "red", aliceId);
        Assert.Equal([aliceId], await fixture.Bridge.GetGroupAsync("game", "red"));
        await fixture.Bridge.PushAsync("game", new HubPushRequest("Chat", [JsonSerializer.SerializeToElement("red team", WebGameJson.Default.String)], new HubPushTarget(Groups: ["red"])));
        await fixture.Bridge.PushAsync("game", new HubPushRequest("Chat", [JsonSerializer.SerializeToElement("not alice", WebGameJson.Default.String)], new HubPushTarget(Except: [aliceId])));

        await WaitFor(() => aliceChat.Count == 2 && bobChat.Count == 2);
        Assert.Equal(["hello all", "red team"], aliceChat);
        Assert.Equal(["hello all", "not alice"], bobChat);

        await fixture.Bridge.DisconnectAsync("game", aliceId, "bye");
        await WaitFor(() => alice.Status == BleHubClientStatus.Disconnected);
    }

    [Fact]
    public async Task Streams_carry_the_web_apps_items()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);
        await using var client = await fixture.ConnectAsync("Alice");

        var items = new List<int>();
        await foreach (var i in client.Hub.Count(3, CancellationToken.None))
            items.Add(i);

        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public async Task Without_a_web_app_to_answer_clients_are_refused_and_calls_fail()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);
        await using var client = await fixture.ConnectAsync("Alice");

        // the page goes away: no page to run the hub
        fixture.Page.Unhandle("hub:game");

        var call = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Hub.MakeMove(1));
        Assert.Equal("HubUnavailable", call.RemoteErrorType);

        var refused = await Assert.ThrowsAsync<BleHubException>(() => fixture.ConnectAsync("Bob"));
        Assert.Contains("not running game", refused.Message);
    }

    [Fact]
    public async Task Defining_again_keeps_the_hub_and_a_changed_hub_replaces_it()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);
        await using var client = await fixture.ConnectAsync("Alice");

        // the page loads again: same hub, same clients, a method list it can change
        var again = await fixture.Bridge.DefineAsync(Game with { Methods = [.. Game.Methods, new HubMethod("Extra")] });
        Assert.Equal(1, again.Connections);
        Assert.Contains(again.Methods, x => x.Name == "Extra");
        Assert.Equal(new MoveResult(true, 2), await client.Hub.MakeMove(2));

        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;
        var replaced = await fixture.Bridge.DefineAsync(Game with { MaxClients = 6 });
        Assert.Equal(0, replaced.Connections);
        Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.None, replaced.RunningTransports);
        await WaitFor(() => disconnect is not null);
        Assert.Equal(NativeReason.ServerShutdown, disconnect!.Reason);

        await fixture.Bridge.RemoveAsync("game", "done");
        Assert.Empty(await fixture.Bridge.GetHubsAsync());
    }

    [Fact]
    public async Task Definitions_and_requests_are_checked()
    {
        await using var fixture = await Fixture.StartAsync();

        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.DefineAsync(Game with { Name = "has space" }));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.DefineAsync(Game with { Name = "streams" }));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.DefineAsync(Game with { CharacteristicUuid = "nope" }));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.DefineAsync(Game with { ServiceType = "_other._tcp" }));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.DefineAsync(Game with { Methods = [new HubMethod("A"), new HubMethod("A")] }));
        await AssertStatus(HttpStatusCode.NotFound, () => fixture.Bridge.StartAsync("nobody", new HubStartRequest()));

        await fixture.Bridge.DefineAsync(Game);
        await AssertStatus(HttpStatusCode.Conflict, () => fixture.Bridge.DefineAsync(Game with { Name = "chat" }));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.StartAsync("game", new HubStartRequest(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.None)));
        await AssertStatus(HttpStatusCode.Gone, () => fixture.Bridge.SendStreamItemAsync("unknown", JsonSerializer.SerializeToElement(1, WebGameJson.Default.Int32)));
    }

    [Fact]
    public async Task A_page_without_the_csharp_client_gets_client_js()
    {
        await using var fixture = await Fixture.StartAsync();
        var script = await fixture.Page.Client.GetStringAsync("/_bridge/hubs/client.js");

        Assert.Contains("export async function hostHub", script);
        Assert.Contains("from \"/_bridge/invoke/client.js\"", script);
    }

    [Fact]
    public async Task A_WebAppHub_serves_clients_from_csharp()
    {
        await using var fixture = await Fixture.StartAsync();
        var services = new ServiceCollection()
            .AddSingleton<IBridgeTransport>(fixture.Page)
            .AddSingleton<IWebAppNativeCalls>(fixture.Page)
            .AddWebAppHub<CSharpGameHub>(new WebAppHubOptions("game", Characteristic, ServiceType) { MaxClients = 3 })
            .BuildServiceProvider();
        await using var _ = services;

        var hubs = services.GetRequiredService<WebAppHubs>();
        var defined = Assert.Single(await hubs.DefineAsync());
        Assert.Contains(defined.Methods, x => x is { Name: "Count", Kind: HubMethodKind.Stream });
        await hubs.StartAsync("game", Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http);

        var refused = await Assert.ThrowsAsync<BleHubException>(() => fixture.ConnectAsync("Mallory"));
        Assert.Contains("No Mallory", refused.Message);

        await using var client = await fixture.ConnectAsync("Alice");
        var chat = new ConcurrentQueue<string>();
        client.Hub.Chat += chat.Enqueue;

        Assert.Equal(new MoveResult(true, 5), await client.Hub.MakeMove(5));
        await WaitFor(() => chat.Contains("Alice took 5"));

        var rule = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Hub.Fail());
        Assert.Equal("GameRule", rule.RemoteErrorType);

        var items = new List<int>();
        await foreach (var i in client.Hub.Count(4, CancellationToken.None))
            items.Add(i);
        Assert.Equal([1, 2, 3, 4], items);

        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;
        await client.Hub.Kick("enough");
        await WaitFor(() => disconnect is not null);
        Assert.Equal(new HubDisconnect(NativeReason.ServerDisconnect, "enough"), disconnect);
        await WaitFor(() => CSharpGameHub.Left.Contains("Alice:ServerDisconnect"));
    }

    [Fact]
    public async Task Without_Bluetooth_hosting_registered_hubs_run_over_wifi()
    {
        // Windows, macOS or Linux where the app registered no BLE hosting: All skips BLE rather than failing
        await using var fixture = await Fixture.StartAsync(noBluetooth: false);
        fixture.Page.Handle("hub:game", ScriptedGame);

        await fixture.Bridge.DefineAsync(Game);
        var started = await fixture.Bridge.StartAsync("game", new HubStartRequest());
        Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http, started.RunningTransports);

        await using var client = await fixture.ConnectAsync("Alice");
        Assert.Equal(new MoveResult(true, 7), await client.Hub.MakeMove(7));
    }


    [Fact]
    public async Task The_web_app_joins_a_hub_by_name_and_calls_it()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Page.Handle("hub:game", ScriptedGame);
        await fixture.DefineAndStartAsync(Game);

        var pushes = new ConcurrentQueue<HubClientPush>();
        var changes = new ConcurrentQueue<HubClientChanged>();
        var items = new ConcurrentQueue<HubClientStreamItem>();
        await using var _ = await fixture.Bridge.OnPushAsync(p => { pushes.Enqueue(p); return Task.CompletedTask; });
        await using var __ = await fixture.Bridge.OnClientChangedAsync(c => { changes.Enqueue(c); return Task.CompletedTask; });
        await using var ___ = await fixture.Bridge.OnStreamItemAsync(i => { items.Enqueue(i); return Task.CompletedTask; });

        var client = await fixture.Bridge.CreateClientAsync(new HubClientRequest(Characteristic, ServiceType));
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        await AssertStatus(HttpStatusCode.Conflict, () => fixture.Bridge.InvokeAsync(client.Id, new HubInvokeRequest("MakeMove", [Int(1)])));

        // this device's own hub, by address: a host on the network the page was told about, or a tunnel's URL
        var connected = await fixture.Bridge.ConnectAsync(client.Id, new HubConnectRequest(Url: fixture.Origin, Name: "Joiner", Allowed: Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http));
        Assert.Equal(HubClientStatus.Connected, connected.Status);
        Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http, connected.Transport);
        await WaitFor(() => changes.Any(x => x.Status == HubClientStatus.Connected));
        Assert.Contains(fixture.Page.Calls, x => x is { Kind: HubCallKind.Connected, Connection.Name: "Joiner" });

        var moved = await fixture.Bridge.InvokeAsync(client.Id, new HubInvokeRequest("MakeMove", [Int(3)]));
        Assert.Equal(new MoveResult(true, 3), moved.Result!.Value.Deserialize(WebGameJson.Default.MoveResult));

        var failed = await fixture.Bridge.InvokeAsync(client.Id, new HubInvokeRequest("Fail", []));
        Assert.Equal(new HubError("GameRule", "Not your turn"), failed.Error);

        var stream = await fixture.Bridge.StreamAsync(client.Id, new HubInvokeRequest("Count", [Int(2)]));
        await WaitFor(() => items.Any(x => x.StreamId == stream.StreamId && x.End));
        Assert.Equal([1, 2], items.Where(x => !x.End).Select(x => x.Item!.Value.GetInt32()));

        await fixture.Bridge.PushAsync("game", new HubPushRequest("Chat", [JsonSerializer.SerializeToElement("to the joiner", WebGameJson.Default.String)]));
        await WaitFor(() => pushes.Any(x => x is { Event: "Chat" } && x.ClientId == client.Id));
        Assert.Equal("to the joiner", pushes.First(x => x.Event == "Chat").Arguments.Single().GetString());

        await fixture.Bridge.DisconnectClientAsync(client.Id);
        await WaitFor(() => changes.Any(x => x.Status == HubClientStatus.Disconnected));
        await fixture.Bridge.RemoveClientAsync(client.Id);
        Assert.Empty(await fixture.Bridge.GetClientsAsync());
    }


    [Fact]
    public async Task Joining_checks_its_requests()
    {
        await using var fixture = await Fixture.StartAsync();
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.CreateClientAsync(new HubClientRequest("nope", ServiceType)));

        var client = await fixture.Bridge.CreateClientAsync(new HubClientRequest(Characteristic, ServiceType));
        await AssertStatus(HttpStatusCode.Conflict, () => fixture.Bridge.DiscoverAsync(client.Id, new HubDiscoverRequest()));   // nobody listening
        await AssertStatus(HttpStatusCode.NotFound, () => fixture.Bridge.ConnectAsync(client.Id, new HubConnectRequest(Host: "Nowhere")));
        await AssertStatus(HttpStatusCode.BadRequest, () => fixture.Bridge.ConnectAsync(client.Id, new HubConnectRequest()));

        // nothing hosts the hub: the host refuses, and the client says why
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Bridge.ConnectAsync(client.Id, new HubConnectRequest(Url: fixture.Origin, Allowed: Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http)));
        Assert.Equal("refused", refused.Code);
        await AssertStatus(HttpStatusCode.NotFound, () => fixture.Bridge.RemoveClientAsync("unknown"));
    }


    static JsonElement Int(int value) => JsonSerializer.SerializeToElement(value, WebGameJson.Default.Int32);


    [Fact]
    public void Hubs_leave_the_bridge_servers_address_and_port_alone()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NoBluetooth.Create());
        services.AddShinyHttpServer(http =>
        {
            http.Options.Port = 5781;
            http.AddAppDeviceBridge(bridge => bridge.AddHubsBridge(server => server.AllowDynamicHubs(o => o.ServiceTypes.Add(ServiceType))));
        }, autoStart: false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<HttpServerOptions>();

        Assert.Equal(5781, options.Port);
        Assert.Equal(IPAddress.Loopback, options.Address);
        Assert.NotNull(provider.GetService<IDynamicHubs>());
        Assert.False(provider.GetRequiredService<UniversalHubHostOptions>().ManageHttpServer);
    }

    /// <summary>The game a page might write with client.js — the native call payloads, answered by hand.</summary>
    static Task<string?> ScriptedGame(FakeHubPage page, string json)
    {
        var call = JsonSerializer.Deserialize(json, HubsJsonContext.Default.HubCall)!;
        page.Record(call);

        HubCallReply reply = call switch
        {
            { Kind: HubCallKind.Connected, Connection.Name: "Mallory" } => new(Error: new HubError("Refused", "No Mallory")),
            { Kind: HubCallKind.Invoke, Method: "MakeMove" } => new(JsonSerializer.SerializeToElement(
                new MoveResult(true, call.Arguments![0].GetInt32()),
                WebGameJson.Default.MoveResult
            )),
            { Kind: HubCallKind.Invoke, Method: "Fail" } => new(Error: new HubError("GameRule", "Not your turn")),
            { Kind: HubCallKind.Invoke, Method: "Kick" } => new(Abort: true, AbortReason: call.Arguments![0].GetString()),
            { Kind: HubCallKind.Stream, Method: "Count" } => page.RunStream(call.StreamId!, call.Arguments![0].GetInt32()),
            _ => new()
        };

        return Task.FromResult<string?>(JsonSerializer.Serialize(reply, HubsJsonContext.Default.HubCallReply));
    }

    static async Task AssertStatus(HttpStatusCode status, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<BridgeException>(call);
        Assert.Equal(status, ex.StatusCode);
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition(), "condition not met in time");
    }

    public sealed class CSharpGameHub : WebAppHub
    {
        public static readonly ConcurrentBag<string> Left = new();

        protected override void Map(WebAppHubMethods methods) => methods
            .Invoke("MakeMove", WebGameJson.Default.Int32, WebGameJson.Default.MoveResult, this.MakeMove)
            .Invoke("Fail", () => throw new HubException("GameRule", "Not your turn"))
            .Invoke("Kick", WebGameJson.Default.String, reason =>
            {
                this.Abort(reason);
                return Task.CompletedTask;
            })
            .Stream("Count", WebGameJson.Default.Int32, WebGameJson.Default.Int32, Count);

        public override Task OnConnectedAsync(HubConnectionInfo connection)
            => connection.Name == "Mallory" ? throw new HubException("Refused", "No Mallory") : Task.CompletedTask;

        public override Task OnDisconnectedAsync(HubConnectionInfo connection, Shiny.AppDeviceBridge.Hubs.Client.HubDisconnectReason reason, string? message)
        {
            Left.Add($"{connection.Name}:{reason}");
            return Task.CompletedTask;
        }

        async Task<MoveResult> MakeMove(int cell)
        {
            await this.PushAsync("Chat", WebGameJson.Default.String, $"{this.Connection.Name} took {cell}");
            return new MoveResult(true, cell);
        }

        static async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 1; i <= to; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly TestApp app;
        readonly WebAppHost host;
        readonly List<ServiceProvider> clients = new();

        Fixture(TestApp app, WebAppHost host, FakeHubPage page)
        {
            this.app = app;
            this.host = host;
            this.Page = page;
            this.Bridge = new HubsBridgeClient(page);
        }

        public FakeHubPage Page { get; }

        public string Origin => this.host.Origin!.ToString();

        /// <summary>The typed client a Blazor page uses.</summary>
        public IHubsBridge Bridge { get; }

        /// <param name="noBluetooth">Register a hosting manager that throws, as a radio that fails would; otherwise none at all.</param>
        public static async Task<Fixture> StartAsync(bool noBluetooth = true)
        {
            var app = new TestApp();
            app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await app.StartReleaseServerAsync();

            var host = app.CreateHost(
                app.Options(),
                null,
                null,
                [
                    new Deferred(() => app.Services!.GetRequiredService<WebAppInvoker>()),
                    new Deferred(() => new HubsBridge(app.Services!))
                ],
                http =>
                {
                    if (noBluetooth)
                        http.Services.AddSingleton(NoBluetooth.Create());

                    new AppDeviceBridgeBuilder(http).AddHubsBridge(server => server
                        .Ble(o => o.LocalName = "Host")
                        .Http(o => o.Advertise = false)
                        .AllowDynamicHubs(o => o.ServiceTypes.Add(ServiceType)));
                }
            );

            var page = await FakeHubPage.OpenAsync(host);
            return new Fixture(app, host, page);
        }

        public async Task DefineAndStartAsync(HubDefinition definition)
        {
            await this.Bridge.DefineAsync(definition);
            var started = await this.Bridge.StartAsync(definition.Name, new HubStartRequest(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http));
            Assert.Equal(Shiny.AppDeviceBridge.Hubs.Client.HubTransports.Http, started.RunningTransports);
        }

        /// <summary>A Universal Hubs client on another device, reaching the hub over Wi-Fi on the bridge server.</summary>
        public async Task<HubClient> ConnectAsync(string name)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddUniversalHubClient<IWebGameHub>(Characteristic, ServiceType, o => o.HttpConnectTimeout = TimeSpan.FromSeconds(5))
                .BuildServiceProvider();

            var client = services.GetRequiredService<IUniversalHubClient<IWebGameHub>>();
            try
            {
                await client.Connect(HubHostInfo.FromUrl(this.host.Origin!), new HubConnectOptions(name, "1.0") { Allowed = Shiny.UniversalHubs.HubTransports.Http });
            }
            catch
            {
                await services.DisposeAsync();
                throw;
            }

            return new HubClient(client, services);
        }

        public async ValueTask DisposeAsync()
        {
            await this.Page.DisposeAsync();
            await this.host.DisposeAsync();
            await this.app.DisposeAsync();
        }
    }

    public sealed class HubClient(IUniversalHubClient<IWebGameHub> client, ServiceProvider services) : IAsyncDisposable
    {
        public IWebGameHub Hub => client.Hub;

        public BleHubClientStatus Status => client.Status;

        public event EventHandler<HubDisconnect>? Disconnected
        {
            add => client.Disconnected += value;
            remove => client.Disconnected -= value;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.Status == BleHubClientStatus.Connected)
                    await client.Disconnect();
            }
            catch
            {
            }

            await services.DisposeAsync();
        }
    }

    /// <summary>A bridge whose instance comes from the host's container once it exists.</summary>
    sealed class Deferred(Func<IWebAppBridge> create) : IWebAppBridge
    {
        IWebAppBridge? bridge;

        IWebAppBridge Bridge => this.bridge ??= create();

        public string Name => this.Bridge.Name;

        public bool IsSupported => this.Bridge.IsSupported;

        public void Map(WebAppBridgeRoutes routes) => this.Bridge.Map(routes);
    }
}

/// <summary>
/// Plays the page: a launch session, one event stream, native calls answered the way client.js and
/// <c>WebAppNativeCalls</c> answer them, and the transport the typed clients send through.
/// </summary>
public sealed class FakeHubPage : IWebAppNativeCalls, IBridgeTransport, IAsyncDisposable
{
    readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
    readonly ConcurrentDictionary<string, Func<string, Task<string?>>> handlers = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, List<Func<string, Task>>> subscriptions = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<HubCall> calls = new();
    readonly SemaphoreSlim topics = new(1, 1);
    TestEventStream stream = null!;
    Task pump = Task.CompletedTask;

    public HttpClient Client { get; private set; } = null!;

    public IReadOnlyList<HubCall> Calls => this.calls.ToList();

    public static async Task<FakeHubPage> OpenAsync(WebAppHost host)
    {
        var page = new FakeHubPage();
        var start = await host.StartAsync();

        page.Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
        await page.Client.GetStringAsync(start);

        page.stream = await TestEventStream.OpenAsync(page.Client, WebAppInvoker.InvokeEventName, page.lifetime.Token);
        page.pump = Task.Run(page.PumpAsync);
        return page;
    }

    public void Record(HubCall call) => this.calls.Enqueue(call);

    /// <summary>A raw handler, as client.js's <c>on(name, fn)</c> registers one.</summary>
    public void Handle(string name, Func<FakeHubPage, string, Task<string?>> handler)
        => this.HandleAsync(name, json => handler(this, json)).GetAwaiter().GetResult();

    public void Unhandle(string name)
    {
        this.handlers.TryRemove(name, out _);
        this.DeclareAsync().GetAwaiter().GetResult();
    }

    public async Task<IAsyncDisposable> HandleAsync(string name, Func<string, Task<string?>> handler)
    {
        this.handlers[name] = handler;
        await this.DeclareAsync();
        return new Subscription(async () =>
        {
            this.handlers.TryRemove(name, out _);
            await this.DeclareAsync();
        });
    }

    /// <summary>Sends a streaming call's items to the bridge and ends it, as client.js does — after the reply.</summary>
    public HubCallReply RunStream(string streamId, int to)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            for (var i = 1; i <= to; i++)
                await this.Client.PostAsJsonAsync($"/_bridge/hubs/streams/{streamId}", i);

            await this.Client.PostAsJsonAsync($"/_bridge/hubs/streams/{streamId}/end", new HubStreamEnd(), HubsJsonContext.Default.HubStreamEnd);
        });
        return new HubCallReply();
    }

    // ---- IBridgeTransport: what BlazorBridgeTransport does, minus the WebAssembly

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = new Uri("/_bridge/" + request.RequestUri!.OriginalString, UriKind.Relative);
        return this.Client.SendAsync(request, cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string eventName, Func<string, Task> handler)
    {
        var list = this.subscriptions.GetOrAdd(eventName, _ => new List<Func<string, Task>>());
        lock (list)
            list.Add(handler);

        await this.SetTopicsAsync();
        return new Subscription(() =>
        {
            lock (list)
                list.Remove(handler);
            return ValueTask.CompletedTask;
        });
    }

    async Task DeclareAsync()
        => (await this.Client.PutAsJsonAsync("/_bridge/invoke/handlers", new { handlers = this.handlers.Keys.ToArray() })).EnsureSuccessStatusCode();

    async Task SetTopicsAsync()
    {
        await this.topics.WaitAsync();
        try
        {
            await this.stream.SetTopicsAsync(this.lifetime.Token, [WebAppInvoker.InvokeEventName, .. this.subscriptions.Keys]);
        }
        finally
        {
            this.topics.Release();
        }
    }

    async Task PumpAsync()
    {
        try
        {
            while (!this.lifetime.IsCancellationRequested)
            {
                var (name, data) = await this.stream.NextAsync(this.lifetime.Token);
                if (name == WebAppInvoker.InvokeEventName)
                {
                    _ = Task.Run(() => this.AnswerAsync(data));
                }
                else if (this.subscriptions.TryGetValue(name, out var list))
                {
                    Func<string, Task>[] targets;
                    lock (list)
                        targets = [.. list];

                    // in order, as a page's one event stream delivers them
                    foreach (var target in targets)
                        await target(data);
                }
            }
        }
        catch (Exception) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
    }

    async Task AnswerAsync(string data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        var id = root.GetProperty("id").GetString()!;

        if (!this.handlers.TryGetValue(root.GetProperty("handler").GetString()!, out var handler))
            return;

        if ((await this.Client.PostAsync($"/_bridge/invoke/{id}/accept", null)).StatusCode != HttpStatusCode.NoContent)
            return;

        object reply;
        try
        {
            var result = await handler(root.GetProperty("payload").GetRawText());
            reply = new { ok = true, result = result is null ? (JsonElement?)null : JsonDocument.Parse(result).RootElement.Clone() };
        }
        catch (Exception ex)
        {
            reply = new { ok = false, error = ex.Message };
        }

        await this.Client.PostAsJsonAsync($"/_bridge/invoke/{id}", reply);
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        await this.stream.DisposeAsync();
        try
        {
            await this.pump;
        }
        catch
        {
        }

        this.Client.Dispose();
        this.lifetime.Dispose();
    }

    sealed class Subscription(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}

/// <summary>No Bluetooth on a test machine: the hubs run over Wi-Fi only.</summary>
public class NoBluetooth : DispatchProxy
{
    public static IBleHostingManager Create() => DispatchProxy.Create<IBleHostingManager, NoBluetooth>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("Bluetooth hosting is not available in tests");
}
