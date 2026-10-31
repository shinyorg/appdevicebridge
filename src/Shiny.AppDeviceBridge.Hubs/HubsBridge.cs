using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.UniversalHubs;
using Contracts = Shiny.AppDeviceBridge.Hubs.Client;

namespace Shiny.AppDeviceBridge.Hubs;

public static class HubsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/hubs</c>: Universal Hubs the web app defines, starts and answers. The app serves them over BLE,
    /// Wi-Fi and Multipeer on the bridge's own server, and every call a client makes reaches the web app as the native call
    /// <c>hub:{name}</c> — the page while it is open, otherwise background.js — so a hub's behavior ships with the web app.
    /// <code>
    /// bridge.AddHubsBridge(server => server
    ///     .ServiceUuid("…")                                                    // optional - clients must use the same one
    ///     .AllowDynamicHubs(o => o.ServiceTypes.Add("_myapp._tcp")));         // the mDNS types web app hubs may use
    /// </code>
    /// <para>
    /// <paramref name="configure"/> is Universal Hubs' own <c>AddUniversalServer</c> builder: compiled hubs of the app's own
    /// can sit beside the web app's. Dynamic hubs are always allowed; what they may use is fixed here because the platforms
    /// fix it when the app is built — iOS lists every mDNS type in <c>NSBonjourServices</c>.
    /// </para>
    /// <para>
    /// Bluetooth LE hosting comes with Universal Hubs on Android, iOS and Mac Catalyst. On Windows, macOS and Linux, register
    /// it first (<c>services.AddBluetoothLeHosting()</c>) to host over BLE; without it the hubs run over Wi-Fi and Multipeer.
    /// </para>
    /// <para>
    /// The hubs share the bridge's server and never start or stop it. Wi-Fi clients reach it only when it listens beyond
    /// loopback (<c>http.Options.Address = IPAddress.Any</c>); the bridges stay on-device only either way, because the hub
    /// routes (<c>/hubs</c>) are outside the bridge prefix and governed by each hub's own rules instead.
    /// </para>
    /// </summary>
    /// <param name="configure">The hubs this app serves: Universal Hubs' <c>AddUniversalServer</c> builder.</param>
    /// <param name="clients">
    /// What the web app's clients of other devices' hubs share — the hosts' BLE <c>ServiceUuid</c>, timeouts, Multipeer
    /// (<c>o.Multipeer("myapp-mc")</c>). Universal Hubs' <c>AddDynamicHubClients</c> options.
    /// </param>
    public static TBuilder AddHubsBridge<TBuilder>(
        this TBuilder bridge,
        Action<UniversalServerBuilder>? configure = null,
        Action<UniversalHubClientOptions>? clients = null
    )
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        bridge.Services.AddUniversalServer(server =>
        {
            server.AllowDynamicHubs();
            server.Http(o => o.ManageHttpServer = false);
            configure?.Invoke(server);
        });

        // Android, iOS and Mac Catalyst got the platform's BLE hosting from AddUniversalServer. Elsewhere it is the app's to
        // register; without it, the hubs still run over Wi-Fi and Multipeer.
        bridge.Services.TryAddSingleton<IBleHostingManager, UnavailableBleHosting>();

        // the web app joining other devices' hubs
        bridge.Services.AddDynamicHubClients(clients);

        bridge.AddBridge<HubsBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/hubs</c> over Universal Hubs' <see cref="IDynamicHubs"/>, with every hub call carried to the web app by
/// <see cref="WebAppInvoker"/>.
/// <code>
/// GET    /_bridge/hubs                                          every hub
/// PUT    /_bridge/hubs                                          { "name": "game", "characteristicUuid": "…", "serviceType": "_myapp._tcp", "methods": [{ "name": "MakeMove" }] }
/// DELETE /_bridge/hubs/{name}?reason=…
/// POST   /_bridge/hubs/{name}/start                             { "transports": "All" }
/// POST   /_bridge/hubs/{name}/stop                              { "reason": "Game over" }
/// GET    /_bridge/hubs/{name}/connections                       DELETE /_bridge/hubs/{name}/connections/{id}?reason=…
/// POST   /_bridge/hubs/{name}/push                              { "event": "StateChanged", "arguments": [{…}], "target": { "groups": ["red"] } }
/// GET    /_bridge/hubs/{name}/groups/{group}                    PUT|DELETE /_bridge/hubs/{name}/groups/{group}/{id}
/// POST   /_bridge/hubs/streams/{streamId}                       one item, as JSON
/// POST   /_bridge/hubs/streams/{streamId}/end                   { "error": null }
/// GET    /_bridge/hubs/client.js                                hostHub(definition, handlers) for a page without the C# client
///
/// native call: hub:{name} carrying a HubCall, answered with a HubCallReply
/// events: hubs.stream
/// </code>
/// </summary>
public sealed partial class HubsBridge : IWebAppBridge, IAsyncDisposable
{
    const int MaxNameLength = 64;
    const string StreamsSegment = "streams";

