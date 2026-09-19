using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Wearables.Client;

/// <summary>
/// The companion app on a paired Apple Watch or Wear OS device. Bodies are JSON: what the page sends reaches the watch
/// as UTF-8 JSON, and what the watch sends reaches the page as JSON — or, when it is not JSON, as a base64 string with
/// <c>binary</c> set. Files are <see cref="BridgeFile"/>s, so they move through the files bridge's roots.
/// </summary>
[BridgeClient("wearables", typeof(WearablesJsonContext))]
public interface IWearablesBridge
{
    /// <summary>Whether a wearable is paired, has the companion app, and is reachable for live messages.</summary>
    [BridgeGet]
    Task<WearableStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a live message and waits for the companion app's reply. Fails with 409 (<c>not_reachable</c>) when no
    /// wearable is reachable — a message never queues; use <see cref="TransferAsync"/> for that.
    /// </summary>
    [BridgePost("messages")]
    Task<WearableReply> SendMessageAsync(WearableMessageRequest message, CancellationToken cancellationToken = default);

    /// <summary>The context this app last shared and the context the wearable last shared.</summary>
    [BridgeGet("context")]
    Task<WearableContextState> GetContextAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the context shared with the wearable. Only the latest value is kept; it arrives when the wearable can take it.</summary>
    [BridgePut("context")]
    Task UpdateContextAsync(WearableContextUpdate context, CancellationToken cancellationToken = default);

    /// <summary>Queues data for the wearable, delivered in order even if it is out of range or its app is not running.</summary>
    [BridgePost("transfers")]
    Task<WearableTransferTicket> TransferAsync(WearableTransferRequest transfer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a file from a file root for the wearable. Fails with 404 when the file does not exist. The platform reads it
    /// while it transfers, so leave it in place until <see cref="OnTransferCompletedAsync"/> reports the id.
    /// </summary>
    [BridgePost("files")]
    Task<WearableTransferTicket> SendFileAsync(WearableFileRequest file, CancellationToken cancellationToken = default);

    /// <summary>Outgoing transfers and files the wearable has not received yet.</summary>
    [BridgeGet("transfers")]
    Task<IReadOnlyList<WearablePendingTransfer>> GetPendingTransfersAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels an outgoing transfer or file. Fails with 404 when nothing pending has the id.</summary>
    [BridgeDelete("transfers/{id}")]
    Task CancelTransferAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The wearable was paired or unpaired, installed or removed the app, or became reachable or unreachable.</summary>
    [BridgeEvent("wearables.status")]
    Task<IAsyncDisposable> OnStatusChangedAsync(Func<WearableStatus, Task> handler);

    /// <summary>
    /// A live message arrived. To answer it, register a <c>wearables.message</c> handler instead (page or background.js):
    /// what the handler returns is the reply.
    /// </summary>
    [BridgeEvent("wearables.message")]
    Task<IAsyncDisposable> OnMessageAsync(Func<WearableMessage, Task> handler);

    /// <summary>The wearable shared new context. Also handed to a <c>wearables.context</c> handler.</summary>
    [BridgeEvent("wearables.context")]
    Task<IAsyncDisposable> OnContextAsync(Func<WearableContext, Task> handler);

    /// <summary>A queued transfer arrived. Also handed to a <c>wearables.transfer</c> handler.</summary>
    [BridgeEvent("wearables.transfer")]
    Task<IAsyncDisposable> OnTransferAsync(Func<WearableTransfer, Task> handler);

    /// <summary>A file arrived and was filed into a file root. Also handed to a <c>wearables.file</c> handler.</summary>
    [BridgeEvent("wearables.file")]
    Task<IAsyncDisposable> OnFileAsync(Func<WearableReceivedFile, Task> handler);

    /// <summary>An outgoing transfer or file was delivered, failed, or was cancelled.</summary>
    [BridgeEvent("wearables.completed")]
    Task<IAsyncDisposable> OnTransferCompletedAsync(Func<WearableTransferCompleted, Task> handler);
}
