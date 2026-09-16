using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Calendar.Client;

/// <summary>The access to ask for. Write-only is iOS 17 and later; elsewhere it is treated as read-write.</summary>
public enum CalendarAccessType
{
    ReadOnly,
    WriteOnly,
    ReadWrite
}

public enum EventAvailability
{
    Busy,
    Free,
    Tentative,
    Unavailable
}

public enum AttendeeRole
{
    Required,
    Optional,
    Resource,
    Unknown
}

public enum AttendeeStatus
{
    Unknown,
    Pending,
    Accepted,
    Declined,
    Tentative
}

public sealed record CalendarAccessRequest(CalendarAccessType Access = CalendarAccessType.ReadWrite);

public sealed record CalendarAccessResult(AccessState Access);

public sealed record CalendarInfo(string? Id, string Name, string? Color, bool IsReadOnly, string? Account);

public sealed record CalendarList(IReadOnlyList<CalendarInfo> Calendars);

public sealed record CalendarAttendee(string? Name, string? Email, AttendeeRole Role, AttendeeStatus Status, bool IsOrganizer);

/// <summary>An event as the device holds it. Attendees, the organizer and recurrence can be read but not written.</summary>
public sealed record CalendarEvent(
    string? Id,
    string? CalendarId,
    string Title,
    string? Description,
    string? Location,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    EventAvailability Availability,
    string? Url,
    bool IsRecurring,
    string? RecurrenceRule,
    IReadOnlyList<int> ReminderMinutes,
    IReadOnlyList<CalendarAttendee> Attendees,
    CalendarAttendee? Organizer
);

public sealed record CalendarEventPage(IReadOnlyList<CalendarEvent> Items, int Offset, int Limit, bool HasMore);

/// <summary>An event to create. <see cref="Title"/>, <see cref="Start"/> and <see cref="End"/> are required.</summary>
public sealed class NewCalendarEvent
{
    /// <summary>At most 1,000 characters.</summary>
    public required string Title { get; init; }

    public required DateTimeOffset Start { get; init; }

    /// <summary>Not before <see cref="Start"/>.</summary>
    public required DateTimeOffset End { get; init; }

    /// <summary>The calendar to create it in; the device's default calendar when null.</summary>
    public string? CalendarId { get; init; }

    /// <summary>At most 10,000 characters.</summary>
    public string? Description { get; init; }

    public string? Location { get; init; }

    public bool IsAllDay { get; init; }

    public EventAvailability Availability { get; init; } = EventAvailability.Busy;

    /// <summary>An absolute http or https URL.</summary>
    public string? Url { get; init; }

    /// <summary>Whole minutes before the start; at most 10, each up to four weeks.</summary>
    public IReadOnlyList<int>? ReminderMinutes { get; init; }
}

/// <summary>Changes to an event. Anything left null stays as it is; an empty <see cref="ReminderMinutes"/> clears them, and an empty <see cref="Url"/> removes it.</summary>
public sealed class CalendarEventChanges
{
    public string? Title { get; init; }

    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public string? Description { get; init; }

    public string? Location { get; init; }

    public bool? IsAllDay { get; init; }

    public EventAvailability? Availability { get; init; }

    public string? Url { get; init; }

    public IReadOnlyList<int>? ReminderMinutes { get; init; }
}

/// <summary>Serialization for every calendar contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CalendarAccessRequest))]
[JsonSerializable(typeof(CalendarAccessResult))]
[JsonSerializable(typeof(CalendarList))]
[JsonSerializable(typeof(CalendarEvent))]
[JsonSerializable(typeof(CalendarEventPage))]
[JsonSerializable(typeof(NewCalendarEvent))]
[JsonSerializable(typeof(CalendarEventChanges))]
public partial class CalendarJsonContext : JsonSerializerContext;