    readonly IServiceProvider services;
    readonly ConcurrentDictionary<string, WebAppHubHandler> hubs = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, WebAppHubStream> streams = new(StringComparer.Ordinal);
    readonly WebAppEventSource<Contracts.HubStreamClosed> streamClosed = new();
    readonly SemaphoreSlim defining = new(1, 1);
    readonly ILogger logger;

    public HubsBridge(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        this.services = services;
        this.logger = services.GetOptionalService<ILoggerFactory>()?.CreateLogger<HubsBridge>() ?? NullLogger<HubsBridge>.Instance;
    }

    public string Name => "hubs";

    public bool IsSupported => this.services.GetOptionalService<IDynamicHubs>() is not null;

    IDynamicHubs Dynamic => this.services.GetRequiredService<IDynamicHubs>();

    WebAppInvoker Invoker => this.services.GetRequiredService<WebAppInvoker>();

    public void Map(WebAppBridgeRoutes routes)
    {
        var script = ClientScript.Replace("{bridge}", routes.BridgePrefix, StringComparison.Ordinal);

        this.MapClients(routes);
        routes
            .MapGet("", this.ListAsync)
            .MapPut("", this.DefineAsync)
            .MapGet("/client.js", context => Results.Text(script, "text/javascript").ExecuteAsync(context))
            .MapPost("/streams/{streamId}", this.StreamItemAsync)
            .MapPost("/streams/{streamId}/end", this.StreamEndAsync)
            .MapDelete("/{name}", this.RemoveAsync)
            .MapPost("/{name}/start", this.StartAsync)
            .MapPost("/{name}/stop", this.StopAsync)
            .MapGet("/{name}/connections", this.ConnectionsAsync)
            .MapDelete("/{name}/connections/{connectionId}", this.DisconnectAsync)
            .MapPost("/{name}/push", this.PushAsync)
            .MapGet("/{name}/groups/{group}", this.GroupAsync)
            .MapPut("/{name}/groups/{group}/{connectionId}", this.AddToGroupAsync)
            .MapDelete("/{name}/groups/{group}/{connectionId}", this.RemoveFromGroupAsync)
            .MapEvent("hubs.stream", this.streamClosed.ListenAsync, Contracts.HubsJsonContext.Default.HubStreamClosed);
    }

    // ---- hubs

    ValueTask ListAsync(HttpContext context)
        => WebAppBridgeResults.Json(
            context,
            this.hubs.Values.OrderBy(x => x.Definition.Name, StringComparer.Ordinal).Select(x => x.ToInfo()).ToList(),
            Contracts.HubsJsonContext.Default.IReadOnlyListHubInfo
        );

