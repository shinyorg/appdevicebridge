using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Calendar;
using Shiny.Net.HttpServer;
using DeviceCalendar = global::Shiny.Calendar.Calendar;

namespace Shiny.WebAppHost.Bridge.Calendar;

public static class CalendarBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/calendar</c> and registers Shiny's calendar store — there is nothing else to call.
    /// <code>
    /// builder.AddCalendarBridge();
    /// </code>
    /// <para>
    /// Shiny.Calendar has Android, iOS, Mac Catalyst, macOS and Windows implementations; on Linux the endpoints
    /// answer 501. The platform setup is its own: <c>READ_CALENDAR</c> and <c>WRITE_CALENDAR</c> on Android;
    /// <c>NSCalendarsFullAccessUsageDescription</c> on Apple platforms, plus the
    /// <c>com.apple.security.personal-information.calendars</c> entitlement where the app is sandboxed; the
    /// <c>appointments</c> capability on Windows.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddCalendarBridge(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#elif MACOS
        builder.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        if (!builder.Services.Any(x => x.ServiceType == typeof(ICalendarStore)))
            builder.Services.AddCalendarStore();
#endif

        builder.Services.AddWebAppBridge<CalendarBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/calendar</c> over <see cref="ICalendarStore"/>.
/// <code>
/// GET    /_bridge/calendar                    { "access": "Available" }
/// POST   /_bridge/calendar/access             { "access": "ReadWrite" | "ReadOnly" | "WriteOnly" }; prompts
/// GET    /_bridge/calendar/calendars          { "calendars": [...] }
/// GET    /_bridge/calendar/events?start=…&amp;end=…&amp;calendarId=&amp;search=&amp;offset=0&amp;limit=50
///                                             start and end are required, at most 366 days apart
/// POST   /_bridge/calendar/events             201 with the event
/// GET    /_bridge/calendar/events/{id}
/// PUT    /_bridge/calendar/events/{id}        only the properties sent are changed
/// DELETE /_bridge/calendar/events/{id}?series=true   series: the rest of a recurring event, not one occurrence
/// </code>
/// Writes to a read-only calendar answer 403 <c>read_only</c>. Attendees, the organizer and recurrence are
/// reported but cannot be written.
/// </summary>
public sealed class CalendarBridge(IServiceProvider services) : IWebAppBridge
{
    const int DefaultLimit = 50;
    const int MaxLimit = 500;
    const int MaxSearchLength = 200;
    static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    readonly ICalendarStore? store = services.GetOptionalService<ICalendarStore>();

    public string Name => "calendar";

