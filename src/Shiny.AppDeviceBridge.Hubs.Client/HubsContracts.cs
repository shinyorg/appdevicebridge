using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Hubs.Client;

/// <summary>How hub clients reach this device. Flags: a hub can run on several at once.</summary>
[Flags]
public enum HubTransports
{
    None = 0,
    Ble = 1,

    /// <summary>Wi-Fi — the app's HTTP server, advertised over mDNS.</summary>
    Http = 2,

    /// <summary>Apple's MultipeerConnectivity, between Apple devices; needs Shiny.UniversalHubs.Multipeer in the app.</summary>
    Multipeer = 4,

    /// <summary>Google Nearby Connections, between Android, iOS and macOS; needs Shiny.UniversalHubs.Nearby in the app.</summary>
    Nearby = 8,

    All = Ble | Http | Multipeer | Nearby
}

public enum HubMethodKind
{
    /// <summary>Returns one result, or nothing.</summary>
    Invoke,

    /// <summary>Sends items until it ends.</summary>
    Stream
}

/// <summary>Why a client left. The same value the client sees.</summary>
public enum HubDisconnectReason
{
    ClientDisconnect,
    ClientTimeout,
    ServerDisconnect,
    ServerShutdown,
    ConnectionFailed
}

/// <param name="Name">The method name clients call, exactly as their contract spells it.</param>
public sealed record HubMethod(string Name, HubMethodKind Kind = HubMethodKind.Invoke);

/// <summary>
/// A hub the web app serves. Defining it again with the same values changes nothing. It is served over every transport the
/// app set up: Wi-Fi, Multipeer and Nearby always, Bluetooth LE only when it has a <see cref="CharacteristicUuid"/>.
/// </summary>
/// <param name="Name">
/// Letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, up to 64. Clients ask for the hub by this name on every transport, and
/// calls reach the web app as the native call <c>hub:{name}</c>.
/// </param>
/// <param name="Methods">Every method clients may call. Anything else is answered as not found without reaching the web app.</param>
/// <param name="ServiceType">The mDNS service type it is advertised under over Wi-Fi, such as <c>_myapp._tcp</c>: one the app allows for web app hubs. Null for the app's default.</param>
/// <param name="CharacteristicUuid">Serve it over Bluetooth LE as this characteristic, 128-bit or 16/32-bit. Null keeps it off BLE.</param>
/// <param name="ServiceUuid">The GATT service its characteristic is in. Null for the app's dynamic hub service.</param>
/// <param name="MaxClients">Counted across every transport; null for no limit.</param>
/// <param name="AllowTunnel">Let clients reach the hub through a tunnel — the public internet.</param>
public sealed record HubDefinition(
    string Name,
    IReadOnlyList<HubMethod> Methods,
    string? ServiceType = null,
    string? CharacteristicUuid = null,
    string? ServiceUuid = null,
    int? MaxClients = 8,
    bool AllowTunnel = false
);

/// <param name="RunningTransports">Where the hub is accepting clients right now.</param>
/// <param name="Connections">Clients connected, over every transport.</param>
public sealed record HubInfo(
    string Name,
    IReadOnlyList<HubMethod> Methods,
    string? ServiceType,
    string? CharacteristicUuid,
    string? ServiceUuid,
    int? MaxClients,
    bool AllowTunnel,
    HubTransports RunningTransports,
    int Connections
);

/// <param name="Transports">Where to accept clients. One that cannot start here is skipped as long as another does.</param>
public sealed record HubStartRequest(HubTransports Transports = HubTransports.All);

/// <param name="Reason">Told to every connected client.</param>
public sealed record HubStopRequest(string? Reason = null);

/// <summary>A client connected to a hub.</summary>
/// <param name="Id">The connection id: what pushes, groups and disconnects name it by.</param>
/// <param name="Properties">The values the client sent when it connected.</param>
public sealed record HubConnectionInfo(
    string Id,
    HubTransports Transport,
    string? Name,
    string? AppVersion,
    IReadOnlyDictionary<string, string> Properties,
    DateTimeOffset ConnectedAt
);

public enum HubCallKind
{
    /// <summary>A method call. Reply with its result.</summary>
    Invoke,

