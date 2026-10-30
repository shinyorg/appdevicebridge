using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Biometrics.Client;

/// <summary>
/// Verifies the device owner with the platform's own biometric prompt: Touch ID, Face ID and Optic ID on iOS, Mac
/// Catalyst and macOS, fingerprint, face and iris on Android, and Windows Hello. The answer is a local yes/no for gating
/// the page's UI. Don't send it to a server as proof of identity. Linux answers 501.
/// </summary>
[BridgeClient("biometrics", typeof(BiometricsJsonContext))]
public interface IBiometricsBridge
{
    /// <summary>
    /// What the device can do right now (hardware, enrollment, lockout, passcode), with no UI. On Android,
    /// <c>strength</c> is the class of biometric the page will ask for.
    /// </summary>
    [BridgeGet]
    Task<BiometricAvailability> GetAvailabilityAsync(BiometricStrength strength = BiometricStrength.Strong, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the platform prompt. A cancel, lockout or missing enrollment is a result
    /// (<see cref="BiometricResult.Status"/>), not an error. Fails with 400 when the reason is empty or longer than 256
    /// characters, and with <c>409 cannot_prompt</c> when the app has nothing to show the prompt from (no Android
    /// activity, or an iOS app missing <c>NSFaceIDUsageDescription</c>).
    /// </summary>
    [BridgePost("authenticate")]
    Task<BiometricResult> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default);
}
