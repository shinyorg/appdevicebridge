using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.UniversalHubs;
using Contracts = Shiny.AppDeviceBridge.Hubs.Client;

namespace Shiny.AppDeviceBridge.Hubs;

/// <summary>
/// The other half: the web app joining hubs on other devices, through Universal Hubs' dynamic hub clients — any hub, by
/// characteristic, with methods and events by name.
/// <code>
/// GET    /_bridge/hubs/clients                            POST /_bridge/hubs/clients { "characteristicUuid": "…", "serviceType": "_myapp._tcp" }
/// DELETE /_bridge/hubs/clients/{id}
/// POST   /_bridge/hubs/clients/{id}/discover              { "transports": "Ble, Http" }   hosts as hubs.client.host
/// DELETE /_bridge/hubs/clients/{id}/discover
/// POST   /_bridge/hubs/clients/{id}/connect               { "host": "Kitchen", "prefer": "Ble", "allowed": "All", "name": "Allan" }
/// POST   /_bridge/hubs/clients/{id}/disconnect
/// POST   /_bridge/hubs/clients/{id}/invoke                { "method": "Send", "arguments": ["hi"] }
/// POST   /_bridge/hubs/clients/{id}/streams               same body; items as hubs.client.stream
/// DELETE /_bridge/hubs/clients/{id}/streams/{streamId}
///
/// events: hubs.client.host, hubs.client.push, hubs.client.status, hubs.client.stream
/// </code>
/// </summary>
public sealed partial class HubsBridge
{
    const int MaxClients = 8;

    readonly ConcurrentDictionary<string, WebAppHubClient> clients = new(StringComparer.Ordinal);
    readonly WebAppEventSource<Contracts.HubHostSeen> hostsSeen = new();
    readonly WebAppEventSource<Contracts.HubClientPush> clientPushes = new();
    readonly WebAppEventSource<Contracts.HubClientChanged> clientChanges = new();
    readonly WebAppEventSource<Contracts.HubClientStreamItem> clientStreams = new();

    IDynamicHubClients? DynamicClients => this.services.GetOptionalService<IDynamicHubClients>();

    void MapClients(WebAppBridgeRoutes routes) => routes
        .MapGet("/clients", this.ListClientsAsync)
        .MapPost("/clients", this.CreateClientAsync)
        .MapDelete("/clients/{clientId}", this.RemoveClientAsync)
        .MapPost("/clients/{clientId}/discover", this.DiscoverAsync)
        .MapDelete("/clients/{clientId}/discover", this.StopDiscoveringAsync)
        .MapPost("/clients/{clientId}/connect", this.ConnectAsync)
        .MapPost("/clients/{clientId}/disconnect", this.DisconnectClientAsync)
        .MapPost("/clients/{clientId}/invoke", this.InvokeAsync)
        .MapPost("/clients/{clientId}/streams", this.StreamAsync)
        .MapDelete("/clients/{clientId}/streams/{streamId}", this.StopStreamAsync)
        .MapEvent("hubs.client.host", ct => this.hostsSeen.ListenAsync(this.OnHostListenerStopped, ct), Contracts.HubsJsonContext.Default.HubHostSeen)
        .MapEvent("hubs.client.push", this.clientPushes.ListenAsync, Contracts.HubsJsonContext.Default.HubClientPush)
        .MapEvent("hubs.client.status", this.clientChanges.ListenAsync, Contracts.HubsJsonContext.Default.HubClientChanged)
        .MapEvent("hubs.client.stream", this.clientStreams.ListenAsync, Contracts.HubsJsonContext.Default.HubClientStreamItem);

    ValueTask ListClientsAsync(HttpContext context)
        => WebAppBridgeResults.Json(context, this.clients.Values.Select(x => x.ToInfo()).ToList(), Contracts.HubsJsonContext.Default.IReadOnlyListHubClientInfo);

