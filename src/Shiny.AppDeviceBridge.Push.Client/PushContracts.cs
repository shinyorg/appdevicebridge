using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Push.Client;

/// <summary>Push permission and registration.</summary>
/// <param name="Access">Whether the app may receive pushes.</param>
/// <param name="Token">The provider's token — what a server sends to. Null until registered.</param>
/// <param name="NativeToken">The platform's own token (APNs, FCM) where it differs from <paramref name="Token"/>.</param>
public sealed record PushStatus(AccessState Access, string? Token, string? NativeToken);

/// <summary>The tags a device is registered with.</summary>
public sealed record PushTags(IReadOnlyList<string> Tags);

/// <summary>A token that was issued or unregistered.</summary>
public sealed record PushTokenEvent(string Token);

/// <summary>A push as the web app sees it.</summary>
/// <param name="Data">The push's data fields.</param>
/// <param name="Title">The visible notification's title, when the push carried one.</param>
/// <param name="Message">The visible notification's body, when the push carried one.</param>
public sealed record PushPayload(IReadOnlyDictionary<string, string> Data, string? Title, string? Message);

/// <summary>Serialization for every push contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PushStatus))]
[JsonSerializable(typeof(PushTags))]
[JsonSerializable(typeof(PushTokenEvent))]
[JsonSerializable(typeof(PushPayload))]
public partial class PushJsonContext : JsonSerializerContext;
