using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Notifications.Client;

public enum NotificationCancelScope
{
    /// <summary>Pending and displayed.</summary>
    All,

    Pending,

    Displayed
}

public enum ChannelImportance { Low = 1, Normal = 2, High = 3, Critical = 4 }

public enum ChannelSound { None, Default, High, Custom }

public enum ChannelActionType { TextReply, Destructive, OpenApp, None }

/// <param name="Badge">Whether the app badge can be read and set.</param>
/// <param name="Entry">Whether taps are reported.</param>
/// <param name="Received">Whether notifications arriving while the app is open are reported.</param>
/// <param name="Geofences">Whether a notification can wait for the device to enter an area.</param>
/// <param name="Images">Whether a notification can carry an image.</param>
public sealed record NotificationStatus(AccessState Access, bool Badge, bool Entry, bool Received, bool Geofences, bool Images);

public sealed record NotificationAccessRequest(bool TimeSensitive = false, bool LocationAware = false);

public sealed record NotificationAccessResult(AccessState Access);

/// <summary>A repeating schedule: every <see cref="IntervalSeconds"/>, or at <see cref="TimeOfDay"/>, optionally on one day.</summary>
public sealed record NotificationRepeat(double? IntervalSeconds = null, TimeSpan? TimeOfDay = null, DayOfWeek? DayOfWeek = null);

/// <summary>Show the notification when the device enters an area.</summary>
public sealed record NotificationGeofence(double Latitude, double Longitude, double RadiusMeters, bool Repeat = false);

/// <summary>A notification to send.</summary>
/// <param name="Message">At most 4,000 characters.</param>
/// <param name="Id">Replaces a notification with the same id; assigned when null.</param>
/// <param name="Channel">An Android channel identifier.</param>
/// <param name="Thread">Groups notifications together.</param>
/// <param name="ScheduleDate">Show it then rather than now.</param>
/// <param name="Image">A .png, .jpg, .gif or .heic of at most 10 MB in a file root.</param>
/// <param name="Data">Comes back on the tap.</param>
public sealed record NotificationRequest(
    string Message,
    string? Title = null,
    string? Subtitle = null,
    int? Id = null,
    string? Channel = null,
    string? Thread = null,
    int? BadgeCount = null,
    DateTimeOffset? ScheduleDate = null,
    NotificationRepeat? Repeat = null,
    NotificationGeofence? Geofence = null,
    BridgeFile? Image = null,
    IReadOnlyDictionary<string, string>? Data = null
);

public sealed record NotificationSent(int Id);

public sealed record NotificationBadge(int Value);

/// <param name="FromWebApp">Whether the web app sent it, rather than the native app.</param>
public sealed record PendingNotification(
    int Id,
    string? Title,
    string? Message,
    string? Channel,
    string? Thread,
    int? BadgeCount,
    DateTimeOffset? ScheduleDate,
    NotificationRepeat? Repeat,
    NotificationGeofence? Geofence,
    bool FromWebApp,
    IReadOnlyDictionary<string, string> Data
);

/// <summary>A notification the user tapped or that arrived while the app was open.</summary>
/// <param name="Action">The action chosen, when the user picked one rather than the notification itself.</param>
/// <param name="Text">What the user typed, for a text reply action.</param>
public sealed record NotificationEvent(
    int Id,
    string? Title,
    string? Message,
    string? Channel,
    string? Thread,
    IReadOnlyDictionary<string, string> Data,
    string? Action,
    string? Text
);

public sealed record NotificationChannelAction(string Identifier, string Title, ChannelActionType ActionType = ChannelActionType.None);

/// <param name="Identifier">1–64 letters, digits, <c>.</c>, <c>-</c> or <c>_</c>.</param>
public sealed record NotificationChannel(
    string Identifier,
    string? Description = null,
    ChannelImportance Importance = ChannelImportance.Normal,
    ChannelSound Sound = ChannelSound.Default,
    IReadOnlyList<NotificationChannelAction>? Actions = null
);

/// <summary>Serialization for every notification contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NotificationStatus))]
[JsonSerializable(typeof(NotificationAccessRequest))]
[JsonSerializable(typeof(NotificationAccessResult))]
[JsonSerializable(typeof(NotificationRequest))]
[JsonSerializable(typeof(NotificationSent))]
[JsonSerializable(typeof(NotificationBadge))]
[JsonSerializable(typeof(IReadOnlyList<PendingNotification>))]
[JsonSerializable(typeof(NotificationEvent))]
[JsonSerializable(typeof(NotificationChannel))]
[JsonSerializable(typeof(IReadOnlyList<NotificationChannel>))]
public partial class NotificationsJsonContext : JsonSerializerContext;
