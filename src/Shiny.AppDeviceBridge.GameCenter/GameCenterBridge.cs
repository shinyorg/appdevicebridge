using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.GameCenter.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.GameCenter;

namespace Shiny.AppDeviceBridge.GameCenter;

public static class GameCenterBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/games</c> and registers Shiny's game center manager — there is nothing else to call.
    /// <code>
    /// bridge.AddGameCenterBridge(o => o
    ///     .AddAchievement("first-win", appleId: "com.acme.firstwin", googleId: "CgkI…")
    ///     .AddLeaderboard("high-score", appleId: "com.acme.highscore", googleId: "CgkI…"));
    /// </code>
    /// <para>
    /// The page names achievements and leaderboards by these keys. An app that already called <c>AddGameCenter</c> keeps
    /// its registration and options, and <paramref name="configure"/> is not used. Apple (iOS, Mac Catalyst, macOS) and
    /// Android; every other platform answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddGameCenterBridge<TBuilder>(this TBuilder bridge, Action<Native.GameCenterOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (!bridge.Services.Any(x => x.ServiceType == typeof(Native.IGameCenterManager)))
            bridge.Services.AddGameCenter(configure);

        bridge.AddBridge<GameCenterBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/games</c> over <see cref="Native.IGameCenterManager"/>.
/// <code>
/// GET  /_bridge/games                                  { "platform": "AppleGameCenter", "player": {…}, "pendingCount": 0 }
/// POST /_bridge/games/signin                           { "player": {…} | null }
/// GET  /_bridge/games/achievements?forceReload=true    [ { "key", "platformId", "currentSteps", "totalSteps", … } ]
/// POST /_bridge/games/achievements/{key}/unlock
/// POST /_bridge/games/achievements/{key}/progress      { "steps": 7 }
/// POST /_bridge/games/achievements/{key}/increment     { "steps": 1 }
/// POST /_bridge/games/achievements/{key}/reveal
/// POST /_bridge/games/achievements/show
/// POST /_bridge/games/leaderboards/{key}/scores        { "score": 1200 }
/// POST /_bridge/games/leaderboards/{key}/query         { "scope": "Friends", "timeScope": "Week", "maxResults": 25 }
/// POST /_bridge/games/leaderboards/show                { "leaderboardKey": "high-score" }
/// GET  /_bridge/games/friends                          { "status": "Granted", "friends": [ … ] }
/// POST /_bridge/games/flush
///
/// event: games.player
/// </code>
/// </summary>
public sealed class GameCenterBridge(IServiceProvider services) : IWebAppBridge, IDisposable
{
    internal const string PlayerEvent = "games.player";
    const int MaxScores = 100;

    readonly Native.IGameCenterManager? manager = services.GetOptionalService<Native.IGameCenterManager>();
    WebAppEventSource<PlayerChanged>? playerChanged;

    public string Name => "games";

    // Shiny.GameCenter registers a local-only manager where there is no game service; there is nothing to bridge to.
    public bool IsSupported => this.manager is { Platform: not Native.GameServicePlatform.None };

    public void Map(WebAppBridgeRoutes routes)
    {
        this.playerChanged = routes.Events.Source(PlayerEvent, GameCenterJsonContext.Default.PlayerChanged);
        if (this.IsSupported)
        {
            // The server is composed again when it restarts on resume; subscribe once.
            this.manager!.PlayerChanged -= this.OnPlayerChanged;
            this.manager.PlayerChanged += this.OnPlayerChanged;
        }

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/signin", this.SignInAsync)
            .MapGet("/achievements", this.AchievementsAsync)
            .MapPost("/achievements/show", this.ShowAchievementsAsync)
            .MapPost("/achievements/{key}/unlock", this.UnlockAsync)
            .MapPost("/achievements/{key}/progress", this.ProgressAsync)
            .MapPost("/achievements/{key}/increment", this.IncrementAsync)
            .MapPost("/achievements/{key}/reveal", this.RevealAsync)
            .MapPost("/leaderboards/show", this.ShowLeaderboardAsync)
            .MapPost("/leaderboards/{key}/scores", this.SubmitScoreAsync)
            .MapPost("/leaderboards/{key}/query", this.ScoresAsync)
            .MapGet("/friends", this.FriendsAsync)
            .MapPost("/flush", this.FlushAsync);
    }