    async ValueTask DefineAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubDefinition);
        if (Validate(body) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        var definition = body!;
        if (!this.IsAllowedServiceType(definition.ServiceType, out var allowed))
        {
            await WebAppBridgeResults.BadRequest(context, $"'{definition.ServiceType}' is not one of the app's hub service types ({String.Join(", ", allowed)}).");
            return;
        }

        await this.defining.WaitAsync(context.RequestAborted);
        try
        {
            if (this.hubs.TryGetValue(definition.Name, out var existing))
            {
                if (existing.HasSameIdentity(definition))
                {
                    // the page defines its hubs on every launch; only the method list can change without a new hub
                    existing.Definition = definition;
                    await WebAppBridgeResults.Json(context, existing.ToInfo(), Contracts.HubsJsonContext.Default.HubInfo);
                    return;
                }

                await this.Dynamic.Remove(definition.Name, "The hub was redefined");
                this.hubs.TryRemove(definition.Name, out _);
            }

            var hub = new WebAppHubHandler(this, definition);
            try
            {
                hub.Hub = this.Dynamic.Add(
                    new DynamicHubDefinition(definition.Name, definition.CharacteristicUuid, definition.ServiceType)
                    {
                        MaxClients = definition.MaxClients,
                        AllowTunnel = definition.AllowTunnel
                    },
                    hub
                );
            }
            catch (ArgumentException ex)
            {
                await WebAppBridgeResults.BadRequest(context, ex.Message);
                return;
            }
            catch (InvalidOperationException ex)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "conflict", ex.Message);
                return;
            }

            this.hubs[definition.Name] = hub;
            await WebAppBridgeResults.Json(context, hub.ToInfo(), Contracts.HubsJsonContext.Default.HubInfo);
        }
        finally
        {
            this.defining.Release();
        }
    }

    async ValueTask RemoveAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        await this.defining.WaitAsync(context.RequestAborted);
        try
        {
            await this.Dynamic.Remove(hub.Definition.Name, Reason(context));
            this.hubs.TryRemove(hub.Definition.Name, out _);
        }
        finally
        {
            this.defining.Release();
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StartAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubStartRequest);
        var transports = ToNative(body?.Transports ?? Contracts.HubTransports.All);
        if (transports == HubTransports.None)
        {
            await WebAppBridgeResults.BadRequest(context, "Pick at least one transport.");
            return;
        }

        try
        {
            await hub.Hub.Start(transports, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bluetooth off, no network, a transport this platform lacks: nothing could start
            this.logger.LogWarning(ex, "Hub {Hub} did not start", hub.Definition.Name);
            await WebAppBridgeResults.Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, hub.ToInfo(), Contracts.HubsJsonContext.Default.HubInfo);
    }

    async ValueTask StopAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubStopRequest);
        await hub.Hub.Stop(body?.Reason);
        await WebAppBridgeResults.Json(context, hub.ToInfo(), Contracts.HubsJsonContext.Default.HubInfo);
    }

    // ---- clients

    async ValueTask ConnectionsAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        await WebAppBridgeResults.Json(
            context,
            hub.Hub.Connections.Select(ToContract).ToList(),
            Contracts.HubsJsonContext.Default.IReadOnlyListHubConnectionInfo
        );
    }

    async ValueTask DisconnectAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        var id = Route(context, "connectionId");
        if (!hub.Hub.Connections.Any(x => x.Id == id))
        {
            await WebAppBridgeResults.NotFound(context, $"No client '{id}' is connected to {hub.Definition.Name}.");
            return;
        }

        await hub.Hub.Disconnect(id, Reason(context));
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask PushAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubPushRequest);
        if (body is not { Event.Length: > 0, Arguments: not null })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"event\": \"…\", \"arguments\": [ … ] }.");
            return;
        }

        var arguments = body.Arguments.Select(x => (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(x, Contracts.HubsJsonContext.Default.JsonElement)).ToList();
        await Recipients(hub.Hub, body.Target).Send(body.Event, arguments, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    /// <summary>Everyone without a target; otherwise the connections and groups it names, less its exceptions.</summary>
    static DynamicHubPush Recipients(IDynamicHub hub, Contracts.HubPushTarget? target)
    {
        if (target is null || (target.Connections is not { Count: > 0 } && target.Groups is not { Count: > 0 }))
            return target?.Except is { Count: > 0 } except ? hub.Clients.AllExcept(except) : hub.Clients.All;

        var ids = new HashSet<string>(target.Connections ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var group in target.Groups ?? [])
            ids.UnionWith(hub.Groups.GetMembers(group));

        ids.ExceptWith(target.Except ?? []);
        return hub.Clients.Clients(ids);
    }

    async ValueTask GroupAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        await WebAppBridgeResults.Json(context, hub.Hub.Groups.GetMembers(Route(context, "group")), Contracts.HubsJsonContext.Default.IReadOnlyListString);
    }

    async ValueTask AddToGroupAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        var id = Route(context, "connectionId");
        if (!hub.Hub.Connections.Any(x => x.Id == id))
        {
            await WebAppBridgeResults.NotFound(context, $"No client '{id}' is connected to {hub.Definition.Name}.");
            return;
        }

        await hub.Hub.Groups.AddToGroupAsync(id, Route(context, "group"), context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask RemoveFromGroupAsync(HttpContext context)
    {
        if (await this.FindAsync(context) is not { } hub)
            return;

        await hub.Hub.Groups.RemoveFromGroupAsync(Route(context, "connectionId"), Route(context, "group"), context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    // ---- streams

    async ValueTask StreamItemAsync(HttpContext context)
    {
        if (!this.streams.TryGetValue(Route(context, "streamId"), out var stream))
        {
            await StreamGone(context);
            return;
        }

        var item = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.JsonElement);
        if (item.ValueKind == JsonValueKind.Undefined)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected one item, as JSON.");
            return;
        }

        if (!stream.Items.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(item, Contracts.HubsJsonContext.Default.JsonElement)))
        {
            await StreamGone(context);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StreamEndAsync(HttpContext context)
    {
        if (!this.streams.TryGetValue(Route(context, "streamId"), out var stream))
        {
            await StreamGone(context);
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubStreamEnd);
        stream.Items.Writer.TryComplete(body?.Error is { } error ? new BleHubRemoteException(error.Type, error.Message) : null);
        await WebAppBridgeResults.NoContent(context);
    }

    static ValueTask StreamGone(HttpContext context)
        => WebAppBridgeResults.Error(context, 410, "gone", "The stream's caller has gone, or it already ended.");

    // ---- calls into the web app

    /// <summary>
    /// Sends one hub call to the web app — the page, or background.js when no page takes it — and reads its reply. Throws
    /// what the client should see when it failed.
    /// </summary>
    internal async Task<Contracts.HubCallReply> CallAsync(Contracts.HubCall call, CancellationToken cancellationToken)
    {
        var result = await this.Invoker.InvokeAsync(HandlerName(call.Hub), call, Contracts.HubsJsonContext.Default.HubCall, cancellationToken);

        if (!result.Handled)
            throw new BleHubRemoteException("HubUnavailable", $"The app is not running {call.Hub} right now.");

        if (!result.Succeeded)
            throw new BleHubRemoteException("WebAppError", result.Error ?? "The web app failed the call.");

        var reply = result.ResultJson is { } json
            ? JsonSerializer.Deserialize(json, Contracts.HubsJsonContext.Default.HubCallReply)
            : null;

        reply ??= new Contracts.HubCallReply();
        if (reply.Error is { } error)
            throw new BleHubRemoteException(error.Type, error.Message);

        return reply;
    }

    internal WebAppHubStream OpenStream(string hub)
    {
        var stream = new WebAppHubStream(Guid.NewGuid().ToString("n"), hub);
        this.streams[stream.Id] = stream;
        return stream;
    }

    internal void CloseStream(WebAppHubStream stream, bool tellWebApp)
    {
        if (!this.streams.TryRemove(stream.Id, out _))
            return;

        stream.Items.Writer.TryComplete();
        if (tellWebApp)
            this.streamClosed.Publish(new Contracts.HubStreamClosed(stream.Id, stream.Hub));
    }

    internal ILogger Logger => this.logger;

    /// <summary>The native call a hub's traffic arrives as.</summary>
    public static string HandlerName(string hub) => "hub:" + hub;

    // ---- helpers

    async ValueTask<WebAppHubHandler?> FindAsync(HttpContext context)
    {
        var name = Route(context, "name");
        if (this.hubs.TryGetValue(name, out var hub))
            return hub;

        await WebAppBridgeResults.NotFound(context, $"There is no hub named '{name}'. Define it first.");
        return null;
    }

    bool IsAllowedServiceType(string serviceType, out IReadOnlyList<string> allowed)
    {
        var options = this.services.GetRequiredService<DynamicHubOptions>();
        allowed = options.ServiceTypes.Count > 0
            ? options.ServiceTypes.ToList()
            : [this.services.GetRequiredService<UniversalHubHostOptions>().ServiceType];

        return allowed.Contains(serviceType, StringComparer.OrdinalIgnoreCase);
    }

    static string? Validate(Contracts.HubDefinition? definition)
    {
        if (definition is null)
            return "Expected { \"name\": \"…\", \"characteristicUuid\": \"…\", \"serviceType\": \"_name._tcp\", \"methods\": [ … ] }.";

        if (!IsValidName(definition.Name))
            return $"A hub name is 1-{MaxNameLength} letters, digits, '-', '_' and '.'.";

        if (!Guid.TryParse(definition.CharacteristicUuid, out _))
            return "characteristicUuid must be a 128-bit UUID.";

        if (String.IsNullOrWhiteSpace(definition.ServiceType))
            return "serviceType is required, such as _myapp._tcp.";

        if (definition.Methods is null || definition.Methods.Any(x => String.IsNullOrWhiteSpace(x?.Name)))
            return "methods must name every method clients may call.";

        if (definition.Methods.GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() > 1))
            return "Each method is listed once.";

        if (definition.MaxClients is < 1)
            return "maxClients is at least 1, or null for no limit.";

        return null;
    }

    static bool IsValidName(string? name)
        => name is { Length: > 0 and <= MaxNameLength }
           && name != StreamsSegment
           && name != "clients"
           && name != "client.js"
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    static string Route(HttpContext context, string name) => context.Request.RouteValues[name] ?? String.Empty;

    static string? Reason(HttpContext context) => context.Request.Query["reason"].ToString() is { Length: > 0 } reason ? reason : null;

    internal static Contracts.HubConnectionInfo ToContract(HubConnection connection) => new(
        connection.Id,
        ToContract(connection.Transport),
        connection.Client.Name,
        connection.Client.AppVersion,
        connection.Client.Properties,
        connection.Client.ConnectedAt
    );

    internal static Contracts.HubTransports ToContract(HubTransports transports) => (Contracts.HubTransports)(int)(transports & HubTransports.All);

    static HubTransports ToNative(Contracts.HubTransports transports) => (HubTransports)(int)(transports & Contracts.HubTransports.All);

    internal static Contracts.HubDisconnectReason ToContract(HubDisconnectReason reason) => reason switch
    {
        HubDisconnectReason.ClientDisconnect => Contracts.HubDisconnectReason.ClientDisconnect,
        HubDisconnectReason.ClientTimeout => Contracts.HubDisconnectReason.ClientTimeout,
        HubDisconnectReason.ServerDisconnect => Contracts.HubDisconnectReason.ServerDisconnect,
        HubDisconnectReason.ServerShutdown => Contracts.HubDisconnectReason.ServerShutdown,
        _ => Contracts.HubDisconnectReason.ConnectionFailed
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var client in this.clients.Values)
            await client.DisposeAsync();

        this.clients.Clear();
        foreach (var stream in this.streams.Values)
            stream.Items.Writer.TryComplete();

        this.streams.Clear();
        this.defining.Dispose();
    }

    const string ClientScript = """
        // Shiny.AppDeviceBridge: serve a Universal Hub from the page. The app hosts it over BLE, Wi-Fi and Multipeer;
        // every call reaches the handlers here.
        //
        //   import { hostHub } from "{bridge}/hubs/client.js";
        //   const game = await hostHub(
        //       { name: "game", characteristicUuid: "…", serviceType: "_myapp._tcp" },
        //       {
        //           async MakeMove(cell) { …; return { accepted: true }; },     // an invoke: its result goes back
        //           async *Watch() { yield state; },                              // an async generator is a stream
        //           onConnected(connection) { },                                  // throw to refuse the client
        //           onDisconnected(connection, reason, message) { },
        //       });
        //   await game.start();
        //   await game.push("StateChanged", [state], { groups: ["red"] });
        //
        // A thrown HubError keeps its type for the client: throw new HubError("GameRule", "Not your turn").
        // While no page is open, background.js can answer the same hub with appdevicebridge.on("hub:game", …).

        import { on } from "{bridge}/invoke/client.js";

        const lifecycle = new Set(["onConnected", "onDisconnected", "onRenamed"]);
        const streams = new Map();
        let events;

        export class HubError extends Error {
            constructor(type, message) {
                super(message);
                this.name = "HubError";
                this.type = type;
            }
        }

        export async function hostHub(definition, handlers) {
            const methods = Object.entries(handlers)
                .filter(([name, fn]) => typeof fn === "function" && !lifecycle.has(name))
                .map(([name, fn]) => ({ name, kind: fn[Symbol.toStringTag] === "AsyncGeneratorFunction" ? "Stream" : "Invoke" }));

            const name = definition.name;
            const off = on("hub:" + name, call => dispatch(handlers, call));
            listenForClosedStreams();
            const info = await send("PUT", "", { ...definition, methods });

            return {
                info,
                start: (transports = "All") => send("POST", `${enc(name)}/start`, { transports }),
                stop: reason => send("POST", `${enc(name)}/stop`, { reason }),
                remove: async reason => { off(); await send("DELETE", `${enc(name)}${reason ? "?reason=" + encodeURIComponent(reason) : ""}`); },
                connections: () => send("GET", `${enc(name)}/connections`),
                disconnect: (id, reason) => send("DELETE", `${enc(name)}/connections/${enc(id)}${reason ? "?reason=" + encodeURIComponent(reason) : ""}`),
                push: (event, args = [], target) => send("POST", `${enc(name)}/push`, { event, arguments: args, target }),
                addToGroup: (group, id) => send("PUT", `${enc(name)}/groups/${enc(group)}/${enc(id)}`),
                removeFromGroup: (group, id) => send("DELETE", `${enc(name)}/groups/${enc(group)}/${enc(id)}`),
                group: group => send("GET", `${enc(name)}/groups/${enc(group)}`),
                off
            };
        }

        async function dispatch(handlers, call) {
            try {
                switch (call.kind) {
                    case "Invoke": {
                        let abort;
                        const context = { connection: call.connection, abort: reason => abort = { reason: reason ?? null } };
                        const result = await handlers[call.method].apply(context, call.arguments ?? []);
                        return { result: result === undefined ? null : result, abort: !!abort, abortReason: abort?.reason ?? null };
                    }
                    case "Stream":
                        runStream(handlers[call.method], call);
                        return {};
                    case "Connected":
                        await handlers.onConnected?.(call.connection);
                        return {};
                    case "Disconnected":
                        await handlers.onDisconnected?.(call.connection, call.reason, call.message);
                        return {};
                    case "Renamed":
                        await handlers.onRenamed?.(call.connection, call.previousName);
                        return {};
                }
                return {};
            } catch (e) {
                return { error: toError(e) };
            }
        }

        async function runStream(fn, call) {
            const id = call.streamId;
            const state = { closed: false };
            streams.set(id, state);
            let error = null;
            try {
                for await (const item of fn.apply({ connection: call.connection }, call.arguments ?? [])) {
                    if (state.closed)
                        break;
                    const response = await post(`streams/${id}`, item === undefined ? null : item);
                    if (response.status === 410)
                        break;
                }
            } catch (e) {
                error = toError(e);
            } finally {
                streams.delete(id);
                if (!state.closed)
                    await post(`streams/${id}/end`, { error }).catch(() => {});
            }
        }

        function listenForClosedStreams() {
            if (events)
                return;
            events = new EventSource("{bridge}/events?topics=hubs.stream");
            events.addEventListener("hubs.stream", e => {
                const closed = JSON.parse(e.data);
                const state = streams.get(closed.streamId);
                if (state)
                    state.closed = true;
            });
        }

        function toError(e) {
            return e instanceof HubError
                ? { type: e.type, message: e.message }
                : { type: e?.name || "Error", message: e?.message || String(e) };
        }

        function enc(value) {
            return encodeURIComponent(value);
        }

        function post(path, body) {
            return fetch(`{bridge}/hubs/${path}`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
        }

        async function send(method, path, body) {
            const response = await fetch(path ? `{bridge}/hubs/${path}` : "{bridge}/hubs", {
                method,
                headers: body === undefined ? {} : { "Content-Type": "application/json" },
                body: body === undefined ? undefined : JSON.stringify(body)
            });
            if (!response.ok) {
                const problem = await response.json().catch(() => ({}));
                throw new Error(problem.message || `${method} hubs/${path} answered ${response.status}`);
            }
            return response.status === 204 ? undefined : response.json();
        }
        """;
}

