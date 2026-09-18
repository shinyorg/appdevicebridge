using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.Notifications;
using Notification = Shiny.Notifications.Notification;
using Contracts = Shiny.AppDeviceBridge.Notifications.Client;
#if IOS || MACCATALYST || MACOS
using Foundation;
using UserNotifications;
#endif

namespace Shiny.AppDeviceBridge.Notifications;

/// <summary>Which notifications reach the web app's handlers.</summary>
public enum WebAppNotificationDispatch
{
    /// <summary>Only those the web app sent through the bridge. The native app keeps its own.</summary>
    WebApp,

    /// <summary>Every local notification, whoever sent it.</summary>
    All,

    /// <summary>None: the bridge only sends and manages notifications.</summary>
    None
}

public sealed class WebAppNotificationOptions
{
    /// <summary>
    /// Which notifications are handed to the web app's <c>notification.entry</c> and <c>notification.received</c>
    /// handlers — in the page if it is listening, in background.js otherwise. The web app's own by default.
    /// </summary>
    public WebAppNotificationDispatch Dispatch { get; set; } = WebAppNotificationDispatch.WebApp;

    /// <summary>
    /// Registers Shiny's notification service. Turn off to call <c>AddNotifications</c> yourself — on iOS, to pass
    /// your own <c>IosConfiguration</c>. It is skipped anyway when the app registered it before adding the bridge.
    /// </summary>
    public bool RegisterNotificationService { get; set; } = true;
}

public static class NotificationsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/notifications</c> and registers Shiny's notification service for the platform — there is
    /// nothing else to call.
    /// <code>
    /// builder.AddNotificationsBridge();
    /// builder.AddNotificationsBridge(o => o.Dispatch = WebAppNotificationDispatch.All);
    /// </code>
    /// <para>
    /// The platform setup is Shiny.Notifications': <c>POST_NOTIFICATIONS</c>, and <c>SCHEDULE_EXACT_ALARM</c> for
    /// on-time scheduled notifications, plus a drawable named <c>notification</c> for the small icon on Android.
    /// Location usage descriptions for geofence triggers.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddNotificationsBridge(this MauiAppBuilder builder, Action<WebAppNotificationOptions>? configure = null)
        => builder.AddNotificationsBridge<WebAppNotificationDelegate>(configure);

    /// <summary>As <see cref="AddNotificationsBridge(MauiAppBuilder, Action{WebAppNotificationOptions}?)"/>, with your own delegate deciding per notification.</summary>
    public static MauiAppBuilder AddNotificationsBridge<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDelegate>(
        this MauiAppBuilder builder,
        Action<WebAppNotificationOptions>? configure = null
    ) where TDelegate : WebAppNotificationDelegate
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WebAppNotificationOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#elif MACOS
        builder.Services.EnsureShinyCore();
#endif

        // Shiny registers its manager as every interface it implements — on Apple platforms, one of them is the
        // lifecycle notification handler — so a second registration would answer every notification twice.
        var registered = builder.Services.Any(x => x.ServiceType == typeof(INotificationManager));

        if (options.RegisterNotificationService && !registered)
        {
            // Called as static methods: extension syntax would bind to whichever AddNotifications is in scope.
#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
            global::Shiny.NotificationsServiceCollectionExtensions.AddNotifications(builder.Services);
#else
            if (OperatingSystem.IsLinux())
            {
                global::Shiny.NotificationLinuxServiceCollectionExtensions.AddNotifications(builder.Services);
                builder.Services.AddSingleton<IMauiInitializeService, LinuxNotificationStarter>();
            }
#endif
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationDelegate, TDelegate>());

#if IOS || MACCATALYST
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<global::Shiny.Hosting.IIosLifecycle.INotificationHandler, WebAppNotificationPresenter>());
#elif MACOS
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<global::Shiny.Hosting.IMacLifecycle.INotificationHandler, WebAppNotificationPresenter>());
#endif

        builder.Services.AddWebAppBridge<NotificationsBridge>();
        return builder;
    }
}