    /// <summary>A streaming call. Send items to <c>streams/{streamId}</c>, then end it; reply once it has started.</summary>
    Stream,

    /// <summary>A client is connecting. Reply with an error to refuse it.</summary>
    Connected,

    /// <summary>A client left. Its groups are still intact.</summary>
    Disconnected,

    /// <summary>A client renamed itself. Reply with an error to refuse.</summary>
    Renamed
}

/// <summary>
/// What a hub asks the page, as the native call <c>hub:{name}</c>. Reply with a <see cref="HubCallReply"/>.
/// </summary>
/// <param name="Arguments">Each argument exactly as the caller serialized it.</param>
/// <param name="StreamId">For <see cref="HubCallKind.Stream"/>: where its items go.</param>
/// <param name="Reason">For <see cref="HubCallKind.Disconnected"/>.</param>
/// <param name="Message">For <see cref="HubCallKind.Disconnected"/>: the message that came with it, if any.</param>
/// <param name="PreviousName">For <see cref="HubCallKind.Renamed"/>; the connection carries the new name.</param>
public sealed record HubCall(
    string Hub,
    HubCallKind Kind,
    HubConnectionInfo Connection,
    string? Method = null,
    IReadOnlyList<JsonElement>? Arguments = null,
    string? StreamId = null,
    HubDisconnectReason? Reason = null,
    string? Message = null,
    string? PreviousName = null
);

/// <param name="Type">The error's type, such as an exception's name. Clients see it with the message.</param>
public sealed record HubError(string Type, string Message);

/// <summary>The web app's answer to a <see cref="HubCall"/>.</summary>
/// <param name="Result">An <see cref="HubCallKind.Invoke"/>'s result; absent for a method with none.</param>
/// <param name="Error">Fails the call, or refuses a connection or rename. The client gets its type and message.</param>
/// <param name="Abort">End the caller's session once the reply is sent.</param>
/// <param name="AbortReason">Told to the caller when <paramref name="Abort"/> is set.</param>
public sealed record HubCallReply(JsonElement? Result = null, HubError? Error = null, bool Abort = false, string? AbortReason = null);

/// <summary>Who a push goes to: everyone without a target, otherwise the connections and groups named, less <see cref="Except"/>.</summary>
public sealed record HubPushTarget(
    IReadOnlyList<string>? Connections = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<string>? Except = null
);

/// <summary>Raises an event on the hub's clients.</summary>
/// <param name="Event">The event name, exactly as the clients' contract spells it.</param>
/// <param name="Arguments">Each argument as JSON.</param>
public sealed record HubPushRequest(string Event, IReadOnlyList<JsonElement> Arguments, HubPushTarget? Target = null);

/// <param name="Error">Ends the stream with an error after the items already sent.</param>
public sealed record HubStreamEnd(HubError? Error = null);

/// <summary>A stream's caller went away or the hub stopped: stop sending its items.</summary>
public sealed record HubStreamClosed(string StreamId, string Hub);

// ---- joining a hub on another device

public enum HubClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,

    /// <summary>The link dropped and the client is getting back onto it; calls fail until it is <see cref="Connected"/> again.</summary>
    Reconnecting
}

/// <summary>
/// A client for the hub named <paramref name="Hub"/> — this app joining another device's hub — set up with the hub's own
/// settings, as its host gave them. Wi-Fi always, Bluetooth LE when <paramref name="CharacteristicUuid"/> is given, and
/// whatever else the app shares between its hub clients (Multipeer, Nearby).
/// </summary>
/// <param name="ServiceType">The hub's mDNS service type, browsed for over Wi-Fi. Null for the default.</param>
/// <param name="CharacteristicUuid">The hub's BLE characteristic. Null keeps the client off BLE.</param>
/// <param name="ServiceUuid">The GATT service its characteristic is in. Null for Universal Hubs' default service.</param>
public sealed record HubClientRequest(string Hub, string? ServiceType = null, string? CharacteristicUuid = null, string? ServiceUuid = null);

/// <param name="Transport">How it is connected now; <see cref="HubTransports.None"/> when it is not.</param>
/// <param name="HostName">The host it is connected to.</param>
public sealed record HubClientInfo(
    string Id,
    string Hub,
    string? ServiceType,
    string? CharacteristicUuid,
    string? ServiceUuid,
    HubClientStatus Status,
    HubTransports Transport,
    string? HostName
);

