using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Calendar;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Calendar.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using DeviceCalendar = global::Shiny.Calendar.Calendar;
using static Shiny.AppDeviceBridge.Calendar.CalendarContractMapping;

namespace Shiny.AppDeviceBridge.Calendar;

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
/// <c>/_bridge/calendar</c> over <see cref="ICalendarStore"/>. Pages call it through
/// <see cref="Contracts.ICalendarBridge"/>, whose contracts this bridge reads and writes.
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

        return WebAppBridgeResults.Json(context, ToContract(s.GetCurrentAccess()), Contracts.CalendarJsonContext.Default.CalendarAccessResult);
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Calendar");
            return;
        }

        // An empty body is a request for read-write access.
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.CalendarJsonContext.Default.CalendarAccessRequest);
        var requested = body?.Access ?? Contracts.CalendarAccessType.ReadWrite;

        if (!Enum.IsDefined(requested))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"access\": \"ReadWrite\" | \"ReadOnly\" | \"WriteOnly\" }.");
            return;
        }

        var type = BridgeEnum.Convert<Contracts.CalendarAccessType, CalendarAccessType>(requested);

        // The permission prompt is UI.
        var access = Application.Current?.Dispatcher is { } dispatcher
            ? await dispatcher.DispatchAsync(() => s.RequestAccess(type, context.RequestAborted))
            : await s.RequestAccess(type, context.RequestAborted);

        await WebAppBridgeResults.Json(context, ToContract(access), Contracts.CalendarJsonContext.Default.CalendarAccessResult);
    }

    ValueTask CalendarsAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var calendars = await s.GetAll(context.RequestAborted);
        await WebAppBridgeResults.Json(
            context,
            new Contracts.CalendarList([.. calendars.Select(ToContract)]),
            Contracts.CalendarJsonContext.Default.CalendarList
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
            new Contracts.CalendarEventPage([.. events.Take(limit).Select(ToContract)], offset, limit, events.Count > limit),
            Contracts.CalendarJsonContext.Default.CalendarEventPage
        );
    });

    ValueTask GetEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        if (await FindEventAsync(context, s) is { } e)
            await WebAppBridgeResults.Json(context, ToContract(e), Contracts.CalendarJsonContext.Default.CalendarEvent);
    });

    ValueTask CreateEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        // Missing a required member reads as null: the contract's required title, start and end are enforced here.
        var input = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.CalendarJsonContext.Default.NewCalendarEvent);
        if (input is null || String.IsNullOrWhiteSpace(input.Title))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"title\": \"…\", \"start\": \"…\", \"end\": \"…\" }.");
            return;
        }

        var fields = EventFields.From(input);
        if (fields.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (input.CalendarId is { } calendarId && !await EnsureWritableAsync(context, s, calendarId))
            return;

        var e = new CalendarEvent(input.Title, input.Start, input.End) { CalendarId = input.CalendarId };
        fields.ApplyTo(e);

        var id = await s.CreateEvent(e, context.RequestAborted);
        var created = await s.GetEvent(id, context.RequestAborted);

        if (created is null)
            e.Id = id;

        await WebAppBridgeResults.Json(
            context,
            ToContract(created ?? e),
            Contracts.CalendarJsonContext.Default.CalendarEvent,
            StatusCodes.Status201Created
        );
    });

    ValueTask UpdateEventAsync(HttpContext context) => this.GuardAsync(context, async s =>
    {
        var input = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.CalendarJsonContext.Default.CalendarEventChanges);
        if (input is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the event properties to change.");
            return;
        }

        var fields = EventFields.From(input);
        if (fields.Validate() is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (await FindEventAsync(context, s) is not { } e)
            return;

        if (e.CalendarId is { } calendarId && !await EnsureWritableAsync(context, s, calendarId))
            return;

        fields.ApplyTo(e);

        if (e.End < e.Start)
        {
            await WebAppBridgeResults.BadRequest(context, "end must not be before start.");
            return;
        }

        await s.UpdateEvent(e, context.RequestAborted);

        var updated = await s.GetEvent(e.Id!, context.RequestAborted) ?? e;
        await WebAppBridgeResults.Json(context, ToContract(updated), Contracts.CalendarJsonContext.Default.CalendarEvent);
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

/// <summary>From the device's types to the contracts the page reads.</summary>
static class CalendarContractMapping
{
    public static Contracts.CalendarAccessResult ToContract(AccessState access)
        => new(BridgeEnum.Convert<AccessState, ContractAccess>(access));

    public static Contracts.CalendarInfo ToContract(DeviceCalendar c)
        => new(c.Id, c.Name, c.Color, c.IsReadOnly, c.Account);

    public static Contracts.CalendarAttendee ToContract(EventAttendee a) => new(
        a.Name,
        a.Email,
        BridgeEnum.Convert<AttendeeRole, Contracts.AttendeeRole>(a.Role),
        BridgeEnum.Convert<AttendeeStatus, Contracts.AttendeeStatus>(a.Status),
        a.IsOrganizer
    );

    // Reminders as whole minutes before the start: a TimeSpan's "00:15:00" is awkward to build in JavaScript.
    public static Contracts.CalendarEvent ToContract(CalendarEvent e) => new(
        e.Id,
        e.CalendarId,
        e.Title,
        e.Description,
        e.Location,
        e.Start,
        e.End,
        e.IsAllDay,
        BridgeEnum.Convert<EventAvailability, Contracts.EventAvailability>(e.Availability),
        e.Url,
        e.IsRecurring,
        e.RecurrenceRule,
        [.. e.Reminders.Select(x => (int)Math.Round(x.Offset.TotalMinutes))],
        [.. e.Attendees.Select(ToContract)],
        e.Organizer is { } o ? ToContract(o) : null
    );
}

/// <summary>
/// What a create or an update writes, validated and applied the same way whichever contract it came from. A null
/// field is left as it is; on create, the contract's required members are already set.
/// </summary>
sealed record EventFields(
    string? Title,
    string? Description,
    string? Location,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool? IsAllDay,
    Contracts.EventAvailability? Availability,
    string? Url,
    IReadOnlyList<int>? ReminderMinutes,
    string? CalendarId
)
{
    const int MaxText = 1_000;
    const int MaxDescription = 10_000;
    const int MaxReminders = 10;
    const int MaxReminderMinutes = 60 * 24 * 28;

    public static EventFields From(Contracts.NewCalendarEvent e) => new(
        e.Title, e.Description, e.Location, e.Start, e.End, e.IsAllDay, e.Availability, e.Url, e.ReminderMinutes, e.CalendarId
    );

    public static EventFields From(Contracts.CalendarEventChanges c) => new(
        c.Title, c.Description, c.Location, c.Start, c.End, c.IsAllDay, c.Availability, c.Url, c.ReminderMinutes, null
    );

    public string? Validate()
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

    public void ApplyTo(CalendarEvent e)
    {
        if (this.Title is not null) e.Title = this.Title;
        if (this.Description is not null) e.Description = this.Description;
        if (this.Location is not null) e.Location = this.Location;
        if (this.Start is { } start) e.Start = start;
        if (this.End is { } end) e.End = end;
        if (this.IsAllDay is { } allDay) e.IsAllDay = allDay;
        if (this.Availability is { } availability) e.Availability = BridgeEnum.Convert<Contracts.EventAvailability, EventAvailability>(availability);
        if (this.Url is not null) e.Url = this.Url.Length == 0 ? null : this.Url;

        if (this.ReminderMinutes is { } reminders)
            e.Reminders = [.. reminders.Distinct().Select(x => new EventReminder(TimeSpan.FromMinutes(x)))];
    }
}
