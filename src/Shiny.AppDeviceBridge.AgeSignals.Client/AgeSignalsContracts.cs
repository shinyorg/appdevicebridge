using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.AgeSignals.Client;

/// <summary>Whether an age range was shared.</summary>
public enum AgeSignalStatus
{
    /// <summary>No store age API on this OS version.</summary>
    NotSupported,

    /// <summary>The user, or their guardian, declined to share an age range.</summary>
    Declined,

    /// <summary>
    /// Google Play: the user is where verification is mandatory and their age is unknown. Ask them to open the Play Store
    /// to verify or set up supervision.
    /// </summary>
    VerificationRequired,

    /// <summary>An age range was shared.</summary>
    Shared
}

/// <summary>How the shared age range was established, weakest first, so an app can require a minimum.</summary>
public enum AgeRangeSource
{
    /// <summary>Not shared, or the store did not say.</summary>
    Unknown,

    /// <summary>The user declared it themselves.</summary>
    SelfDeclared,

    /// <summary>A parent or guardian manages it: the account is supervised.</summary>
    GuardianDeclared,

    /// <summary>Checked by another method, such as a payment card, ID or age estimation.</summary>
    Confirmed,

    /// <summary>Government ID plus a selfie, or a digital ID (Google Play; Apple reports these as <see cref="Confirmed"/>).</summary>
    IdVerified
}

/// <summary>Google Play: a guardian's approval of the app's significant changes.</summary>
public enum SignificantChangeStatus
{
    /// <summary>The most recent significant change, and every one before it, is approved.</summary>
    Approved,

    /// <summary>The guardian has not yet approved one or more significant changes.</summary>
    Pending,

    /// <summary>The guardian declined one or more significant changes.</summary>
    Declined
}

/// <param name="IsSupported">The OS version has a store age API: iOS or macOS 26+, or Android.</param>
/// <param name="IsEligible">Apple (26.2+): the user is in a region or account where age features apply. Null on Android.</param>
/// <param name="AgeRangeRequired">Apple (26.4+): the app must obtain the age range.</param>
/// <param name="SignificantChangeRequiresParentalConsent">Apple (26.4+): significant changes need a guardian's consent.</param>
/// <param name="SignificantChangeRequiresAdultNotification">
/// Apple (26.4+): significant changes must be acknowledged — see <see cref="IAgeSignalsBridge.ShowSignificantUpdateAsync"/>.
/// </param>
public sealed record AgeSignalsEligibility(
    bool IsSupported,
    bool? IsEligible = null,
    bool? AgeRangeRequired = null,
    bool? SignificantChangeRequiresParentalConsent = null,
    bool? SignificantChangeRequiresAdultNotification = null
);

/// <param name="Gates">
/// One to three ascending ages the returned range is bounded by, such as <c>[13, 16, 18]</c>. Google Play ignores these
/// and uses the bands set in Play Console, so configure the same ages there.
/// </param>
public sealed record AgeRangeRequest(IReadOnlyList<int> Gates);

/// <param name="Status">Whether a range was shared.</param>
/// <param name="LowerBound">Inclusive lower bound; null when not shared, and on Apple for the lowest band.</param>
/// <param name="UpperBound">Inclusive upper bound; null when not shared, and for the highest band (such as 18+).</param>
/// <param name="Source">How the range was established.</param>
/// <param name="CommunicationLimits">Apple: a guardian has communication limits turned on for this child.</param>
/// <param name="SignificantChange">
/// Google Play: guardian approval of significant changes; null for unsupervised accounts, supervised ones with no changes
/// recorded, and on Apple.
/// </param>
/// <param name="SignificantChangeApprovedThrough">Google Play: the effective date of the latest approved significant change.</param>
/// <param name="InstallId">Google Play: the id Play gives a supervised install, to match revoked-approval notifications on a server.</param>
public sealed record AgeSignal(
    AgeSignalStatus Status,
    int? LowerBound = null,
    int? UpperBound = null,
    AgeRangeSource Source = AgeRangeSource.Unknown,
    bool CommunicationLimits = false,
    SignificantChangeStatus? SignificantChange = null,
    DateTimeOffset? SignificantChangeApprovedThrough = null,
    string? InstallId = null
)
{
    /// <summary>
    /// True when the store positively says the user is at least <paramref name="age"/>, false when it positively says they
    /// are younger, and null when it can't tell (nothing shared, or the range straddles the age).
    /// </summary>
    public bool? IsAtLeast(int age)
    {
        if (this.Status != AgeSignalStatus.Shared || (this.LowerBound is null && this.UpperBound is null))
            return null;

        if (this.LowerBound >= age)
            return true;

        if (this.UpperBound < age)
            return false;

        return null;
    }
}

/// <param name="Description">What changed, shown to the guardian.</param>
public sealed record SignificantUpdateRequest(string Description);

/// <param name="Shown">Whether the acknowledgment sheet was shown; false where the platform has none.</param>
public sealed record SignificantUpdateResult(bool Shown);

/// <summary>Serialization for every age signals contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgeSignalsEligibility))]
[JsonSerializable(typeof(AgeRangeRequest))]
[JsonSerializable(typeof(AgeSignal))]
[JsonSerializable(typeof(SignificantUpdateRequest))]
[JsonSerializable(typeof(SignificantUpdateResult))]
public partial class AgeSignalsJsonContext : JsonSerializerContext;
