using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.GameCenter;
using Shiny.AppDeviceBridge.GameCenter.Client;
using Native = Shiny.GameCenter;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The game center bridge over a fake <see cref="Native.IGameCenterManager"/>: the routes, the mapping both ways, the
/// service's error codes, UI calls on the main thread, and the player-changed event. GameKit and Play Games themselves
/// are Shiny.GameCenter's and need a device and a signed-in player.
/// </summary>
public class GameCenterBridgeTests
{
    static readonly Native.GamePlayer Player = new("G:123", "Allan", Native.GameServicePlatform.AppleGameCenter);

    [Fact]
    public async Task Answers_501_without_a_game_service()
    {
        await using var fixture = await GamesFixture.StartAsync(new FakeGameCenter { Platform = Native.GameServicePlatform.None });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync());
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "games").IsSupported);
    }

    [Fact]
    public async Task Answers_501_without_a_manager()
    {
        await using var fixture = await GamesFixture.StartAsync(null);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SignInAsync())).IsNotSupported);
    }

    [Fact]
    public async Task Reports_the_service_the_player_and_the_queue()
    {
        var manager = new FakeGameCenter { Player = Player, PendingCount = 3 };
        await using var fixture = await GamesFixture.StartAsync(manager);

        var status = await fixture.Client.GetStatusAsync();

        Assert.Equal(new GameCenterStatus(GameServicePlatform.AppleGameCenter, new GamePlayer("G:123", "Allan", GameServicePlatform.AppleGameCenter), 3), status);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "games").IsSupported);
    }

    [Fact]
    public async Task Signs_in_on_the_main_thread_and_a_decline_is_a_result()
    {
        var manager = new FakeGameCenter { SignInAs = Player };
        await using var fixture = await GamesFixture.StartAsync(manager);

        Assert.Equal("G:123", (await fixture.Client.SignInAsync()).Player!.Id);
        Assert.Equal(1, fixture.MainThread.Calls);

        manager.SignInAs = null;
        Assert.Null((await fixture.Client.SignInAsync()).Player);
    }

    [Fact]
    public async Task Loads_achievements_with_their_progress()
    {
        var manager = new FakeGameCenter();
        manager.Achievements.Add(new Native.Achievement("ten-wins", "com.acme.tenwins", "Ten Wins", "Win ten games", 4, 10, false, false));
        await using var fixture = await GamesFixture.StartAsync(manager);

        var achievement = Assert.Single(await fixture.Client.GetAchievementsAsync(forceReload: true));

        Assert.Equal(new Achievement("ten-wins", "com.acme.tenwins", "Ten Wins", "Win ten games", 4, 10, 40d, false, false), achievement);
        Assert.Equal([true], manager.Loads);
    }

    [Fact]
    public async Task Records_unlocks_progress_reveals_and_scores_by_key()
    {
        var manager = new FakeGameCenter();
        await using var fixture = await GamesFixture.StartAsync(manager);

        await fixture.Client.UnlockAsync("first-win");
        await fixture.Client.SetProgressAsync("ten-wins", new AchievementProgressRequest(7));
        await fixture.Client.IncrementAsync("ten-wins", new AchievementIncrementRequest(2));
        await fixture.Client.RevealAsync("secret");
        await fixture.Client.SubmitScoreAsync("high-score", new SubmitScoreRequest(1_200));

        Assert.Equal(["unlock first-win", "progress ten-wins 7", "increment ten-wins 2", "reveal secret", "score high-score 1200"], manager.Calls);
        Assert.Equal(0, fixture.MainThread.Calls);
    }

    [Theory]
    [InlineData("progress", """{ "steps": -1 }""")]
    [InlineData("progress", "not json")]
    [InlineData("increment", """{ "steps": 0 }""")]
    public async Task Refuses_progress_it_cannot_record(string operation, string body)
    {
        var manager = new FakeGameCenter();
        await using var fixture = await GamesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync($"/_bridge/games/achievements/ten-wins/{operation}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.Calls);
    }

    [Fact]
    public async Task Loads_scores_for_the_scope_the_page_asked_for()
    {
        var manager = new FakeGameCenter();
        manager.Scores.Add(new Native.LeaderboardEntry(Player, 1_200, "1,200", 1, true));
        await using var fixture = await GamesFixture.StartAsync(manager);

        var entry = Assert.Single(await fixture.Client.GetScoresAsync("high-score", new LeaderboardScoresRequest(LeaderboardScope.Friends, LeaderboardTimeScope.Week, 10)));

        Assert.Equal(new LeaderboardEntry(new GamePlayer("G:123", "Allan", GameServicePlatform.AppleGameCenter), 1_200, "1,200", 1, true), entry);
        Assert.Equal(("high-score", Native.LeaderboardScope.Friends, Native.LeaderboardTimeScope.Week, 10), Assert.Single(manager.ScoreQueries));

        var tooMany = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetScoresAsync("high-score", new LeaderboardScoresRequest(MaxResults: 101)));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task Opens_the_services_ui_on_the_main_thread()
    {
        var manager = new FakeGameCenter();
        await using var fixture = await GamesFixture.StartAsync(manager);

        await fixture.Client.ShowAchievementsAsync();
        await fixture.Client.ShowLeaderboardAsync(new ShowLeaderboardRequest("high-score"));
        await fixture.Client.ShowLeaderboardAsync(new ShowLeaderboardRequest());

        Assert.Equal(["show achievements", "show leaderboard high-score", "show leaderboard "], manager.Calls);
        Assert.Equal(3, fixture.MainThread.Calls);
    }

    [Fact]
    public async Task Loads_friends_with_the_consent_state()
    {
        var manager = new FakeGameCenter { Friends = new Native.FriendsResult(Native.FriendsAccessStatus.ConsentRequested, []) };
        await using var fixture = await GamesFixture.StartAsync(manager);

        var friends = await fixture.Client.GetFriendsAsync();

        Assert.Equal(FriendsAccessStatus.ConsentRequested, friends.Status);
        Assert.Empty(friends.Friends);
        Assert.Equal(1, fixture.MainThread.Calls);
    }

    [Fact]
    public async Task Flushing_answers_what_is_left()
    {
        var manager = new FakeGameCenter { PendingCount = 4 };
        await using var fixture = await GamesFixture.StartAsync(manager);

        Assert.Equal(0, (await fixture.Client.FlushAsync()).PendingCount);
    }

    [Theory]
    [InlineData(Native.GameCenterErrorCode.NotAuthenticated, HttpStatusCode.Unauthorized, "not_authenticated")]
    [InlineData(Native.GameCenterErrorCode.Network, HttpStatusCode.ServiceUnavailable, "network")]
    [InlineData(Native.GameCenterErrorCode.InvalidId, HttpStatusCode.NotFound, "invalid_id")]
    [InlineData(Native.GameCenterErrorCode.NotConfigured, HttpStatusCode.ServiceUnavailable, "not_configured")]
    [InlineData(Native.GameCenterErrorCode.Unavailable, HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(Native.GameCenterErrorCode.NoUserInterface, HttpStatusCode.Conflict, "no_user_interface")]
    [InlineData(Native.GameCenterErrorCode.Unknown, HttpStatusCode.BadGateway, "game_service_failed")]
    public async Task Service_failures_become_error_codes(Native.GameCenterErrorCode error, HttpStatusCode status, string code)
    {
        var manager = new FakeGameCenter { Throw = new Native.GameCenterException(error, "nope") { NativeErrorCode = "7" } };
        await using var fixture = await GamesFixture.StartAsync(manager);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetAchievementsAsync());

        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, failed.Code);
        Assert.Contains("service code 7", failed.Message);
    }

    [Fact]
    public async Task Player_changes_reach_the_page()
    {
        var manager = new FakeGameCenter();
        await using var fixture = await GamesFixture.StartAsync(manager);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = await TestEventStream.OpenAsync(fixture.WebView, GameCenterBridge.PlayerEvent, timeout.Token);
        manager.RaisePlayerChanged(Player);
        var (name, data) = await events.NextAsync(timeout.Token);
        Assert.Equal("games.player", name);
        Assert.Contains("\"G:123\"", data);

        manager.RaisePlayerChanged(null);
        Assert.Contains("\"player\":null", (await events.NextAsync(timeout.Token)).Data);
    }

    sealed class GamesFixture : IAsyncDisposable
    {
        public BuiltInClientTests.HostFixture Host { get; private set; } = null!;
        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.Host.Transport;
        public GameCenterBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<GamesFixture> StartAsync(Native.IGameCenterManager? manager)
        {
            var fixture = new GamesFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.Host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new GameCenterBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new GameCenterBridgeClient(fixture.Host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.Host.DisposeAsync();
    }

    sealed class CountingMainThread : IWebAppMainThread
    {
        public int Calls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            this.Calls++;
            return action();
        }
    }

    sealed class FakeGameCenter : Native.IGameCenterManager
    {
        public Native.GameServicePlatform Platform { get; init; } = Native.GameServicePlatform.AppleGameCenter;
        public Native.GamePlayer? Player { get; init; }
        public int PendingCount { get; set; }
        public Native.GamePlayer? SignInAs { get; set; }
        public Native.FriendsResult Friends { get; init; } = new(Native.FriendsAccessStatus.Granted, []);
        public Exception? Throw { get; init; }

        public List<Native.Achievement> Achievements { get; } = [];
        public List<Native.LeaderboardEntry> Scores { get; } = [];
        public List<bool> Loads { get; } = [];
        public List<string> Calls { get; } = [];
        public List<(string, Native.LeaderboardScope, Native.LeaderboardTimeScope, int)> ScoreQueries { get; } = [];

        public event EventHandler<Native.GamePlayer?>? PlayerChanged;

        public void RaisePlayerChanged(Native.GamePlayer? player) => this.PlayerChanged?.Invoke(this, player);

        Task Record(string call)
        {
            if (this.Throw is not null)
                throw this.Throw;
            this.Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task<Native.GamePlayer?> SignInAsync(CancellationToken cancelToken = default) => Task.FromResult(this.SignInAs);

        public Task<IReadOnlyList<Native.Achievement>> GetAchievementsAsync(bool forceReload = false, CancellationToken cancelToken = default)
        {
            if (this.Throw is not null)
                throw this.Throw;
            this.Loads.Add(forceReload);
            return Task.FromResult<IReadOnlyList<Native.Achievement>>(this.Achievements);
        }

        public Task UnlockAsync(string achievementKey, CancellationToken cancelToken = default) => this.Record($"unlock {achievementKey}");
        public Task SetProgressAsync(string achievementKey, int steps, CancellationToken cancelToken = default) => this.Record($"progress {achievementKey} {steps}");
        public Task IncrementAsync(string achievementKey, int steps = 1, CancellationToken cancelToken = default) => this.Record($"increment {achievementKey} {steps}");
        public Task RevealAsync(string achievementKey, CancellationToken cancelToken = default) => this.Record($"reveal {achievementKey}");
        public Task SubmitScoreAsync(string leaderboardKey, long score, CancellationToken cancelToken = default) => this.Record($"score {leaderboardKey} {score}");
        public Task ShowAchievementsAsync(CancellationToken cancelToken = default) => this.Record("show achievements");
        public Task ShowLeaderboardAsync(string? leaderboardKey = null, CancellationToken cancelToken = default) => this.Record($"show leaderboard {leaderboardKey}");

        public Task<IReadOnlyList<Native.LeaderboardEntry>> GetScoresAsync(
            string leaderboardKey,
            Native.LeaderboardScope scope = Native.LeaderboardScope.Global,
            Native.LeaderboardTimeScope timeScope = Native.LeaderboardTimeScope.AllTime,
            int maxResults = 25,
            CancellationToken cancelToken = default
        )
        {
            this.ScoreQueries.Add((leaderboardKey, scope, timeScope, maxResults));
            return Task.FromResult<IReadOnlyList<Native.LeaderboardEntry>>(this.Scores);
        }

        public Task<Native.FriendsResult> GetFriendsAsync(CancellationToken cancelToken = default) => Task.FromResult(this.Friends);

        public Task FlushAsync(CancellationToken cancelToken = default)
        {
            this.PendingCount = 0;
            return Task.CompletedTask;
        }
    }
}