/// <summary>
/// Turns notification taps — and, on Apple platforms, notifications arriving while the app is open — into page
/// events and calls to the web app's <c>notification.entry</c> and <c>notification.received</c> handlers. Subclass
/// and override <see cref="ShouldDispatch"/> to decide per notification.
/// </summary>
public class WebAppNotificationDelegate(WebAppEventHub events, WebAppInvoker invoker, WebAppNotificationOptions options) : INotificationDelegate
{
    internal const string EntryEvent = "notification.entry";
    internal const string ReceivedEvent = "notification.received";

    readonly WebAppEventSource<Contracts.NotificationEvent> entries = events.Source(EntryEvent, Contracts.NotificationsJsonContext.Default.NotificationEvent);
    readonly WebAppEventSource<Contracts.NotificationEvent> received = events.Source(ReceivedEvent, Contracts.NotificationsJsonContext.Default.NotificationEvent);

    public virtual Task OnEntry(NotificationResponse response)
        => this.HandleAsync(EntryEvent, this.entries, response.Notification, response.ActionIdentifier, response.Text);

    /// <summary>A notification presented while the app is in the foreground. Apple platforms only: nowhere else says so.</summary>
    public virtual Task OnReceived(Notification notification)
        => this.HandleAsync(ReceivedEvent, this.received, notification, null, null);

    /// <summary>Whether a notification goes to the web app. <see cref="WebAppNotificationOptions.Dispatch"/> by default.</summary>
    protected virtual bool ShouldDispatch(string handler, Notification notification) => options.Dispatch switch
    {
        WebAppNotificationDispatch.All => true,
        WebAppNotificationDispatch.WebApp => WebAppNotifications.IsFromWebApp(notification),
        _ => false
    };

    async Task HandleAsync(string name, WebAppEventSource<Contracts.NotificationEvent> source, Notification notification, string? action, string? text)
    {
        if (!this.ShouldDispatch(name, notification))
            return;

        var payload = NotificationContractMapping.ToEvent(notification, action, text);

        // As with push: the event is for pages that only display; the handler call is the one that does work.
        source.Publish(payload);
        await invoker.InvokeAsync(name, payload, Contracts.NotificationsJsonContext.Default.NotificationEvent);
    }
}

/// <summary>What differs by platform, kept in one place so the bridge reads the same everywhere.</summary>
static class NotificationPlatform
{
#if IOS || MACCATALYST || MACOS
    public const bool SupportsBadge = true;
    public const bool SupportsReceived = true;
#else
    public const bool SupportsBadge = false;
    public const bool SupportsReceived = false;
#endif

    // Linux and Windows never call the delegate.
#if ANDROID || IOS || MACCATALYST || MACOS
    public const bool SupportsEntry = true;
#else
    public const bool SupportsEntry = false;
#endif

#if ANDROID || IOS || MACCATALYST
    public const bool SupportsGeofences = true;
#else
    public const bool SupportsGeofences = false;
#endif

#if IOS || MACCATALYST
    public const bool SupportsImages = true;

    public static Notification Create(string? subtitle) => new AppleNotification { Subtitle = subtitle };

    public static bool TryAttachImage(Notification notification, string path, out string? copy, out string? error)
    {
        // The system moves an attachment into its own store, so it gets a copy rather than the page's file.
        var directory = Path.Combine(Path.GetTempPath(), "appdevicebridge-notifications");
        Directory.CreateDirectory(directory);
        copy = Path.Combine(directory, Guid.NewGuid().ToString("n") + Path.GetExtension(path));
        File.Copy(path, copy);

        var attachment = UNNotificationAttachment.FromIdentifier("image", NSUrl.FromFilename(copy), new UNNotificationAttachmentOptions(), out var nsError);
        if (attachment is null)
        {
            DiscardAttachment(copy);
            copy = null;
            error = nsError?.LocalizedDescription ?? "unknown error";
            return false;
        }

        ((AppleNotification)notification).Attachments = [attachment];
        error = null;
        return true;
    }
#else
    public const bool SupportsImages = false;

