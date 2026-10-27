using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AgeSignals.Client;

/// <summary>
/// The user's age range as the app store shares it — Apple Declared Age Range (iOS and macOS 26+) and Google Play Age
/// Signals — for app store accountability laws such as Texas SB 2420, and for any app that gates features by age.
/// Nothing is cached: consent can be revoked and a birthday can cross a band, so ask at the moments the law cares
/// about. iOS, macOS and Android; everywhere else answers 501.
/// </summary>
[BridgeClient("agesignals", typeof(AgeSignalsJsonContext))]
public interface IAgeSignalsBridge
{
    /// <summary>
    /// Whether this OS version has a store age API, and what the store says applies to this user, with no UI. Values the
    /// platform cannot answer are null.
    /// </summary>
    [BridgeGet]
    Task<AgeSignalsEligibility> GetEligibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the store for the user's age range. Apple may present a sheet; Google Play shows its sharing prompt only
    /// outside the US law states. Declining is a result (<see cref="AgeSignalStatus.Declined"/>), not an error. Fails
    /// with 400 when the gates are not one to three positive, ascending ages, and with the store's reason
    /// (<c>503 network</c>, <c>403 app_not_owned</c>, …) when it cannot answer.
    /// </summary>
    [BridgePost("request")]
    Task<AgeSignal> RequestAgeRangeAsync(AgeRangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows Apple's significant-update acknowledgment (iOS 26.4+), which tells a supervised child's guardian about a
    /// significant change to the app. <see cref="SignificantUpdateResult.Shown"/> is false where there is no such sheet:
    /// on Google Play, declare the change in Play Console and read <see cref="AgeSignal.SignificantChange"/> instead.
    /// </summary>
    [BridgePost("significant-update")]
    Task<SignificantUpdateResult> ShowSignificantUpdateAsync(SignificantUpdateRequest request, CancellationToken cancellationToken = default);
}
