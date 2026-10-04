using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.LiveActivities.Client;

/// <summary>Whether this app can show live activities.</summary>
/// <param name="Access">The iOS Live Activities switch for this app, or notification permission on Android.</param>
/// <param name="PushToStartToken">The device's push-to-start token (iOS 17.2+); null on Android, older iOS, and until the system issues one.</param>
public sealed record LiveActivitiesStatus(AccessState Access, string? PushToStartToken);

/// <summary>Where a live activity is in its life.</summary>
public enum LiveActivityState
{
    /// <summary>Running and visible.</summary>
    Active,

    /// <summary>Past its stale date: still showing, but out of date.</summary>
    Stale,

    /// <summary>Ended, but still on screen until it is dismissed.</summary>
    Ended,

    /// <summary>Gone from the Lock Screen or notification shade.</summary>
    Dismissed
}

/// <summary>A progress bar. Set <paramref name="Value"/>, or <paramref name="Start"/> and <paramref name="End"/> for a timer the system advances itself, or <paramref name="Indeterminate"/>.</summary>
/// <param name="Value">The completed fraction, 0 to 1.</param>
/// <param name="Start">The start of a time range the system animates between.</param>
/// <param name="End">The end of that range.</param>
/// <param name="Indeterminate">Progress of unknown length.</param>
public sealed record LiveActivityProgress(double? Value = null, DateTimeOffset? Start = null, DateTimeOffset? End = null, bool Indeterminate = false);

/// <summary>What an activity says — everything that can change while it runs.</summary>
/// <param name="Title">The headline: "Arriving in 5 min", the team names.</param>
/// <param name="Body">Detail under the title.</param>
/// <param name="ShortStatus">A few characters for the tightest places — the compact Dynamic Island and the Android 16 status bar chip: "5 min", "2-1".</param>
/// <param name="Progress">An optional progress bar.</param>
/// <param name="StaleDate">When this content goes out of date; the activity turns <see cref="LiveActivityState.Stale"/> then.</param>
/// <param name="RelevanceScore">Ranks this activity against the app's others when several run (iOS).</param>
/// <param name="Data">Strings for the app's own widget to read.</param>
public sealed record LiveActivityContent(
    string? Title = null,
    string? Body = null,
    string? ShortStatus = null,
    LiveActivityProgress? Progress = null,
    DateTimeOffset? StaleDate = null,
    double? RelevanceScore = null,
    IReadOnlyDictionary<string, string>? Data = null
);

/// <summary>An activity to start.</summary>
/// <param name="Content">What it says first.</param>
/// <param name="Attributes">What never changes for its lifetime — an order number, the teams. A push-to-start payload names the same attributes.</param>
/// <param name="Kind">Which layout renders it, for an app whose widget has more than one.</param>
/// <param name="RequestPushToken">Ask for a push token so a server can update it; it arrives as <c>liveactivities.token</c>. On iOS the app needs the <c>aps-environment</c> entitlement for this, or the start fails.</param>
public sealed record LiveActivityStartRequest(
    LiveActivityContent Content,
    IReadOnlyDictionary<string, string>? Attributes = null,
    string? Kind = null,
    bool RequestPushToken = true
);

/// <summary>Turns an update into a banner, and a tap on a paired watch, instead of a silent refresh.</summary>
public sealed record LiveActivityAlert(string Title, string? Body = null);

/// <summary>New content for a running activity.</summary>
/// <param name="Content">Replaces what it says.</param>
/// <param name="Alert">Alert the user about the change; silent when omitted.</param>
public sealed record LiveActivityUpdateRequest(LiveActivityContent Content, LiveActivityAlert? Alert = null);

/// <summary>How an activity ends.</summary>
/// <param name="Content">Final content to show until it is dismissed; the last content when omitted.</param>
/// <param name="DismissAt">When to take it off screen; the system decides when omitted, and a past time removes it now.</param>
public sealed record LiveActivityEndRequest(LiveActivityContent? Content = null, DateTimeOffset? DismissAt = null);

/// <summary>A running, or recently ended, live activity.</summary>
/// <param name="Id">The system's id, which updates and ends it.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="PushToken">The token that addresses it from a server, once the system issued one (iOS).</param>
public sealed record LiveActivity(string Id, LiveActivityState State, string? PushToken);

/// <summary>An activity's push token.</summary>
/// <param name="ActivityId">The activity it addresses.</param>
/// <param name="Token">The APNs token, hex.</param>
public sealed record LiveActivityPushToken(string ActivityId, string Token);

/// <summary>The device's push-to-start token.</summary>
public sealed record LiveActivityPushToStartToken(string Token);

/// <summary>Serialization for every live activities contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LiveActivitiesStatus))]
[JsonSerializable(typeof(LiveActivityStartRequest))]
[JsonSerializable(typeof(LiveActivityUpdateRequest))]
[JsonSerializable(typeof(LiveActivityEndRequest))]
[JsonSerializable(typeof(LiveActivity))]
[JsonSerializable(typeof(IReadOnlyList<LiveActivity>))]
[JsonSerializable(typeof(LiveActivityPushToken))]
[JsonSerializable(typeof(LiveActivityPushToStartToken))]
public partial class LiveActivitiesJsonContext : JsonSerializerContext;