    public static Notification Create(string? subtitle) => new();

    public static bool TryAttachImage(Notification notification, string path, out string? copy, out string? error)
    {
        copy = null;
        error = "not supported";
        return false;
    }
#endif

    public static void DiscardAttachment(string? copy)
    {
        try
        {
            if (copy is not null)
                File.Delete(copy);
        }
        catch (IOException)
        {
            // A temp file; the OS clears it eventually.
        }
    }

#if IOS || MACCATALYST
    public static Task<int> GetBadgeAsync(IServiceProvider services)
        => services.GetRequiredService<IPlatform>().InvokeOnMainThreadAsync(() =>
        {
            // Deprecated in iOS 17 without a replacement for reading it.
#pragma warning disable CA1422
            return (int)UIKit.UIApplication.SharedApplication.ApplicationIconBadgeNumber;
#pragma warning restore CA1422
        });

    public static Task SetBadgeAsync(IServiceProvider services, int value)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
            return UNUserNotificationCenter.Current.SetBadgeCountAsync(value);

        // Only reached below iOS 16, where there is nothing else; the analyzer does not follow the guard above.
        return services.GetRequiredService<IPlatform>().InvokeOnMainThreadAsync(() =>
        {
#pragma warning disable CA1422
            UIKit.UIApplication.SharedApplication.ApplicationIconBadgeNumber = value;
#pragma warning restore CA1422
        });
    }
#elif MACOS
    public static Task<int> GetBadgeAsync(IServiceProvider services)
        => services.GetRequiredService<IPlatform>().InvokeOnMainThreadAsync(
            () => Int32.TryParse(AppKit.NSApplication.SharedApplication.DockTile.BadgeLabel, out var value) ? value : 0
        );

    public static Task SetBadgeAsync(IServiceProvider services, int value)
        => services.GetRequiredService<IPlatform>().InvokeOnMainThreadAsync(() =>
        {
            AppKit.NSApplication.SharedApplication.DockTile.BadgeLabel = value == 0 ? null : value.ToString();
        });
#else
    public static Task<int> GetBadgeAsync(IServiceProvider services) => Task.FromResult(0);

    public static Task SetBadgeAsync(IServiceProvider services, int value) => Task.CompletedTask;
#endif
}

#if IOS || MACCATALYST || MACOS
/// <summary>
/// Hears notifications presented while the app is open. Shiny's own manager answers the presentation and runs the
/// delegates for taps; this only reports arrivals, so it never calls a completion handler.
/// </summary>
sealed class WebAppNotificationPresenter(IServiceProvider services, ILogger<WebAppNotificationPresenter> logger)
#if MACOS
    : global::Shiny.Hosting.IMacLifecycle.INotificationHandler
#else
    : global::Shiny.Hosting.IIosLifecycle.INotificationHandler
#endif
{
    public void OnDidReceiveNotificationResponse(UNNotificationResponse response, Action completionHandler)
    {
    }

    public void OnWillPresentNotification(UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
    {
        // Pushes belong to the push bridge.
        if (notification.Request.Trigger is UNPushNotificationTrigger)
            return;

        var shiny = notification.Request.FromNative();

        foreach (var target in services.GetServices<INotificationDelegate>().OfType<WebAppNotificationDelegate>())
        {
            target.OnReceived(shiny).ContinueWith(
                t => logger.LogError(t.Exception, "notification.received failed"),
                TaskContinuationOptions.OnlyOnFaulted
            );
        }
    }
}
#endif

#if !(ANDROID || IOS || MACCATALYST || MACOS || WINDOWS)
/// <summary>
/// The Linux manager schedules from a startup task, and no Shiny host runs startup tasks under the GTK4 backend.
/// </summary>
sealed class LinuxNotificationStarter : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        if (!global::Shiny.Hosting.Host.IsInitialized && services.GetService<INotificationManager>() is IShinyStartupTask task)
            task.Start();
    }
}
#endif
