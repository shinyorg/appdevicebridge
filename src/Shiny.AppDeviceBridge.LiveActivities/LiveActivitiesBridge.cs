using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.LiveActivities.Client;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.LiveActivities;

namespace Shiny.AppDeviceBridge.LiveActivities;

public static class LiveActivitiesBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/liveactivities</c> and registers Shiny's live activity service — there is nothing else to call.
    /// <code>
    /// bridge.AddLiveActivitiesBridge();
    /// bridge.AddLiveActivitiesBridge(o => o.ChannelName = "Deliveries");   // the Android notification channel
    /// </code>
    /// <para>
    /// iOS renders an activity from a widget extension: set <c>&lt;ShinyLiveActivityWidget&gt;true&lt;/ShinyLiveActivityWidget&gt;</c>
    /// in the app's project and the build compiles Shiny's stock widget (or the app's own SwiftUI) into it. Token changes
    /// and state changes go to the web app's <c>liveactivities.*</c> handlers — the page if it is listening, background.js
    /// otherwise. An app that already called <c>AddLiveActivities</c> keeps its registration; the bridge only adds its
    /// delegate, and Shiny runs every delegate. iOS and Android only; every other platform answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddLiveActivitiesBridge<TBuilder>(this TBuilder bridge, Action<Native.LiveActivityOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        if (services.Any(x => x.ImplementationType == typeof(WebAppLiveActivityDelegate)))
            return bridge;

        if (services.Any(x => x.ServiceType == typeof(Native.ILiveActivityManager)))
            services.AddSingleton<Native.ILiveActivityDelegate, WebAppLiveActivityDelegate>();
        else
            services.AddLiveActivities<WebAppLiveActivityDelegate>(configure);

        bridge.AddBridge<LiveActivitiesBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/liveactivities</c> over <see cref="Native.ILiveActivityManager"/>.
/// <code>
/// GET    /_bridge/liveactivities                      { "access": "Available", "pushToStartToken": "…" }
/// POST   /_bridge/liveactivities/access               { "access": "Available", "pushToStartToken": "…" }
/// GET    /_bridge/liveactivities/activities           [ { "id", "state", "pushToken" } ]
/// POST   /_bridge/liveactivities/activities           { "content": { "title": "…", "progress": { "value": 0.4 } }, "attributes": {…} }  →  { "id", "state" }
/// PUT    /_bridge/liveactivities/activities/{id}      { "content": {…}, "alert": { "title": "…" } }
/// POST   /_bridge/liveactivities/activities/{id}/end  { "content": {…}, "dismissAt": "…" }
/// DELETE /_bridge/liveactivities/activities
///
/// events:   liveactivities.started, liveactivities.state, liveactivities.token, liveactivities.starttoken
/// handlers: the same four, in the page or background.js
/// </code>
/// </summary>
public sealed class LiveActivitiesBridge(IServiceProvider services) : IWebAppBridge
{
    readonly Native.ILiveActivityManager? manager = services.GetOptionalService<Native.ILiveActivityManager>();

    public string Name => "liveactivities";

    // Off iOS and Android the manager is Shiny's no-op, which says so here.
    public bool IsSupported => this.manager?.IsSupported == true;

