using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>What the simulator is and is doing: where it serves, what it reports as, and what is playing.</summary>
public sealed record SimulatorStatus(
    string? Origin,
    string Platform,
    string AppId,
    bool StickyWrites,
    bool IsRecording,
    int Bridges,
    int Exchanges,
    IReadOnlyList<TrailStatus> Trails
);

/// <summary>A bridge, as a list of them shows it.</summary>
public sealed record BridgeSummary(
    string Name,
    string Package,
    bool IsSupported,
    int Routes,
    int Hits,
    IReadOnlyList<string> Events
);

/// <summary>A bridge with everything it answers and raises.</summary>
public sealed record BridgeDetail(
    string Name,
    string Package,
    bool IsSupported,
    IReadOnlyList<RouteDetail> Routes,
    IReadOnlyList<EventDetail> Events
);

/// <summary>
/// One route: what it is, and what it answers right now. <see cref="Sample"/> is the generated sample of its contract —
/// the shape a <c>value</c> has to have, which is what a caller needs before setting one.
/// </summary>
public sealed record RouteDetail(
    string Bridge,
    string Key,
    string Method,
    string Path,
    string Kind,
    string? Contract,
    string? BodyContract,
    IReadOnlyList<string> QueryParameters,
    string Mode,
    JsonNode? Value,
    IReadOnlyList<JsonNode?>? Sequence,
    int Step,
    int? Status,
    string? Code,
    string? Message,
    int DelayMs,
    string? File,
    int Hits,
    JsonNode? Sample
);

/// <summary>
/// One event. <see cref="Listeners"/> is how many page streams are subscribed right now — fire it with none and the
/// payload goes nowhere, which is nearly always why an event "did not arrive".
/// </summary>
public sealed record EventDetail(
    string Bridge,
    string Name,
    string Contract,
    JsonNode? Payload,
    JsonNode? Sample,
    int Listeners,
    int Fired
);

/// <summary>What firing an event did. <see cref="Listeners"/> is how many page streams it reached.</summary>
public sealed record FireResult(string Event, int Listeners, JsonNode? Payload);

/// <summary>A loaded trail and where its player is.</summary>
public sealed record TrailStatus(
    string Name,
    string Playback,
    int Position,
    int Steps,
    int Passes,
    double Speed,
    bool Loop,
    long DurationMs
);

/// <summary>One request the page made and what it got back.</summary>
public sealed record TrafficEntry(
    string Id,
    DateTimeOffset At,
    string Method,
    string Target,
    int Status,
    string Origin,
    double ElapsedMs,
    string? RequestBody,
    string? ResponseBody,
    string? Error
);

/// <summary>A line of the activity log.</summary>
public sealed record ActivityEntry(DateTimeOffset At, string Text);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(SimulatorStatus))]
[JsonSerializable(typeof(BridgeSummary))]
[JsonSerializable(typeof(BridgeDetail))]
[JsonSerializable(typeof(RouteDetail))]
[JsonSerializable(typeof(EventDetail))]
[JsonSerializable(typeof(FireResult))]
[JsonSerializable(typeof(TrailStatus))]
[JsonSerializable(typeof(TrafficEntry))]
[JsonSerializable(typeof(ActivityEntry))]
[JsonSerializable(typeof(IReadOnlyList<BridgeSummary>))]
[JsonSerializable(typeof(IReadOnlyList<RouteDetail>))]
[JsonSerializable(typeof(IReadOnlyList<EventDetail>))]
[JsonSerializable(typeof(IReadOnlyList<TrailStatus>))]
[JsonSerializable(typeof(IReadOnlyList<TrafficEntry>))]
[JsonSerializable(typeof(IReadOnlyList<ActivityEntry>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<JsonNode>))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonNode[]))]

// The tools' own parameters and returns. The MCP SDK builds each tool's schema against these options, so every type that
// appears in a tool signature has to be here — including the nullable scalars an optional parameter becomes.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(double?))]
public partial class ControlJsonContext : JsonSerializerContext;
