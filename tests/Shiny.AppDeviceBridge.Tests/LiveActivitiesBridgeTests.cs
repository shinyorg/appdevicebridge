using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.LiveActivities;
using Shiny.AppDeviceBridge.LiveActivities.Client;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.LiveActivities;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The live activities bridge over a fake <see cref="Native.ILiveActivityManager"/>: the routes, the contract-to-native
/// mapping, the error mapping and the delegate that turns ActivityKit's callbacks into page events. ActivityKit and
/// Android's Live Updates themselves are Shiny.Mobile.LiveActivities' and need a device.
/// </summary>
public class LiveActivitiesBridgeTests
{
    [Fact]
    public async Task Answers_501_without_a_manager()
    {
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager: null);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync());
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "liveactivities").IsSupported);
    }

    [Fact]
    public async Task Answers_501_where_the_manager_is_shinys_no_op()
    {
        await using var fixture = await LiveActivitiesFixture.StartAsync(new Native.NoOpLiveActivityManager());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new LiveActivityStartRequest(new LiveActivityContent("Hi"))));
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "liveactivities").IsSupported);
    }

    [Fact]
    public async Task Reports_access_and_the_push_to_start_token()
    {
        var manager = new FakeLiveActivityManager { Access = Shiny.AccessState.Denied, PushToStartToken = "abc" };
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var status = await fixture.Client.GetStatusAsync();
        Assert.Equal(ContractAccess.Denied, status.Access);
        Assert.Equal("abc", status.PushToStartToken);

        manager.Access = Shiny.AccessState.Available;
        Assert.Equal(ContractAccess.Available, (await fixture.Client.RequestAccessAsync()).Access);
        Assert.Equal(1, manager.AccessRequests);
    }

    [Fact]
    public async Task Starts_an_activity_with_everything_the_page_sent()
    {
        var manager = new FakeLiveActivityManager();
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);
        var stale = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        var activity = await fixture.Client.StartAsync(new LiveActivityStartRequest(
            new LiveActivityContent(
                "Order 42",
                "On its way",
                "5 min",
                new LiveActivityProgress(Value: 0.4),
                stale,
                0.5,
                new Dictionary<string, string> { ["driver"] = "Sam" }
            ),
            new Dictionary<string, string> { ["order"] = "42" },
            "delivery",
            RequestPushToken: false
        ));

        Assert.Equal(new LiveActivity("a1", LiveActivityState.Active, null), activity);

        var started = Assert.Single(manager.Started);
        Assert.Equal("Order 42", started.Content.Title);
        Assert.Equal("On its way", started.Content.Body);
        Assert.Equal("5 min", started.Content.ShortStatus);
        Assert.Equal(0.4, started.Content.Progress?.Value);
        Assert.Equal(stale, started.Content.StaleDate);
        Assert.Equal(0.5, started.Content.RelevanceScore);
        Assert.Equal("Sam", started.Content.Data["driver"]);
        Assert.Equal("42", started.Attributes["order"]);
        Assert.Equal("delivery", started.Kind);
        Assert.False(started.RequestPushToken);
    }

    [Fact]
    public async Task A_push_token_is_requested_unless_the_page_says_otherwise()
    {
        var manager = new FakeLiveActivityManager();
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync(
            "/_bridge/liveactivities/activities",
            new StringContent("""{ "content": { "title": "Hi" } }""", Encoding.UTF8, "application/json")
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var started = Assert.Single(manager.Started);
        Assert.True(started.RequestPushToken);
        Assert.Empty(started.Attributes);
        Assert.Empty(started.Content.Data);
    }

    [Fact]
    public async Task Lists_updates_and_ends_activities()
    {
        var manager = new FakeLiveActivityManager();
        manager.Running.Add(new Native.LiveActivity("a1", Native.LiveActivityState.Stale, "tok"));
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        Assert.Equal(new LiveActivity("a1", LiveActivityState.Stale, "tok"), Assert.Single(await fixture.Client.GetActivitiesAsync()));

        await fixture.Client.UpdateAsync("a1", new LiveActivityUpdateRequest(new LiveActivityContent("Nearly there"), new LiveActivityAlert("Driver close", "2 min")));
        var update = Assert.Single(manager.Updates);
        Assert.Equal("a1", update.Id);
        Assert.Equal("Nearly there", update.Content.Title);
        Assert.Equal(new Native.LiveActivityAlert("Driver close", "2 min"), update.Alert);

        var dismiss = DateTimeOffset.UtcNow.AddMinutes(5);
        await fixture.Client.EndAsync("a1", new LiveActivityEndRequest(new LiveActivityContent("Delivered"), dismiss));
        var end = Assert.Single(manager.Ends);
        Assert.Equal("Delivered", end.Content?.Title);
        Assert.Equal(dismiss, end.DismissAt);

        await fixture.Client.EndAllAsync();
        Assert.Equal(1, manager.EndAllCount);
    }

    [Fact]
    public async Task Ending_without_a_body_keeps_the_last_content()
    {
        var manager = new FakeLiveActivityManager();
        manager.Running.Add(new Native.LiveActivity("a1", Native.LiveActivityState.Active));
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/liveactivities/activities/a1/end", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var end = Assert.Single(manager.Ends);
        Assert.Null(end.Content);
        Assert.Null(end.DismissAt);
    }

    [Fact]
    public async Task An_unknown_activity_is_a_404()
    {
        var manager = new FakeLiveActivityManager();
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var update = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.UpdateAsync("nope", new LiveActivityUpdateRequest(new LiveActivityContent("x"))));
        var end = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.EndAsync("nope", new LiveActivityEndRequest()));

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, end.StatusCode);
        Assert.Empty(manager.Updates);
        Assert.Empty(manager.Ends);
    }

    [Theory]
    [InlineData("""{ "attributes": {} }""")]
    [InlineData("""{ "content": { "progress": { "value": 1.5 } } }""")]
    [InlineData("not json")]
    public async Task Refuses_start_requests_it_cannot_use(string body)
    {
        var manager = new FakeLiveActivityManager();
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var response = await fixture.WebView.PostAsync("/_bridge/liveactivities/activities", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(manager.Started);
    }

    [Fact]
    public async Task A_refused_start_is_a_502()
    {
        var manager = new FakeLiveActivityManager { Throw = new InvalidOperationException("Failed to start live activity: unsupported target") };
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new LiveActivityStartRequest(new LiveActivityContent("x"))));

        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Equal("live_activity_failed", refused.Code);
    }

    [Fact]
    public async Task A_refused_start_that_asked_for_a_push_token_says_what_it_needs()
    {
        var manager = new FakeLiveActivityManager { Throw = new InvalidOperationException("Failed to start live activity: The operation couldn't be completed. (com.apple.ActivityKit.ActivityInput error 0.)") };
        await using var fixture = await LiveActivitiesFixture.StartAsync(manager);

        var withToken = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new LiveActivityStartRequest(new LiveActivityContent("x"))));
        var withoutToken = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new LiveActivityStartRequest(new LiveActivityContent("x"), RequestPushToken: false)));

        Assert.Equal("live_activity_failed", withToken.Code);
        Assert.Contains("aps-environment", withToken.Message);
        Assert.Equal("live_activity_failed", withoutToken.Code);
        Assert.DoesNotContain("aps-environment", withoutToken.Message);
    }

    [Fact]
    public async Task The_delegate_publishes_what_activitykit_reports()
    {
        await using var app = new TestApp();
        var events = new WebAppEventHub();
        var d = new WebAppLiveActivityDelegate(events, new WebAppInvoker(app.BridgeOptions()));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var started = Listen(events.Source("liveactivities.started", LiveActivitiesJsonContext.Default.LiveActivity), cancel.Token);
        var states = Listen(events.Source("liveactivities.state", LiveActivitiesJsonContext.Default.LiveActivity), cancel.Token);
        var tokens = Listen(events.Source("liveactivities.token", LiveActivitiesJsonContext.Default.LiveActivityPushToken), cancel.Token);
        var startTokens = Listen(events.Source("liveactivities.starttoken", LiveActivitiesJsonContext.Default.LiveActivityPushToStartToken), cancel.Token);

        // No page and no background.js: the handler calls find nobody, and the events still go out.
        await d.OnStarted(new Native.LiveActivity("a1", Native.LiveActivityState.Active));
        await d.OnStateChanged(new Native.LiveActivity("a1", Native.LiveActivityState.Ended));
        await d.OnPushTokenChanged(new Native.LiveActivity("a1", Native.LiveActivityState.Active), "tok");
        await d.OnPushToStartTokenChanged("start");

        Assert.Equal(new LiveActivity("a1", LiveActivityState.Active, null), await started);
        Assert.Equal(new LiveActivity("a1", LiveActivityState.Ended, null), await states);
        Assert.Equal(new LiveActivityPushToken("a1", "tok"), await tokens);
        Assert.Equal(new LiveActivityPushToStartToken("start"), await startTokens);
    }

    [Fact]
    public void Registers_once_and_leaves_an_apps_own_registration_alone()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
        {
            bridge.AddLiveActivitiesBridge();
            bridge.AddLiveActivitiesBridge();
        }), autoStart: false);

        Assert.Single(services, x => x.ServiceType == typeof(Native.ILiveActivityManager));
        Assert.Single(services, x => x.ImplementationType == typeof(WebAppLiveActivityDelegate));
        Assert.Single(services, x => x.ImplementationType == typeof(LiveActivitiesBridge));

        var own = new ServiceCollection();
        own.AddSingleton<Native.ILiveActivityManager>(new FakeLiveActivityManager());
        own.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge.AddLiveActivitiesBridge()), autoStart: false);

        Assert.Single(own, x => x.ServiceType == typeof(Native.ILiveActivityManager));
        Assert.Single(own, x => x.ServiceType == typeof(Native.ILiveActivityDelegate) && x.ImplementationType == typeof(WebAppLiveActivityDelegate));
    }

    /// <summary>The first value published to <paramref name="source"/> from now on.</summary>
    static Task<T> Listen<T>(WebAppEventSource<T> source, CancellationToken cancellationToken)
    {
        var enumerator = source.ListenAsync(cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = enumerator.MoveNextAsync();
        return Read();

        async Task<T> Read()
        {
            Assert.True(await next);
            var value = enumerator.Current;
            await enumerator.DisposeAsync();
            return value;
        }
    }

    sealed class LiveActivitiesFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public LiveActivitiesBridgeClient Client { get; private set; } = null!;

        public static async Task<LiveActivitiesFixture> StartAsync(Native.ILiveActivityManager? manager)
        {
            var fixture = new LiveActivitiesFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new LiveActivitiesBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new LiveActivitiesBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    sealed class FakeLiveActivityManager : Native.ILiveActivityManager
    {
        public bool IsSupported => true;
        public string? PushToStartToken { get; set; }
        public Shiny.AccessState Access { get; set; } = Shiny.AccessState.Available;
        public Exception? Throw { get; init; }
        public int AccessRequests { get; private set; }
        public int EndAllCount { get; private set; }

        public List<Native.LiveActivity> Running { get; } = [];
        public List<Native.LiveActivityRequest> Started { get; } = [];
        public List<(string Id, Native.LiveActivityContent Content, Native.LiveActivityAlert? Alert)> Updates { get; } = [];
        public List<(string Id, Native.LiveActivityContent? Content, DateTimeOffset? DismissAt)> Ends { get; } = [];

        public Task<Shiny.AccessState> GetCurrentAccess() => Task.FromResult(this.Access);

        public Task<Shiny.AccessState> RequestAccess(CancellationToken cancelToken = default)
        {
            this.AccessRequests++;
            return Task.FromResult(this.Access);
        }

        public IReadOnlyList<Native.LiveActivity> GetAll() => this.Running;

        public Task<Native.LiveActivity> Start(Native.LiveActivityRequest request, CancellationToken cancelToken = default)
        {
            if (this.Throw is not null)
                throw this.Throw;

            this.Started.Add(request);
            var activity = new Native.LiveActivity($"a{this.Started.Count}", Native.LiveActivityState.Active);
            this.Running.Add(activity);
            return Task.FromResult(activity);
        }

        public Task Update(string activityId, Native.LiveActivityContent content, Native.LiveActivityAlert? alert = null, CancellationToken cancelToken = default)
        {
            this.Updates.Add((activityId, content, alert));
            return Task.CompletedTask;
        }

        public Task End(string activityId, Native.LiveActivityContent? content = null, DateTimeOffset? dismissAt = null, CancellationToken cancelToken = default)
        {
            this.Ends.Add((activityId, content, dismissAt));
            return Task.CompletedTask;
        }

        public Task EndAll(CancellationToken cancelToken = default)
        {
            this.EndAllCount++;
            this.Running.Clear();
            return Task.CompletedTask;
        }
    }
}
