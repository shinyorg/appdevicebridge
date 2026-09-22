using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Scenarios;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>
/// Everything the TUI can do to the simulator, as plain calls over the same <see cref="SimulatorState"/>: what a route
/// answers, what an event carries, which bridges exist, which trails play, and what the page actually asked for.
/// <para>
/// This is the whole control surface. <see cref="McpTools"/> exposes it over MCP so an agent can drive a running
/// simulator; nothing here knows that. Every change goes through the state, so it is validated against the bridge's
/// contract, recorded by a trail that is recording, and written to the activity log the human is watching.
/// </para>
/// </summary>
/// <param name="actor">Who the activity log says made a change: <c>agent</c> over MCP, <c>panel</c> from the web panel.</param>
public sealed class SimulatorControl(SimulatorState state, TrafficRecorder traffic, TrailLibrary trails, AppDeviceBridgeServer server, string actor = "agent")
{
    /// <summary>Text kept per body in <see cref="GetTraffic"/>, so one exchange cannot fill an agent's context.</summary>
    public const int MaxBodyText = 4 * 1024;

    /// <summary>Over a host, for a caller that already has one.</summary>
    public static SimulatorControl For(SimulatorHost host, string actor = "agent")
    {
        ArgumentNullException.ThrowIfNull(host);
        return new SimulatorControl(host.State, host.Traffic, host.Trails, host.Server, actor);
    }

    public SimulatorStatus GetStatus() => new(
        server.Origin?.ToString(),
        state.Platform,
        state.AppId,
        state.StickyWrites,
        traffic.IsRecording,
        state.Bridges.Count,
        traffic.Snapshot().Count,
        [.. trails.Players.Select(Describe)]
    );

    public IReadOnlyList<BridgeSummary> ListBridges() => [.. state.Bridges.Select(Summarize)];

    public BridgeDetail DescribeBridge(string bridge)
    {
        var found = this.Bridge(bridge);
        return new BridgeDetail(
            found.Name,
            found.Bridge.Package,
            found.IsSupported,
            [.. found.Routes.Select(x => Describe(found, x))],
            [.. found.Events.Select(x => Describe(found, x))]
        );
    }

    public RouteDetail GetRoute(string bridge, string key)
    {
        var found = this.Bridge(bridge);
        return Describe(found, this.Route(found, key));
    }

    /// <summary>
    /// Sets what a route answers. What is not given is kept, so a delay can be added without restating the value.
    /// </summary>
    /// <exception cref="ArgumentException">The route does not exist, the mode is not one, or the value is not the route's contract.</exception>
    public RouteDetail SetRoute(
        string bridge,
        string key,
        string? mode = null,
        JsonNode? value = null,
        int? status = null,
        string? code = null,
        string? message = null,
        int? delayMs = null,
        string? file = null
    )
    {
        var found = this.Bridge(bridge);
        var route = this.Route(found, key);
        var current = route.Behavior;

        // A value with no mode means the obvious thing: answer with it.
        var parsed = TrailStep.ParseMode(mode ?? (value is not null ? "value" : TrailStep.FormatMode(current.Mode)));

        state.SetBehavior(bridge, route.Route.Key, current with
        {
            Mode = parsed,
            Json = value?.ToJsonString() ?? current.Json,

            // A plain value replaces a sequence outright; leaving it would answer from the old list and ignore what was set.
            Sequence = value is not null ? null : current.Sequence,
            StatusCode = status ?? (parsed == ResponseMode.Error && current.Mode != ResponseMode.Error ? 501 : current.StatusCode),
            ErrorCode = code ?? current.ErrorCode,
            ErrorMessage = message ?? current.ErrorMessage,
            DelayMs = delayMs ?? current.DelayMs,
            FilePath = file ?? current.FilePath
        });

        state.Log($"{actor} set {route.Route.Path} → {TrailStep.FormatMode(parsed)}");
        return Describe(found, route);
    }

