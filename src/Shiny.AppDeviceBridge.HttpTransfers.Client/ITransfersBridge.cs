using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.HttpTransfers.Client;

/// <summary>
/// Background HTTP transfers that keep going while the app is suspended: downloads into a file root and uploads from
/// one. Only the web app's own transfers are listed or cancelled. A download lands at its path only once it completes.
/// </summary>
[BridgeClient("transfers", typeof(TransfersJsonContext))]
public interface ITransfersBridge
{
    /// <summary>The web app's transfers.</summary>
    [BridgeGet]
    Task<TransferList> GetTransfersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a transfer. Fails with 403 for a URL the app does not allow, 404 for an unknown root or a missing upload
    /// file, 409 when a download's destination exists and overwrite is off, 429 when too many are queued.
    /// </summary>
    [BridgePost]
    Task<TransferInfo> QueueAsync(TransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels every web app transfer, and none of the native app's.</summary>
    [BridgeDelete]
    Task CancelAllAsync(CancellationToken cancellationToken = default);

    /// <summary>One transfer. Fails with 404 when there is no such transfer.</summary>
    [BridgeGet("{id}")]
    Task<TransferInfo> GetTransferAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Cancels a transfer.</summary>
    [BridgeDelete("{id}")]
    Task CancelAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Pauses a transfer.</summary>
    [BridgePost("{id}/pause")]
    Task PauseAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused transfer.</summary>
    [BridgePost("{id}/resume")]
    Task ResumeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>A transfer moved on or changed state. Throttled per transfer; a state change always comes through.</summary>
    [BridgeEvent("transfer.progress")]
    Task<IAsyncDisposable> OnProgressAsync(Func<TransferInfo, Task> handler);

    /// <summary>A transfer completed; a download is in place at its path.</summary>
    [BridgeEvent("transfer.completed")]
    Task<IAsyncDisposable> OnCompletedAsync(Func<TransferInfo, Task> handler);

    /// <summary>A transfer failed.</summary>
    [BridgeEvent("transfer.failed")]
    Task<IAsyncDisposable> OnFailedAsync(Func<TransferInfo, Task> handler);

    /// <summary>A transfer was cancelled.</summary>
    [BridgeEvent("transfer.cancelled")]
    Task<IAsyncDisposable> OnCancelledAsync(Func<TransferInfo, Task> handler);
}