    async ValueTask CreateClientAsync(HttpContext context)
    {
        if (this.DynamicClients is not { } factory)
        {
            await WebAppBridgeResults.NotSupported(context, "Joining hubs");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubClientRequest);
        if (body is not { CharacteristicUuid.Length: > 0, ServiceType.Length: > 0 } || !Guid.TryParse(body.CharacteristicUuid, out _))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"characteristicUuid\": \"<uuid>\", \"serviceType\": \"_name._tcp\" }.");
            return;
        }

        if (this.clients.Count >= MaxClients)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "too_many_clients", $"At most {MaxClients} hub clients at once - remove one first.");
            return;
        }

        IUniversalHubClient<IDynamicHubCalls> native;
        try
        {
            native = factory.Create(body.CharacteristicUuid, body.ServiceType);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
            return;
        }

        var client = new WebAppHubClient(Guid.NewGuid().ToString("n"), body, native, this);
        this.clients[client.Id] = client;
        await WebAppBridgeResults.Json(context, client.ToInfo(), Contracts.HubsJsonContext.Default.HubClientInfo);
    }

    async ValueTask RemoveClientAsync(HttpContext context)
    {
        if (!this.clients.TryRemove(Route(context, "clientId"), out var client))
        {
            await ClientNotFound(context);
            return;
        }

        await client.DisposeAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask DiscoverAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return;

        if (!this.hostsSeen.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen to hubs.client.host before discovering.");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubDiscoverRequest);
        var transports = ToNative(body?.Transports ?? Contracts.HubTransports.All);
        if (transports == HubTransports.None)
        {
            await WebAppBridgeResults.BadRequest(context, "Pick at least one transport.");
            return;
        }

        client.Discover(transports);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StopDiscoveringAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return;

        client.StopDiscovering();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask ConnectAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubConnectRequest);
        HubHostInfo? host = null;
        if (body?.Host is { Length: > 0 } name)
        {
            host = client.FindHost(name);
            if (host is null)
            {
                await WebAppBridgeResults.NotFound(context, $"No host named '{name}' has been discovered.");
                return;
            }
        }
        else if (body?.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
        {
            host = HubHostInfo.FromUrl(address);
        }

        if (host is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"host\": \"<discovered name>\" } or { \"url\": \"http://…\" }.");
            return;
        }

        var allowed = ToNative(body!.Allowed);
        if (allowed == HubTransports.None)
        {
            await WebAppBridgeResults.BadRequest(context, "Allow at least one transport.");
            return;
        }

        try
        {
            await client.Native.Connect(
                host,
                new HubConnectOptions(body.Name, null, body.Properties is null ? null : new Dictionary<string, string>(body.Properties))
                {
                    Prefer = ToNative(body.Prefer),
                    Allowed = allowed
                },
                context.RequestAborted
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogInformation(ex, "Hub client {Client} could not connect to {Host}", client.Id, host.Name);
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "refused", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, client.ToInfo(), Contracts.HubsJsonContext.Default.HubClientInfo);
    }

    async ValueTask DisconnectClientAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return;

        await client.DisconnectAsync();
        await WebAppBridgeResults.Json(context, client.ToInfo(), Contracts.HubsJsonContext.Default.HubClientInfo);
    }

    async ValueTask InvokeAsync(HttpContext context)
    {
        if (await this.FindConnectedAsync(context) is not { } client)
            return;

        if (await ReadCallAsync(context) is not { } call)
            return;

        Contracts.HubInvokeResult result;
        try
        {
            var value = await client.Native.Hub.Invoke(call.Method, Serialize(call.Arguments), context.RequestAborted);
            result = new Contracts.HubInvokeResult(value.IsEmpty ? null : Parse(value));
        }
        catch (BleHubRemoteException ex)
        {
            result = new Contracts.HubInvokeResult(Error: new Contracts.HubError(ex.RemoteErrorType, ex.Message));
        }
        catch (BleHubException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_connected", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, result, Contracts.HubsJsonContext.Default.HubInvokeResult);
    }

    async ValueTask StreamAsync(HttpContext context)
    {
        if (await this.FindConnectedAsync(context) is not { } client)
            return;

        if (await ReadCallAsync(context) is not { } call)
            return;

        var id = client.StartStream(call.Method, Serialize(call.Arguments));
        await WebAppBridgeResults.Json(context, new Contracts.HubClientStream(id), Contracts.HubsJsonContext.Default.HubClientStream);
    }

    async ValueTask StopStreamAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return;

        if (!client.StopStream(Route(context, "streamId")))
        {
            await WebAppBridgeResults.NotFound(context, "No such stream.");
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    /// <summary>Discovery has nowhere to report once the page stops listening, so every client stops looking.</summary>
    void OnHostListenerStopped(int remaining)
    {
        if (remaining > 0)
            return;

        foreach (var client in this.clients.Values)
            client.StopDiscovering();
    }

    internal void Publish(Contracts.HubHostSeen seen) => this.hostsSeen.Publish(seen);

    internal void Publish(Contracts.HubClientPush push) => this.clientPushes.Publish(push);

    internal void Publish(Contracts.HubClientChanged changed) => this.clientChanges.Publish(changed);

    internal void Publish(Contracts.HubClientStreamItem item) => this.clientStreams.Publish(item);

    async ValueTask<WebAppHubClient?> FindClientAsync(HttpContext context)
    {
        if (this.clients.TryGetValue(Route(context, "clientId"), out var client))
            return client;

        await ClientNotFound(context);
        return null;
    }

    async ValueTask<WebAppHubClient?> FindConnectedAsync(HttpContext context)
    {
        if (await this.FindClientAsync(context) is not { } client)
            return null;

        if (client.Native.Status == BleHubClientStatus.Connected)
            return client;

        await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_connected", "Connect the client first.");
        return null;
    }

    static ValueTask ClientNotFound(HttpContext context) => WebAppBridgeResults.NotFound(context, "No such hub client. Create it first.");

    static async ValueTask<Contracts.HubInvokeRequest?> ReadCallAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HubsJsonContext.Default.HubInvokeRequest);
        if (body is { Method.Length: > 0, Arguments: not null })
            return body;

        await WebAppBridgeResults.BadRequest(context, "Expected { \"method\": \"…\", \"arguments\": [ … ] }.");
        return null;
    }

    static IReadOnlyList<ReadOnlyMemory<byte>> Serialize(IReadOnlyList<JsonElement> arguments)
        => arguments.Select(x => (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(x, Contracts.HubsJsonContext.Default.JsonElement)).ToList();

    /// <summary>A value the host's serializer wrote, as JSON — which a host reached over Wi-Fi always writes.</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new BleHubRemoteException("HubValueError", $"The host sent a value that is not JSON ({ex.Message}); the web app can only read JSON.");
        }
    }

    internal static Contracts.HubClientStatus ToContract(BleHubClientStatus status) => status switch
    {
        BleHubClientStatus.Connecting => Contracts.HubClientStatus.Connecting,
        BleHubClientStatus.Connected => Contracts.HubClientStatus.Connected,
        BleHubClientStatus.Disconnecting => Contracts.HubClientStatus.Disconnecting,
        _ => Contracts.HubClientStatus.Disconnected
    };
}

