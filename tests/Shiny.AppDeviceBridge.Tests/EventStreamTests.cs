using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The page's one event stream: each topic is an async enumerable the stream runs for as long as it wants the topic, so a
/// native event hooked in the enumerable is unhooked however the page lets go of it.
/// </summary>
public class EventStreamTests
{
    [Fact]
    public async Task Delivers_only_the_topics_the_page_names()
    {
        await using var fixture = await Fixture.StartAsync();
        var pings = fixture.Events.Source("test.ping", AppDeviceBridgeJsonContext.Default.JobRun);
        var pongs = fixture.Events.Source("test.pong", AppDeviceBridgeJsonContext.Default.JobRun);

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "test.pong", fixture.Timeout);
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);

        pings.Publish(new JobRun("ignored"));
        pongs.Publish(new JobRun("sync"));

        Assert.Equal(("test.pong", """{"name":"sync"}"""), await stream.NextAsync(fixture.Timeout));
        Assert.False(pings.HasListeners);
    }

    [Fact]
    public async Task Hooks_a_native_event_only_while_a_stream_wants_it()
    {
        await using var fixture = await Fixture.StartAsync();
        var battery = new FakeNativeEvent();
        fixture.Events.Map("test.battery", ct => WebAppEventStream.FromEvent<JobRun>(emit =>
        {
            Action<string> handler = level => emit(new JobRun(level));
            battery.Changed += handler;
            return () => battery.Changed -= handler;
        }, ct), AppDeviceBridgeJsonContext.Default.JobRun);

        Assert.Equal(0, battery.Handlers);

        var stream = await TestEventStream.OpenAsync(fixture.WebView, "test.battery", fixture.Timeout);
        Assert.Equal(1, battery.Handlers);

        battery.Raise("80");
        Assert.Equal(("test.battery", """{"name":"80"}"""), await stream.NextAsync(fixture.Timeout));

        // The page goes away without a word: the host notices and unhooks.
        await stream.DisposeAsync();
        await WaitAsync(() => battery.Handlers == 0, fixture.Timeout);
    }

    [Fact]
    public async Task Changing_topics_starts_and_stops_sources_without_reconnecting()
    {
        await using var fixture = await Fixture.StartAsync();
        var gps = new FakeNativeEvent();
        var wifi = new FakeNativeEvent();
        fixture.Events.Map("test.gps", ct => gps.Stream(ct), AppDeviceBridgeJsonContext.Default.JobRun);
        fixture.Events.Map("test.wifi", ct => wifi.Stream(ct), AppDeviceBridgeJsonContext.Default.JobRun);

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "test.gps", fixture.Timeout);
        Assert.Equal((1, 0), (gps.Handlers, wifi.Handlers));

        await stream.SetTopicsAsync(fixture.Timeout, "test.gps", "test.wifi");
        Assert.Equal((1, 1), (gps.Handlers, wifi.Handlers));

        await stream.SetTopicsAsync(fixture.Timeout, "test.wifi");
        await WaitAsync(() => gps.Handlers == 0, fixture.Timeout);
        Assert.Equal(1, wifi.Handlers);

        wifi.Raise("home");
        Assert.Equal(("test.wifi", """{"name":"home"}"""), await stream.NextAsync(fixture.Timeout));
    }

    [Fact]
    public async Task Two_streams_each_hook_and_unhook_their_own()
    {
        await using var fixture = await Fixture.StartAsync();
        var native = new FakeNativeEvent();
        fixture.Events.Map("test.native", ct => native.Stream(ct), AppDeviceBridgeJsonContext.Default.JobRun);

        await using var first = await TestEventStream.OpenAsync(fixture.WebView, "test.native", fixture.Timeout);
        var second = await TestEventStream.OpenAsync(fixture.WebView, "test.native", fixture.Timeout);
        Assert.Equal(2, native.Handlers);
        Assert.Equal(2, fixture.Events.StreamCount);

        await second.DisposeAsync();
        await WaitAsync(() => native.Handlers == 1, fixture.Timeout);

        native.Raise("still here");
        Assert.Equal(("test.native", """{"name":"still here"}"""), await first.NextAsync(fixture.Timeout));
    }

    [Fact]
    public async Task A_failing_source_is_reported_and_unhooked_while_the_rest_keep_flowing()
    {
        await using var fixture = await Fixture.StartAsync();
        var native = new FakeNativeEvent();
        var healthy = fixture.Events.Source("test.healthy", AppDeviceBridgeJsonContext.Default.JobRun);
        fixture.Events.Map("test.broken", ct => native.Stream(ct, failAfter: 1), AppDeviceBridgeJsonContext.Default.JobRun);

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "test.broken,test.healthy", fixture.Timeout);

        native.Raise("boom");
        var error = await stream.NextAsync(EventStreamProtocol.ErrorEvent, fixture.Timeout);
        Assert.Equal("""{"event":"test.broken","message":"The sensor fell over."}""", error);
        await WaitAsync(() => native.Handlers == 0, fixture.Timeout);

        healthy.Publish(new JobRun("fine"));
        Assert.Equal("""{"name":"fine"}""", await stream.NextAsync("test.healthy", fixture.Timeout));
    }

    [Fact]
    public async Task A_topic_named_before_anything_maps_it_starts_when_it_is_mapped()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "push.token", fixture.Timeout);

        // A delegate the OS calls later is the first to ask for its source.
        var tokens = fixture.Events.Source("push.token", AppDeviceBridgeJsonContext.Default.JobRun);
        await WaitAsync(() => tokens.HasListeners, fixture.Timeout);

        tokens.Publish(new JobRun("abc"));
        Assert.Equal("""{"name":"abc"}""", await stream.NextAsync("push.token", fixture.Timeout));
    }

    [Fact]
    public async Task Refuses_topics_for_a_stream_that_is_gone_or_malformed()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "", fixture.Timeout);

        using var gone = await fixture.WebView.PutAsJsonAsync("/_bridge/events/nope", new EventStreamTopics(["a"]), AppDeviceBridgeJsonContext.Default.EventStreamTopics, fixture.Timeout);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var invalid = await fixture.WebView.PutAsJsonAsync($"/_bridge/events/{stream.Id}", new EventStreamTopics(["not a name"]), AppDeviceBridgeJsonContext.Default.EventStreamTopics, fixture.Timeout);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var tooMany = await fixture.WebView.PutAsJsonAsync($"/_bridge/events/{stream.Id}", new EventStreamTopics([.. Enumerable.Range(0, EventStreamProtocol.MaxTopics + 1).Select(x => $"t{x}")]), AppDeviceBridgeJsonContext.Default.EventStreamTopics, fixture.Timeout);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public void Refuses_to_map_one_name_twice()
    {
        var hub = new WebAppEventHub();
        hub.Map("test.once", ct => new FakeNativeEvent().Stream(ct), AppDeviceBridgeJsonContext.Default.JobRun);

        Assert.Throws<InvalidOperationException>(() => hub.Map("test.once", ct => new FakeNativeEvent().Stream(ct), AppDeviceBridgeJsonContext.Default.JobRun));
        Assert.Throws<InvalidOperationException>(() => hub.Source("test.once", AppDeviceBridgeJsonContext.Default.JobRun));
        Assert.Same(hub.Source("test.twice", AppDeviceBridgeJsonContext.Default.JobRun), hub.Source("test.twice", AppDeviceBridgeJsonContext.Default.JobRun));
        Assert.Throws<ArgumentException>(() => hub.Source("not valid", AppDeviceBridgeJsonContext.Default.JobRun));
    }

    [Fact]
    public async Task A_source_tells_its_owner_how_many_listeners_remain()
    {
        var source = new WebAppEventSource<int>();
        var remaining = new List<int>();

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = Drain(source.ListenAsync(remaining.Add, first.Token));
        var b = Drain(source.ListenAsync(remaining.Add, second.Token));
        await WaitAsync(() => source.ListenerCount == 2, TestContext.Current.CancellationToken);

        source.Publish(1);
        await first.CancelAsync();
        await a;
        await second.CancelAsync();
        await b;

        Assert.Equal([1, 0], remaining);
        Assert.False(source.HasListeners);

        static async Task Drain(IAsyncEnumerable<int> values)
        {
            try
            {
                await foreach (var _ in values)
                {
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(10, cancellationToken);
    }

    /// <summary>A singleton's event, counting who is hooked to it.</summary>
    sealed class FakeNativeEvent
    {
        Action<string>? changed;

        public event Action<string>? Changed
        {
            add => this.changed += value;
            remove => this.changed -= value;
        }

        public int Handlers => this.changed?.GetInvocationList().Length ?? 0;

        public void Raise(string value) => this.changed?.Invoke(value);

        public async IAsyncEnumerable<JobRun> Stream([EnumeratorCancellation] CancellationToken cancellationToken, int? failAfter = null)
        {
            var count = 0;

            await foreach (var value in WebAppEventStream.FromEvent<string>(emit =>
            {
                this.Changed += emit;
                return () => this.Changed -= emit;
            }, cancellationToken))
            {
                if (++count == failAfter)
                    throw new InvalidOperationException("The sensor fell over.");

                yield return new JobRun(value);
            }
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly TestApp app = new();
        readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        WebAppHost host = null!;

        public WebAppEventHub Events { get; } = new();

        public HttpClient WebView { get; private set; } = null!;

        public CancellationToken Timeout => this.timeout.Token;

        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await fixture.app.StartReleaseServerAsync();

            fixture.host = fixture.app.CreateHost(fixture.app.Options(), null, fixture.Events, []);
            var start = await fixture.host.StartAsync();

            fixture.WebView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = fixture.host.Origin };
            await fixture.WebView.GetStringAsync(start);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            this.WebView?.Dispose();
            await this.host.DisposeAsync();
            await this.app.DisposeAsync();
            this.timeout.Dispose();
        }
    }
}
