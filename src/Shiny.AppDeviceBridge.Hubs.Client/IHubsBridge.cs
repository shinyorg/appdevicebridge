using System.Text.Json;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Hubs.Client;

/// <summary>
/// Universal Hubs whose logic lives in the web app — and joining other devices' hubs (<c>clients</c>). The app hosts them over BLE, Wi-Fi and Multipeer; every call a client
/// makes reaches the web app as the native call <c>hub:{name}</c>, carrying a <see cref="HubCall"/> — answered by the page
/// while it is open — and the web app's <see cref="HubCallReply"/> goes back to the client. Define a hub
/// once per launch (again with the same values changes nothing), start it, and push to its clients from here. A host
/// without <c>AddHubsBridge</c> answers 501.
/// </summary>
[BridgeClient("hubs", typeof(HubsJsonContext))]
public interface IHubsBridge
{
    /// <summary>Every hub the web app has defined.</summary>
    [BridgeGet]
    Task<IReadOnlyList<HubInfo>> GetHubsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Defines a hub, or updates its methods. Changing its characteristic, service type, client limit or tunnel setting
    /// replaces it, disconnecting its clients. 400 for a service type the app does not allow, 409 when its name or
    /// characteristic is taken or the app's hub limit is reached.
    /// </summary>
    [BridgePut]
    Task<HubInfo> DefineAsync(HubDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Stops a hub on every transport, telling its clients why, and forgets it. 404 when there is none.</summary>
    [BridgeDelete("{name}")]
    Task RemoveAsync(string name, [BridgeQuery("reason")] string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Starts accepting clients. 503 when no transport could start — Bluetooth off and no network, say.</summary>
    [BridgePost("{name}/start")]
    Task<HubInfo> StartAsync(string name, HubStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops accepting clients on every transport; the connected ones are told why.</summary>
    [BridgePost("{name}/stop")]
    Task<HubInfo> StopAsync(string name, HubStopRequest request, CancellationToken cancellationToken = default);

    /// <summary>The clients connected right now, over every transport.</summary>
    [BridgeGet("{name}/connections")]
    Task<IReadOnlyList<HubConnectionInfo>> GetConnectionsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Ends a client's session, telling it why.</summary>
    [BridgeDelete("{name}/connections/{connectionId}")]
    Task DisconnectAsync(string name, string connectionId, [BridgeQuery("reason")] string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Raises an event on the hub's clients — all of them, or a target's.</summary>
    [BridgePost("{name}/push")]
    Task PushAsync(string name, HubPushRequest request, CancellationToken cancellationToken = default);

    /// <summary>The connection ids in a group.</summary>
    [BridgeGet("{name}/groups/{group}")]
    Task<IReadOnlyList<string>> GetGroupAsync(string name, string group, CancellationToken cancellationToken = default);

    /// <summary>Adds a connection to a group. Groups exist while they have members.</summary>
    [BridgePut("{name}/groups/{group}/{connectionId}")]
    Task AddToGroupAsync(string name, string group, string connectionId, CancellationToken cancellationToken = default);

    [BridgeDelete("{name}/groups/{group}/{connectionId}")]
    Task RemoveFromGroupAsync(string name, string group, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Sends a streaming call's next item. 410 once its caller has gone — stop sending.</summary>
    [BridgePost("streams/{streamId}")]
    Task SendStreamItemAsync(string streamId, [BridgeBody] JsonElement item, CancellationToken cancellationToken = default);

    /// <summary>Ends a streaming call, with an error or without. 410 once its caller has gone.</summary>
    [BridgePost("streams/{streamId}/end")]
    Task EndStreamAsync(string streamId, HubStreamEnd end, CancellationToken cancellationToken = default);

    /// <summary>A streaming call's caller went away, or its hub stopped.</summary>
    [BridgeEvent("hubs.stream")]
    Task<IAsyncDisposable> OnStreamClosedAsync(Func<HubStreamClosed, Task> handler);

    // ---- joining a hub on another device

    /// <summary>The clients created so far.</summary>
    [BridgeGet("clients")]
    Task<IReadOnlyList<HubClientInfo>> GetClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>A client for another device's hub. It finds hosts with <see cref="DiscoverAsync"/> and joins one with <see cref="ConnectAsync"/>.</summary>
    [BridgePost("clients")]
    Task<HubClientInfo> CreateClientAsync(HubClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Disconnects a client and forgets it.</summary>
    [BridgeDelete("clients/{clientId}")]
    Task RemoveClientAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for hosts on the transports asked for; each arrives through <see cref="OnHostAsync"/>, which must be listened to
    /// first (409 <c>not_listening</c>). Runs until stopped, the client is removed, or nothing listens.
    /// </summary>
    [BridgePost("clients/{clientId}/discover")]
    Task DiscoverAsync(string clientId, HubDiscoverRequest request, CancellationToken cancellationToken = default);

    [BridgeDelete("clients/{clientId}/discover")]
    Task StopDiscoveringAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>Joins a host. 404 for a host discovery has not seen; 409 <c>refused</c> when the host refuses, with its reason.</summary>
    [BridgePost("clients/{clientId}/connect")]
    Task<HubClientInfo> ConnectAsync(string clientId, HubConnectRequest request, CancellationToken cancellationToken = default);

    [BridgePost("clients/{clientId}/disconnect")]
    Task<HubClientInfo> DisconnectClientAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>Calls a hub method. A hub's own failure is the result's <c>error</c>; 409 <c>not_connected</c> before connecting.</summary>
    [BridgePost("clients/{clientId}/invoke")]
    Task<HubInvokeResult> InvokeAsync(string clientId, HubInvokeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts a streaming call; its items and its end arrive through <see cref="OnStreamItemAsync"/>.</summary>
    [BridgePost("clients/{clientId}/streams")]
    Task<HubClientStream> StreamAsync(string clientId, HubInvokeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops a streaming call early.</summary>
    [BridgeDelete("clients/{clientId}/streams/{streamId}")]
    Task StopStreamAsync(string clientId, string streamId, CancellationToken cancellationToken = default);

    /// <summary>A host was found, changed or went away.</summary>
    [BridgeEvent("hubs.client.host")]
    Task<IAsyncDisposable> OnHostAsync(Func<HubHostSeen, Task> handler);

    /// <summary>A host raised an event on one of the clients.</summary>
    [BridgeEvent("hubs.client.push")]
    Task<IAsyncDisposable> OnPushAsync(Func<HubClientPush, Task> handler);

    /// <summary>A client connected or disconnected.</summary>
    [BridgeEvent("hubs.client.status")]
    Task<IAsyncDisposable> OnClientChangedAsync(Func<HubClientChanged, Task> handler);

    /// <summary>A streaming call's next item, or its end.</summary>
    [BridgeEvent("hubs.client.stream")]
    Task<IAsyncDisposable> OnStreamItemAsync(Func<HubClientStreamItem, Task> handler);
}
