using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sample.Blazor;

/// <summary>What background.js has left behind, as the Background page shows it.</summary>
public sealed record BackgroundReport(int BackgroundSyncRuns, JsonElement? LastBackgroundPosition, string Log);

/// <summary>What the sample's sync job reports back to the host.</summary>
public sealed record JobResult(string RanIn);

/// <summary>What the sample answers a watch's message with.</summary>
public sealed record WatchReply(string AnsweredBy, string Path);

/// <summary>The sample's own values: settings it reads with its own types, and what its pages display.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(BackgroundReport))]
[JsonSerializable(typeof(JobResult))]
[JsonSerializable(typeof(WatchReply))]
public partial class SampleJson : JsonSerializerContext;
