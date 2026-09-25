using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Locations;
using Shiny.Net.HttpServer;
using Shiny.Notifications;
using Contracts = Shiny.AppDeviceBridge.Notifications.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Notification = Shiny.Notifications.Notification;
using static Shiny.AppDeviceBridge.Notifications.NotificationContractMapping;

namespace Shiny.AppDeviceBridge.Notifications;

/// <summary>
/// <c>/_bridge/notifications</c> over <see cref="INotificationManager"/>.
/// <code>
/// GET    /_bridge/notifications                   { "access": "Available", "badge": true, "entry": true, … }
/// POST   /_bridge/notifications/access            { "timeSensitive": true, "locationAware": false }
/// POST   /_bridge/notifications/send              { "title": "…", "message": "…", "scheduleDate": "…", "data": {…} }  → { "id": 7 }
/// GET    /_bridge/notifications/pending
/// DELETE /_bridge/notifications?scope=all|pending|displayed
/// DELETE /_bridge/notifications/{id}
/// GET    /_bridge/notifications/badge             501 where the platform has no app badge
/// PUT    /_bridge/notifications/badge             { "value": 3 }
/// GET    /_bridge/notifications/channels
/// POST   /_bridge/notifications/channels          { "identifier": "reply", "importance": "High", "actions": [{ "identifier": "reply", "title": "Reply", "actionType": "TextReply" }] }
/// DELETE /_bridge/notifications/channels/{identifier}
///
/// events:   notification.entry, notification.received
/// handlers: notification.entry, notification.received
/// </code>
/// </summary>
public sealed class NotificationsBridge(IServiceProvider services, WebAppFileRoots roots) : IWebAppBridge
{
    const int MaxDataEntries = 64;
    const long MaxImageBytes = 10 * 1024 * 1024;

    readonly INotificationManager? notifications = services.GetOptionalService<INotificationManager>();

    public string Name => "notifications";

