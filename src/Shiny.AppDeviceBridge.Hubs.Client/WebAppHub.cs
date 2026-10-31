using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Hubs.Client;

/// <summary>
/// A Universal Hub whose logic is this web app's. The app serves it over BLE, Wi-Fi and Multipeer; each call a client
/// makes runs here, with the caller in <see cref="Connection"/>. Methods are mapped by name — no generated code, every
/// value read and written through source-generated JSON metadata — and must match the names in the clients' contract.
/// <code>
/// public sealed class GameHub(GameEngine engine) : WebAppHub
/// {
///     protected override void Map(WebAppHubMethods methods) => methods
///         .Invoke("MakeMove", GameJson.Default.Int32, GameJson.Default.MoveResult, this.MakeMove)
///         .Stream("Watch", GameJson.Default.GameState, engine.Changes);
///
///     async Task&lt;MoveResult&gt; MakeMove(int cell)
///     {
///         var result = engine.Move(this.Connection.Id, cell);
///         await this.PushAsync("StateChanged", GameJson.Default.GameState, engine.State);
///         return result;
///     }
/// }
/// </code>
/// One instance serves every call, and calls can overlap: keep per-caller state keyed by <see cref="HubConnectionInfo.Id"/>.
/// </summary>
public abstract class WebAppHub
{
    static readonly AsyncLocal<WebAppHubCall?> current = new();

    /// <summary>The hub's name: what it was registered under.</summary>
    public string Name { get; internal set; } = null!;

    /// <summary>The bridge, for anything not wrapped here.</summary>
    protected IHubsBridge Bridge { get; private set; } = null!;

    /// <summary>The client whose call or lifecycle event is running.</summary>
    protected HubConnectionInfo Connection
        => current.Value?.Connection ?? throw new InvalidOperationException("Connection is only available while the hub is handling a client.");

    /// <summary>Methods clients may call, by name.</summary>
    protected abstract void Map(WebAppHubMethods methods);

    /// <summary>A client is connecting. Throw — a <see cref="HubException"/> or anything else — to refuse it with that message.</summary>
    public virtual Task OnConnectedAsync(HubConnectionInfo connection) => Task.CompletedTask;

    /// <summary>A client left. Its groups are still intact.</summary>
    public virtual Task OnDisconnectedAsync(HubConnectionInfo connection, HubDisconnectReason reason, string? message) => Task.CompletedTask;

    /// <summary>A client renamed itself; <see cref="HubConnectionInfo.Name"/> is its new name. Throw to refuse.</summary>
    public virtual Task OnRenamedAsync(HubConnectionInfo connection, string? previousName) => Task.CompletedTask;

    /// <summary>Ends the caller's session once the running method's reply is sent.</summary>
    protected void Abort(string? reason = null)
    {
        var call = current.Value ?? throw new InvalidOperationException("Abort is only available while the hub is handling a call.");
        call.Abort = true;
        call.AbortReason = reason;
    }

    /// <summary>Raises <paramref name="eventName"/> with one argument — on every client, or a target's.</summary>
    protected Task PushAsync<T>(string eventName, JsonTypeInfo<T> type, T value, HubPushTarget? target = null, CancellationToken cancellationToken = default)
        => this.PushAsync(eventName, [JsonSerializer.SerializeToElement(value, type)], target, cancellationToken);

    /// <summary>Raises <paramref name="eventName"/> with its arguments already as JSON — for events with several, or none.</summary>
    protected Task PushAsync(string eventName, IReadOnlyList<JsonElement> arguments, HubPushTarget? target = null, CancellationToken cancellationToken = default)
        => this.Bridge.PushAsync(this.Name, new HubPushRequest(eventName, arguments, target), cancellationToken);

    protected Task AddToGroupAsync(string connectionId, string group, CancellationToken cancellationToken = default)
        => this.Bridge.AddToGroupAsync(this.Name, group, connectionId, cancellationToken);

