using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using Shiny.AppDeviceBridge.Maui;
using Shiny.AppDeviceBridge.ShareTarget.Client;
using Shiny.Net.HttpServer;
#if ANDROID
using Android.App;
using Android.Content;
using Android.Provider;
#elif IOS || MACCATALYST
using Foundation;
using UIKit;
#elif WINDOWS
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
#endif

namespace Shiny.AppDeviceBridge.ShareTarget;

public static class ShareTargetBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/sharetarget</c> and routes what other apps share to this one into a queue the page reads — there
    /// is nothing else to call in code.
    /// <code>
    /// bridge.AddShareTargetBridge();
    /// bridge.AddShareTargetBridge(o => o.Root = "cache");
    /// </code>
    /// <para>
    /// The OS only offers the app as a target for what it declared: an <c>ACTION_SEND</c> / <c>ACTION_SEND_MULTIPLE</c>
    /// intent filter on the main activity (a <c>SingleTop</c> one) on Android; <c>CFBundleDocumentTypes</c> on iOS and
    /// Mac Catalyst, where "Open in" and "Copy to" deliver files — text and links from the share sheet need a share
    /// extension, which hands over through <see cref="ShareTarget.ReceiveAsync"/>; the Share Target declaration, and
    /// file type associations, in a packaged Windows app's manifest; and files on the command line on Linux when
    /// <see cref="ShareTargetOptions.AcceptCommandLineFiles"/> is on. The AppKit head calls
    /// <see cref="ShareTarget.ReceiveAsync"/> from its app delegate.
    /// </para>
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddShareTargetBridge(this MauiAppDeviceBridgeBuilder bridge, Action<ShareTargetOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (bridge.Services.Any(x => x.ServiceType == typeof(ShareInbox)))
            return bridge;

        var options = new ShareTargetOptions();
        configure?.Invoke(options);

        bridge.Services.AddSingleton(options);
        bridge.Services.AddSingleton(sp => new ShareInbox(
            options,
            sp.GetRequiredService<WebAppFileRoots>(),
            sp.GetRequiredService<AppDeviceBridgeOptions>(),
            sp.GetService<ILoggerFactory>()
        ));
        // Attached as soon as the app is up, not when a page first calls: a share that launched the app is waiting.
        bridge.Services.AddSingleton<IMauiInitializeService, ShareTargetStartup>();
        bridge.AddBridge<ShareTargetBridge>();

#if ANDROID
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddAndroid(android => android
            // A saved state means the activity is being recreated, and its intent is the share it already handled.
            .OnCreate((activity, state) =>
            {
                if (state is null)
                    Receive(activity, activity.Intent);
            })
            .OnNewIntent(Receive)
        ));
#elif IOS || MACCATALYST
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddiOS(ios => ios
            .OpenUrl((_, url, _) => Receive([url]))
            .SceneWillConnect((_, _, connection) =>
            {
                if (connection?.UrlContexts is { } contexts)
                    Receive([.. contexts.ToArray().Select(x => x.Url)]);
            })
            .SceneOpenUrl((_, contexts) => Receive([.. contexts.ToArray().Select(x => x.Url)]))
        ));
#elif WINDOWS
        bridge.Maui.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows
            .OnLaunched((_, _) =>
            {
                var instance = AppInstance.GetCurrent();
                _ = Receive(instance.GetActivatedEventArgs());

                // Raised only for activations redirected to this instance; see AppInstance.RedirectActivationToAsync.
                instance.Activated += (_, args) => _ = Receive(args);
            })
        ));
#else
        // GTK4: "Open With" launches `app %F`, so the files are arguments. Opt-in, because any readable file named on the
        // command line would reach the page.
        if (options.AcceptCommandLineFiles && OperatingSystem.IsLinux())
        {
            var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).Select(x => IncomingShareFile.FromPath(x)).ToList();
            if (files.Count > 0)
                _ = ShareTarget.ReceiveAsync(new IncomingShare(Files: files));
        }
#endif

        return bridge;
    }

#if ANDROID
    static void Receive(Activity activity, Intent? intent)
    {
        if (intent is not { Action: Intent.ActionSend or Intent.ActionSendMultiple })
            return;

        // Reopened from recents, Android hands back the intent that first launched the activity.
        if (intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory))
            return;

        var resolver = activity.ContentResolver!;
        var files = new List<IncomingShareFile>();

        // startActivity moves EXTRA_STREAM into the clip data, which is also what the read grant covers.
        for (var i = 0; i < (intent.ClipData?.ItemCount ?? 0); i++)
        {
            // content: only. A file: URI names a path, and it could name this app's own private files.
            if (intent.ClipData!.GetItemAt(i)?.Uri is not { Scheme: "content" } uri)
                continue;

            files.Add(new IncomingShareFile(
                DisplayName(resolver, uri),
                resolver.GetType(uri) ?? intent.Type,
                _ => Task.FromResult(resolver.OpenInputStream(uri) ?? throw new IOException($"{uri} could not be opened."))
            ));
        }

        var text = intent.GetStringExtra(Intent.ExtraText) ?? intent.GetCharSequenceExtra(Intent.ExtraText);
        var subject = intent.GetStringExtra(Intent.ExtraSubject) ?? intent.GetStringExtra(Intent.ExtraTitle);

        _ = ShareTarget.ReceiveAsync(new IncomingShare(text, subject, null, files));
    }

    static string? DisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            return cursor?.MoveToFirst() == true ? cursor.GetString(0) : uri.LastPathSegment;
        }
        catch (Java.Lang.Exception)
        {
            // A provider that refuses the query still lets the file be read; it just goes unnamed.
            return uri.LastPathSegment;
        }
    }