/// <param name="Transports">Where to look: BLE scanning, mDNS browsing, Multipeer, Nearby.</param>
public sealed record HubDiscoverRequest(HubTransports Transports = HubTransports.All);

/// <summary>A host found, changed or gone (<see cref="Transports"/> is <see cref="HubTransports.None"/>), merged across transports by name.</summary>
/// <param name="Transports">How the host can be reached.</param>
/// <param name="Rssi">Its BLE signal, when seen over BLE.</param>
public sealed record HubHostSeen(string ClientId, string Name, string? HostId, HubTransports Transports, int? Rssi);

/// <summary>Connects to a host found by discovery, or one at an address.</summary>
/// <param name="Host">A host's name, as discovery reported it.</param>
/// <param name="Url">Or a host's address — <c>http://192.168.1.20:5780</c>, a tunnel's public URL — reached over Wi-Fi only.</param>
/// <param name="Name">The name this client goes by on the host.</param>
/// <param name="Prefer">Tried first when the host is reachable more than one way; the rest follow in the order the client's transports were set up.</param>
/// <param name="Allowed">The only transports that may be used.</param>
/// <param name="Properties">Sent to the host in the handshake.</param>
public sealed record HubConnectRequest(
    string? Host = null,
    string? Url = null,
    string? Name = null,
    HubTransports Prefer = HubTransports.Http,
    HubTransports Allowed = HubTransports.All,
    IReadOnlyDictionary<string, string>? Properties = null
);

/// <param name="Method">The method name, exactly as the hub's contract spells it.</param>
/// <param name="Arguments">Each argument as JSON.</param>
public sealed record HubInvokeRequest(string Method, IReadOnlyList<JsonElement> Arguments);

/// <param name="Result">The method's result; absent for a method with none.</param>
/// <param name="Error">The hub failed the call: the error's type and message, as the hub gave them.</param>
public sealed record HubInvokeResult(JsonElement? Result = null, HubError? Error = null);

public sealed record HubClientStream(string StreamId);

/// <summary>An item of a stream started with <c>clients/{id}/streams</c>, or its end.</summary>
public sealed record HubClientStreamItem(string ClientId, string StreamId, JsonElement? Item = null, bool End = false, HubError? Error = null);

/// <summary>The host raised an event on this client.</summary>
public sealed record HubClientPush(string ClientId, string Event, IReadOnlyList<JsonElement> Arguments);

/// <summary>A client connected, or disconnected and why.</summary>
public sealed record HubClientChanged(string ClientId, HubClientStatus Status, HubTransports Transport, HubDisconnectReason? Reason = null, string? Message = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HubDefinition))]
[JsonSerializable(typeof(HubInfo))]
[JsonSerializable(typeof(IReadOnlyList<HubInfo>))]
[JsonSerializable(typeof(HubStartRequest))]
[JsonSerializable(typeof(HubStopRequest))]
[JsonSerializable(typeof(HubConnectionInfo))]
[JsonSerializable(typeof(IReadOnlyList<HubConnectionInfo>))]
[JsonSerializable(typeof(HubCall))]
[JsonSerializable(typeof(HubCallReply))]
[JsonSerializable(typeof(HubPushRequest))]
[JsonSerializable(typeof(HubStreamEnd))]
[JsonSerializable(typeof(HubStreamClosed))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(HubClientRequest))]
[JsonSerializable(typeof(HubClientInfo))]
[JsonSerializable(typeof(IReadOnlyList<HubClientInfo>))]
[JsonSerializable(typeof(HubDiscoverRequest))]
[JsonSerializable(typeof(HubHostSeen))]
[JsonSerializable(typeof(HubConnectRequest))]
[JsonSerializable(typeof(HubInvokeRequest))]
[JsonSerializable(typeof(HubInvokeResult))]
[JsonSerializable(typeof(HubClientStream))]
[JsonSerializable(typeof(HubClientStreamItem))]
[JsonSerializable(typeof(HubClientPush))]
[JsonSerializable(typeof(HubClientChanged))]
[JsonSerializable(typeof(JsonElement))]
public partial class HubsJsonContext : JsonSerializerContext;