    protected Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken cancellationToken = default)
        => this.Bridge.RemoveFromGroupAsync(this.Name, group, connectionId, cancellationToken);

    /// <summary>Ends a client's session, telling it why.</summary>
    protected Task DisconnectAsync(string connectionId, string? reason = null, CancellationToken cancellationToken = default)
        => this.Bridge.DisconnectAsync(this.Name, connectionId, reason, cancellationToken);


    // ---- the runtime: native calls in, replies out

    WebAppHubMethods? methods;
    readonly ConcurrentDictionary<string, CancellationTokenSource> streams = new(StringComparer.Ordinal);

    internal IReadOnlyList<HubMethod> Initialize(string name, IHubsBridge bridge)
    {
        this.Name = name;
        this.Bridge = bridge;
        this.methods = new WebAppHubMethods();
        this.Map(this.methods);
        return this.methods.Describe();
    }

    internal void StreamClosed(string streamId)
    {
        if (this.streams.TryRemove(streamId, out var cts))
            cts.Cancel();
    }

    internal async Task<string?> DispatchAsync(string json)
    {
        var call = JsonSerializer.Deserialize(json, HubsJsonContext.Default.HubCall)
                   ?? throw new InvalidOperationException("Expected a hub call.");

        var state = new WebAppHubCall(call.Connection);
        current.Value = state;

        HubCallReply reply;
        try
        {
            reply = call.Kind switch
            {
                HubCallKind.Invoke => await this.InvokeAsync(call, state),
                HubCallKind.Stream => this.StartStream(call),
                HubCallKind.Connected => await Done(this.OnConnectedAsync(call.Connection)),
                HubCallKind.Disconnected => await Done(this.OnDisconnectedAsync(call.Connection, call.Reason ?? HubDisconnectReason.ConnectionFailed, call.Message)),
                HubCallKind.Renamed => await Done(this.OnRenamedAsync(call.Connection, call.PreviousName)),
                _ => new HubCallReply()
            };
        }
        catch (Exception ex)
        {
            reply = new HubCallReply(Error: ToError(ex));
        }
        finally
        {
            current.Value = null;
        }

        return JsonSerializer.Serialize(reply, HubsJsonContext.Default.HubCallReply);
    }

    async Task<HubCallReply> InvokeAsync(HubCall call, WebAppHubCall state)
    {
        var handler = this.methods!.FindInvoke(call.Method);
        var result = await handler(call.Arguments ?? [], CancellationToken.None);
        return new HubCallReply(result, Abort: state.Abort, AbortReason: state.AbortReason);
    }

    HubCallReply StartStream(HubCall call)
    {
        var handler = this.methods!.FindStream(call.Method);
        var streamId = call.StreamId ?? throw new InvalidOperationException("A streaming call needs its stream id.");
        var cts = new CancellationTokenSource();
        this.streams[streamId] = cts;

        // answered at once: the items and the end go to the stream's own routes for as long as the stream runs
        _ = this.RunStreamAsync(streamId, call, handler, cts);
        return new HubCallReply();
    }

    async Task RunStreamAsync(string streamId, HubCall call, Func<IReadOnlyList<JsonElement>, CancellationToken, IAsyncEnumerable<JsonElement>> handler, CancellationTokenSource cts)
    {
        current.Value = new WebAppHubCall(call.Connection);
        HubError? error = null;
        try
        {
            await foreach (var item in handler(call.Arguments ?? [], cts.Token).WithCancellation(cts.Token))
                await this.Bridge.SendStreamItemAsync(streamId, item, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;     // the caller left: the host already ended the stream
        }
        catch (BridgeException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ToError(ex);
        }
        finally
        {
            this.streams.TryRemove(streamId, out _);
            cts.Dispose();
        }

        try
        {
            await this.Bridge.EndStreamAsync(streamId, new HubStreamEnd(error));
        }
        catch (BridgeException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
        }
    }

    static async Task<HubCallReply> Done(Task task)
    {
        await task;
        return new HubCallReply();
    }

    static HubError ToError(Exception ex) => ex is HubException hub
        ? new HubError(hub.Type, hub.Message)
        : new HubError(ex.GetType().Name, ex.Message);
}


