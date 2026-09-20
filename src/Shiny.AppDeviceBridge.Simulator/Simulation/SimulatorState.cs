using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Trails;

namespace Shiny.AppDeviceBridge.Simulator.Simulation;

/// <summary>How a route answers.</summary>
public enum ResponseMode
{
    /// <summary>200 with <see cref="RouteBehavior.Json"/> — or 204 for a route that returns nothing, or the file for a binary one.</summary>
    Value,

    /// <summary>204: the "nothing there" answer, which the page's client reads as <c>null</c>.</summary>
    Null,

    /// <summary><see cref="RouteBehavior.StatusCode"/> with a bridge error body, exactly as a real bridge fails.</summary>
    Error
}

/// <summary>What one route does when the page calls it. Immutable; the state swaps whole behaviors, so a request never sees half an edit.</summary>
/// <param name="Json">The value, for <see cref="ResponseMode.Value"/>. May hold <see cref="PayloadTokens"/>.</param>
/// <param name="DelayMs">Held before answering, to show the page's loading state or trip its timeout.</param>
/// <param name="FilePath">For a binary route: the file to send. A small placeholder image when null.</param>
/// <param name="Sequence">
/// Values answered one per call, the last one repeating once they run out — a route that changes as the page polls it:
/// queued, then running, then done. <see cref="Json"/> is the first of them, so anything reading a single value still
/// sees something sensible.
/// </param>
public sealed record RouteBehavior(
    ResponseMode Mode,
    string Json,
    int StatusCode = 501,
    string ErrorCode = "not_supported",
    string ErrorMessage = "Simulated failure.",
    int DelayMs = 0,
    string? FilePath = null,
    IReadOnlyList<string>? Sequence = null
)
{
    /// <summary>The failures a bridge sends, as the page's <c>BridgeException</c> sees them.</summary>
    public static readonly IReadOnlyList<(int Status, string Code, string Message)> ErrorPresets =
    [
        (501, "not_supported", "This is not available on this platform."),
        (403, "access_denied", "The user denied access."),
        (400, "bad_request", "The request was not valid."),
        (404, "not_found", "Nothing matches that."),
        (409, "conflict", "The device is not in a state to do that."),
        (500, "bridge_failed", "The native call failed.")
    ];
}

public sealed class RouteState
{
    RouteBehavior behavior;
    int hits;
    int step;

    internal RouteState(SimRoute route)
    {
        this.Route = route;
        this.DefaultJson = route.ResultType is { } type ? SampleJson.For(type, route.Json) : String.Empty;
        this.behavior = new RouteBehavior(ResponseMode.Value, this.DefaultJson);
    }

    public SimRoute Route { get; }

    /// <summary>The generated sample the route starts with.</summary>
    public string DefaultJson { get; }

    public RouteBehavior Behavior
    {
        get => Volatile.Read(ref this.behavior);
        internal set
        {
            // A new behavior starts its sequence again; otherwise setting one twice would answer from halfway through it.
            Volatile.Write(ref this.step, 0);
            Volatile.Write(ref this.behavior, value);
        }
    }

    /// <summary>How far through <see cref="RouteBehavior.Sequence"/> the route is.</summary>
    public int Step => Volatile.Read(ref this.step);

    /// <summary>
    /// The value this call answers with, advancing a sequence by one. The last value repeats, so a page that keeps
    /// polling keeps getting the end state rather than falling off the end.
    /// </summary>
    internal string NextJson(RouteBehavior current)
    {
        if (current.Sequence is not { Count: > 0 } sequence)
            return current.Json;

        var index = Interlocked.Increment(ref this.step) - 1;
        return sequence[Math.Min(index, sequence.Count - 1)];
    }

    /// <summary>How many times the page has called it.</summary>
    public int Hits => Volatile.Read(ref this.hits);

    internal void Hit() => Interlocked.Increment(ref this.hits);
}

public sealed class EventState
{
    string json;
    int fired;

    internal EventState(SimEvent evt)
    {
        this.Event = evt;
        this.DefaultJson = SampleJson.For(evt.PayloadType, evt.Json);
        this.json = this.DefaultJson;
    }

    public SimEvent Event { get; }

    public string DefaultJson { get; }

    /// <summary>The payload sent when the event is fired without one.</summary>
    public string Json
    {
        get => Volatile.Read(ref this.json);
        internal set => Volatile.Write(ref this.json, value);
    }

    public int Fired => Volatile.Read(ref this.fired);

    /// <summary>Page streams listening right now — zero means the page has not subscribed, and a fired event goes nowhere.</summary>
    public int Listeners => this.Source?.ListenerCount ?? 0;

    internal WebAppEventSource<JsonElement>? Source { get; set; }

    internal void Fire() => Interlocked.Increment(ref this.fired);
}

public sealed class BridgeState
{
    volatile bool isSupported = true;