    public bool IsSupported => this.notifications is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        // The delegate raises these; mapping them here means the topics exist as soon as the server is composed.
        routes.Events.Source(WebAppNotificationDelegate.EntryEvent, Contracts.NotificationsJsonContext.Default.NotificationEvent);
        routes.Events.Source(WebAppNotificationDelegate.ReceivedEvent, Contracts.NotificationsJsonContext.Default.NotificationEvent);

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapPost("/send", this.SendAsync)
            .MapGet("/pending", this.PendingAsync)
            .MapDelete("", this.CancelAllAsync)
            .MapDelete("/{id}", this.CancelAsync)
            .MapGet("/badge", this.GetBadgeAsync)
            .MapPut("/badge", this.SetBadgeAsync)
            .MapGet("/channels", this.ChannelsAsync)
            .MapPost("/channels", this.AddChannelAsync)
            .MapDelete("/channels/{identifier}", this.RemoveChannelAsync);
    }

    async ValueTask StatusAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        var access = await n.GetCurrentAccess(AccessRequestFlags.Notification);
        await WebAppBridgeResults.Json(
            context,
            new Contracts.NotificationStatus(
                BridgeEnum.Convert<AccessState, ContractAccess>(access),
                NotificationPlatform.SupportsBadge,
                NotificationPlatform.SupportsEntry,
                NotificationPlatform.SupportsReceived,
                NotificationPlatform.SupportsGeofences,
                NotificationPlatform.SupportsImages
            ),
            Contracts.NotificationsJsonContext.Default.NotificationStatus
        );
    }

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        // An empty body is a plain request; a malformed one is still a mistake worth reporting.
        var body = context.Request.ContentLength is null or 0
            ? new Contracts.NotificationAccessRequest()
            : await WebAppBridgeResults.ReadBodyAsync(context, Contracts.NotificationsJsonContext.Default.NotificationAccessRequest);

        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"timeSensitive\": false, \"locationAware\": false }.");
            return;
        }

        var flags = AccessRequestFlags.Notification;
        if (body.TimeSensitive)
            flags |= AccessRequestFlags.TimeSensitivity;

        if (body.LocationAware)
            flags |= AccessRequestFlags.LocationAware;

        // The permission prompt is UI.
        AccessState access;
        try
        {
            access = await services.GetRequiredService<IWebAppMainThread>().InvokeAsync(() => n.RequestAccess(flags));
        }
        catch (Exception)
        {
            // macOS fails the request ("Notifications are not allowed for this application") rather than answering
            // no once the user has turned them off. That is an answer, not a failure.
            if (await n.GetCurrentAccess(AccessRequestFlags.Notification) != AccessState.Denied)
                throw;

            access = AccessState.Denied;
        }

        await WebAppBridgeResults.Json(context, new Contracts.NotificationAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)), Contracts.NotificationsJsonContext.Default.NotificationAccessResult);
    }

    // No dispatcher here: background.js sends from jobs and geofences with no page and often no UI.
    async ValueTask SendAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, Contracts.NotificationsJsonContext.Default.NotificationRequest) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"message\": \"…\" }.");
            return;
        }

        if (Validate(body) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (body.Geofence is not null && !NotificationPlatform.SupportsGeofences)
        {
            await WebAppBridgeResults.NotSupported(context, "Geofence notifications");
            return;
        }

        var notification = NotificationPlatform.Create(body.Subtitle);
        notification.Id = body.Id ?? 0;
        notification.Title = body.Title;
        notification.Message = body.Message;
        notification.Channel = body.Channel;
        notification.Thread = body.Thread;
        notification.BadgeCount = body.BadgeCount;
        notification.ScheduleDate = body.ScheduleDate;
        notification.Payload = WebAppNotifications.Stamp(body.Data is null ? null : new Dictionary<string, string>(body.Data));

        if (body.Repeat is { } repeat)
        {
            notification.RepeatInterval = new IntervalTrigger
            {
                Interval = repeat.IntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                TimeOfDay = repeat.TimeOfDay,
                DayOfWeek = repeat.DayOfWeek
            };
        }

        if (body.Geofence is { } geofence)
        {
            notification.Geofence = new GeofenceTrigger
            {
                Center = new Position(geofence.Latitude, geofence.Longitude),
                Radius = Distance.FromMeters(geofence.RadiusMeters),
                Repeat = geofence.Repeat
            };
        }

        string? attachment = null;
        if (body.Image is { } image && NotificationPlatform.SupportsImages)
        {
            if (this.ResolveImage(image) is not { } path)
            {
                await WebAppBridgeResults.BadRequest(context, "The image must be an existing .png, .jpg, .gif or .heic file of at most 10 MB inside a file root.");
                return;
            }

            if (!NotificationPlatform.TryAttachImage(notification, path, out attachment, out var error))
            {
                await WebAppBridgeResults.BadRequest(context, $"The image could not be attached: {error}");
                return;
            }
        }

        try
        {
            await n.Send(notification);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidProgramException or ArgumentException)
        {
            // Shiny's own validation (a channel that does not exist, a date already past) and a missing Android icon.
            NotificationPlatform.DiscardAttachment(attachment);
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "invalid_notification", ex.Message);
            return;
        }
        catch (NotSupportedException ex)
        {
            NotificationPlatform.DiscardAttachment(attachment);
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, new Contracts.NotificationSent(notification.Id), Contracts.NotificationsJsonContext.Default.NotificationSent);
    }

    async ValueTask PendingAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        var pending = await n.GetPendingNotifications();
        await WebAppBridgeResults.Json(
            context,
            [.. pending.Select(ToContract)],
            Contracts.NotificationsJsonContext.Default.IReadOnlyListPendingNotification
        );
    }

    async ValueTask CancelAllAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        CancelScope? scope = context.Request.Query["scope"].ToString().ToLowerInvariant() switch
        {
            "" or "all" => CancelScope.All,
            "pending" => CancelScope.Pending,
            "displayed" => CancelScope.DisplayedOnly,
            _ => null
        };

        if (scope is not { } s)
        {
            await WebAppBridgeResults.BadRequest(context, "scope must be all, pending or displayed.");
            return;
        }

        await n.Cancel(s);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CancelAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        if (!Int32.TryParse(context.Request.RouteValues["id"], out var id) || id <= 0)
        {
            await WebAppBridgeResults.BadRequest(context, "The notification id must be a positive integer.");
            return;
        }

        await n.Cancel(id);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask GetBadgeAsync(HttpContext context)
    {
        if (this.notifications is null || !NotificationPlatform.SupportsBadge)
        {
            await WebAppBridgeResults.NotSupported(context, "The app badge");
            return;
        }

        var value = await NotificationPlatform.GetBadgeAsync(services);
        await WebAppBridgeResults.Json(context, new Contracts.NotificationBadge(value), Contracts.NotificationsJsonContext.Default.NotificationBadge);
    }

    async ValueTask SetBadgeAsync(HttpContext context)
    {
        if (this.notifications is null || !NotificationPlatform.SupportsBadge)
        {
            await WebAppBridgeResults.NotSupported(context, "The app badge");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, Contracts.NotificationsJsonContext.Default.NotificationBadge) is not { Value: >= 0 and <= 99_999 } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"value\": 0 } with a value from 0 to 99999.");
            return;
        }

        await NotificationPlatform.SetBadgeAsync(services, body.Value);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask ChannelsAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        await WebAppBridgeResults.Json(
            context,
            [.. n.GetChannels().Select(ToContract)],
            Contracts.NotificationsJsonContext.Default.IReadOnlyListNotificationChannel
        );
    }

    async ValueTask AddChannelAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, Contracts.NotificationsJsonContext.Default.NotificationChannel) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"identifier\": \"…\" }.");
            return;
        }

        if (ValidateChannel(body) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        var channel = new Channel
        {
            Identifier = body.Identifier,
            Description = body.Description,
            Importance = BridgeEnum.Convert<Contracts.ChannelImportance, ChannelImportance>(body.Importance),
            Sound = BridgeEnum.Convert<Contracts.ChannelSound, ChannelSound>(body.Sound),
            Actions = [.. (body.Actions ?? []).Select(x => ChannelAction.Create(x.Identifier, x.Title, BridgeEnum.Convert<Contracts.ChannelActionType, ChannelActionType>(x.ActionType)))]
        };

        try
        {
            n.AddChannel(channel);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "invalid_channel", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, ToContract(channel), Contracts.NotificationsJsonContext.Default.NotificationChannel);
    }

    async ValueTask RemoveChannelAsync(HttpContext context)
    {
        if (this.notifications is not { } n)
        {
            await WebAppBridgeResults.NotSupported(context, "Notifications");
            return;
        }

        var identifier = context.Request.RouteValues["identifier"] ?? String.Empty;

        if (String.Equals(identifier, Channel.Default.Identifier, StringComparison.Ordinal))
        {
            await WebAppBridgeResults.BadRequest(context, "The default channel cannot be removed.");
            return;
        }

        if (!IsIdentifier(identifier) || n.GetChannel(identifier) is null)
        {
            await WebAppBridgeResults.NotFound(context, $"There is no channel '{identifier}'.");
            return;
        }

        n.RemoveChannel(identifier);
        await WebAppBridgeResults.NoContent(context);
    }

    string? ResolveImage(BridgeFile image)
    {
        if (!roots.TryResolve(image.Root, image.Path, out var path)
            || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".heic"))
            return null;

        var file = new FileInfo(path);
        return file.Exists && file.Length <= MaxImageBytes ? file.FullName : null;
    }

    static string? Validate(Contracts.NotificationRequest body)
    {
        if (String.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 4000)
            return "message is required, at most 4000 characters.";

        if (body.Title?.Length > 256 || body.Subtitle?.Length > 256 || body.Thread?.Length > 256)
            return "title, subtitle and thread are at most 256 characters.";

        if (body.Id is <= 0)
            return "id must be a positive integer, or left out to assign one.";

        if (body.Channel is not null && !IsIdentifier(body.Channel))
            return "channel must be an existing channel identifier.";

        if (body.Data is { } data)
        {
            if (data.Count > MaxDataEntries)
                return $"data holds at most {MaxDataEntries} entries.";

            foreach (var (key, value) in data)
            {
                if (key.Length is 0 or > 128 || value is null || value.Length > 4096)
                    return "data keys are 1 to 128 characters and values at most 4096.";

                if (WebAppNotifications.IsReserved(key))
                    return $"data keys starting with '{WebAppNotifications.ReservedPrefix}' are reserved.";
            }
        }

        var triggers = (body.ScheduleDate is null ? 0 : 1) + (body.Repeat is null ? 0 : 1) + (body.Geofence is null ? 0 : 1);
        if (triggers > 1)
            return "Use one of scheduleDate, repeat and geofence.";

        if (body.ScheduleDate is { } date && date <= DateTimeOffset.UtcNow)
            return "scheduleDate must be in the future.";

        if (body.BadgeCount is { } badge && (badge < 0 || triggers > 0))
            return "badgeCount must be zero or more, and only applies to a notification sent now.";

        if (body.Repeat is { } repeat)
        {
            if (repeat.IntervalSeconds.HasValue == repeat.TimeOfDay.HasValue)
                return "repeat needs exactly one of intervalSeconds and timeOfDay.";

            // iOS refuses a repeating interval under a minute.
            if (repeat.IntervalSeconds is { } seconds && (Double.IsNaN(seconds) || seconds < 60 || seconds > TimeSpan.FromDays(366).TotalSeconds))
                return "repeat.intervalSeconds must be from 60 seconds to a year.";

            if (repeat.TimeOfDay is { } time && (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)))
                return "repeat.timeOfDay must be a time of day, such as \"09:30:00\".";

            if (repeat.DayOfWeek is not null && repeat.TimeOfDay is null)
                return "repeat.dayOfWeek needs repeat.timeOfDay.";
        }

        if (body.Geofence is { } geofence
            && (geofence.Latitude is < -90 or > 90 || geofence.Longitude is < -180 or > 180 || geofence.RadiusMeters is <= 0 or > 100_000 || Double.IsNaN(geofence.RadiusMeters)))
            return "geofence needs a latitude, a longitude and a radiusMeters up to 100000.";

        return null;
    }

    static string? ValidateChannel(Contracts.NotificationChannel body)
    {
        if (!IsIdentifier(body.Identifier))
            return "identifier is 1 to 64 letters, digits, '.', '-' or '_'.";

        if (body.Description?.Length > 256)
            return "description is at most 256 characters.";

        // A custom sound names a resource bundled in the app; the page has no business choosing files.
        if (body.Sound == Contracts.ChannelSound.Custom)
            return "sound must be None, Default or High.";

        if (body.Actions is { } actions)
        {
            if (actions.Count > 4)
                return "A channel has at most 4 actions.";

            if (actions.Any(x => !IsIdentifier(x.Identifier) || String.IsNullOrWhiteSpace(x.Title) || x.Title.Length > 64))
                return "Each action needs an identifier and a title of at most 64 characters.";
        }

        return null;
    }

    static bool IsIdentifier(string? value)
        => !String.IsNullOrEmpty(value)
           && value.Length <= 64
           && value.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

