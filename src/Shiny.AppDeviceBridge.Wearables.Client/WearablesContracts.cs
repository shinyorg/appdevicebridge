using System.Text.Json;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Wearables.Client;

/// <summary>A connected wearable.</summary>
/// <param name="Id">The node id — always <c>watch</c> on iOS, which has one active watch.</param>
/// <param name="DisplayName">The name the platform gives it.</param>
/// <param name="IsNearby">Directly connected rather than reached through the cloud.</param>
/// <param name="HasApp">Runs the companion app.</param>
public sealed record WearableNode(string Id, string DisplayName, bool IsNearby, bool HasApp);

/// <summary>Whether there is a wearable to talk to.</summary>
/// <param name="Supported">False where the platform has no wearable API — anything but iOS and Android, an iPad, an Android device without the Wear OS app.</param>
/// <param name="Paired">A wearable is paired (iOS) or connected (Android).</param>
/// <param name="AppInstalled">The companion app is installed on it.</param>
/// <param name="Reachable">A live message can be sent now. Context, transfers and files queue and do not need this.</param>
/// <param name="Nodes">The wearables the platform reports.</param>
public sealed record WearableStatus(bool Supported, bool Paired, bool AppInstalled, bool Reachable, IReadOnlyList<WearableNode> Nodes);

/// <summary>A live message to send.</summary>
/// <param name="Path">What it is about — <c>sync</c>, <c>workout/start</c>.</param>
/// <param name="Data">The body, any JSON value. Keep it small: about 64 KB on iOS, 100 KB on Wear OS.</param>
/// <param name="NodeId">Wear OS only: the node to send to. Omitted, the nearby node that runs the companion app.</param>
public sealed record WearableMessageRequest(string Path, JsonElement? Data = null, string? NodeId = null);

/// <summary>The companion app's reply to a message.</summary>
/// <param name="Data">The reply; JSON null when the companion app replied with nothing.</param>
/// <param name="Binary">The reply was not JSON: <paramref name="Data"/> is its bytes as a base64 string.</param>
public sealed record WearableReply(JsonElement Data, bool Binary);

/// <summary>A live message from the wearable.</summary>
/// <param name="Path">What it is about.</param>
/// <param name="Data">The body.</param>
/// <param name="Binary">The body was not JSON: <paramref name="Data"/> is its bytes as a base64 string.</param>
/// <param name="NodeId">The node that sent it.</param>
/// <param name="ExpectsReply">The sender waits on a reply — what a <c>wearables.message</c> handler returns.</param>
public sealed record WearableMessage(string Path, JsonElement Data, bool Binary, string? NodeId, bool ExpectsReply);

/// <summary>Shared context.</summary>
/// <param name="Data">The context.</param>
/// <param name="Binary">It was not JSON: <paramref name="Data"/> is its bytes as a base64 string.</param>
/// <param name="NodeId">The node that shared it; null for this app's own.</param>
public sealed record WearableContext(JsonElement Data, bool Binary, string? NodeId);

/// <summary>Both sides' context.</summary>
/// <param name="Sent">What this app last shared, or null.</param>
/// <param name="Received">What the wearable last shared, or null.</param>
public sealed record WearableContextState(WearableContext? Sent, WearableContext? Received);

/// <summary>New context to share.</summary>
public sealed record WearableContextUpdate(JsonElement Data);

/// <summary>Data to queue for the wearable.</summary>
/// <param name="Path">What it is.</param>
/// <param name="Data">Any JSON value.</param>
public sealed record WearableTransferRequest(string Path, JsonElement Data);

/// <summary>A file to queue for the wearable.</summary>
/// <param name="Path">What it is — <c>maps/offline</c>.</param>
/// <param name="File">The file, in a root on disk.</param>
/// <param name="Metadata">Strings the companion app receives with it.</param>
public sealed record WearableFileRequest(string Path, BridgeFile File, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A queued transfer or file.</summary>
/// <param name="Id">What <c>wearables.completed</c> and the pending list report it by.</param>
public sealed record WearableTransferTicket(string Id);

public enum WearableTransferKind
{
    Data,
    File
}

/// <summary>An outgoing transfer or file the wearable has not received yet.</summary>
/// <param name="Id">The id the send returned.</param>
/// <param name="Path">What it is.</param>
/// <param name="Kind">Data or a file.</param>
/// <param name="Progress">0–1 where the platform reports it (iOS files); null otherwise.</param>
public sealed record WearablePendingTransfer(string Id, string Path, WearableTransferKind Kind, double? Progress);

/// <summary>A queued transfer from the wearable.</summary>
public sealed record WearableTransfer(string Id, string Path, JsonElement Data, bool Binary, string? NodeId);

/// <summary>A file from the wearable, filed into a file root.</summary>
/// <param name="Id">The sender's transfer id.</param>
/// <param name="Path">What the sender said it is.</param>
/// <param name="FileName">The file's name on the sender.</param>
/// <param name="File">Where it is now — read, move or delete it through the files bridge.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Metadata">The strings sent with it.</param>
/// <param name="NodeId">The node that sent it.</param>
public sealed record WearableReceivedFile(
    string Id,
    string Path,
    string FileName,
    BridgeFile File,
    long Size,
    IReadOnlyDictionary<string, string> Metadata,
    string? NodeId
);

/// <summary>An outgoing transfer or file finished.</summary>
/// <param name="Id">The id the send returned.</param>
/// <param name="Path">What it was; empty on Wear OS, where the delivery receipt carries only the id.</param>
/// <param name="Kind">Data or a file.</param>
/// <param name="Succeeded">The wearable received it.</param>
/// <param name="Error">Why it failed or was cancelled; null when delivered.</param>
public sealed record WearableTransferCompleted(string Id, string Path, WearableTransferKind Kind, bool Succeeded, string? Error);

/// <summary>Serialization for every wearables contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WearableStatus))]
[JsonSerializable(typeof(WearableMessageRequest))]
[JsonSerializable(typeof(WearableReply))]
[JsonSerializable(typeof(WearableMessage))]
[JsonSerializable(typeof(WearableContext))]
[JsonSerializable(typeof(WearableContextState))]
[JsonSerializable(typeof(WearableContextUpdate))]
[JsonSerializable(typeof(WearableTransferRequest))]
[JsonSerializable(typeof(WearableFileRequest))]
[JsonSerializable(typeof(WearableTransferTicket))]
[JsonSerializable(typeof(IReadOnlyList<WearablePendingTransfer>))]
[JsonSerializable(typeof(WearableTransfer))]
[JsonSerializable(typeof(WearableReceivedFile))]
[JsonSerializable(typeof(WearableTransferCompleted))]
[JsonSerializable(typeof(string))]
public partial class WearablesJsonContext : JsonSerializerContext;
