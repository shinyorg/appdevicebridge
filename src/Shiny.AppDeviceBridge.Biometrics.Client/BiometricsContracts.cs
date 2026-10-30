using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Biometrics.Client;

/// <summary>Android: the class of biometric asked about or accepted. Apple biometrics are all strong; Windows Hello does not grade them.</summary>
public enum BiometricStrength
{
    /// <summary>Android class 3 (<c>BIOMETRIC_STRONG</c>).</summary>
    Strong,

    /// <summary>Android class 2 (<c>BIOMETRIC_WEAK</c>), which includes most camera-based face unlock.</summary>
    Weak
}

/// <summary>A kind of biometric hardware.</summary>
public enum BiometricType
{
    /// <summary>Touch ID, or an Android fingerprint sensor.</summary>
    Fingerprint,

    /// <summary>Face ID, or Android face unlock.</summary>
    Face,

    /// <summary>Optic ID, or an Android iris scanner.</summary>
    Iris,

    /// <summary>Biometrics are present but the platform does not say which: Windows Hello, or a newer Apple biometry type.</summary>
    Unknown
}

/// <summary>Whether biometrics can be used right now.</summary>
public enum BiometricStatus
{
    /// <summary>Biometrics can be used now.</summary>
    Available,

    /// <summary>The device has no biometric hardware.</summary>
    NotSupported,

    /// <summary>The hardware exists but nothing is enrolled (Windows: Hello is not set up).</summary>
    NotEnrolled,

    /// <summary>Too many failed attempts: the user must unlock with their passcode first.</summary>
    LockedOut,

    /// <summary>Apple requires a device passcode before biometrics can be used.</summary>
    PasscodeNotSet,

    /// <summary>The hardware is busy, disconnected or disabled by policy. Try again later.</summary>
    Unavailable,

    /// <summary>Android: the sensor needs a security update.</summary>
    SecurityUpdateRequired
}

/// <param name="Status">Whether biometrics can be used right now.</param>
/// <param name="Types">
/// The hardware present, reported even when nothing is enrolled or biometrics are locked out. Apple reports one type,
/// Android every sensor the device declares, and Windows <see cref="BiometricType.Unknown"/>.
/// </param>
/// <param name="IsDeviceCredentialSet">A passcode, PIN or pattern is set, so <see cref="BiometricRequest.AllowDeviceCredential"/> has something to fall back to.</param>
public sealed record BiometricAvailability(
    BiometricStatus Status,
    IReadOnlyList<BiometricType> Types,
    bool IsDeviceCredentialSet
);

/// <param name="Reason">Why the page is asking; shown in the prompt on every platform. Required, at most 256 characters.</param>
/// <param name="Title">Android: the prompt title (defaults to <paramref name="Reason"/>).</param>
/// <param name="Subtitle">Android: a line under the title.</param>
/// <param name="CancelText">Apple and Android: the cancel button.</param>
/// <param name="FallbackText">
/// Apple: the button shown after a failed match. Null keeps the system "Enter Password" and an empty string hides it.
/// With <paramref name="AllowDeviceCredential"/> off, tapping it returns <see cref="BiometricResultStatus.Fallback"/>.
/// </param>
/// <param name="AllowDeviceCredential">Accept the passcode, PIN or pattern too. Windows Hello always offers its PIN.</param>
/// <param name="Strength">Android: the class of biometric accepted.</param>
/// <param name="ConfirmationRequired">Android: for passive biometrics such as face, wait for the user to confirm after a match.</param>
public sealed record BiometricRequest(
    string Reason,
    string? Title = null,
    string? Subtitle = null,
    string? CancelText = null,
    string? FallbackText = null,
    bool AllowDeviceCredential = false,
    BiometricStrength Strength = BiometricStrength.Strong,
    bool ConfirmationRequired = true
);

/// <summary>The outcome of a prompt.</summary>
public enum BiometricResultStatus
{
    /// <summary>The device owner was verified.</summary>
    Success,

    /// <summary>The user or the system dismissed the prompt.</summary>
    Cancelled,

    /// <summary>Apple: the user tapped the fallback button. Show the page's own login.</summary>
    Fallback,

    /// <summary>Verification failed.</summary>
    Failed,

    /// <summary>Too many failed attempts: biometrics stay locked until the user enters their passcode.</summary>
    LockedOut,

    /// <summary>Nothing enrolled.</summary>
    NotEnrolled,

    /// <summary>A device credential was needed but none is set.</summary>
    PasscodeNotSet,

    /// <summary>No hardware, or it is busy or disabled.</summary>
    NotAvailable,

    /// <summary>Android: the prompt timed out.</summary>
    Timeout
}

/// <param name="Status">The outcome.</param>
/// <param name="ErrorMessage">The platform's own (localized) message for a failure, when it gives one.</param>
public sealed record BiometricResult(BiometricResultStatus Status, string? ErrorMessage = null);

/// <summary>Serialization for every biometrics contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BiometricAvailability))]
[JsonSerializable(typeof(BiometricRequest))]
[JsonSerializable(typeof(BiometricResult))]
public partial class BiometricsJsonContext : JsonSerializerContext;
