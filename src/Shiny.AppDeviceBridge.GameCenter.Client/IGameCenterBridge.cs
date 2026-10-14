using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.GameCenter.Client;

/// <summary>
/// Achievements, leaderboards and friends through Apple Game Center (iOS, Mac Catalyst, macOS) and Google Play Games
/// (Android). Achievements and leaderboards are named by the logical keys the app registered with
/// <c>AddGameCenterBridge(o => o.AddAchievement(…))</c>, or by their platform ids.
/// <para>
/// Unlocks, progress, reveals and scores are recorded on the device first and sent when the player is signed in and
/// online, so they succeed offline and signed out. Progress is an absolute step count, so a retry never counts twice.
/// </para>
/// <para>
/// Service failures answer with the error code the page switches on: 401 <c>not_authenticated</c>, 404 <c>invalid_id</c>,
/// 409 <c>no_user_interface</c>, 503 <c>network</c>, <c>unavailable</c> or <c>not_configured</c>, and 502
/// <c>game_service_failed</c>. A device with no game service answers 501.
/// </para>
/// </summary>
[BridgeClient("games", typeof(GameCenterJsonContext))]
public interface IGameCenterBridge
{
    /// <summary>The service on this device, the signed-in player, and how much is waiting to be sent.</summary>
    [BridgeGet]
    Task<GameCenterStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs the player in, showing the service's sign-in UI when it is needed. Declining is a result, not an error:
    /// <see cref="SignInResult.Player"/> is null.
    /// </summary>
    [BridgePost("signin")]
    Task<SignInResult> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Every achievement, merged with progress not sent yet. Needs a signed-in player.</summary>
    /// <param name="forceReload">Skip the service's cache.</param>
    [BridgeGet("achievements")]
    Task<IReadOnlyList<Achievement>> GetAchievementsAsync(bool? forceReload = null, CancellationToken cancellationToken = default);

    /// <summary>Completes an achievement.</summary>
    [BridgePost("achievements/{key}/unlock")]
    Task UnlockAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets an incremental achievement to an absolute step count. Progress never goes backwards.</summary>
    [BridgePost("achievements/{key}/progress")]
    Task SetProgressAsync(string key, AchievementProgressRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds steps to an incremental achievement, counted from the furthest progress this device knows of.</summary>
    [BridgePost("achievements/{key}/increment")]
    Task IncrementAsync(string key, AchievementIncrementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Makes a hidden achievement visible. Google only; Game Center has no reveal, so it does nothing there.</summary>
    [BridgePost("achievements/{key}/reveal")]
    Task RevealAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Opens the service's achievements UI. Completes once it is open, not when the player closes it.</summary>
    [BridgePost("achievements/show")]
    Task ShowAchievementsAsync(CancellationToken cancellationToken = default);

    /// <summary>Submits a score. The service decides whether it is a new best.</summary>
    [BridgePost("leaderboards/{key}/scores")]
    Task SubmitScoreAsync(string key, SubmitScoreRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The top scores of a leaderboard, for everyone or for the player's friends. Needs a signed-in player, and friends
    /// consent for <see cref="LeaderboardScope.Friends"/>.
    /// </summary>
    [BridgePost("leaderboards/{key}/query")]
    Task<IReadOnlyList<LeaderboardEntry>> GetScoresAsync(string key, LeaderboardScoresRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens one leaderboard, or all of them.</summary>
    [BridgePost("leaderboards/show")]
    Task ShowLeaderboardAsync(ShowLeaderboardRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The player's friends who can see this game, asking for consent the first time. Apple needs
    /// <c>NSGKFriendListUsageDescription</c> in Info.plist. Google shows a dialog and answers
    /// <see cref="FriendsAccessStatus.ConsentRequested"/>: ask again when the player is back.
    /// </summary>
    [BridgeGet("friends")]
    Task<FriendsResult> GetFriendsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends what is queued now and answers with what is left. It runs on its own after every change, at sign-in and when
    /// connectivity returns, so call it only to wait for the queue to drain.
    /// </summary>
    [BridgePost("flush")]
    Task<GameCenterStatus> FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>The player signed in, signed out or switched accounts.</summary>
    [BridgeEvent("games.player")]
    Task<IAsyncDisposable> OnPlayerChangedAsync(Func<PlayerChanged, Task> handler);
}