    void OnPlayerChanged(object? sender, Native.GamePlayer? player)
        => this.playerChanged?.Publish(new PlayerChanged(player is null ? null : ToContract(player)));

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, m =>
        WebAppBridgeResults.Json(context, Status(m), GameCenterJsonContext.Default.GameCenterStatus).AsTask()
    );

    ValueTask SignInAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // The sign-in sheet is UI.
        var player = await this.OnMainThread(() => m.SignInAsync(context.RequestAborted));
        var result = new SignInResult(player is null ? null : ToContract(player));
        await WebAppBridgeResults.Json(context, result, GameCenterJsonContext.Default.SignInResult);
    });

    ValueTask AchievementsAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var forceReload = Boolean.TryParse(context.Request.Query["forceReload"].ToString(), out var force) && force;
        var achievements = await m.GetAchievementsAsync(forceReload, context.RequestAborted);
        IReadOnlyList<Achievement> list = [.. achievements.Select(ToContract)];
        await WebAppBridgeResults.Json(context, list, GameCenterJsonContext.Default.IReadOnlyListAchievement);
    });

    ValueTask UnlockAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        await m.UnlockAsync(key, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ProgressAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, GameCenterJsonContext.Default.AchievementProgressRequest) is not { Steps: >= 0 } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"steps\": 0 or more }.");
            return;
        }
        await m.SetProgressAsync(key, body.Steps, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask IncrementAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, GameCenterJsonContext.Default.AchievementIncrementRequest) ?? new AchievementIncrementRequest();
        if (body.Steps < 1)
        {
            await WebAppBridgeResults.BadRequest(context, "steps is at least 1.");
            return;
        }
        await m.IncrementAsync(key, body.Steps, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask RevealAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        await m.RevealAsync(key, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ShowAchievementsAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await this.OnMainThread(() => m.ShowAchievementsAsync(context.RequestAborted));
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask SubmitScoreAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, GameCenterJsonContext.Default.SubmitScoreRequest) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"score\": 1200 }.");
            return;
        }
        await m.SubmitScoreAsync(key, body.Score, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ScoresAsync(HttpContext context) => this.RunKeyedAsync(context, async (m, key) =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, GameCenterJsonContext.Default.LeaderboardScoresRequest) ?? new LeaderboardScoresRequest();
        if (body.MaxResults is < 1 or > MaxScores)
        {
            await WebAppBridgeResults.BadRequest(context, $"maxResults is 1 to {MaxScores}.");
            return;
        }

        var entries = await m.GetScoresAsync(
            key,
            BridgeEnum.Convert<LeaderboardScope, Native.LeaderboardScope>(body.Scope),
            BridgeEnum.Convert<LeaderboardTimeScope, Native.LeaderboardTimeScope>(body.TimeScope),
            body.MaxResults,
            context.RequestAborted
        );
        IReadOnlyList<LeaderboardEntry> list = [.. entries.Select(x => new LeaderboardEntry(ToContract(x.Player), x.Score, x.FormattedScore, x.Rank, x.IsLocalPlayer))];
        await WebAppBridgeResults.Json(context, list, GameCenterJsonContext.Default.IReadOnlyListLeaderboardEntry);
    });

    ValueTask ShowLeaderboardAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // The body is optional: without one, every leaderboard opens.
        var body = await WebAppBridgeResults.ReadBodyAsync(context, GameCenterJsonContext.Default.ShowLeaderboardRequest);
        await this.OnMainThread(() => m.ShowLeaderboardAsync(body?.LeaderboardKey, context.RequestAborted));
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask FriendsAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // Both services can ask the player for consent.
        var result = await this.OnMainThread(() => m.GetFriendsAsync(context.RequestAborted));
        var contract = new FriendsResult(
            BridgeEnum.Convert<Native.FriendsAccessStatus, FriendsAccessStatus>(result.Status),
            [.. result.Friends.Select(ToContract)]
        );
        await WebAppBridgeResults.Json(context, contract, GameCenterJsonContext.Default.FriendsResult);
    });

    ValueTask FlushAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await m.FlushAsync(context.RequestAborted);
        await WebAppBridgeResults.Json(context, Status(m), GameCenterJsonContext.Default.GameCenterStatus);
    });

    Task<T> OnMainThread<T>(Func<Task<T>> action) => services.GetRequiredService<IWebAppMainThread>().InvokeAsync(action);

    Task OnMainThread(Func<Task> action) => this.OnMainThread(async () =>
    {
        await action();
        return true;
    });

    ValueTask RunKeyedAsync(HttpContext context, Func<Native.IGameCenterManager, string, Task> action) => this.RunAsync(context, async m =>
    {
        var key = context.Request.RouteValues["key"] as string;
        if (String.IsNullOrWhiteSpace(key))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected an achievement or leaderboard key.");
            return;
        }
        await action(m, key);
    });

    /// <summary>501 where there is no game service; the service's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.IGameCenterManager, Task> action)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Game services");
            return;
        }

        try
        {
            await action(this.manager!);
        }
        catch (Native.GameCenterException ex)
        {
            var (status, code) = ToError(ex.ErrorCode);
            var message = ex.NativeErrorCode is { } native ? $"{ex.Message} (service code {native})" : ex.Message;
            await WebAppBridgeResults.Error(context, status, code, message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    internal static (int Status, string Code) ToError(Native.GameCenterErrorCode code) => code switch
    {
        Native.GameCenterErrorCode.NotAuthenticated => (StatusCodes.Status401Unauthorized, "not_authenticated"),
        Native.GameCenterErrorCode.Network => (StatusCodes.Status503ServiceUnavailable, "network"),
        Native.GameCenterErrorCode.InvalidId => (StatusCodes.Status404NotFound, "invalid_id"),
        Native.GameCenterErrorCode.NotConfigured => (StatusCodes.Status503ServiceUnavailable, "not_configured"),
        Native.GameCenterErrorCode.Unavailable => (StatusCodes.Status503ServiceUnavailable, "unavailable"),
        Native.GameCenterErrorCode.NoUserInterface => (StatusCodes.Status409Conflict, "no_user_interface"),
        _ => (StatusCodes.Status502BadGateway, "game_service_failed")
    };

    static GameCenterStatus Status(Native.IGameCenterManager m) => new(
        ToContract(m.Platform),
        m.Player is { } p ? ToContract(p) : null,
        m.PendingCount
    );

    static GameServicePlatform ToContract(Native.GameServicePlatform platform) => platform switch
    {
        Native.GameServicePlatform.AppleGameCenter => GameServicePlatform.AppleGameCenter,
        Native.GameServicePlatform.GooglePlayGames => GameServicePlatform.GooglePlayGames,
        _ => GameServicePlatform.Custom
    };

    internal static GamePlayer ToContract(Native.GamePlayer player) => new(player.Id, player.DisplayName, ToContract(player.Platform));

    internal static Achievement ToContract(Native.Achievement a) => new(
        a.Key,
        a.PlatformId,
        a.Title,
        a.Description,
        a.CurrentSteps,
        a.TotalSteps,
        a.PercentComplete,
        a.IsUnlocked,
        a.IsHidden
    );

    public void Dispose()
    {
        if (this.IsSupported)
            this.manager!.PlayerChanged -= this.OnPlayerChanged;
    }
}