/// <summary>An error whose type the hub's client sees as given, rather than the exception's class name.</summary>
public class HubException(string type, string message) : Exception(message)
{
    public string Type { get; } = type;
}


/// <summary>The methods a <see cref="WebAppHub"/> answers, by the name clients call them.</summary>
public sealed class WebAppHubMethods
{
    readonly Dictionary<string, Func<IReadOnlyList<JsonElement>, CancellationToken, Task<JsonElement?>>> invokes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Func<IReadOnlyList<JsonElement>, CancellationToken, IAsyncEnumerable<JsonElement>>> streams = new(StringComparer.Ordinal);

    internal WebAppHubMethods() { }

    /// <summary>A method with no arguments and no result.</summary>
    public WebAppHubMethods Invoke(string name, Func<Task> handler)
        => this.AddInvoke(name, 0, async (_, _) =>
        {
            await handler();
            return null;
        });

    /// <summary>A method with no arguments.</summary>
    public WebAppHubMethods Invoke<TResult>(string name, JsonTypeInfo<TResult> result, Func<Task<TResult>> handler)
        => this.AddInvoke(name, 0, async (_, _) => JsonSerializer.SerializeToElement(await handler(), result));

    /// <summary>A method with one argument and no result.</summary>
    public WebAppHubMethods Invoke<TArg>(string name, JsonTypeInfo<TArg> arg, Func<TArg, Task> handler)
        => this.AddInvoke(name, 1, async (args, _) =>
        {
            await handler(Read(args[0], arg));
            return null;
        });

    /// <summary>A method with one argument.</summary>
    public WebAppHubMethods Invoke<TArg, TResult>(string name, JsonTypeInfo<TArg> arg, JsonTypeInfo<TResult> result, Func<TArg, Task<TResult>> handler)
        => this.AddInvoke(name, 1, async (args, _) => JsonSerializer.SerializeToElement(await handler(Read(args[0], arg)), result));

    /// <summary>A method with two arguments.</summary>
    public WebAppHubMethods Invoke<TArg1, TArg2, TResult>(
        string name,
        JsonTypeInfo<TArg1> arg1,
        JsonTypeInfo<TArg2> arg2,
        JsonTypeInfo<TResult> result,
        Func<TArg1, TArg2, Task<TResult>> handler
    ) => this.AddInvoke(name, 2, async (args, _) => JsonSerializer.SerializeToElement(await handler(Read(args[0], arg1), Read(args[1], arg2)), result));

    /// <summary>A method that reads and writes JSON itself — any number of arguments; return null for no result.</summary>
    public WebAppHubMethods Invoke(string name, Func<IReadOnlyList<JsonElement>, CancellationToken, Task<JsonElement?>> handler)
        => this.AddInvoke(name, null, handler);

    /// <summary>A streaming method with no arguments.</summary>
    public WebAppHubMethods Stream<TItem>(string name, JsonTypeInfo<TItem> item, Func<CancellationToken, IAsyncEnumerable<TItem>> handler)
        => this.AddStream(name, 0, (_, ct) => Items(handler(ct), item, ct));

    /// <summary>A streaming method with one argument.</summary>
    public WebAppHubMethods Stream<TArg, TItem>(string name, JsonTypeInfo<TArg> arg, JsonTypeInfo<TItem> item, Func<TArg, CancellationToken, IAsyncEnumerable<TItem>> handler)
        => this.AddStream(name, 1, (args, ct) => Items(handler(Read(args[0], arg), ct), item, ct));

    /// <summary>A streaming method that reads and writes JSON itself.</summary>
    public WebAppHubMethods Stream(string name, Func<IReadOnlyList<JsonElement>, CancellationToken, IAsyncEnumerable<JsonElement>> handler)
        => this.AddStream(name, null, handler);

