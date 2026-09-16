using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Sse;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Native events for the page, delivered over one Server-Sent Events stream at
/// <c>GET /_bridge/events</c>.
/// <code>
/// const events = new EventSource("/_bridge/events");
/// events.addEventListener("gps.reading", e => show(JSON.parse(e.data)));
/// </code>
/// <para>
/// One stream rather than one per bridge, because a WebView shares a small per-origin connection
/// limit between everything the page does, and each open stream holds one for good.
/// </para>
/// <para>
/// Delivery is best effort. An event published while no page is listening is dropped, and a page that
/// falls more than a few hundred events behind loses the oldest. That suits positions, scan results
/// and notifications, where only the latest value matters; state the page must not miss belongs behind
/// a GET it can call when it reconnects.
/// </para>
/// </summary>
public sealed class WebAppEventHub
{
    readonly Lock gate = new();
    Channel<BridgeEvent>[] subscribers = [];

    /// <summary>True while a page is listening. Check it before building an expensive payload.</summary>
    public bool HasSubscribers => this.SubscriberCount > 0;

    /// <summary>Open event streams. A page usually holds one; a page mid-reload briefly holds two.</summary>
    public int SubscriberCount => Volatile.Read(ref this.subscribers).Length;

    /// <summary>
    /// Raised when a stream opens or closes, on the server thread that opened or closed it. Bridges use it to
    /// start native watchers only while a page is listening, and to clean up after a page that went away.
    /// </summary>
    public event Action? SubscribersChanged;

    public void Publish<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (this.HasSubscribers)
            this.PublishJson(eventName, JsonSerializer.Serialize(payload, typeInfo));
    }

    /// <summary>Publishes a payload that is already JSON.</summary>
    internal void PublishJson(string eventName, string json)
    {
        var message = new BridgeEvent(eventName, json);

        foreach (var subscriber in Volatile.Read(ref this.subscribers))
            subscriber.Writer.TryWrite(message);
    }

    internal async ValueTask StreamAsync(HttpContext context)
    {
        var channel = Channel.CreateBounded<BridgeEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        lock (this.gate)
            this.subscribers = [.. this.subscribers, channel];

        this.SubscribersChanged?.Invoke();

        try
        {
            await context.SendEventsAsync(async stream =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(stream.Aborted))
                    await stream.SendAsync(message.Name, message.Data, stream.Aborted);
            });
        }
        catch (OperationCanceledException)
        {
            // The page navigated away or the WebView went with the app. Nothing to report.
        }
        finally
        {
            lock (this.gate)
                this.subscribers = [.. this.subscribers.Where(x => x != channel)];

            channel.Writer.TryComplete();
            this.SubscribersChanged?.Invoke();
        }
    }

    readonly record struct BridgeEvent(string Name, string Data);
}
