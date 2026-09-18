using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Push.Client;
using Shiny.Net.HttpServer;
using Shiny.Push;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Push;

public sealed class WebAppPushOptions
{
    /// <summary>
    /// Hands arriving pushes to the web app's handlers — <c>push.received</c> when one arrives,
    /// <c>push.entry</c> when the user opens the app from one — in the page if it is listening, in
    /// background.js otherwise. Off by default: the bridge then only registers, unregisters, and reports the
    /// token, and the native app keeps pushes to itself.
    /// </summary>
    public bool DispatchToWebApp { get; set; }

    /// <summary>
    /// Registers Shiny's push service. Turn off to call <c>AddPush</c> yourself — on Android, to pass your own
    /// <c>FirebaseConfig</c> instead of the embedded google-services.json.
    /// </summary>
    public bool RegisterPushService { get; set; } = true;
}

public static class PushBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/push</c> and registers Shiny's push service for the platform — there is nothing else
    /// to call.
    /// <code>
    /// bridge.AddPushBridge();                                  // register, unregister, token
    /// bridge.AddPushBridge(o => o.DispatchToWebApp = true);    // …and the web app handles pushes
    /// </code>
    /// <para>
    /// The platform setup is Shiny.Push's: the <c>aps-environment</c> entitlement and the
    /// <c>remote-notification</c> background mode on Apple platforms; google-services.json on Android.
    /// </para>
    /// </summary>
    public static TBuilder AddPushBridge<TBuilder>(this TBuilder bridge, Action<WebAppPushOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var options = new WebAppPushOptions();
        configure?.Invoke(options);
        bridge.Services.TryAddSingleton(options);

#if MACOS
        bridge.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        // The delegate is registered either way: token changes reach the page as events even when the app
        // registered the push service itself. Shiny runs every registered delegate.
        if (options.RegisterPushService)
            bridge.Services.AddPush<WebAppPushDelegate>();
        else
            bridge.Services.AddSingleton<IPushDelegate, WebAppPushDelegate>();
#endif

        bridge.AddBridge<PushBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/push</c> over <see cref="IPushManager"/>.
/// <code>
/// GET    /_bridge/push              { "access": "Available", "token": "…", "nativeToken": "…" }
/// POST   /_bridge/push/registration requests permission and registers; answers the same shape
/// DELETE /_bridge/push/registration
/// GET    /_bridge/push/tags         501 where the provider has no tags
/// PUT    /_bridge/push/tags         { "tags": ["news"] }
///
/// events:   push.token, push.unregistered, push.received, push.entry
/// handlers: push.received, push.entry   (only with DispatchToWebApp)
/// </code>
/// </summary>
public sealed class PushBridge(IServiceProvider services) : IWebAppBridge
{
    readonly IPushManager? push = services.GetOptionalService<IPushManager>();

    public string Name => "push";

    public bool IsSupported => this.push is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        // The delegate raises these; mapping them here means the topics exist as soon as the server is composed.
        routes.Events.Source(WebAppPushDelegate.TokenEvent, PushJsonContext.Default.PushTokenEvent);
        routes.Events.Source(WebAppPushDelegate.UnregisteredEvent, PushJsonContext.Default.PushTokenEvent);
        routes.Events.Source(WebAppPushDelegate.ReceivedEvent, PushJsonContext.Default.PushPayload);
        routes.Events.Source(WebAppPushDelegate.EntryEvent, PushJsonContext.Default.PushPayload);

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/registration", this.RegisterAsync)
            .MapDelete("/registration", this.UnregisterAsync)
            .MapGet("/tags", this.GetTagsAsync)
            .MapPut("/tags", this.SetTagsAsync);
    }

    async ValueTask StatusAsync(HttpContext context)
    {
        if (this.push is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Push");
            return;
        }

        var access = await p.GetCurrentAccess();
        await WebAppBridgeResults.Json(
            context,
            new PushStatus(BridgeEnum.Convert<AccessState, ContractAccess>(access), p.RegistrationToken, p.NativeRegistrationToken),
            PushJsonContext.Default.PushStatus
        );
    }

    async ValueTask RegisterAsync(HttpContext context)
    {
        if (this.push is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Push");
            return;
        }

        // The permission prompt is UI.
        var state = await services.GetRequiredService<IWebAppMainThread>().InvokeAsync(() => p.RequestAccess(context.RequestAborted));

        await WebAppBridgeResults.Json(
            context,
            new PushStatus(BridgeEnum.Convert<AccessState, ContractAccess>(state.Status), state.RegistrationToken ?? p.RegistrationToken, p.NativeRegistrationToken),
            PushJsonContext.Default.PushStatus
        );
    }

    async ValueTask UnregisterAsync(HttpContext context)
    {
        if (this.push is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "Push");
            return;
        }

        await p.UnRegister();
        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask GetTagsAsync(HttpContext context)
    {
        if (this.push is not { } p || !p.IsTagsSupport())
            return WebAppBridgeResults.NotSupported(context, "Push tags");

        return WebAppBridgeResults.Json(context, new PushTags(p.TryGetTags() ?? []), PushJsonContext.Default.PushTags);
    }

    async ValueTask SetTagsAsync(HttpContext context)
    {
        if (this.push is not { } p || !p.IsTagsSupport())
        {
            await WebAppBridgeResults.NotSupported(context, "Push tags");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, PushJsonContext.Default.PushTags) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"tags\": [\"…\"] }.");
            return;
        }

        if (!await p.TrySetTags([.. body.Tags]))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "tags_failed", "The push provider did not accept the tags.");
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }
}