/// <summary>How the bridge tells the web app's notifications from the native app's.</summary>
public static class WebAppNotifications
{
    public const string ReservedPrefix = "appdevicebridge.";

    /// <summary>Set on every notification the web app sends.</summary>
    public const string SourceKey = ReservedPrefix + "source";

    const string SourceValue = "webapp";

    public static bool IsFromWebApp(Notification notification)
        => notification.Payload?.TryGetValue(SourceKey, out var value) == true && value == SourceValue;

    internal static bool IsReserved(string key) => key.StartsWith(ReservedPrefix, StringComparison.Ordinal);

    internal static Dictionary<string, string> Stamp(Dictionary<string, string>? data)
        => new(data ?? []) { [SourceKey] = SourceValue };

    /// <summary>The payload as the page wrote it, without the bridge's own keys.</summary>
    internal static Dictionary<string, string> Unstamp(IDictionary<string, string>? payload)
        => payload?.Where(x => !IsReserved(x.Key)).ToDictionary(x => x.Key, x => x.Value) ?? [];
}

static class NotificationContractMapping
{
    public static Contracts.PendingNotification ToContract(Notification notification) => new(
        notification.Id,
        notification.Title,
        notification.Message,
        notification.Channel,
        notification.Thread,
        notification.BadgeCount,
        notification.ScheduleDate,
        notification.RepeatInterval is { } r ? new Contracts.NotificationRepeat(r.Interval?.TotalSeconds, r.TimeOfDay, r.DayOfWeek) : null,
        notification.Geofence is { Center: { } center, Radius: { } radius } g
            ? new Contracts.NotificationGeofence(center.Latitude, center.Longitude, radius.TotalMeters, g.Repeat)
            : null,
        WebAppNotifications.IsFromWebApp(notification),
        WebAppNotifications.Unstamp(notification.Payload)
    );

    public static Contracts.NotificationEvent ToEvent(Notification notification, string? action, string? text) => new(
        notification.Id,
        notification.Title,
        notification.Message,
        notification.Channel,
        notification.Thread,
        WebAppNotifications.Unstamp(notification.Payload),
        action,
        text
    );

    public static Contracts.NotificationChannel ToContract(Channel channel) => new(
        channel.Identifier,
        channel.Description,
        BridgeEnum.Convert<ChannelImportance, Contracts.ChannelImportance>(channel.Importance),
        BridgeEnum.Convert<ChannelSound, Contracts.ChannelSound>(channel.Sound),
        [
            .. (channel.Actions ?? []).Select(x => new Contracts.NotificationChannelAction(
                x.Identifier,
                x.Title,
                BridgeEnum.Convert<ChannelActionType, Contracts.ChannelActionType>(x.ActionType)
            ))
        ]
    );
}
