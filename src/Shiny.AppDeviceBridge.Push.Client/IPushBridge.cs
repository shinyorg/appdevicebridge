using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Push.Client;

/// <summary>Push notifications: registration, the token, tags, and what arrives.</summary>
[BridgeClient("push", typeof(PushJsonContext))]
public interface IPushBridge
{
    /// <summary>The permission state and the current tokens, without prompting.</summary>
    [BridgeGet]
    Task<PushStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests permission if it has not been decided, then registers with the push provider.</summary>
    [BridgePost("registration")]
    Task<PushStatus> RegisterAsync(CancellationToken cancellationToken = default);

    /// <summary>Unregisters from the push provider.</summary>
    [BridgeDelete("registration")]
    Task UnregisterAsync(CancellationToken cancellationToken = default);

    /// <summary>The tags the device is registered with. Fails with 501 where the provider has no tags.</summary>
    [BridgeGet("tags")]
    Task<PushTags> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the device's tags. Fails with 501 where the provider has no tags, 409 when it refuses them.</summary>
    [BridgePut("tags")]
    Task SetTagsAsync(PushTags tags, CancellationToken cancellationToken = default);

    /// <summary>The provider issued a new token.</summary>
    [BridgeEvent("push.token")]
    Task<IAsyncDisposable> OnTokenAsync(Func<PushTokenEvent, Task> handler);

    /// <summary>The device was unregistered.</summary>
    [BridgeEvent("push.unregistered")]
    Task<IAsyncDisposable> OnUnregisteredAsync(Func<PushTokenEvent, Task> handler);

    /// <summary>A push arrived. Raised only when the app hands pushes to the web app.</summary>
    [BridgeEvent("push.received")]
    Task<IAsyncDisposable> OnReceivedAsync(Func<PushPayload, Task> handler);

    /// <summary>The user opened the app from a push. Raised only when the app hands pushes to the web app.</summary>
    [BridgeEvent("push.entry")]
    Task<IAsyncDisposable> OnEntryAsync(Func<PushPayload, Task> handler);
}
