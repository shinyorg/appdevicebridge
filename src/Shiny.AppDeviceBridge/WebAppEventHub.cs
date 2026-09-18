using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Sse;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Native events for the page, delivered over one Server-Sent Events stream at <c>GET /_bridge/events</c>. The page
/// names the events it wants; the host listens to the native source behind each only while a stream asks for it.
/// <code>
/// const events = new EventSource("/_bridge/events?topics=gps.reading,app.battery");
/// events.addEventListener("gps.reading", e => show(JSON.parse(e.data)));
/// </code>
/// <para>
/// Every event is an <see cref="IAsyncEnumerable{T}"/> a bridge maps with <see cref="Map{T}"/> — typically one that hooks
/// a native event and unhooks it in <c>finally</c> (<see cref="WebAppEventStream.FromEvent{T}(Func{Action{T}, Action}, CancellationToken, int)"/>), or a
/// <see cref="WebAppEventSource{T}"/> for values raised by our own code. Each stream enumerates its topics' sources
/// itself, and when the page disconnects, drops a topic, or a source throws, those enumerations end and their
/// <c>finally</c> blocks run. A source that throws is reported as <c>bridge.error</c> and dropped from that stream only.
/// </para>
/// <para>
/// One stream rather than one per event, because a WebView talks HTTP/1.1 to the loopback server and shares a
/// connection limit of about six between everything the page does; each open stream holds one for good. The first
/// event on a stream is <c>bridge.stream</c>, carrying the id <c>PUT /_bridge/events/{id}</c> takes to change its
/// topics without reconnecting.
/// </para>
/// <para>
/// Delivery is best effort. Nothing is buffered for a page that is not listening, and a page that falls more than a few
/// hundred events behind loses the oldest. State the page must not miss belongs behind a GET it calls when it connects.
/// </para>
/// </summary>
public sealed class WebAppEventHub
{
    readonly ConcurrentDictionary<string, Topic> topics = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, EventStream> streams = new(StringComparer.Ordinal);
    readonly ILogger logger;

    public WebAppEventHub(ILoggerFactory? loggerFactory = null)
        => this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebAppEventHub>();

    /// <summary>Open event streams. A page usually holds one; a page mid-reload briefly holds two.</summary>
    public int StreamCount => this.streams.Count;

    /// <summary>
    /// Maps an event to the stream behind it. Each page stream that asks for <paramref name="eventName"/> enumerates
    /// <paramref name="source"/> once, and cancels the token it passed when it no longer wants it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something is already mapped to <paramref name="eventName"/>.</exception>
    public void Map<T>(string eventName, Func<CancellationToken, IAsyncEnumerable<T>> source, JsonTypeInfo<T> typeInfo)
    {
        ValidateName(eventName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!this.topics.TryAdd(eventName, new Topic<T>(source, typeInfo)))
            throw new InvalidOperationException($"'{eventName}' is already mapped to an event source.");

        this.OnMapped(eventName);
    }

    /// <summary>
    /// The <see cref="WebAppEventSource{T}"/> mapped to <paramref name="eventName"/>, created and mapped by the first call.
    /// For events raised by our own code — a Shiny delegate, a bridge's session — which have no native event to hook.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something other than a source of <typeparamref name="T"/> is mapped to the name.</exception>
    public WebAppEventSource<T> Source<T>(string eventName, JsonTypeInfo<T> typeInfo)
    {
        ValidateName(eventName);
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (this.topics.TryGetValue(eventName, out var topic))
            return Existing(topic);

        var source = new WebAppEventSource<T>();
        if (!this.topics.TryAdd(eventName, new Topic<T>(source.ListenAsync, typeInfo, source)))
            return Existing(this.topics[eventName]);

        this.OnMapped(eventName);
        return source;

        WebAppEventSource<T> Existing(Topic mapped) => mapped is Topic<T> { Source: { } existing }
            ? existing
            : throw new InvalidOperationException($"'{eventName}' is mapped to something other than a {nameof(WebAppEventSource<T>)}<{typeof(T).Name}>.");
    }

    void OnMapped(string eventName)
    {
        foreach (var stream in this.streams.Values)
            stream.TopicMapped(eventName);
    }

