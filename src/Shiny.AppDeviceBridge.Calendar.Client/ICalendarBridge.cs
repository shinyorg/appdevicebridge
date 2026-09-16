using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Calendar.Client;

/// <summary>
/// The device's calendars, from the page. Android, iOS, Mac Catalyst, macOS and Windows; Linux answers
/// <see cref="BridgeException.IsNotSupported"/>. Every call but <see cref="GetAccessAsync"/> and
/// <see cref="RequestAccessAsync"/> fails with <c>access_denied</c> until access has been granted.
/// <code>
/// @inject ICalendarBridge Calendar
///
/// await Calendar.RequestAccessAsync(new());
/// var created = await Calendar.CreateEventAsync(new NewCalendarEvent
/// {
///     Title = "Standup",
///     Start = start,
///     End = start.AddMinutes(15)
/// });
/// </code>
/// </summary>
[BridgeClient("calendar", typeof(CalendarJsonContext))]
public interface ICalendarBridge
{
    /// <summary>Where calendar access stands, without asking.</summary>
    [BridgeGet]
    Task<CalendarAccessResult> GetAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks for access, prompting if the OS has not asked before.</summary>
    [BridgePost("access")]
    Task<CalendarAccessResult> RequestAccessAsync(CalendarAccessRequest request, CancellationToken cancellationToken = default);

    [BridgeGet("calendars")]
    Task<CalendarList> GetCalendarsAsync(CancellationToken cancellationToken = default);

    /// <summary>Events overlapping a range of at most 366 days, a page at a time, in start order.</summary>
    /// <param name="start">The start of the range.</param>
    /// <param name="end">The end of the range; at most 366 days after <paramref name="start"/>.</param>
    /// <param name="calendarId">Only this calendar's events.</param>
    /// <param name="search">Only events whose title contains this — at most 200 characters.</param>
    /// <param name="offset">Events to skip.</param>
    /// <param name="limit">Events per page, 1 to 500.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [BridgeGet("events")]
    Task<CalendarEventPage> GetEventsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? calendarId = null,
        string? search = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates an event. A read-only calendar fails with <c>read_only</c>.</summary>
    [BridgePost("events")]
    Task<CalendarEvent> CreateEventAsync(NewCalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    [BridgeGet("events/{id}")]
    Task<CalendarEvent> GetEventAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Changes only what <paramref name="changes"/> sets. An event cannot move between calendars.</summary>
    [BridgePut("events/{id}")]
    Task<CalendarEvent> UpdateEventAsync(string id, CalendarEventChanges changes, CancellationToken cancellationToken = default);

    /// <summary>Deletes an event — or, with <paramref name="series"/>, the rest of the recurring series it belongs to.</summary>
    [BridgeDelete("events/{id}")]
    Task DeleteEventAsync(string id, bool series = false, CancellationToken cancellationToken = default);
}