    public void Map(WebAppBridgeRoutes routes)
    {
        // The delegate raises these; mapping them here means the topics exist as soon as the server is composed.
        routes.Events.Source(WebAppLiveActivityDelegate.StartedEvent, LiveActivitiesJsonContext.Default.LiveActivity);
        routes.Events.Source(WebAppLiveActivityDelegate.StateEvent, LiveActivitiesJsonContext.Default.LiveActivity);
        routes.Events.Source(WebAppLiveActivityDelegate.TokenEvent, LiveActivitiesJsonContext.Default.LiveActivityPushToken);
        routes.Events.Source(WebAppLiveActivityDelegate.StartTokenEvent, LiveActivitiesJsonContext.Default.LiveActivityPushToStartToken);

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/access", this.RequestAccessAsync)
            .MapGet("/activities", this.ListAsync)
            .MapPost("/activities", this.StartAsync)
            .MapPut("/activities/{id}", this.UpdateAsync)
            .MapPost("/activities/{id}/end", this.EndAsync)
            .MapDelete("/activities", this.EndAllAsync);
    }

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var access = await m.GetCurrentAccess();
        await WebAppBridgeResults.Json(context, ToStatus(m, access), LiveActivitiesJsonContext.Default.LiveActivitiesStatus);
    });

    ValueTask RequestAccessAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // Android's notification permission prompt is UI.
        var access = await services.GetRequiredService<IWebAppMainThread>().InvokeAsync(() => m.RequestAccess(context.RequestAborted));
        await WebAppBridgeResults.Json(context, ToStatus(m, access), LiveActivitiesJsonContext.Default.LiveActivitiesStatus);
    });

    ValueTask ListAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        IReadOnlyList<LiveActivity> list = [.. m.GetAll().Select(ToContract)];
        await WebAppBridgeResults.Json(context, list, LiveActivitiesJsonContext.Default.IReadOnlyListLiveActivity);
    });

    ValueTask StartAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, LiveActivitiesJsonContext.Default.LiveActivityStartRequest) is not { Content: not null } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"content\": { \"title\": \"…\" } }.");
            return;
        }

        Native.LiveActivity activity;
        try
        {
            activity = await m.Start(
                new Native.LiveActivityRequest
                {
                    Content = ToNative(body.Content),
                    Attributes = body.Attributes ?? new Dictionary<string, string>(),
                    Kind = body.Kind,
                    RequestPushToken = body.RequestPushToken
                },
                context.RequestAborted
            );
        }
        catch (InvalidOperationException ex) when (body.RequestPushToken)
        {
            // ActivityKit refuses a push-token request from an app without the aps-environment entitlement, and says only
            // "ActivityInput error 0"; the page cannot tell that apart from anything else.
            throw new InvalidOperationException(
                $"{ex.Message} A push token needs the aps-environment entitlement; without push, start with \"requestPushToken\": false.",
                ex
            );
        }
        await WebAppBridgeResults.Json(context, ToContract(activity), LiveActivitiesJsonContext.Default.LiveActivity);
    });

    ValueTask UpdateAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await this.FindAsync(context, m) is not { } id)
            return;

        if (await WebAppBridgeResults.ReadBodyAsync(context, LiveActivitiesJsonContext.Default.LiveActivityUpdateRequest) is not { Content: not null } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"content\": { \"title\": \"…\" } }.");
            return;
        }

        var alert = body.Alert is { } a ? new Native.LiveActivityAlert(a.Title, a.Body) : null;
        await m.Update(id, ToNative(body.Content), alert, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask EndAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await this.FindAsync(context, m) is not { } id)
            return;

        // The body is optional: ending with nothing keeps the last content.
        var body = await WebAppBridgeResults.ReadBodyAsync(context, LiveActivitiesJsonContext.Default.LiveActivityEndRequest);
        await m.End(id, body?.Content is { } c ? ToNative(c) : null, body?.DismissAt, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask EndAllAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await m.EndAll(context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    /// <summary>The route's id when this app has a running activity by it; otherwise a 404 has been written.</summary>
    async ValueTask<string?> FindAsync(HttpContext context, Native.ILiveActivityManager m)
    {
        var id = context.Request.RouteValues["id"] as string;
        if (!String.IsNullOrWhiteSpace(id) && m.GetAll().Any(x => x.Id == id))
            return id;

        await WebAppBridgeResults.NotFound(context, $"No live activity '{id}'.");
        return null;
    }

    /// <summary>501 where live activities are not supported; the manager's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.ILiveActivityManager, Task> action)
    {
        if (this.manager is not { IsSupported: true } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Live activities");
            return;
        }

        try
        {
            await action(m);
        }
        catch (NotSupportedException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // ActivityKit refused: live activities switched off, too many running, or the activity is gone.
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "live_activity_failed", ex.Message);
        }
    }

    static LiveActivitiesStatus ToStatus(Native.ILiveActivityManager m, Shiny.AccessState access)
        => new(BridgeEnum.Convert<Shiny.AccessState, ContractAccess>(access), m.PushToStartToken);

    internal static LiveActivity ToContract(Native.LiveActivity activity)
        => new(activity.Id, BridgeEnum.Convert<Native.LiveActivityState, LiveActivityState>(activity.State), activity.PushToken);

    internal static Native.LiveActivityContent ToNative(LiveActivityContent content)
    {
        if (content.Progress?.Value is < 0 or > 1)
            throw new ArgumentException("progress.value is a fraction from 0 to 1.");

        return new Native.LiveActivityContent
        {
            Title = content.Title,
            Body = content.Body,
            ShortStatus = content.ShortStatus,
            Progress = content.Progress is { } p
                ? new Native.LiveActivityProgress { Value = p.Value, Start = p.Start, End = p.End, Indeterminate = p.Indeterminate }
                : null,
            StaleDate = content.StaleDate,
            RelevanceScore = content.RelevanceScore,
            Data = content.Data ?? new Dictionary<string, string>()
        };
    }
}