/// <summary>One of the web app's hub clients: its discovered hosts and the streams it is reading.</summary>
sealed class WebAppHubClient : IAsyncDisposable
{
    readonly HubsBridge bridge;
    readonly ConcurrentDictionary<string, HubHostInfo> hosts = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, CancellationTokenSource> streams = new(StringComparer.Ordinal);
    readonly Lock gate = new();
    IDisposable? discovery;

    public WebAppHubClient(string id, Contracts.HubClientRequest request, IUniversalHubClient<IDynamicHubCalls> native, HubsBridge bridge)
    {
        this.Id = id;
        this.Request = request;
        this.Native = native;
        this.bridge = bridge;

        native.StatusChanged += (_, e) => bridge.Publish(new Contracts.HubClientChanged(
            id,
            HubsBridge.ToContract(e.Status),
            HubsBridge.ToContract(native.Transport),
            e.Disconnect is { } d ? HubsBridge.ToContract(d.Reason) : null,
            e.Disconnect?.Message
        ));
        native.Hub.Pushed += (name, args) =>
        {
            try
            {
                bridge.Publish(new Contracts.HubClientPush(id, name, args.Select(HubsBridge.Parse).ToList()));
            }
            catch (BleHubRemoteException ex)
            {
                bridge.Logger.LogWarning(ex, "Dropped {Event} from a hub: not JSON", name);
            }
        };
    }