    /// <summary>
    /// Makes a route answer a different value on each call, the last one repeating — the shape of a page that polls:
    /// queued, then running, then done. Every value is checked against the contract before any of them is used.
    /// </summary>
    /// <exception cref="ArgumentException">There are no values, or one of them is not the route's contract.</exception>
    public RouteDetail SetRouteSequence(string bridge, string key, IReadOnlyList<JsonNode?> values, int? delayMs = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
            throw new ArgumentException("A sequence needs at least one value.", nameof(values));

        var found = this.Bridge(bridge);
        var route = this.Route(found, key);
        var sequence = values.Select(x => x?.ToJsonString() ?? "null").ToList();

        state.SetBehavior(bridge, route.Route.Key, route.Behavior with
        {
            Mode = ResponseMode.Value,
            Json = sequence[0],
            Sequence = sequence,
            DelayMs = delayMs ?? route.Behavior.DelayMs
        });

        state.Log($"{actor} set {route.Route.Path} → {sequence.Count} values in turn");
        return Describe(found, route);
    }

    /// <summary>Puts a route back to the generated sample of its contract.</summary>
    public RouteDetail ResetRoute(string bridge, string key)
    {
        var found = this.Bridge(bridge);
        var route = this.Route(found, key);

        state.ResetRoute(bridge, route.Route.Key);
        state.Log($"{actor} reset {route.Route.Path}");
        return Describe(found, route);
    }

    /// <summary>Switches a whole bridge off — every route answers 501, as on a platform without it — or back on.</summary>
    public BridgeSummary SetBridgeSupported(string bridge, bool supported)
    {
        var found = this.Bridge(bridge);
        state.SetSupported(found.Name, supported);
        return Summarize(found);
    }

    /// <summary>What <c>GET /_bridge/host</c> reports, which is what a page's platform checks read.</summary>
    /// <exception cref="ArgumentException">Not a platform the library has a head for.</exception>
    public SimulatorStatus SetPlatform(string platform)
    {
        var name = (platform ?? String.Empty).ToLowerInvariant();
        if (!SimulatorOptions.Platforms.Contains(name))
            throw new ArgumentException($"'{platform}' is not a platform: use {String.Join(", ", SimulatorOptions.Platforms)}.", nameof(platform));

        state.Platform = name;
        return this.GetStatus();
    }

    public SimulatorStatus SetStickyWrites(bool on)
    {
        state.StickyWrites = on;
        state.Log($"{actor} turned sticky writes {(on ? "on" : "off")}");
        return this.GetStatus();
    }

    /// <summary>Raises an event on the page's stream, as the native side would.</summary>
    /// <exception cref="ArgumentException">No bridge raises it, or the payload is not its contract.</exception>
    public FireResult FireEvent(string name, JsonNode? payload = null)
    {
        var evt = this.Event(name);
        var listeners = state.Fire(evt.Event.Name, payload?.ToJsonString());
        return new FireResult(evt.Event.Name, listeners, Parse(evt.Json));
    }

    /// <summary>Sets what an event carries when it is fired without a payload.</summary>
    public EventDetail SetEventPayload(string name, JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var evt = this.Event(name);
        state.SetEventPayload(evt.Event.Name, payload.ToJsonString());
        state.Log($"{actor} set the payload of {evt.Event.Name}");

        var bridge = this.Bridge(evt.Event.Bridge);
        return Describe(bridge, evt);
    }

    public IReadOnlyList<TrailStatus> ListTrails() => [.. trails.Players.Select(Describe)];

    /// <summary>Loads a trail from a <c>.gpx</c> or <c>.trail.json</c> file, replacing one of the same name.</summary>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    public TrailStatus LoadTrailFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"There is no trail at {full}.", full);

