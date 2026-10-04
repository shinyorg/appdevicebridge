using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.LiveActivities.Client;

/// <summary>
/// Live activities: iOS/iPadOS Live Activities on the Lock Screen and in the Dynamic Island, and Android 16 Live Updates
/// (an ordinary ongoing notification on older Android). The page decides what an activity says; the app's widget
/// decides how it looks.
/// </summary>
[BridgeClient("liveactivities", typeof(LiveActivitiesJsonContext))]
public interface ILiveActivitiesBridge
{
    /// <summary>Whether the user allows live activities, and the device's push-to-start token when it has one.</summary>
    [BridgeGet]
    Task<LiveActivitiesStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the permission live activities need — notification permission on Android. iOS has no prompt, so it
    /// reports the Live Activities switch in Settings.
    /// </summary>
    [BridgePost("access")]
    Task<LiveActivitiesStatus> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>The activities this app has running, newest first.</summary>
    [BridgeGet("activities")]
    Task<IReadOnlyList<LiveActivity>> GetActivitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an activity. Fails with 502 (<c>live_activity_failed</c>) when the system refuses it — the user switched
    /// live activities off, or the app has too many running.
    /// </summary>
    [BridgePost("activities")]
    Task<LiveActivity> StartAsync(LiveActivityStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces a running activity's content. Fails with 404 when no running activity has the id.</summary>
    [BridgePut("activities/{id}")]
    Task UpdateAsync(string id, LiveActivityUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends an activity, optionally showing final content until it is dismissed. Fails with 404 when no running activity has the id.</summary>
    [BridgePost("activities/{id}/end")]
    Task EndAsync(string id, LiveActivityEndRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends every activity this app has running.</summary>
    [BridgeDelete("activities")]
    Task EndAllAsync(CancellationToken cancellationToken = default);

    /// <summary>An activity started — from this app, or from a push-to-start notification. Also handed to a <c>liveactivities.started</c> handler.</summary>
    [BridgeEvent("liveactivities.started")]
    Task<IAsyncDisposable> OnStartedAsync(Func<LiveActivity, Task> handler);

    /// <summary>An activity became stale, ended or was dismissed. Also handed to a <c>liveactivities.state</c> handler.</summary>
    [BridgeEvent("liveactivities.state")]
    Task<IAsyncDisposable> OnStateChangedAsync(Func<LiveActivity, Task> handler);

    /// <summary>
    /// The system issued or rotated the push token for one activity (iOS). Send it to your server: it is the only way to
    /// update that activity remotely. Also handed to a <c>liveactivities.token</c> handler, so background.js can send it
    /// when no page is open.
    /// </summary>
    [BridgeEvent("liveactivities.token")]
    Task<IAsyncDisposable> OnPushTokenAsync(Func<LiveActivityPushToken, Task> handler);

    /// <summary>
    /// The device's push-to-start token appeared or rotated (iOS 17.2+). A server uses it to start an activity while
    /// the app is not running. Also handed to a <c>liveactivities.starttoken</c> handler.
    /// </summary>
    [BridgeEvent("liveactivities.starttoken")]
    Task<IAsyncDisposable> OnPushToStartTokenAsync(Func<LiveActivityPushToStartToken, Task> handler);
}
