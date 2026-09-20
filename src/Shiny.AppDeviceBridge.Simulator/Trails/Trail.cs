using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>
/// A timed script the simulator plays against the page: events fired, route answers changed, bridges switched off — a walk
/// with the GPS, a BLE device appearing and dropping, the network going away mid-sync.
/// <code>
/// {
///   "name": "Lose Wi-Fi",
///   "steps": [
///     { "delayMs": 0,    "event": "wifi.changed", "payload": { "current": null } },
///     { "delayMs": 0,    "bridge": "wifi", "route": "GET current", "mode": "null" },
///     { "delayMs": 5000, "bridge": "wifi", "route": "GET current", "value": { "interfaceName": "en0", "ssid": "Home" } }
///   ]
/// }
/// </code>
/// </summary>
public sealed class Trail
{
    public string Name { get; set; } = "Trail";

    /// <summary>Starts over after the last step, until stopped.</summary>
    public bool Loop { get; set; }

    public List<TrailStep> Steps { get; set; } = [];

    /// <summary>How long one pass takes at normal speed.</summary>
    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromMilliseconds(this.Steps.Sum(x => (long)Math.Max(0, x.DelayMs)));
}

/// <summary>
/// One step: after <see cref="DelayMs"/>, fires <see cref="Event"/>, or sets what <see cref="Bridge"/>'s <see cref="Route"/>
/// answers, or — with only <see cref="Bridge"/> and <see cref="Supported"/> — switches a whole bridge on or off.
/// </summary>
public sealed class TrailStep
{
    /// <summary>Waited before the step, from the one before it; divided by the playback speed.</summary>
    public int DelayMs { get; set; }

    /// <summary>A note shown while the step plays.</summary>
    public string? Note { get; set; }

    /// <summary>An event to fire: <c>gps.reading</c>.</summary>
    public string? Event { get; set; }

    /// <summary>The event's payload; the event's current payload when absent. May hold <c>"$now"</c>.</summary>
    public JsonNode? Payload { get; set; }

    /// <summary>The bridge whose route or support the step changes: <c>wifi</c>.</summary>
    public string? Bridge { get; set; }

    /// <summary>The route's key within the bridge: <c>GET current</c>, <c>POST connection</c>.</summary>
    public string? Route { get; set; }

    /// <summary><c>value</c> (the default), <c>null</c> for a 204, or <c>error</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>What the route answers with, in <c>value</c> mode.</summary>
    public JsonNode? Value { get; set; }

    /// <summary>Values answered one per call, the last repeating — instead of <see cref="Value"/>.</summary>
    public List<JsonNode?>? Values { get; set; }

    /// <summary>The error's status, in <c>error</c> mode; 501 when absent.</summary>
    public int? Status { get; set; }

    /// <summary>The error's code, in <c>error</c> mode: <c>access_denied</c>.</summary>
    public string? Code { get; set; }

    public string? Message { get; set; }

    /// <summary>With <see cref="Route"/>, a delay before the route answers; with neither route nor event, nothing.</summary>
    public int? ResponseDelayMs { get; set; }

    /// <summary>With only <see cref="Bridge"/>: switches the bridge on or off (off answers 501 everywhere).</summary>
    public bool? Supported { get; set; }

    /// <summary>What the step does, in a few words.</summary>
    public string Describe()
    {
        if (this.Note is { Length: > 0 } note)
            return note;

        if (this.Event is { } evt)
            return $"fire {evt}";

        if (this.Bridge is { } bridge && this.Route is { } route)
        {
            return ParseMode(this.Mode) switch
            {
                ResponseMode.Null => $"{bridge} {route} → 204",
                ResponseMode.Error => $"{bridge} {route} → {this.Status ?? 501} {this.Code}",
                _ => $"{bridge} {route} → value"
            };
        }

        if (this.Bridge is { } only && this.Supported is { } supported)
            return $"{only} {(supported ? "supported" : "unsupported")}";

        return "wait";
    }

    /// <summary>Plays the step against <paramref name="state"/>.</summary>
    /// <exception cref="ArgumentException">It names something that does not exist, or carries a value that is not its contract.</exception>
    public void Apply(SimulatorState state)
    {
        if (this.Event is { } evt)
        {
            state.Fire(evt, this.Payload?.ToJsonString());
            return;
        }

        if (this.Bridge is not { } bridge)
            return;

        if (this.Route is { } key)
        {
            var route = state.FindRoute(bridge, key) ?? throw new ArgumentException($"{bridge} has no route '{key}'.");
            var current = route.Behavior;
            var mode = ParseMode(this.Mode);

            var sequence = this.Values is { Count: > 0 } values ? values.Select(x => x?.ToJsonString() ?? "null").ToList() : null;

            state.SetBehavior(bridge, key, current with
            {
                Mode = mode,
                Json = sequence?[0] ?? this.Value?.ToJsonString() ?? current.Json,
                Sequence = sequence,
                StatusCode = this.Status ?? (mode == ResponseMode.Error ? 501 : current.StatusCode),
                ErrorCode = this.Code ?? current.ErrorCode,
                ErrorMessage = this.Message ?? current.ErrorMessage,
                DelayMs = this.ResponseDelayMs ?? current.DelayMs
            });
            return;
        }

        if (this.Supported is { } supported)
            state.SetSupported(bridge, supported);
    }

    public static ResponseMode ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" or "value" => ResponseMode.Value,
        "null" or "nocontent" or "204" => ResponseMode.Null,
        "error" => ResponseMode.Error,
        _ => throw new ArgumentException($"'{mode}' is not a mode: use value, null or error.")
    };

    public static string FormatMode(ResponseMode mode) => mode switch
    {
        ResponseMode.Null => "null",
        ResponseMode.Error => "error",
        _ => "value"
    };
}
