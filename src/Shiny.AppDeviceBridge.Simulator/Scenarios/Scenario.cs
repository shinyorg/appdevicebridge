using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;

namespace Shiny.AppDeviceBridge.Simulator.Scenarios;

/// <summary>
/// A saved simulator setup — the platform, which bridges exist, what routes answer, what events carry, and trails — so the
/// same test can be set up again, by anyone, from a file checked in beside the app.
/// <code>
/// {
///   "platform": "ios",
///   "bridges": {
///     "wifi": {
///       "routes": { "GET current": { "mode": "null" } },
///       "events": { "wifi.changed": { "current": null } }
///     },
///     "ble": { "supported": false }
///   },
///   "trails": [ { "name": "Walk", "steps": [ … ] } ]
/// }
/// </code>
/// Only what differs from the defaults is written.
/// </summary>
public sealed class Scenario
{
    public string? Platform { get; set; }

    public bool? StickyWrites { get; set; }

    public Dictionary<string, ScenarioBridge> Bridges { get; set; } = [];

    public List<Trail> Trails { get; set; } = [];

    public static Scenario Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.Scenario)
               ?? throw new InvalidDataException($"{path} is not a scenario.");
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, ScenarioJsonContext.Default.Scenario);
    }

    /// <summary>What <paramref name="state"/> holds that differs from a fresh simulator, plus <paramref name="trails"/>.</summary>
    public static Scenario Capture(SimulatorState state, IEnumerable<Trail> trails)
    {
        var scenario = new Scenario
        {
            Platform = state.Platform,
            StickyWrites = state.StickyWrites ? null : false,
            Trails = [.. trails]
        };

        foreach (var bridge in state.Bridges)
        {
            var saved = new ScenarioBridge { Supported = bridge.IsSupported ? null : false };

            foreach (var route in bridge.Routes)
            {
                var behavior = route.Behavior;
                if (behavior == new RouteBehavior(ResponseMode.Value, route.DefaultJson))
                    continue;

                saved.Routes[route.Route.Key] = new ScenarioRoute
                {
                    Mode = TrailStep.FormatMode(behavior.Mode),
                    Value = behavior.Mode == ResponseMode.Value && behavior.Sequence is null && behavior.Json != route.DefaultJson && behavior.Json.Length > 0 ? JsonNode.Parse(behavior.Json) : null,
                    Values = behavior.Sequence is { Count: > 0 } sequence ? [.. sequence.Select(x => JsonNode.Parse(x))] : null,
                    Status = behavior.Mode == ResponseMode.Error ? behavior.StatusCode : null,
                    Code = behavior.Mode == ResponseMode.Error ? behavior.ErrorCode : null,
                    Message = behavior.Mode == ResponseMode.Error ? behavior.ErrorMessage : null,
                    DelayMs = behavior.DelayMs > 0 ? behavior.DelayMs : null,
                    File = behavior.FilePath
                };
            }

            foreach (var evt in bridge.Events)
            {
                if (evt.Json != evt.DefaultJson)
                    saved.Events[evt.Event.Name] = JsonNode.Parse(evt.Json);
            }

            if (saved.Supported is not null || saved.Routes.Count > 0 || saved.Events.Count > 0)
                scenario.Bridges[bridge.Name] = saved;
        }

        return scenario;
    }

    /// <summary>Applies the scenario to <paramref name="state"/>. Returns what could not be applied, one line each.</summary>
    public IReadOnlyList<string> Apply(SimulatorState state)
    {
        var problems = new List<string>();

        if (this.Platform is { Length: > 0 } platform)
            state.Platform = platform;

        if (this.StickyWrites is { } sticky)
            state.StickyWrites = sticky;

        foreach (var (name, saved) in this.Bridges)
        {
            var bridge = state.Find(name);
            if (bridge is null)
            {
                problems.Add($"There is no '{name}' bridge.");
                continue;
            }

            if (saved.Supported is { } supported)
                state.SetSupported(name, supported);

            foreach (var (key, route) in saved.Routes)
            {
                try
                {
                    var current = bridge.FindRoute(key) ?? throw new ArgumentException($"{name} has no route '{key}'.");
                    var sequence = route.Values is { Count: > 0 } values ? values.Select(x => x?.ToJsonString() ?? "null").ToList() : null;

                    state.SetBehavior(name, key, new RouteBehavior(
                        TrailStep.ParseMode(route.Mode),
                        sequence?[0] ?? route.Value?.ToJsonString() ?? current.DefaultJson,
                        route.Status ?? 501,
                        route.Code ?? "not_supported",
                        route.Message ?? "Simulated failure.",
                        route.DelayMs ?? 0,
                        route.File,
                        sequence
                    ));
                }
                catch (ArgumentException ex)
                {
                    problems.Add(ex.Message);
                }
            }

            foreach (var (eventName, payload) in saved.Events)
            {
                try
                {
                    state.SetEventPayload(eventName, payload?.ToJsonString() ?? "null");
                }
                catch (ArgumentException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }

        return problems;
    }
}

public sealed class ScenarioBridge
{
    /// <summary>False: the bridge answers 501 everywhere and reports itself unsupported.</summary>
    public bool? Supported { get; set; }

    /// <summary>By route key: <c>GET current</c>.</summary>
    public Dictionary<string, ScenarioRoute> Routes { get; set; } = [];

    /// <summary>Payloads, by event name.</summary>
    public Dictionary<string, JsonNode?> Events { get; set; } = [];
}

public sealed class ScenarioRoute
{
    /// <summary><c>value</c>, <c>null</c> or <c>error</c>.</summary>
    public string? Mode { get; set; }

    public JsonNode? Value { get; set; }

    /// <summary>Values answered one per call, the last repeating — instead of <see cref="Value"/>.</summary>
    public List<JsonNode?>? Values { get; set; }

    public int? Status { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }

    public int? DelayMs { get; set; }

    /// <summary>For a route that answers with bytes: the file it sends.</summary>
    public string? File { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true
)]
[JsonSerializable(typeof(Scenario))]
[JsonSerializable(typeof(Trail))]
public partial class ScenarioJsonContext : JsonSerializerContext;
