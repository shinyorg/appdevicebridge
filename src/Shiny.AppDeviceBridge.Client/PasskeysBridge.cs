using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// Creates and uses passkeys (WebAuthn / FIDO2 public-key credentials) with the platform authenticator: Face ID and
/// Touch ID on iOS and Mac Catalyst, Android's credential manager, and Windows Hello. The page is the go-between — it
/// fetches the options from its relying party server, hands them to the bridge as they are, and posts the response back
/// for the server to verify. The bridge never creates challenges or verifies anything. Only the relying party ids the app
/// registered are accepted. The macOS AppKit and Linux heads answer 501.
/// </summary>
[BridgeClient("passkeys", typeof(PasskeysJsonContext))]
public interface IPasskeysBridge
{
    /// <summary>Whether this platform and OS version can create and use passkeys, and the relying party ids the app accepts.</summary>
    [BridgeGet]
    Task<PasskeyStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new passkey through the native "create a passkey" sheet. Fails with 400 when the options have no
    /// <c>rp.id</c>, 403 <c>rp_not_allowed</c> when the app did not register that id, 409 <c>cancelled</c> when the user
    /// dismisses the sheet, and 409 <c>failed</c> when the ceremony fails — most often a missing associated domain (Apple)
    /// or Digital Asset Links file (Android) for the relying party.
    /// </summary>
    [BridgePost("create")]
    Task<PasskeyCreateResult> CreateAsync(PasskeyCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in with an existing passkey through the native sheet. Fails like <see cref="CreateAsync"/>; the options must
    /// carry <c>rpId</c>, and 409 <c>failed</c> also covers no matching passkey on the device.
    /// </summary>
    [BridgePost("assert")]
    Task<PasskeyAssertResult> AssertAsync(PasskeyAssertRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Supported">This platform and OS version can create and use passkeys.</param>
/// <param name="RelyingPartyIds">The relying party ids the app registered; options for any other id are refused.</param>
public sealed record PasskeyStatus(bool Supported, IReadOnlyList<string> RelyingPartyIds);

/// <param name="Options">The relying party server's <c>PublicKeyCredentialCreationOptions</c> JSON, passed through as it came.</param>
/// <param name="PreferImmediatelyAvailable">
/// Stay on this device: only create the passkey if the local authenticator can, with no QR code or "use another
/// device" step. Apple and Android; ignored on Windows.
/// </param>
public sealed record PasskeyCreateRequest(JsonElement Options, bool PreferImmediatelyAvailable = false);

/// <param name="Id">The new passkey's credential id (base64url), the WebAuthn <c>PublicKeyCredential.id</c>.</param>
/// <param name="Response">The WebAuthn registration response JSON. Post it to the relying party server to verify and store.</param>
public sealed record PasskeyCreateResult(string Id, JsonElement Response);

/// <param name="Options">The relying party server's <c>PublicKeyCredentialRequestOptions</c> JSON, passed through as it came.</param>
/// <param name="PreferImmediatelyAvailable">Only offer a passkey already on this device. Apple and Android; ignored on Windows.</param>
public sealed record PasskeyAssertRequest(JsonElement Options, bool PreferImmediatelyAvailable = false);

/// <param name="Id">The credential id (base64url) of the passkey used.</param>
/// <param name="UserHandle">
/// The <c>user.id</c> (base64url) the relying party set at registration, for username-less sign-in; null when the
/// authenticator does not return one.
/// </param>
/// <param name="Response">The WebAuthn authentication response JSON. Post it to the relying party server to verify.</param>
public sealed record PasskeyAssertResult(string Id, string? UserHandle, JsonElement Response);

/// <summary>Serialization for every passkeys contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PasskeyStatus))]
[JsonSerializable(typeof(PasskeyCreateRequest))]
[JsonSerializable(typeof(PasskeyCreateResult))]
[JsonSerializable(typeof(PasskeyAssertRequest))]
[JsonSerializable(typeof(PasskeyAssertResult))]
public partial class PasskeysJsonContext : JsonSerializerContext;
