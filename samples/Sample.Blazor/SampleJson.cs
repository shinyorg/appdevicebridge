using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sample.Blazor;

/// <summary>What background.js has left behind, as the Background page shows it.</summary>
public sealed record BackgroundReport(int BackgroundSyncRuns, JsonElement? LastBackgroundPosition, string Log);

/// <summary>What the sample's sync job reports back to the host.</summary>
public sealed record JobResult(string RanIn);

/// <summary>What the sample answers a watch's message with.</summary>
public sealed record WatchReply(string AnsweredBy, string Path);

/// <summary>The native sample's traffic settings, from its <c>sample-traffic</c> bridge. Which keys are set, never the keys.</summary>
public sealed record TrafficProviderSettings(string Flow, bool Incidents, IReadOnlyList<string> Keys, bool FlowActive, bool IncidentsActive);

public sealed record TrafficProviderKey(string Key);

public sealed record TrafficProviderSelection(string Flow, bool Incidents);

/// <summary>The sample's own values: settings it reads with its own types, and what its pages display.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(BackgroundReport))]
[JsonSerializable(typeof(JobResult))]
[JsonSerializable(typeof(WatchReply))]
[JsonSerializable(typeof(TrafficProviderSettings))]
[JsonSerializable(typeof(TrafficProviderKey))]
[JsonSerializable(typeof(TrafficProviderSelection))]
public partial class SampleJson : JsonSerializerContext;