    public string Id { get; }

    public Contracts.HubClientRequest Request { get; }

    public IUniversalHubClient<IDynamicHubCalls> Native { get; }

    public Contracts.HubClientInfo ToInfo() => new(
        this.Id,
        this.Request.CharacteristicUuid,
        this.Request.ServiceType,
        HubsBridge.ToContract(this.Native.Status),
        HubsBridge.ToContract(this.Native.Transport),
        this.Native.HostName
    );

    public HubHostInfo? FindHost(string name) => this.hosts.TryGetValue(name, out var host) ? host : null;

    public void Discover(HubTransports transports)
    {
        lock (this.gate)
        {
            this.discovery?.Dispose();
            this.discovery = this.Native.DiscoverAll(transports).Subscribe(
                host =>
                {
                    if (host.Transports == HubTransports.None)
                        this.hosts.TryRemove(host.Name, out _);
                    else
                        this.hosts[host.Name] = host;

                    this.bridge.Publish(new Contracts.HubHostSeen(this.Id, host.Name, host.HostId, HubsBridge.ToContract(host.Transports), host.Rssi));
                },
                ex => this.bridge.Logger.LogWarning(ex, "Hub discovery for client {Client} stopped", this.Id)
            );
        }
    }

    public void StopDiscovering()
    {
        lock (this.gate)
        {
            this.discovery?.Dispose();
            this.discovery = null;
        }
    }

    public string StartStream(string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        var id = Guid.NewGuid().ToString("n");
        var cts = new CancellationTokenSource();
        this.streams[id] = cts;
        _ = this.RunStreamAsync(id, method, arguments, cts);
        return id;
    }

    public bool StopStream(string streamId)
    {
        if (!this.streams.TryRemove(streamId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    async Task RunStreamAsync(string id, string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments, CancellationTokenSource cts)
    {
        Contracts.HubError? error = null;
        try
        {
            await foreach (var item in this.Native.Hub.Stream(method, arguments, cts.Token))
                this.bridge.Publish(new Contracts.HubClientStreamItem(this.Id, id, HubsBridge.Parse(item)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (BleHubRemoteException ex)
        {
            error = new Contracts.HubError(ex.RemoteErrorType, ex.Message);
        }
        catch (Exception ex)
        {
            error = new Contracts.HubError(ex.GetType().Name, ex.Message);
        }
        finally
        {
            this.streams.TryRemove(id, out _);
            cts.Dispose();
        }

        this.bridge.Publish(new Contracts.HubClientStreamItem(this.Id, id, End: true, Error: error));
    }

    public async Task DisconnectAsync()
    {
        foreach (var id in this.streams.Keys.ToList())
            this.StopStream(id);

        if (this.Native.Status is BleHubClientStatus.Connected or BleHubClientStatus.Connecting)
        {
            try
            {
                await this.Native.Disconnect();
            }
            catch (Exception ex)
            {
                this.bridge.Logger.LogDebug(ex, "Hub client {Client} did not disconnect cleanly", this.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.StopDiscovering();
        await this.DisconnectAsync();
        (this.Native as IDisposable)?.Dispose();
    }
}
