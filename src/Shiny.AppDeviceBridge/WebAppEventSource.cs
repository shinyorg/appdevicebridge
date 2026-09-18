using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Values raised by code of ours — a Shiny delegate the OS calls, a session a bridge runs — as streams the page can
/// listen to. Each <see cref="ListenAsync"/> is one listener, added when it starts and removed in its <c>finally</c>,
/// however it ends: the page went away, the stream failed, or the listener stopped.
/// <code>
/// // registered once, by the bridge
/// var readings = routes.Events.Source("gps.reading", LocationsJsonContext.Default.GpsReading);
///
/// // from the delegate
/// readings.Publish(reading);
/// </code>
/// <para>
/// Publishing with no listener does nothing. A listener that falls more than <see cref="Capacity"/> values behind
/// loses the oldest — this is for positions, scan results and notifications, where only the latest matters.
/// </para>
/// </summary>
public sealed class WebAppEventSource<T>
{
    readonly Lock gate = new();
    Channel<T>[] listeners = [];

    public WebAppEventSource(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>True while something listens. Check it before building an expensive value.</summary>
    public bool HasListeners => this.ListenerCount > 0;

    public int ListenerCount => Volatile.Read(ref this.listeners).Length;

    public void Publish(T value)
    {
        foreach (var listener in Volatile.Read(ref this.listeners))
            listener.Writer.TryWrite(value);
    }

    /// <summary>
    /// Every value published from the first <c>MoveNextAsync</c> until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="stopped">
    /// Runs after this listener is removed, with the number still listening — where a bridge stops a native session once
    /// the last listener is gone.
    /// </param>
    public async IAsyncEnumerable<T> ListenAsync(Action<int>? stopped = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(this.Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,

            // The stream's pump runs on the publisher's thread when it is waiting, so what one thread publishes to
            // several topics reaches the page in the order it was published.
            AllowSynchronousContinuations = true
        });

        lock (this.gate)
            this.listeners = [.. this.listeners, channel];

        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return value;
        }
        finally
        {
            int remaining;
            lock (this.gate)
            {
                this.listeners = [.. this.listeners.Where(x => x != channel)];
                remaining = this.listeners.Length;
            }

            channel.Writer.TryComplete();
            stopped?.Invoke(remaining);
        }
    }

    public IAsyncEnumerable<T> ListenAsync(CancellationToken cancellationToken) => this.ListenAsync(null, cancellationToken);
}

/// <summary>Turns a native event into a stream the page can listen to.</summary>
public static class WebAppEventStream
{
    /// <summary>
    /// Hooks a native event when the stream starts and unhooks it when the stream ends — the page went away, it
    /// stopped listening, or the host is shutting down. Nothing is left subscribed to a singleton for a page that is gone.
    /// <code>
    /// WebAppEventStream.FromEvent&lt;BatteryChanged&gt;(emit =>
    /// {
    ///     EventHandler&lt;BatteryInfoChangedEventArgs&gt; handler = (_, e) => emit(ToContract(e));
    ///     Battery.Default.BatteryInfoChanged += handler;
    ///     return () => Battery.Default.BatteryInfoChanged -= handler;
    /// }, cancellationToken);
    /// </code>
    /// </summary>
    /// <param name="hook">Subscribes a handler that calls <c>emit</c>, and returns what unsubscribes it. A throw fails the stream.</param>
    /// <param name="capacity">How far the page may fall behind before the oldest values are dropped.</param>
    public static IAsyncEnumerable<T> FromEvent<T>(Func<Action<T>, Action> hook, CancellationToken cancellationToken = default, int capacity = 512)
    {
        ArgumentNullException.ThrowIfNull(hook);

        return FromEvent<T>(emit =>
        {
            var unhook = hook(emit);
            return Task.FromResult<Func<Task>>(() =>
            {
                unhook();
                return Task.CompletedTask;
            });
        }, cancellationToken, capacity);
    }

    /// <summary>
    /// <see cref="FromEvent{T}(Func{Action{T}, Action}, CancellationToken, int)"/> for an event that has to be hooked and
    /// unhooked somewhere in particular — on the main thread, say.
    /// </summary>
    public static async IAsyncEnumerable<T> FromEvent<T>(
        Func<Action<T>, Task<Func<Task>>> hook,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int capacity = 512
    )
    {
        ArgumentNullException.ThrowIfNull(hook);

        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,

            // The stream's pump runs on the publisher's thread when it is waiting, so what one thread publishes to
            // several topics reaches the page in the order it was published.
            AllowSynchronousContinuations = true
        });

        var unhook = await hook(value => channel.Writer.TryWrite(value)).ConfigureAwait(false);

        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return value;
        }
        finally
        {
            channel.Writer.TryComplete();
            await unhook().ConfigureAwait(false);
        }
    }
}
