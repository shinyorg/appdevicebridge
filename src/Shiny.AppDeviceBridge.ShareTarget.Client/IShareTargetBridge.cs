using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.ShareTarget.Client;

/// <summary>
/// Content other apps shared to this one: text, a link, files. Each share waits in a queue until the page removes it,
/// so a share that arrived while no page was open — or that launched the app — is read once the page boots, and later
/// ones arrive as an event too.
/// </summary>
[BridgeClient("sharetarget", typeof(ShareTargetJsonContext))]
public interface IShareTargetBridge
{
    /// <summary>The shares waiting for the page, oldest first.</summary>
    [BridgeGet("pending")]
    Task<IReadOnlyList<SharedContent>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>One waiting share. Fails with 404 when there is no share by the id.</summary>
    [BridgeGet("pending/{id}")]
    Task<SharedContent> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a share off the queue and deletes its files — move a file elsewhere through the files bridge first to keep
    /// it. Fails with 404 when there is no share by the id.
    /// </summary>
    [BridgeDelete("pending/{id}")]
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>A share arrived while the page is open. It also waits in the queue until removed.</summary>
    [BridgeEvent("sharetarget.received")]
    Task<IAsyncDisposable> OnReceivedAsync(Func<SharedContent, Task> handler);
}