    internal IReadOnlyList<HubMethod> Describe()
        => [.. this.invokes.Keys.Select(x => new HubMethod(x)), .. this.streams.Keys.Select(x => new HubMethod(x, HubMethodKind.Stream))];

    internal Func<IReadOnlyList<JsonElement>, CancellationToken, Task<JsonElement?>> FindInvoke(string? name)
        => name is not null && this.invokes.TryGetValue(name, out var handler)
            ? handler
            : throw new HubException("HubMethodNotFound", $"The hub has no method '{name}'.");

    internal Func<IReadOnlyList<JsonElement>, CancellationToken, IAsyncEnumerable<JsonElement>> FindStream(string? name)
        => name is not null && this.streams.TryGetValue(name, out var handler)
            ? handler
            : throw new HubException("HubMethodNotFound", $"The hub has no stream '{name}'.");

    WebAppHubMethods AddInvoke(string name, int? arguments, Func<IReadOnlyList<JsonElement>, CancellationToken, Task<JsonElement?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        this.EnsureNew(name);
        this.invokes[name] = arguments is { } count ? (args, ct) => handler(Expect(name, args, count), ct) : handler;
        return this;
    }

    WebAppHubMethods AddStream(string name, int? arguments, Func<IReadOnlyList<JsonElement>, CancellationToken, IAsyncEnumerable<JsonElement>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        this.EnsureNew(name);
        this.streams[name] = arguments is { } count ? (args, ct) => handler(Expect(name, args, count), ct) : handler;
        return this;
    }

    void EnsureNew(string name)
    {
        if (this.invokes.ContainsKey(name) || this.streams.ContainsKey(name))
            throw new InvalidOperationException($"'{name}' is already mapped.");
    }

    static IReadOnlyList<JsonElement> Expect(string name, IReadOnlyList<JsonElement> args, int count)
        => args.Count == count
            ? args
            : throw new HubException("HubArgumentError", $"'{name}' takes {count} argument(s) but {args.Count} were sent - are both sides using the same contract?");

    static T Read<T>(JsonElement value, JsonTypeInfo<T> type) => value.Deserialize(type)!;

    static async IAsyncEnumerable<JsonElement> Items<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> type, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken))
            yield return JsonSerializer.SerializeToElement(item, type);
    }
}


/// <summary>How a <see cref="WebAppHub"/> is served: everything the definition takes but its methods, which come from <c>Map</c>.</summary>
/// <param name="Name">Letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, up to 64.</param>
/// <param name="CharacteristicUuid">The BLE characteristic, also the HTTP path. Clients use the same one.</param>
/// <param name="ServiceType">An mDNS service type the app allows for web app hubs, such as <c>_myapp._tcp</c>.</param>
public sealed record WebAppHubOptions(string Name, string CharacteristicUuid, string ServiceType)
{
    /// <summary>Counted across every transport; null for no limit.</summary>
    public int? MaxClients { get; init; } = 8;

    /// <summary>Let clients reach the hub through a tunnel — the public internet.</summary>
    public bool AllowTunnel { get; init; }
}


/// <summary>
/// The web app's hubs: <see cref="DefineAsync"/> defines each one with the app and starts taking its calls. Call it once the
/// page is up — the host forgets a page's handlers when it goes, so a page defines its hubs every time it loads.
/// </summary>
public sealed class WebAppHubs(IServiceProvider services, IHubsBridge bridge, IWebAppNativeCalls nativeCalls) : IAsyncDisposable
{
    readonly List<IAsyncDisposable> subscriptions = new();
    readonly Dictionary<string, WebAppHub> hubs = new(StringComparer.Ordinal);

    /// <summary>The hubs defined so far, by name.</summary>
    public IReadOnlyDictionary<string, WebAppHub> Hubs => this.hubs;

