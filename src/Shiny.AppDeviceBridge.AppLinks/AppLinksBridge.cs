using Microsoft.Maui.LifecycleEvents;
using Shiny.AppDeviceBridge.Maui;
#if ANDROID
using Android.Content;
#elif IOS || MACCATALYST
using Foundation;
using UIKit;
#elif WINDOWS
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
#endif

namespace Shiny.AppDeviceBridge.AppLinks;

public static class AppLinksBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/links</c> and routes the app's incoming links to the web app — there is nothing else to
    /// call in code.
    /// <code>
    /// bridge.AddAppLinksBridge(o =>
    /// {
    ///     o.Schemes.Add("myapp");               // myapp://orders/42  → /orders/42
    ///     o.Hosts.Add("app.example.com");       // https://app.example.com/orders/42 → /orders/42
    ///     o.NavigateOnColdStart = true;
    /// });
    /// </code>
    /// <para>
    /// The OS only sends the app links it was told about: intent filters on Android (and a <c>SingleTop</c> main
    /// activity), <c>CFBundleURLTypes</c> and the Associated Domains entitlement on Apple platforms, a protocol
    /// registration on Windows, <c>x-scheme-handler</c> in the .desktop file on Linux.
    /// </para>
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddAppLinksBridge(this MauiAppDeviceBridgeBuilder bridge, Action<WebAppLinkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(configure);

        var links = bridge.Services.GetOrAddWebAppLinks();
        configure(links.Options);
        AppLinks.Attach(links);

#if ANDROID
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddAndroid(android => android
            // A saved state means the activity is being recreated, and its intent is the link it already handled.
            .OnCreate((activity, state) =>
            {
                if (state is null)
                    Receive(links, activity.Intent);
            })
            .OnNewIntent((_, intent) => Receive(links, intent))
        ));
#elif IOS || MACCATALYST
        // Launch options are not read: when FinishedLaunching returns true, iOS delivers the same link to
        // OpenUrl or ContinueUserActivity straight after.
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddiOS(ios => ios
            .OpenUrl((_, url, _) => Receive(links, url))
            .ContinueUserActivity((_, activity, _) => Receive(links, activity))
            .SceneWillConnect((_, _, connection) =>
            {
                foreach (var context in connection.UrlContexts)
                    Receive(links, context.Url);

                foreach (var activity in connection.UserActivities)
                    Receive(links, activity);
            })
            .SceneOpenUrl((_, contexts) =>
            {
                var handled = false;
                foreach (var context in contexts)
                    handled |= Receive(links, context.Url);

                return handled;
            })
            .SceneContinueUserActivity((_, activity) => Receive(links, activity))
        ));
#elif MACOS
        AppleEventLinkHandler.Register(links);
#elif WINDOWS
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows
            .OnLaunched((_, _) =>
            {
                var instance = AppInstance.GetCurrent();
                Receive(links, instance.GetActivatedEventArgs());

                // Raised only for activations redirected to this instance; see AppInstance.RedirectActivationToAsync.
                instance.Activated += (_, args) => Receive(links, args);
            })
        ));
#else
        // GTK4: the desktop launches `app %u` for a registered x-scheme-handler, so a link is an argument.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && links.Receive(uri))
                break;
        }
#endif

        return bridge;
    }

#if ANDROID
    static void Receive(WebAppLinks links, Intent? intent)
    {
        if (intent is not { Action: Intent.ActionView, Data: { } data })
            return;

        // Reopened from recents, Android hands back the intent that first launched the activity.
        if (intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory))
            return;

        if (Uri.TryCreate(data.ToString(), UriKind.Absolute, out var uri))
            links.Receive(uri);
    }
#elif IOS || MACCATALYST
    static bool Receive(WebAppLinks links, NSUrl? url)
        => url?.AbsoluteString is { } value
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && links.Receive(uri);

    static bool Receive(WebAppLinks links, NSUserActivity activity)
        => activity.ActivityType == NSUserActivityType.BrowsingWeb && Receive(links, activity.WebPageUrl);
#elif WINDOWS
    static void Receive(WebAppLinks links, AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol && args.Data is IProtocolActivatedEventArgs protocol)
            links.Receive(protocol.Uri);
    }
#endif
}

/// <summary>
/// For links the bridge cannot hook itself — a platform head's own delegate, or a head with no lifecycle events.
/// <code>
/// // maui-labs AppKit head: the link that launches the app arrives before MauiProgram runs
/// public override void OpenUrls(NSApplication application, NSUrl[] urls)
/// {
///     foreach (var url in urls)
///         AppLinks.Receive(new Uri(url.AbsoluteString!));
/// }
/// </code>
/// </summary>
public static class AppLinks
{
    static readonly Lock Gate = new();
    static WebAppLinks? current;
    static Uri? early;

    /// <summary>
    /// Offers a link to the web app. One that arrives before <c>AddAppLinksBridge</c> has run is held until it
    /// does; only the latest is kept.
    /// </summary>
    public static void Receive(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        WebAppLinks? links;
        lock (Gate)
        {
            links = current;
            if (links is null)
                early = uri;
        }

        links?.Receive(uri);
    }

    internal static void Attach(WebAppLinks links)
    {
        Uri? held;
        lock (Gate)
        {
            current = links;
            held = early;
            early = null;
        }

        if (held is not null)
            links.Receive(held);
    }
}