    public bool IsSupported => this.store is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/calendars", this.CalendarsAsync)
        .MapGet("/events", this.EventsAsync)
        .MapPost("/events", this.CreateEventAsync)
        .MapGet("/events/{id}", this.GetEventAsync)
        .MapPut("/events/{id}", this.UpdateEventAsync)
        .MapDelete("/events/{id}", this.DeleteEventAsync);

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.store is not { } s)
            return WebAppBridgeResults.NotSupported(context, "Calendar");

        return WebAppBridgeResults.Json(context, new CalendarAccessResponse(s.GetCurrentAccess()), CalendarBridgeJsonContext.Default.CalendarAccessResponse);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Calendar");
            return;
        }

        // An empty body is a request for read-write access.
        var body = await WebAppBridgeResults.ReadBodyAsync(context, CalendarBridgeJsonContext.Default.CalendarAccessRequest);
        var type = body?.Access ?? CalendarAccessType.ReadWrite;

        if (!Enum.IsDefined(type))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"access\": \"ReadWrite\" | \"ReadOnly\" | \"WriteOnly\" }.");
            return;
        }

        // The permission prompt is UI.
        var access = Application.Current?.Dispatcher is { } dispatcher
            ? await dispatcher.DispatchAsync(() => s.RequestAccess(type, context.RequestAborted))
            : await s.RequestAccess(type, context.RequestAborted);

        await WebAppBridgeResults.Json(context, new CalendarAccessResponse(access), CalendarBridgeJsonContext.Default.CalendarAccessResponse);
    }

    ValueTask CalendarsAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var calendars = await s.GetAll(context.RequestAborted);
        await WebAppBridgeResults.Json(
            context,
            new CalendarListResponse(calendars.Select(CalendarDto.From).ToList()),
            CalendarBridgeJsonContext.Default.CalendarListResponse
        );
    });

    ValueTask EventsAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var query = context.Request.Query;

        if (!TryReadDate(query["start"].ToString(), out var start) || !TryReadDate(query["end"].ToString(), out var end))
        {
            await WebAppBridgeResults.BadRequest(context, "start and end are required, as ISO 8601 date-times.");
            return;
        }

        if (end < start || end - start > MaxRange)
        {
            await WebAppBridgeResults.BadRequest(context, "end must follow start, by at most 366 days.");
            return;
        }

        var search = query["search"].ToString().Trim();
        var calendarId = query["calendarId"].ToString();

        if (search.Length > MaxSearchLength || calendarId.Length > 256)
        {
            await WebAppBridgeResults.BadRequest(context, $"search is limited to {MaxSearchLength} characters.");
            return;
        }

        if (!TryReadInt(query["offset"].ToString(), 0, 0, Int32.MaxValue, out var offset)
            || !TryReadInt(query["limit"].ToString(), DefaultLimit, 1, MaxLimit, out var limit))
        {
            await WebAppBridgeResults.BadRequest(context, $"offset must be 0 or more, and limit between 1 and {MaxLimit}.");
            return;
        }

        var q = s.Query().Between(start, end);
        if (calendarId.Length > 0)
            q = q.ForCalendar(calendarId);

        if (search.Length > 0)
            q = q.TitleContains(search);

        // One more than asked for says whether there is another page.
        var events = await q
            .OrderBy(CalendarEventSortField.Start)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            new CalendarEventPage(events.Take(limit).Select(CalendarEventDto.From).ToList(), offset, limit, events.Count > limit),
            CalendarBridgeJsonContext.Default.CalendarEventPage
        );
    });

    ValueTask GetEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await FindEventAsync(context, s) is { } e)
            await WebAppBridgeResults.Json(context, CalendarEventDto.From(e), CalendarBridgeJsonContext.Default.CalendarEventDto);
    });

    ValueTask CreateEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, CalendarBridgeJsonContext.Default.CalendarEventInput);
        if (input is null || String.IsNullOrWhiteSpace(input.Title) || input.Start is null || input.End is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"title\": \"…\", \"start\": \"…\", \"end\": \"…\" }.");
            return;
        }

        if (input.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (input.CalendarId is { } calendarId && !await EnsureWritableAsync(context, s, calendarId))
            return;

        var e = new CalendarEvent(input.Title, input.Start.Value, input.End.Value);
        input.ApplyTo(e);

        var id = await s.CreateEvent(e, context.RequestAborted);
        var created = await s.GetEvent(id, context.RequestAborted);

        if (created is null)
            e.Id = id;

        await WebAppBridgeResults.Json(
            context,
            CalendarEventDto.From(created ?? e),
            CalendarBridgeJsonContext.Default.CalendarEventDto,
            StatusCodes.Status201Created
        );
    });

    ValueTask UpdateEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, CalendarBridgeJsonContext.Default.CalendarEventInput);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the event properties to change.");
            return;
        }

        if (input.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (await FindEventAsync(context, s) is not { } e)
            return;

        // Moving an event between calendars is not something every platform can do in place.
        if (input.CalendarId is not null && e.CalendarId is not null && input.CalendarId != e.CalendarId)
        {
            await WebAppBridgeResults.BadRequest(context, "An event cannot be moved to another calendar. Create it there and delete this one.");
            return;
        }

        if (e.CalendarId is { } calendarId && !await EnsureWritableAsync(context, s, calendarId))
            return;

        input.ApplyTo(e);

        if (e.End < e.Start)
        {
            await WebAppBridgeResults.BadRequest(context, "end must not be before start.");
            return;
        }

        await s.UpdateEvent(e, context.RequestAborted);

        var updated = await s.GetEvent(e.Id!, context.RequestAborted) ?? e;
        await WebAppBridgeResults.Json(context, CalendarEventDto.From(updated), CalendarBridgeJsonContext.Default.CalendarEventDto);
    });

    ValueTask DeleteEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await FindEventAsync(context, s) is not { } e)
            return;

        if (e.CalendarId is { } calendarId && !await EnsureWritableAsync(context, s, calendarId))
            return;

        var series = context.Request.Query["series"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        await s.DeleteEvent(e.Id!, series, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    /// <summary>The event named by the route, or null once a 400 or 404 has been written.</summary>
    static async Task<CalendarEvent?> FindEventAsync(HttpContext context, ICalendarStore s)
    {
        var id = context.Request.RouteValues["id"];
        if (String.IsNullOrWhiteSpace(id) || id.Length > 256)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected an event id.");
            return null;
        }

        var e = await s.GetEvent(id, context.RequestAborted);
        if (e is null)
            await WebAppBridgeResults.NotFound(context, "No such event.");

        return e;
    }

    /// <summary>False once a 404 or 403 has been written.</summary>
    static async Task<bool> EnsureWritableAsync(HttpContext context, ICalendarStore s, string calendarId)
    {
        if (calendarId.Length is 0 or > 256 || await s.GetById(calendarId, context.RequestAborted) is not { } calendar)
        {
            await WebAppBridgeResults.NotFound(context, "No such calendar.");
            return false;
        }

        if (calendar.IsReadOnly)
        {
            await ReadOnly(context);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 501 without a store, 403 when access was refused, and the same 403 when the platform throws for a missing
    /// permission rather than returning nothing.
    /// </summary>
    async ValueTask GuardAsync(HttpContext context, Func<ICalendarStore, Task> action)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Calendar");
            return;
        }

        // Unknown is not refused here: Windows reports it until RequestAccess has run, yet reads work.
        if (s.GetCurrentAccess() is AccessState.Denied or AccessState.Disabled or AccessState.NotSetup or AccessState.NotSupported)
        {
            await AccessDenied(context);
            return;
        }

        try
        {
            await action(s);
        }
        catch (Exception ex) when (!context.Response.HasStarted && IsPermissionFailure(ex))
        {
            await AccessDenied(context);
        }
        // Windows only writes to app-owned calendars and says so by throwing.
        catch (NotSupportedException) when (!context.Response.HasStarted)
        {
            await ReadOnly(context);
        }
    }

    static ValueTask AccessDenied(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status403Forbidden,
        "access_denied",
        "Calendar access has not been granted. POST /_bridge/calendar/access first."
    );

    static ValueTask ReadOnly(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status403Forbidden,
        "read_only",
        "The calendar cannot be written to."
    );

    static bool IsPermissionFailure(Exception ex) => ex is UnauthorizedAccessException
#if ANDROID
        || ex is Java.Lang.SecurityException
#endif
        ;

    static bool TryReadDate(string raw, out DateTimeOffset value)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);

    static bool TryReadInt(string raw, int fallback, int min, int max, out int value)
    {
        if (raw.Length == 0)
        {
            value = fallback;
            return true;
        }

        return Int32.TryParse(raw, out value) && value >= min && value <= max;
    }
}