    /// <summary>Defines every hub registered with <c>AddWebAppHub</c> and takes their calls. Defining again changes nothing.</summary>
    public async Task<IReadOnlyList<HubInfo>> DefineAsync(CancellationToken cancellationToken = default)
    {
        if (this.subscriptions.Count == 0)
            this.subscriptions.Add(await bridge.OnStreamClosedAsync(this.OnStreamClosedAsync));

        var infos = new List<HubInfo>();
        foreach (var registration in services.GetServices<WebAppHubRegistration>())
        {
            var options = registration.Options;
            if (!this.hubs.TryGetValue(options.Name, out var hub))
            {
                hub = registration.Create(services);
                var methods = hub.Initialize(options.Name, bridge);
                this.subscriptions.Add(await nativeCalls.HandleAsync(HandlerName(options.Name), hub.DispatchAsync));
                this.hubs[options.Name] = hub;
                registration.Methods = methods;
            }

            infos.Add(await bridge.DefineAsync(
                new HubDefinition(options.Name, options.CharacteristicUuid, options.ServiceType, registration.Methods!, options.MaxClients, options.AllowTunnel),
                cancellationToken
            ));
        }
        return infos;
    }

    /// <summary>Starts a hub on the transports asked for; one that cannot start here is skipped as long as another does.</summary>
    public Task<HubInfo> StartAsync(string name, HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default)
        => bridge.StartAsync(name, new HubStartRequest(transports), cancellationToken);

    /// <summary>Stops a hub on every transport, telling its clients why.</summary>
    public Task<HubInfo> StopAsync(string name, string? reason = null, CancellationToken cancellationToken = default)
        => bridge.StopAsync(name, new HubStopRequest(reason), cancellationToken);

    /// <summary>The native call a hub's traffic arrives as.</summary>
    public static string HandlerName(string hub) => "hub:" + hub;

    Task OnStreamClosedAsync(HubStreamClosed closed)
    {
        if (this.hubs.TryGetValue(closed.Hub, out var hub))
            hub.StreamClosed(closed.StreamId);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in this.subscriptions)
            await subscription.DisposeAsync();

        this.subscriptions.Clear();
        this.hubs.Clear();
    }
}


/// <summary>One hub registered with <c>AddWebAppHub</c>.</summary>
public sealed class WebAppHubRegistration(WebAppHubOptions options, Func<IServiceProvider, WebAppHub> create)
{
    public WebAppHubOptions Options { get; } = options;

    internal IReadOnlyList<HubMethod>? Methods { get; set; }

    internal WebAppHub Create(IServiceProvider services) => create(services);
}


public static class WebAppHubServiceCollectionExtensions
{
    /// <summary>
    /// Serves <typeparamref name="THub"/> from this web app. Registers the hubs bridge client too. Then, once the page is
    /// up, <c>await services.GetRequiredService&lt;WebAppHubs&gt;().DefineAsync()</c> and start the hub.
    /// <code>
    /// builder.Services
    ///     .AddWebAppHostClient()
    ///     .AddWebAppHub&lt;GameHub&gt;(new("game", "6e400010-…", "_myapp._tcp") { MaxClients = 6 });
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(
        this IServiceCollection services,
        WebAppHubOptions options
    ) where THub : WebAppHub
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (services.Any(x => x.ImplementationInstance is WebAppHubRegistration r && r.Options.Name == options.Name))
            throw new InvalidOperationException($"A web app hub named '{options.Name}' is already registered.");

        services.AddHubsBridgeClient();
        services.AddSingleton(new WebAppHubRegistration(options, sp => ActivatorUtilities.CreateInstance<THub>(sp)));
        if (!services.Any(x => x.ServiceType == typeof(WebAppHubs)))
            services.AddScoped<WebAppHubs>();

        return services;
    }
}


sealed class WebAppHubCall(HubConnectionInfo connection)
{
    public HubConnectionInfo Connection { get; } = connection;

    public bool Abort { get; set; }

    public string? AbortReason { get; set; }
}