#elif IOS || MACCATALYST
    /// <summary>Takes the file URLs among those the app was opened with; links are left to the app links bridge.</summary>
    static bool Receive(NSUrl?[] urls)
    {
        var files = urls.OfType<NSUrl>().Where(x => x.IsFileUrl && x.Path is not null).ToList();
        if (files.Count == 0)
            return false;

        _ = ReceiveAsync(files);
        return true;
    }

    static async Task ReceiveAsync(List<NSUrl> urls)
    {
        // A document opened in place lives in another app's container; access lasts until the copy is done.
        var accessing = urls.Where(x => x.StartAccessingSecurityScopedResource()).ToList();
        try
        {
            await ShareTarget.ReceiveAsync(new IncomingShare(Files: [.. urls.Select(x => IncomingShareFile.FromPath(x.Path!))]));
        }
        finally
        {
            foreach (var url in accessing)
                url.StopAccessingSecurityScopedResource();
        }
    }
#elif WINDOWS
    static async Task Receive(AppActivationArguments args)
    {
        switch (args)
        {
            case { Kind: ExtendedActivationKind.ShareTarget, Data: IShareTargetActivatedEventArgs share }:
                var operation = share.ShareOperation;
                try
                {
                    var data = operation.Data;
                    var text = data.Contains(StandardDataFormats.Text) ? await data.GetTextAsync() : null;
                    var link = data.Contains(StandardDataFormats.WebLink) ? (await data.GetWebLinkAsync())?.AbsoluteUri : null;
                    var items = data.Contains(StandardDataFormats.StorageItems) ? await data.GetStorageItemsAsync() : null;

                    // The sending app's share UI stays up until the files are copied.
                    await ShareTarget.ReceiveAsync(new IncomingShare(text, data.Properties.Title, link, ToFiles(items)));
                    operation.ReportCompleted();
                }
                catch (Exception ex)
                {
                    operation.ReportError(ex.Message);
                }
                break;

            case { Kind: ExtendedActivationKind.File, Data: IFileActivatedEventArgs file }:
                await ShareTarget.ReceiveAsync(new IncomingShare(Files: ToFiles(file.Files)));
                break;
        }
    }

    static List<IncomingShareFile> ToFiles(IReadOnlyList<IStorageItem>? items) =>
    [
        .. (items ?? []).OfType<StorageFile>().Select(x => new IncomingShareFile(
            x.Name,
            String.IsNullOrEmpty(x.ContentType) ? null : x.ContentType,
            async _ => await x.OpenStreamForReadAsync()
        ))
    ];
#endif
}

/// <summary>
/// <c>/_bridge/sharetarget</c> over <see cref="ShareInbox"/>.
/// <code>
/// GET    /_bridge/sharetarget/pending        [ { "id", "receivedAt", "text", "subject", "url", "files": [ { "file": { "root", "path" }, "fileName", "contentType", "size" } ] } ]
/// GET    /_bridge/sharetarget/pending/{id}   one share; 404 when there is none
/// DELETE /_bridge/sharetarget/pending/{id}   removes it and deletes its files; 404 when there is none
///
/// events: sharetarget.received
/// </code>
/// <para>
/// A page reads the queue when it boots and listens after, and removes each share once it has acted on it — so a share
/// seen live is not acted on again after a reload, and one that launched the app is not missed.
/// </para>
/// </summary>
public sealed class ShareTargetBridge(ShareInbox inbox) : IWebAppBridge
{
    public string Name => "sharetarget";

    public bool IsSupported => inbox.IsSupported;

    public ShareInbox Inbox => inbox;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("sharetarget.received", this.Received, ShareTargetJsonContext.Default.SharedContent)
        .MapGet("/pending", ctx => this.Guarded(ctx, this.ListAsync))
        .MapGet("/pending/{id}", ctx => this.Guarded(ctx, this.GetAsync))
        .MapDelete("/pending/{id}", ctx => this.Guarded(ctx, this.RemoveAsync));

    async ValueTask ListAsync(HttpContext context)
    {
        var pending = await inbox.GetPendingAsync(context.RequestAborted);
        await WebAppBridgeResults.Json(context, pending, ShareTargetJsonContext.Default.IReadOnlyListSharedContent);
    }

    async ValueTask GetAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["id"] as string ?? String.Empty;
        if (await inbox.FindAsync(id, context.RequestAborted) is { } share)
            await WebAppBridgeResults.Json(context, share, ShareTargetJsonContext.Default.SharedContent);
        else
            await WebAppBridgeResults.NotFound(context, $"No share '{id}'.");
    }

    async ValueTask RemoveAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["id"] as string ?? String.Empty;
        if (await inbox.RemoveAsync(id, context.RequestAborted))
            await WebAppBridgeResults.NoContent(context);
        else
            await WebAppBridgeResults.NotFound(context, $"No share '{id}'.");
    }

    /// <summary>501 when the configured file root does not exist: shared files would have nowhere to go.</summary>
    async ValueTask Guarded(HttpContext context, Func<HttpContext, ValueTask> action)
    {
        if (!inbox.IsSupported)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status501NotImplemented,
                "not_supported",
                $"Shared files have nowhere to go: there is no file root '{inbox.Options.Root}'."
            );
            return;
        }

        await action(context);
    }

    IAsyncEnumerable<SharedContent> Received(CancellationToken cancellationToken) => WebAppEventStream.FromEvent<SharedContent>(emit =>
    {
        inbox.Received += emit;
        return () => inbox.Received -= emit;
    }, cancellationToken);
}

/// <summary>Creates the inbox at startup and hands it the shares that arrived before it existed.</summary>
sealed class ShareTargetStartup : IMauiInitializeService
{
    public void Initialize(IServiceProvider services) => ShareTarget.Attach(services.GetRequiredService<ShareInbox>());
}