/// <summary>
/// Turns Shiny's push callbacks into page events, and — with <see cref="WebAppPushOptions.DispatchToWebApp"/>
/// — into calls to the web app's <c>push.received</c> and <c>push.entry</c> handlers. Subclass to decide per
/// push, for example to keep some to the native app.
/// </summary>
public class WebAppPushDelegate(WebAppEventHub events, WebAppInvoker invoker, WebAppPushOptions options) : IPushDelegate
{
    internal const string TokenEvent = "push.token";
    internal const string UnregisteredEvent = "push.unregistered";
    internal const string ReceivedEvent = "push.received";
    internal const string EntryEvent = "push.entry";

    readonly WebAppEventSource<PushTokenEvent> tokens = events.Source(TokenEvent, PushJsonContext.Default.PushTokenEvent);
    readonly WebAppEventSource<PushTokenEvent> unregistered = events.Source(UnregisteredEvent, PushJsonContext.Default.PushTokenEvent);
    readonly WebAppEventSource<PushPayload> received = events.Source(ReceivedEvent, PushJsonContext.Default.PushPayload);
    readonly WebAppEventSource<PushPayload> entries = events.Source(EntryEvent, PushJsonContext.Default.PushPayload);

    public virtual Task OnReceived(PushNotification notification) => this.HandleAsync(ReceivedEvent, this.received, notification);

    public virtual Task OnEntry(PushNotification notification) => this.HandleAsync(EntryEvent, this.entries, notification);

    public virtual Task OnNewToken(string token)
    {
        this.tokens.Publish(new PushTokenEvent(token));
        return Task.CompletedTask;
    }

    public virtual Task OnUnRegistered(string token)
    {
        this.unregistered.Publish(new PushTokenEvent(token));
        return Task.CompletedTask;
    }

    /// <summary>Whether a push goes to the web app's handlers. <see cref="WebAppPushOptions.DispatchToWebApp"/> by default.</summary>
    protected virtual bool ShouldDispatch(string handler, PushNotification notification) => options.DispatchToWebApp;

    async Task HandleAsync(string name, WebAppEventSource<PushPayload> source, PushNotification notification)
    {
        var payload = new PushPayload(
            new Dictionary<string, string>(notification.Data),
            notification.Notification?.Title,
            notification.Notification?.Message
        );

        if (!this.ShouldDispatch(name, notification))
            return;

        // The live event is for pages that only display pushes; the handler call is the one that does work,
        // so a page that registers a handler should not also act on the event.
        source.Publish(payload);
        await invoker.InvokeAsync(name, payload, PushJsonContext.Default.PushPayload);
    }
}