    /// <param name="heartbeat">How often to write when idle; a write is how a page that went away is noticed.</param>
    internal async ValueTask StreamAsync(HttpContext context, TimeSpan heartbeat)
    {
        var stream = new EventStream(this, Guid.NewGuid().ToString("n"), context.RequestAborted);
        this.streams[stream.Id] = stream;

        try
        {
            stream.Write(EventStreamProtocol.OpenedEvent, JsonSerializer.Serialize(new EventStreamOpened(stream.Id), AppDeviceBridgeJsonContext.Default.EventStreamOpened));
            stream.SetTopics(ParseTopics(context.Request.Query[EventStreamProtocol.TopicsQuery].ToString()));

            await context.SendEventsAsync(sse => stream.SendAllAsync(sse, heartbeat));
        }
        catch (OperationCanceledException)
        {
            // The page navigated away or the WebView went with the app. Nothing to report.
        }
        finally
        {
            this.streams.TryRemove(stream.Id, out _);
            await stream.CloseAsync();
        }
    }

    /// <summary><c>PUT {bridge}/events/{id}</c>: replaces an open stream's topics.</summary>
    internal async ValueTask UpdateTopicsAsync(HttpContext context)
    {
        if (!this.streams.TryGetValue(context.Request.RouteValues["id"] ?? String.Empty, out var stream))
        {
            await WebAppBridgeResults.NotFound(context, "No open event stream has that id.");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AppDeviceBridgeJsonContext.Default.EventStreamTopics);
        if (body?.Topics is not { } names || names.Count > EventStreamProtocol.MaxTopics || names.Any(x => !EventStreamProtocol.IsValidEventName(x)))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"topics\": [\"gps.reading\", \"app.battery\"] }}, with at most {EventStreamProtocol.MaxTopics} names.");
            return;
        }

        stream.SetTopics(names);
        await WebAppBridgeResults.NoContent(context);
    }

    static IReadOnlyList<string> ParseTopics(string query)
        => [.. query
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(EventStreamProtocol.IsValidEventName)
            .Distinct(StringComparer.Ordinal)
            .Take(EventStreamProtocol.MaxTopics)];

    static void ValidateName(string eventName)
    {
        if (!EventStreamProtocol.IsValidEventName(eventName))
            throw new ArgumentException($"'{eventName}' is not a valid event name.", nameof(eventName));
    }

    abstract class Topic
    {
        /// <summary>The source's values as JSON, for one stream.</summary>
        public abstract IAsyncEnumerable<string> ReadJsonAsync(CancellationToken cancellationToken);
    }

    sealed class Topic<T>(Func<CancellationToken, IAsyncEnumerable<T>> source, JsonTypeInfo<T> typeInfo, WebAppEventSource<T>? owned = null) : Topic
    {
        public WebAppEventSource<T>? Source => owned;

        public override async IAsyncEnumerable<string> ReadJsonAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var value in source(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return JsonSerializer.Serialize(value, typeInfo);
        }
    }

    readonly record struct BridgeEvent(string Name, string Data);

    /// <summary>One page connection: its outbox, the topics it asked for, and one running enumeration per mapped topic.</summary>
    sealed class EventStream(WebAppEventHub hub, string id, CancellationToken aborted)
    {
        readonly Lock gate = new();
        readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
        readonly Channel<BridgeEvent> outbox = Channel.CreateBounded<BridgeEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        HashSet<string> wanted = new(StringComparer.Ordinal);
        bool closed;

        public string Id => id;

        public void Write(string name, string json) => this.outbox.Writer.TryWrite(new BridgeEvent(name, json));

        /// <summary>
        /// Writes the outbox to the page until it goes away. Idle, it writes a heartbeat instead: a page that left without
        /// a word is only noticed when a write to it fails, and until then its sources stay hooked.
        /// </summary>
        public async Task SendAllAsync(ServerSentEventStream sse, TimeSpan heartbeat)
        {
            var reader = this.outbox.Reader;

            while (true)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(sse.Aborted))
                {
                    idle.CancelAfter(heartbeat);

                    try
                    {
                        if (!await reader.WaitToReadAsync(idle.Token).ConfigureAwait(false))
                            return;
                    }
                    catch (OperationCanceledException) when (!sse.Aborted.IsCancellationRequested)
                    {
                        await sse.SendHeartbeatAsync(sse.Aborted).ConfigureAwait(false);
                        continue;
                    }
                }

                while (reader.TryRead(out var message))
                    await sse.SendAsync(message.Name, message.Data, sse.Aborted).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Starts what is new and stops what is gone; a topic kept stays on its running enumeration. A name nothing is
        /// mapped to yet is remembered, and starts if something maps it later. Returns once every new source has run up
        /// to its first wait — for a source that registers a listener or hooks an event first, once it is listening.
        /// </summary>
        public void SetTopics(IEnumerable<string> names)
        {
            List<Subscription> started = [];
            List<Subscription> stopped = [];

            lock (this.gate)
            {
                if (this.closed)
                    return;

                this.wanted = names.ToHashSet(StringComparer.Ordinal);

                foreach (var (name, subscription) in this.subscriptions.Where(x => !this.wanted.Contains(x.Key)).ToList())
                {
                    this.subscriptions.Remove(name);
                    stopped.Add(subscription);
                }

                foreach (var name in this.wanted)
                    this.TryAdd(name, started);
            }

            this.Run(started, stopped);
        }

        /// <summary>Something mapped <paramref name="name"/> after this stream asked for it.</summary>
        public void TopicMapped(string name)
        {
            List<Subscription> started = [];

            lock (this.gate)
            {
                if (!this.closed && this.wanted.Contains(name))
                    this.TryAdd(name, started);
            }

            this.Run(started, []);
        }

        /// <summary>Ends every enumeration and waits for their <c>finally</c> blocks, so nothing is still hooked on return.</summary>
        public async Task CloseAsync()
        {
            Subscription[] running;

            lock (this.gate)
            {
                this.closed = true;
                running = [.. this.subscriptions.Values];
                this.subscriptions.Clear();
            }

            foreach (var subscription in running)
                subscription.Cancel();

            await Task.WhenAll(running.Select(x => x.Pump)).ConfigureAwait(false);
            this.outbox.Writer.TryComplete();
        }

        void TryAdd(string name, List<Subscription> started)
        {
            if (this.subscriptions.ContainsKey(name) || !hub.topics.TryGetValue(name, out var topic))
                return;

            var subscription = new Subscription(name, topic, CancellationTokenSource.CreateLinkedTokenSource(aborted));
            this.subscriptions[name] = subscription;
            started.Add(subscription);
        }

        /// <summary>Outside the lock: a source can do anything up to its first wait, including fail and remove itself.</summary>
        void Run(List<Subscription> started, List<Subscription> stopped)
        {
            foreach (var subscription in stopped)
                subscription.Cancel();

            foreach (var subscription in started)
                subscription.Pump = this.PumpAsync(subscription);
        }

        async Task PumpAsync(Subscription subscription)
        {
            var token = subscription.Token;

            try
            {
                // Not yielded to the pool first, so the source is listening by the time SetTopics returns.
                await foreach (var json in subscription.Topic.ReadJsonAsync(token).ConfigureAwait(false))
                    this.Write(subscription.Name, json);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Dropped, or the page went away.
            }
            catch (Exception ex)
            {
                hub.logger.LogWarning(ex, "The source for {Event} failed; dropping it from the stream", subscription.Name);
                this.Write(
                    EventStreamProtocol.ErrorEvent,
                    JsonSerializer.Serialize(new EventStreamError(subscription.Name, ex.Message), AppDeviceBridgeJsonContext.Default.EventStreamError)
                );
            }
            finally
            {
                // A source that ended leaves the stream's subscriptions; naming the topic again starts it again.
                lock (this.gate)
                {
                    if (this.subscriptions.TryGetValue(subscription.Name, out var current) && current == subscription)
                    {
                        this.subscriptions.Remove(subscription.Name);
                        this.wanted.Remove(subscription.Name);
                    }
                }

                subscription.Dispose();
            }
        }
    }

    sealed class Subscription(string name, Topic topic, CancellationTokenSource cancel) : IDisposable
    {
        public string Name => name;

        public Topic Topic => topic;

        public Task Pump { get; set; } = Task.CompletedTask;

        public CancellationToken Token { get; } = cancel.Token;

        public void Cancel()
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pump already finished.
            }
        }

        public void Dispose() => cancel.Dispose();
    }
}