/// <summary>
/// One web app hub: the definition the page gave it, and the handler Universal Hubs calls — every call goes to the web app
/// </summary>
sealed class WebAppHubHandler(HubsBridge bridge, Contracts.HubDefinition definition) : IDynamicHubHandler
{
    public Contracts.HubDefinition Definition { get; set; } = definition;

    public IDynamicHub Hub { get; set; } = null!;

    public bool HasSameIdentity(Contracts.HubDefinition other)
        => String.Equals(this.Definition.CharacteristicUuid, other.CharacteristicUuid, StringComparison.OrdinalIgnoreCase)
           && String.Equals(this.Definition.ServiceType, other.ServiceType, StringComparison.OrdinalIgnoreCase)
           && this.Definition.MaxClients == other.MaxClients
           && this.Definition.AllowTunnel == other.AllowTunnel;

    public Contracts.HubInfo ToInfo() => new(
        this.Definition.Name,
        this.Definition.CharacteristicUuid,
        this.Definition.ServiceType,
        this.Definition.Methods,
        this.Definition.MaxClients,
        this.Definition.AllowTunnel,
        HubsBridge.ToContract(this.Hub.RunningTransports),
        this.Hub.Connections.Count
    );

    public DynamicHubMethodKind GetMethodKind(string method)
    {
        foreach (var m in this.Definition.Methods)
        {
            if (String.Equals(m.Name, method, StringComparison.Ordinal))
                return m.Kind == Contracts.HubMethodKind.Stream ? DynamicHubMethodKind.Stream : DynamicHubMethodKind.Invoke;
        }

        return DynamicHubMethodKind.NotFound;
    }

