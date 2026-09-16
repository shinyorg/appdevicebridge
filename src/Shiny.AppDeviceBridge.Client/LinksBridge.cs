namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// Deep links and universal links. A link arrives as an event while the page is open, and waits as the pending link
/// otherwise — so a page reads the pending link once it boots, and listens after.
/// </summary>
[BridgeClient("links", typeof(AppDeviceBridgeJsonContext))]
public interface ILinksBridge
{
    /// <summary>The latest link, left in place. Null when there is none.</summary>
    [BridgeGet("pending")]
    Task<AppLink?> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>The latest link, taken off the host so it is acted on once. Null when there is none.</summary>
    [BridgeDelete("pending")]
    Task<AppLink?> ConsumeAsync(CancellationToken cancellationToken = default);

    /// <summary>A link arrived while the page is open. It is also left pending until consumed.</summary>
    [BridgeEvent("app.link")]
    Task<IAsyncDisposable> OnLinkAsync(Func<AppLink, Task> handler);
}

/// <summary>A link the app was opened with.</summary>
/// <param name="Url">The link as the OS delivered it.</param>
/// <param name="Route">The page route it maps to — always a local path, safe to navigate to as it is.</param>
/// <param name="ReceivedAt">When the app received it.</param>
public sealed record AppLink(string Url, string Route, DateTimeOffset ReceivedAt);
