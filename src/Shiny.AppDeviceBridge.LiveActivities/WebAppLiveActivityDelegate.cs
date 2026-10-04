using Shiny.AppDeviceBridge.LiveActivities.Client;
using Native = Shiny.LiveActivities;

namespace Shiny.AppDeviceBridge.LiveActivities;

/// <summary>
/// Turns what ActivityKit and Android report into page events and calls to the web app's <c>liveactivities.*</c>
/// handlers — the page when it is listening, background.js otherwise — so a push token reaches the app's server even
/// when it is issued with no page open.
/// <para>
/// Subclass to keep some of it to the native app: override a member and call the base only for what the web app
/// should see.
/// </para>
/// </summary>
public class WebAppLiveActivityDelegate(WebAppEventHub events, WebAppInvoker invoker) : Native.ILiveActivityDelegate
{
    internal const string StartedEvent = "liveactivities.started";
    internal const string StateEvent = "liveactivities.state";
    internal const string TokenEvent = "liveactivities.token";
    internal const string StartTokenEvent = "liveactivities.starttoken";

    readonly WebAppEventSource<LiveActivity> started = events.Source(StartedEvent, LiveActivitiesJsonContext.Default.LiveActivity);
    readonly WebAppEventSource<LiveActivity> states = events.Source(StateEvent, LiveActivitiesJsonContext.Default.LiveActivity);
    readonly WebAppEventSource<LiveActivityPushToken> tokens = events.Source(TokenEvent, LiveActivitiesJsonContext.Default.LiveActivityPushToken);
    readonly WebAppEventSource<LiveActivityPushToStartToken> startTokens = events.Source(StartTokenEvent, LiveActivitiesJsonContext.Default.LiveActivityPushToStartToken);


    public virtual async Task OnStarted(Native.LiveActivity activity)
    {
        var payload = LiveActivitiesBridge.ToContract(activity);
        this.started.Publish(payload);
        await invoker.InvokeAsync(StartedEvent, payload, LiveActivitiesJsonContext.Default.LiveActivity).ConfigureAwait(false);
    }


    public virtual async Task OnStateChanged(Native.LiveActivity activity)
    {
        var payload = LiveActivitiesBridge.ToContract(activity);
        this.states.Publish(payload);
        await invoker.InvokeAsync(StateEvent, payload, LiveActivitiesJsonContext.Default.LiveActivity).ConfigureAwait(false);
    }


    public virtual async Task OnPushTokenChanged(Native.LiveActivity activity, string token)
    {
        var payload = new LiveActivityPushToken(activity.Id, token);
        this.tokens.Publish(payload);
        await invoker.InvokeAsync(TokenEvent, payload, LiveActivitiesJsonContext.Default.LiveActivityPushToken).ConfigureAwait(false);
    }


    public virtual async Task OnPushToStartTokenChanged(string token)
    {
        var payload = new LiveActivityPushToStartToken(token);
        this.startTokens.Publish(payload);
        await invoker.InvokeAsync(StartTokenEvent, payload, LiveActivitiesJsonContext.Default.LiveActivityPushToStartToken).ConfigureAwait(false);
    }
}
