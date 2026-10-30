using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.ShareTarget.Client;

/// <summary>Something another app shared to this one.</summary>
/// <param name="Id">Names the share for <see cref="IShareTargetBridge.GetAsync"/> and <see cref="IShareTargetBridge.RemoveAsync"/>.</param>
/// <param name="ReceivedAt">When the app received it.</param>
/// <param name="Text">Shared text. Text that is nothing but an http or https link is in <paramref name="Url"/> instead.</param>
/// <param name="Subject">The subject or title the sending app gave, where the platform carries one.</param>
/// <param name="Url">A shared link: always an absolute http or https URL.</param>
/// <param name="Files">Shared files, copied into a file root; empty when only text or a link was shared.</param>
public sealed record SharedContent(
    string Id,
    DateTimeOffset ReceivedAt,
    string? Text,
    string? Subject,
    string? Url,
    IReadOnlyList<SharedFile> Files
);

/// <summary>A shared file, copied into a file root under <c>shares/{id}/</c> and read through <c>/_bridge/files</c>.</summary>
/// <param name="File">Where it is.</param>
/// <param name="FileName">The name the sending app gave it.</param>
/// <param name="ContentType">Its media type, when the platform said.</param>
/// <param name="Size">Its size in bytes.</param>
public sealed record SharedFile(BridgeFile File, string FileName, string? ContentType, long Size);

/// <summary>Serialization for every share target contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SharedContent))]
[JsonSerializable(typeof(IReadOnlyList<SharedContent>))]
[JsonSerializable(typeof(List<SharedContent>))]
public partial class ShareTargetJsonContext : JsonSerializerContext;