    public async Task<byte[]?> Invoke(DynamicHubCall call, CancellationToken cancellationToken)
    {
        var reply = await bridge.CallAsync(this.Call(Contracts.HubCallKind.Invoke, call.Connection, call.Method, Arguments(call)), cancellationToken);

        if (reply.Abort)
            call.Abort(reply.AbortReason);

        return reply.Result is { ValueKind: not JsonValueKind.Undefined } result
            ? JsonSerializer.SerializeToUtf8Bytes(result, Contracts.HubsJsonContext.Default.JsonElement)
            : null;
    }

    public async IAsyncEnumerable<byte[]> Stream(DynamicHubCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = bridge.OpenStream(this.Definition.Name);
        var finished = false;
        try
        {
            // the web app answers once it has started; its items and its end arrive on their own routes
            await bridge.CallAsync(this.Call(Contracts.HubCallKind.Stream, call.Connection, call.Method, Arguments(call), stream.Id), cancellationToken);

            await foreach (var item in stream.Items.Reader.ReadAllAsync(cancellationToken))
                yield return item;

            finished = true;
        }
        finally
        {
            // a caller that left before the end, or a failed start: the web app stops sending
            bridge.CloseStream(stream, tellWebApp: !finished);
        }
    }

    public async Task OnConnectedAsync(HubConnection connection)
    {
        try
        {
            await bridge.CallAsync(this.Call(Contracts.HubCallKind.Connected, connection), CancellationToken.None);
        }
        catch (BleHubRemoteException ex)
        {
            // the refusal the client reads is the message alone
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public async Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        try
        {
            await bridge.CallAsync(
                this.Call(Contracts.HubCallKind.Disconnected, connection) with
                {
                    Reason = HubsBridge.ToContract(disconnect.Reason),
                    Message = disconnect.Message
                },
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            // the client is gone either way
            bridge.Logger.LogDebug(ex, "The web app did not take {Hub}'s disconnect of {Connection}", this.Definition.Name, connection.Id);
        }
    }

    public async Task OnRenamedAsync(HubConnection connection, string? previousName)
    {
        try
        {
            await bridge.CallAsync(this.Call(Contracts.HubCallKind.Renamed, connection) with { PreviousName = previousName }, CancellationToken.None);
        }
        catch (BleHubRemoteException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    Contracts.HubCall Call(
        Contracts.HubCallKind kind,
        HubConnection connection,
        string? method = null,
        IReadOnlyList<JsonElement>? arguments = null,
        string? streamId = null
    ) => new(this.Definition.Name, kind, HubsBridge.ToContract(connection), method, arguments, streamId);

    /// <summary>Each argument as the JSON the caller wrote. Wi-Fi requires JSON, and so does the web app.</summary>
    static IReadOnlyList<JsonElement> Arguments(DynamicHubCall call)
    {
        var list = new JsonElement[call.Arguments.Count];
        for (var i = 0; i < list.Length; i++)
        {
            try
            {
                using var document = JsonDocument.Parse(call.Arguments[i]);
                list[i] = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new BleHubRemoteException("HubArgumentError", $"Argument {i + 1} is not JSON ({ex.Message}); web app hubs need the JSON serializer.");
            }
        }
        return list;
    }
}

/// <summary>A streaming call in flight: the web app posts its items here.</summary>
sealed class WebAppHubStream(string id, string hub)
{
    public string Id { get; } = id;

    public string Hub { get; } = hub;

    public Channel<byte[]> Items { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
}