        var player = trails.Load(full);
        state.Log($"{actor} loaded the trail {player.Trail.Name}");
        return Describe(player);
    }

    /// <summary>Adds a trail written out in full, replacing one of the same name — a script an agent composed itself.</summary>
    /// <exception cref="ArgumentException">The JSON is not a trail.</exception>
    public TrailStatus LoadTrail(JsonNode trail)
    {
        ArgumentNullException.ThrowIfNull(trail);

        var parsed = trail.Deserialize(ScenarioJsonContext.Default.Trail)
            ?? throw new ArgumentException("That is not a trail.", nameof(trail));

        if (parsed.Steps.Count == 0)
            throw new ArgumentException("A trail needs at least one step.", nameof(trail));

        var player = trails.Add(parsed);
        state.Log($"{actor} added the trail {player.Trail.Name}");
        return Describe(player);
    }

    /// <summary>Plays a loaded trail from its first step.</summary>
    public TrailStatus PlayTrail(string name, double? speed = null, bool? loop = null)
    {
        var player = this.Trail(name);

        if (speed is { } value)
        {
            if (value <= 0)
                throw new ArgumentException("Speed must be positive.", nameof(speed));

            player.Speed = value;
        }

        if (loop is { } repeat)
            player.Loop = repeat;

        _ = player.Play();
        state.Log($"{actor} played the trail {player.Trail.Name}");
        return Describe(player);
    }

    public TrailStatus PauseTrail(string name)
    {
        var player = this.Trail(name);
        if (player.Playback == TrailPlayback.Paused)
            player.Resume();
        else
            player.Pause();

        return Describe(player);
    }

    public TrailStatus StopTrail(string name)
    {
        var player = this.Trail(name);
        player.Stop();
        return Describe(player);
    }

    public void RemoveTrail(string name)
    {
        var player = this.Trail(name);
        trails.Remove(player);
        state.Log($"{actor} removed the trail {player.Trail.Name}");
    }

    /// <summary>Applies a saved scenario: the platform, which bridges exist, what routes answer, and its trails.</summary>
    /// <returns>What could not be applied, one line each; empty when all of it was.</returns>
    public IReadOnlyList<string> ApplyScenarioFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"There is no scenario at {full}.", full);

        var scenario = Scenario.Load(full);
        var problems = scenario.Apply(state);

        foreach (var trail in scenario.Trails)
            trails.Add(trail);

        state.Log($"{actor} applied the scenario {Path.GetFileName(full)}");
        return problems;
    }

    /// <summary>Applies a scenario written out in full.</summary>
    public IReadOnlyList<string> ApplyScenario(JsonNode scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var parsed = scenario.Deserialize(ScenarioJsonContext.Default.Scenario)
            ?? throw new ArgumentException("That is not a scenario.", nameof(scenario));

        var problems = parsed.Apply(state);

        foreach (var trail in parsed.Trails)
            trails.Add(trail);

        state.Log($"{actor} applied a scenario");
        return problems;
    }

    /// <summary>Everything set up right now, as a scenario — to save beside the app and replay, or hand back as a reproduction.</summary>
    public JsonNode CaptureScenario()
    {
        var scenario = Scenario.Capture(state, trails.Trails);
        return JsonSerializer.SerializeToNode(scenario, ScenarioJsonContext.Default.Scenario)!;
    }

    /// <summary>
    /// What the page asked for, newest first. <paramref name="match"/> is looked for in <c>METHOD path?query</c>,
    /// case-insensitively: <c>GET /_bridge/wifi</c>, <c>wifi</c>, <c>POST</c>.
    /// </summary>
    public IReadOnlyList<TrafficEntry> GetTraffic(string? match = null, int limit = 50, bool bodies = false)
    {
        if (limit <= 0)
            throw new ArgumentException("Ask for at least one.", nameof(limit));

        return
        [
            .. traffic.Snapshot()
                .Where(x => Matches(x, match))
                .Take(limit)
                .Select(x => Describe(x, bodies))
        ];
    }

    /// <summary>Throws away what was recorded, so the next look holds only what happened after this.</summary>
    public void ClearTraffic() => traffic.Clear();

    public bool SetRecording(bool on)
    {
        traffic.IsRecording = on;
        return traffic.IsRecording;
    }

    public IReadOnlyList<ActivityEntry> GetActivity(int limit = 50)
    {
        if (limit <= 0)
            throw new ArgumentException("Ask for at least one.", nameof(limit));

        return [.. state.Activity.Take(limit).Select(x => new ActivityEntry(x.At, x.Text))];
    }

    /// <summary>
    /// Waits for the page to make a request matching <paramref name="match"/> and returns it — the other half of setting a
    /// value: set it, then wait for the page to ask, instead of guessing how long it takes.
    /// <para>
    /// Only requests made after this call count, so a matching one from earlier does not return at once.
    /// </para>
    /// </summary>
    /// <exception cref="TimeoutException">Nothing matched in time.</exception>
    public async Task<TrafficEntry> WaitForRequestAsync(string match, int timeoutMs = 10_000, bool bodies = true, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(match))
            throw new ArgumentException("Say what to wait for.", nameof(match));

        if (timeoutMs <= 0)
            throw new ArgumentException("Wait for at least a moment.", nameof(timeoutMs));

        // By id, so what is already recorded is ignored without depending on either clock.
        var already = traffic.Snapshot().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), state.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        try
        {
            var exchange = await traffic
                .WaitForAsync(x => x.StatusCode != 0 && !already.Contains(x.Id) && Matches(x, match), linked.Token)
                .ConfigureAwait(false);

            return Describe(exchange, bodies);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var seen = traffic.Snapshot().Where(x => !already.Contains(x.Id)).Take(5).Select(x => $"{x.Method} {x.Target}").ToList();
            var since = seen.Count == 0 ? "Nothing was requested at all." : $"What was requested: {String.Join(", ", seen)}.";
            throw new TimeoutException($"The page did not request '{match}' within {timeoutMs} ms. {since}");
        }
    }

    /// <summary>
    /// Waits until the page has been quiet for <paramref name="quietMs"/> — nothing requested — so a check runs after it
    /// has finished reacting rather than in the middle of it.
    /// </summary>
    /// <exception cref="TimeoutException">It never went quiet for that long.</exception>
    public async Task<SimulatorStatus> WaitForQuietAsync(int quietMs = 750, int timeoutMs = 10_000, CancellationToken cancellationToken = default)
    {
        if (quietMs <= 0)
            throw new ArgumentException("Quiet has to last some time.", nameof(quietMs));

        if (timeoutMs <= 0)
            throw new ArgumentException("Wait for at least a moment.", nameof(timeoutMs));

        var time = state.Time;
        var quiet = TimeSpan.FromMilliseconds(quietMs);
        var deadline = time.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs);
        var last = new StrongBox<long>(time.GetUtcNow().UtcTicks);

        void OnChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref last.Value, time.GetUtcNow().UtcTicks);

        traffic.Changed += OnChanged;
        try
        {
            while (true)
            {
                var now = time.GetUtcNow();
                var still = TimeSpan.FromTicks(now.UtcTicks - Interlocked.Read(ref last.Value));

                if (still >= quiet)
                    return this.GetStatus();

                if (now >= deadline)
                    throw new TimeoutException($"The page was still making requests after {timeoutMs} ms; it never went quiet for {quietMs} ms.");

                var step = quiet - still;
                await Task.Delay(step < TimeSpan.FromMilliseconds(25) ? TimeSpan.FromMilliseconds(25) : step, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            traffic.Changed -= OnChanged;
        }
    }

    BridgeState Bridge(string bridge) =>
        state.Find(bridge)
        ?? throw new ArgumentException($"There is no '{bridge}' bridge. There is {String.Join(", ", state.Bridges.Select(x => x.Name))}.", nameof(bridge));

    RouteState Route(BridgeState bridge, string key) =>
        bridge.FindRoute(key)
        ?? throw new ArgumentException($"{bridge.Name} has no route '{key}'. It has {String.Join(", ", bridge.Routes.Select(x => x.Route.Key))}.", nameof(key));

    EventState Event(string name) =>
        state.FindEvent(name)
        ?? throw new ArgumentException($"No bridge raises '{name}'. There is {String.Join(", ", state.Bridges.SelectMany(x => x.Events).Select(x => x.Event.Name))}.", nameof(name));

    TrailPlayer Trail(string name) =>
        trails.Find(name)
        ?? throw new ArgumentException($"No trail is named '{name}'. Loaded: {String.Join(", ", trails.Trails.Select(x => x.Name))}.", nameof(name));

    static BridgeSummary Summarize(BridgeState bridge) => new(
        bridge.Name,
        bridge.Bridge.Package,
        bridge.IsSupported,
        bridge.Routes.Count,
        bridge.Routes.Sum(x => x.Hits),
        [.. bridge.Events.Select(x => x.Event.Name)]
    );

    static RouteDetail Describe(BridgeState bridge, RouteState route)
    {
        var behavior = route.Behavior;
        var error = behavior.Mode == ResponseMode.Error;

        return new RouteDetail(
            bridge.Name,
            route.Route.Key,
            route.Route.Method,
            route.Route.Path,
            route.Route.Kind.ToString().ToLowerInvariant(),
            route.Route.ResultType?.Name,
            route.Route.BodyType?.Name,
            route.Route.QueryParameters,
            TrailStep.FormatMode(behavior.Mode),
            behavior.Mode == ResponseMode.Value && behavior.Sequence is null ? Parse(behavior.Json) : null,
            behavior.Sequence is { Count: > 0 } sequence ? [.. sequence.Select(Parse)] : null,
            route.Step,
            error ? behavior.StatusCode : null,
            error ? behavior.ErrorCode : null,
            error ? behavior.ErrorMessage : null,
            behavior.DelayMs,
            behavior.FilePath,
            route.Hits,
            Parse(route.DefaultJson)
        );
    }

    static EventDetail Describe(BridgeState bridge, EventState evt) => new(
        bridge.Name,
        evt.Event.Name,
        evt.Event.PayloadType.Name,
        Parse(evt.Json),
        Parse(evt.DefaultJson),
        evt.Listeners,
        evt.Fired
    );

    static TrailStatus Describe(TrailPlayer player) => new(
        player.Trail.Name,
        player.Playback.ToString().ToLowerInvariant(),
        player.Position,
        player.Trail.Steps.Count,
        player.Passes,
        player.Speed,
        player.Loop,
        (long)player.Trail.Duration.TotalMilliseconds
    );

    static TrafficEntry Describe(TrafficExchange exchange, bool bodies) => new(
        exchange.Id,
        exchange.StartedOn,
        exchange.Method,
        exchange.Target,
        exchange.StatusCode,
        exchange.Origin.ToString().ToLowerInvariant(),
        exchange.Elapsed.TotalMilliseconds,
        bodies ? Text(exchange.RequestBody) : null,
        bodies ? Text(exchange.ResponseBody) : null,
        exchange.Error
    );

    static bool Matches(TrafficExchange exchange, string? match) =>
        String.IsNullOrWhiteSpace(match)
        || $"{exchange.Method} {exchange.Target}".Contains(match.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A body as text, cut to <see cref="MaxBodyText"/>: an agent reading traffic should not be handed a whole wasm file.</summary>
    static string? Text(TrafficBody body) => body.Text switch
    {
        null => body.State == TrafficBodyState.Empty ? null : $"({body.State.ToString().ToLowerInvariant()}, {body.ByteCount} bytes)",
        { Length: > MaxBodyText } long_ => long_[..MaxBodyText] + $"… ({body.ByteCount} bytes in all)",
        var text => text
    };

    static JsonNode? Parse(string json) => json.Length == 0 ? null : JsonNode.Parse(json);
}
