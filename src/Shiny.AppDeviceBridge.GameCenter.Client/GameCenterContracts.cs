using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.GameCenter.Client;

/// <summary>The game service behind the bridge on this device.</summary>
public enum GameServicePlatform
{
    /// <summary>Apple Game Center (GameKit) on iOS, Mac Catalyst and macOS.</summary>
    AppleGameCenter,

    /// <summary>Google Play Games Services v2 on Android.</summary>
    GooglePlayGames,

    /// <summary>A provider the app registered itself.</summary>
    Custom
}

/// <summary>Whose scores to load from a leaderboard.</summary>
public enum LeaderboardScope
{
    /// <summary>Everyone.</summary>
    Global,

    /// <summary>The player's friends, and the player. Needs friends-list consent.</summary>
    Friends
}

/// <summary>The window of scores to load from a leaderboard.</summary>
public enum LeaderboardTimeScope { Today, Week, AllTime }

/// <summary>Whether the player has let this game see their friends.</summary>
public enum FriendsAccessStatus
{
    /// <summary>Allowed: <see cref="FriendsResult.Friends"/> is filled in.</summary>
    Granted,

    /// <summary>The player said no. Apple: they change it in Settings. Google: ask again later.</summary>
    Denied,

    /// <summary>Apple: blocked by parental controls or device management.</summary>
    Restricted,

    /// <summary>Google: the consent dialog was shown. Ask again once the player is back in the app.</summary>
    ConsentRequested,

    /// <summary>The service has no friends list on this device.</summary>
    Unavailable
}

/// <summary>A player of the game service.</summary>
/// <param name="Id">Apple <c>gamePlayerID</c> (stable for this game) or Google <c>playerId</c>.</param>
/// <param name="DisplayName">The player's display name or alias.</param>
public sealed record GamePlayer(string Id, string DisplayName, GameServicePlatform Platform);

/// <summary>The game service on this device.</summary>
/// <param name="Player">The signed-in player, or null.</param>
/// <param name="PendingCount">Unlocks, progress and scores recorded on the device that the service has not accepted yet.</param>
public sealed record GameCenterStatus(GameServicePlatform Platform, GamePlayer? Player, int PendingCount);

/// <summary>How a sign-in ended.</summary>
/// <param name="Player">
/// Null when the player declined — or, on Apple, when Game Center has stopped offering its sheet after repeated cancels,
/// and the player has to sign in from Settings.
/// </param>
public sealed record SignInResult(GamePlayer? Player);

/// <summary>The signed-in player changed: signed in, signed out or switched accounts.</summary>
/// <param name="Player">Null when signed out.</param>
public sealed record PlayerChanged(GamePlayer? Player);

/// <summary>An achievement, merged with progress recorded on this device that the service has not accepted yet.</summary>
/// <param name="Key">The app's logical key, or the platform id when the app did not register the achievement.</param>
/// <param name="PlatformId">The Game Center or Play Games achievement id.</param>
/// <param name="Title">Localized, when the service supplied one.</param>
/// <param name="Description">Localized, when the service supplied one.</param>
/// <param name="CurrentSteps">Never lower than what was recorded on this device.</param>
/// <param name="TotalSteps">1 for an unlock-once achievement.</param>
/// <param name="PercentComplete">0 to 100.</param>
/// <param name="IsHidden">Hidden from the player until it has progress (Apple) or is revealed (Google).</param>
public sealed record Achievement(
    string Key,
    string PlatformId,
    string? Title,
    string? Description,
    int CurrentSteps,
    int TotalSteps,
    double PercentComplete,
    bool IsUnlocked,
    bool IsHidden
);

/// <param name="Steps">An absolute step count, never a delta. A value at or below the current progress is ignored; one above the total is clamped.</param>
public sealed record AchievementProgressRequest(int Steps);

/// <param name="Steps">Steps to add, at least 1.</param>
public sealed record AchievementIncrementRequest(int Steps = 1);

public sealed record SubmitScoreRequest(long Score);

/// <param name="MaxResults">1 to 100; the top 25 when not given.</param>
public sealed record LeaderboardScoresRequest(
    LeaderboardScope Scope = LeaderboardScope.Global,
    LeaderboardTimeScope TimeScope = LeaderboardTimeScope.AllTime,
    int MaxResults = 25
);

/// <summary>One row of a leaderboard.</summary>
/// <param name="FormattedScore">The score formatted the way the leaderboard is set up to show it.</param>
/// <param name="IsLocalPlayer">The signed-in player's own row.</param>
public sealed record LeaderboardEntry(GamePlayer Player, long Score, string? FormattedScore, int Rank, bool IsLocalPlayer);

/// <summary>Which leaderboard UI to open.</summary>
/// <param name="LeaderboardKey">One leaderboard, or every leaderboard when null.</param>
public sealed record ShowLeaderboardRequest(string? LeaderboardKey = null);

public sealed record FriendsResult(FriendsAccessStatus Status, IReadOnlyList<GamePlayer> Friends);

/// <summary>Serialization for every game center contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GameCenterStatus))]
[JsonSerializable(typeof(SignInResult))]
[JsonSerializable(typeof(PlayerChanged))]
[JsonSerializable(typeof(IReadOnlyList<Achievement>))]
[JsonSerializable(typeof(AchievementProgressRequest))]
[JsonSerializable(typeof(AchievementIncrementRequest))]
[JsonSerializable(typeof(SubmitScoreRequest))]
[JsonSerializable(typeof(LeaderboardScoresRequest))]
[JsonSerializable(typeof(IReadOnlyList<LeaderboardEntry>))]
[JsonSerializable(typeof(ShowLeaderboardRequest))]
[JsonSerializable(typeof(FriendsResult))]
public partial class GameCenterJsonContext : JsonSerializerContext;