public sealed record CalendarAccessRequest(CalendarAccessType? Access);
public sealed record CalendarAccessResponse(AccessState Access);
public sealed record CalendarListResponse(List<CalendarDto> Calendars);
public sealed record CalendarEventPage(List<CalendarEventDto> Items, int Offset, int Limit, bool HasMore);

public sealed record CalendarDto(string? Id, string Name, string? Color, bool IsReadOnly, string? Account)
{
    internal static CalendarDto From(DeviceCalendar c) => new(c.Id, c.Name, c.Color, c.IsReadOnly, c.Account);
}

public sealed record CalendarAttendeeDto(string? Name, string? Email, AttendeeRole Role, AttendeeStatus Status, bool IsOrganizer)
{
    internal static CalendarAttendeeDto From(EventAttendee a) => new(a.Name, a.Email, a.Role, a.Status, a.IsOrganizer);
}

public sealed record CalendarEventDto(
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
    List<int> ReminderMinutes,
    List<CalendarAttendeeDto> Attendees,
    CalendarAttendeeDto? Organizer
)
{
    // Reminders as whole minutes before the start: a TimeSpan's "00:15:00" is awkward to build in JavaScript.
    internal static CalendarEventDto From(CalendarEvent e) => new(
        e.Id,
        e.CalendarId,
        e.Title,
        e.Description,
        e.Location,
        e.Start,
        e.End,
        e.IsAllDay,
        e.Availability,
        e.Url,
        e.IsRecurring,
        e.RecurrenceRule,
        e.Reminders.Select(x => (int)Math.Round(x.Offset.TotalMinutes)).ToList(),
        e.Attendees.Select(CalendarAttendeeDto.From).ToList(),
        e.Organizer is { } o ? CalendarAttendeeDto.From(o) : null
    );
}