    internal BridgeState(SimBridge bridge)
    {
        this.Bridge = bridge;
        this.Routes = [.. bridge.Routes.Select(x => new RouteState(x))];
        this.Events = [.. bridge.Events.Select(x => new EventState(x))];
    }

    public SimBridge Bridge { get; }

    public string Name => this.Bridge.Name;

    /// <summary>Reported by <c>GET /_bridge/host</c>. Off, every route answers 501 — the bridge on a platform without it.</summary>
    public bool IsSupported
    {
        get => this.isSupported;
        internal set => this.isSupported = value;
    }

    public IReadOnlyList<RouteState> Routes { get; }

    public IReadOnlyList<EventState> Events { get; }

    public RouteState? FindRoute(string key) => this.Routes.FirstOrDefault(x => String.Equals(x.Route.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Something that happened, for the activity log: an event fired, a trail step, a value changed.</summary>
public sealed record ActivityRecord(DateTimeOffset At, string Text);

/// <summary>
/// Everything the simulator answers with: every bridge's routes and events and what each does. Shared by the server, the
/// TUI and trails — each change raises <see cref="Changed"/>. What the page actually sent is the bridge server's
/// <see cref="TrafficRecorder"/>'s to keep.
/// </summary>
public sealed class SimulatorState
{
    const int MaxLog = 500;

    readonly WebAppEventHub hub;
    readonly AppDeviceBridgeOptions options;
    readonly Lock logGate = new();
    readonly LinkedList<ActivityRecord> activity = new();
    volatile bool stickyWrites = true;

    public SimulatorState(WebAppEventHub hub, AppDeviceBridgeOptions options, TimeProvider? time = null, IEnumerable<SimBridge>? bridges = null)
    {
        this.hub = hub;
        this.options = options;
        this.Time = time ?? TimeProvider.System;
        this.Bridges = [.. (bridges ?? BridgeCatalog.All).Select(x => new BridgeState(x))];
    }

    public TimeProvider Time { get; }

    public IReadOnlyList<BridgeState> Bridges { get; }

    /// <summary>
    /// A PUT or POST whose body is the same contract its GET returns — <c>PUT wifi/radio</c>, <c>POST gps/listener</c> —
    /// becomes what that GET answers next, so the page reads back what it wrote.
    /// </summary>
    public bool StickyWrites
    {
        get => this.stickyWrites;
        set
        {
            this.stickyWrites = value;
            this.OnChanged();
        }
    }

    /// <summary><c>android</c>, <c>ios</c>, <c>maccatalyst</c>, <c>macos</c>, <c>windows</c> or <c>linux</c>: what <c>GET /_bridge/host</c> reports.</summary>
    public string Platform
    {
        get => this.options.Platform;
        set
        {
            this.options.Platform = value;
            this.Log($"platform is now {value}");
        }
    }

    public string AppId => this.options.AppId;

    /// <summary>Raised on any change: a behavior, a route called, an event fired. From any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised for each change made through the state — a value set, an event fired, a bridge switched — as the trail step
    /// that would repeat it. What <see cref="TrailRecording"/> listens to. A page's own writes are not changes made here.
    /// </summary>
    public event Action<TrailStep>? Applied;

    /// <summary>Raised for each line added to <see cref="Activity"/>. From any thread.</summary>
    public event Action<ActivityRecord>? Logged;

    public BridgeState? Find(string bridge) => this.Bridges.FirstOrDefault(x => x.Name == bridge);

    public EventState? FindEvent(string name) => this.Bridges.SelectMany(x => x.Events).FirstOrDefault(x => x.Event.Name == name);

    /// <summary>A route by <c>bridge/key</c> or by bridge and key: <c>("wifi", "GET current")</c>.</summary>
    public RouteState? FindRoute(string bridge, string key) => this.Find(bridge)?.FindRoute(key);

    public IReadOnlyList<ActivityRecord> Activity
    {
        get
        {
            lock (this.logGate)
                return [.. this.activity];
        }
    }

    public void SetSupported(string bridge, bool supported)
    {
        var state = this.Find(bridge) ?? throw new ArgumentException($"There is no '{bridge}' bridge.", nameof(bridge));
        state.IsSupported = supported;
        this.Log($"{bridge} is now {(supported ? "supported" : "unsupported (501)")}");
        this.Applied?.Invoke(new TrailStep { Bridge = bridge, Supported = supported });
    }

    /// <summary>Sets what a route answers. A value is checked against the route's contract first.</summary>
    /// <exception cref="ArgumentException">The route does not exist, or the value is not something its client can read.</exception>
    public void SetBehavior(string bridge, string key, RouteBehavior behavior)
    {
        var route = this.FindRoute(bridge, key) ?? throw new ArgumentException($"{bridge} has no route '{key}'.", nameof(key));

        if (behavior.Mode == ResponseMode.Value && route.Route.ResultType is { } type)
        {
            // Every value a sequence will answer with is checked now, not when the page reaches it: a sequence whose
            // third value is wrong should fail where it was set, not two polls into someone's test.
            foreach (var value in behavior.Sequence ?? [behavior.Json])
            {
                if (!SampleJson.TryValidate(value, type, route.Route.Json, out var error))
                    throw new ArgumentException($"{route.Route.Path} answers {type.Name}, and that value is not one: {error}", nameof(behavior));
            }
        }

        if (behavior.Mode == ResponseMode.Error && behavior.StatusCode is < 400 or > 599)
            throw new ArgumentException("An error needs a 4xx or 5xx status.", nameof(behavior));

        route.Behavior = behavior;
        this.OnChanged();
        this.Applied?.Invoke(new TrailStep
        {
            Bridge = bridge,
            Route = route.Route.Key,
            Mode = TrailStep.FormatMode(behavior.Mode),
            Value = behavior.Mode == ResponseMode.Value && behavior.Sequence is null && behavior.Json.Length > 0 ? JsonNode.Parse(behavior.Json) : null,
            Values = behavior.Sequence is { Count: > 0 } sequence ? [.. sequence.Select(x => JsonNode.Parse(x))] : null,
            Status = behavior.Mode == ResponseMode.Error ? behavior.StatusCode : null,
            Code = behavior.Mode == ResponseMode.Error ? behavior.ErrorCode : null,
            Message = behavior.Mode == ResponseMode.Error ? behavior.ErrorMessage : null,
            ResponseDelayMs = behavior.DelayMs > 0 ? behavior.DelayMs : null
        });
    }

    /// <summary>Just the value, keeping the route's other settings; the route answers with it from now on.</summary>
    public void SetValue(string bridge, string key, string json)
    {
        var route = this.FindRoute(bridge, key) ?? throw new ArgumentException($"{bridge} has no route '{key}'.", nameof(key));
        this.SetBehavior(bridge, key, route.Behavior with { Mode = ResponseMode.Value, Json = json });
    }

    public void ResetRoute(string bridge, string key)
    {
        var route = this.FindRoute(bridge, key) ?? throw new ArgumentException($"{bridge} has no route '{key}'.", nameof(key));
        this.SetBehavior(bridge, key, new RouteBehavior(ResponseMode.Value, route.DefaultJson));
    }

    /// <summary>Sets the payload an event sends when fired without one.</summary>
    public void SetEventPayload(string eventName, string json)
    {
        var evt = this.FindEvent(eventName) ?? throw new ArgumentException($"No bridge raises '{eventName}'.", nameof(eventName));
        Validate(evt, json);
        evt.Json = json;
        this.OnChanged();
    }

    /// <summary>
    /// Raises an event on the page's stream, as the native side would. Placeholders are filled in first. Returns how many
    /// page streams it reached.
    /// </summary>
    /// <exception cref="ArgumentException">No bridge raises it, or the payload is not its contract.</exception>
    public int Fire(string eventName, string? json = null)
    {
        var evt = this.FindEvent(eventName) ?? throw new ArgumentException($"No bridge raises '{eventName}'.", nameof(eventName));
        var written = json ?? evt.Json;
        var payload = PayloadTokens.Expand(written, this.Time);
        Validate(evt, payload);

        using var document = JsonDocument.Parse(payload);
        var source = evt.Source ??= this.hub.Source(eventName, AppDeviceBridgeJsonContext.Default.JsonElement);
        var listeners = source.ListenerCount;
        source.Publish(document.RootElement.Clone());

        evt.Fire();
        this.Log($"fired {eventName} → {listeners} stream{(listeners == 1 ? "" : "s")}");
        this.Applied?.Invoke(new TrailStep { Event = eventName, Payload = JsonNode.Parse(written) });
        return listeners;
    }

    /// <summary>Maps every simulated event on the hub, so a page can subscribe before the first one is fired.</summary>
    internal void MapEvents()
    {
        foreach (var evt in this.Bridges.SelectMany(x => x.Events))
            evt.Source ??= this.hub.Source(evt.Event.Name, AppDeviceBridgeJsonContext.Default.JsonElement);
    }

    /// <summary>Adds a line to the activity log.</summary>
    public void Log(string text)
    {
        var record = new ActivityRecord(this.Time.GetLocalNow(), text);
        lock (this.logGate)
        {
            this.activity.AddFirst(record);
            if (this.activity.Count > MaxLog)
                this.activity.RemoveLast();
        }

        this.Logged?.Invoke(record);
        this.OnChanged();
    }

    internal void OnChanged() => this.Changed?.Invoke();

    static void Validate(EventState evt, string json)
    {
        if (!SampleJson.TryValidate(json, evt.Event.PayloadType, evt.Event.Json, out var error))
            throw new ArgumentException($"{evt.Event.Name} carries {evt.Event.PayloadType.Name}, and that payload is not one: {error}", nameof(json));
    }
}
