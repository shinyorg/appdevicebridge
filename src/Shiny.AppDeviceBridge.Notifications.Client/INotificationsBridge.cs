using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Notifications.Client;

/// <summary>
/// Local notifications: permission, sending now or on a schedule, repeating or on entering an area, the pending list,
/// cancelling, the app badge and Android channels. What a platform can do is in <see cref="NotificationStatus"/>; a
/// feature it lacks fails with 501.
/// </summary>
[BridgeClient("notifications", typeof(NotificationsJsonContext))]
public interface INotificationsBridge
{
    /// <summary>Permission and what this platform supports.</summary>
    [BridgeGet]
    Task<NotificationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests permission, optionally for time-sensitive and location-aware notifications too.</summary>
    [BridgePost("access")]
    Task<NotificationAccessResult> RequestAccessAsync(NotificationAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends or schedules a notification. Fails with 400 for an invalid one, such as a date already past.</summary>
    [BridgePost("send")]
    Task<NotificationSent> SendAsync(NotificationRequest notification, CancellationToken cancellationToken = default);

    /// <summary>Scheduled notifications that have not been shown yet.</summary>
    [BridgeGet("pending")]
    Task<IReadOnlyList<PendingNotification>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels pending notifications, clears displayed ones, or both.</summary>
    [BridgeDelete]
    Task CancelAllAsync(NotificationCancelScope scope = NotificationCancelScope.All, CancellationToken cancellationToken = default);

    /// <summary>Cancels one notification.</summary>
    [BridgeDelete("{id}")]
    Task CancelAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The app icon's badge number.</summary>
    [BridgeGet("badge")]
    Task<NotificationBadge> GetBadgeAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the app icon's badge number, 0–99,999.</summary>
    [BridgePut("badge")]
    Task SetBadgeAsync(NotificationBadge badge, CancellationToken cancellationToken = default);

    /// <summary>The notification channels.</summary>
    [BridgeGet("channels")]
    Task<IReadOnlyList<NotificationChannel>> GetChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a channel.</summary>
    [BridgePost("channels")]
    Task<NotificationChannel> AddChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default);

    /// <summary>Removes a channel.</summary>
    [BridgeDelete("channels/{identifier}")]
    Task RemoveChannelAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>The user tapped a notification, or one of its actions.</summary>
    [BridgeEvent("notification.entry")]
    Task<IAsyncDisposable> OnEntryAsync(Func<NotificationEvent, Task> handler);

    /// <summary>A notification arrived while the app was open. Apple platforms only.</summary>
    [BridgeEvent("notification.received")]
    Task<IAsyncDisposable> OnReceivedAsync(Func<NotificationEvent, Task> handler);
}