/// <summary>What the page may write. A property left out (null) is left as it is; an empty reminder list clears.</summary>
public sealed record CalendarEventInput(
    string? CalendarId = null,
    string? Title = null,
    string? Description = null,
    string? Location = null,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    bool? IsAllDay = null,
    EventAvailability? Availability = null,
    string? Url = null,
    List<int>? ReminderMinutes = null
)
{
    const int MaxText = 1_000;
    const int MaxDescription = 10_000;
    const int MaxReminders = 10;
    const int MaxReminderMinutes = 60 * 24 * 28;

    internal string? Validate()
    {
        if (this.Title?.Length > MaxText || this.Location?.Length > MaxText || this.Url?.Length > MaxText || this.CalendarId?.Length > 256)
            return $"title, location and url are limited to {MaxText} characters.";

        if (this.Title is not null && String.IsNullOrWhiteSpace(this.Title))
            return "title cannot be empty.";

        if (this.Description?.Length > MaxDescription)
            return $"description is limited to {MaxDescription} characters.";

        if (this.Start is { } start && this.End is { } end && end < start)
            return "end must not be before start.";

        if (this.Availability is { } availability && !Enum.IsDefined(availability))
            return "availability must be Busy, Free, Tentative or Unavailable.";

        if (this.Url is { Length: > 0 } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            return "url must be an absolute http or https URL.";

        if (this.ReminderMinutes is { } reminders && (reminders.Count > MaxReminders || reminders.Any(x => x is < 0 or > MaxReminderMinutes)))
            return $"reminderMinutes takes at most {MaxReminders} values, each between 0 and {MaxReminderMinutes}.";

        return null;
    }

    internal void ApplyTo(CalendarEvent e)
    {
        if (this.CalendarId is not null) e.CalendarId = this.CalendarId;
        if (this.Title is not null) e.Title = this.Title;
        if (this.Description is not null) e.Description = this.Description;
        if (this.Location is not null) e.Location = this.Location;
        if (this.Start is { } start) e.Start = start;
        if (this.End is { } end) e.End = end;
        if (this.IsAllDay is { } allDay) e.IsAllDay = allDay;
        if (this.Availability is { } availability) e.Availability = availability;
        if (this.Url is not null) e.Url = this.Url.Length == 0 ? null : this.Url;

        if (this.ReminderMinutes is { } reminders)
            e.Reminders = reminders.Distinct().Select(x => new EventReminder(TimeSpan.FromMinutes(x))).ToList();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CalendarAccessRequest))]
[JsonSerializable(typeof(CalendarAccessResponse))]
[JsonSerializable(typeof(CalendarListResponse))]
[JsonSerializable(typeof(CalendarEventPage))]
[JsonSerializable(typeof(CalendarEventDto))]
[JsonSerializable(typeof(CalendarEventInput))]
partial class CalendarBridgeJsonContext : JsonSerializerContext;
