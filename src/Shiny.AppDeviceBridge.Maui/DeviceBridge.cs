using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Maui;

/// <summary>
/// <c>/_bridge/device</c> — the device's apps and feedback, over .NET MAUI Essentials. Each head supplies its own platform
/// build of Essentials, and a backend without a feature throws rather than lacking the API, so every feature answers 501
/// on its own.
/// <code>
/// POST   /_bridge/device/browser          { "uri": "https://…", "launchMode": "External" }
/// POST   /_bridge/device/map              { "latitude": 43.6, "longitude": -79.4, "navigationMode": "Driving" }
/// POST   /_bridge/device/share            { "text": "…", "uri": "https://…", "title": "…" }
///                                         { "files": [{ "root": "data", "path": "photos/cat.jpg" }], "title": "…" }
/// POST   /_bridge/device/haptics          { "type": "click" | "longPress" }
/// POST   /_bridge/device/vibrate          { "durationMs": 500 }     at most 5000
/// DELETE /_bridge/device/vibrate
/// GET    /_bridge/device/keep-awake      { "keepAwake": false }
/// PUT    /_bridge/device/keep-awake      501 where the screen cannot be kept on
/// DELETE /_bridge/device/keep-awake
/// GET    /_bridge/device/clipboard        { "text": null | "…" }
/// PUT    /_bridge/device/clipboard        { "text": "…" }
/// DELETE /_bridge/device/clipboard
/// </code>
/// </summary>
public sealed class DeviceBridge : IWebAppBridge
{
    const int MaxShareFiles = 20;
    const int MaxVibrateMs = 5000;
    const int MaxClipboardChars = 1024 * 1024;

    readonly WebAppFileRoots? fileRoots;

    public DeviceBridge(IServiceProvider services)
        => this.fileRoots = services.GetOptionalService<WebAppFileRoots>();

    public string Name => "device";

    public bool IsSupported => true;

    static Contracts.DeviceJsonContext Json => Contracts.DeviceJsonContext.Default;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapPost("/browser", OpenBrowserAsync)
        .MapPost("/map", OpenMapAsync)
        .MapPost("/share", this.ShareAsync)
        .MapPost("/haptics", HapticsAsync)
        .MapPost("/vibrate", VibrateAsync)
        .MapDelete("/vibrate", ctx => Essentials(ctx, "Vibration", async () =>
        {
            await OnMainThread(() =>
            {
                Vibration.Default.Cancel();
                return true;
            });
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/keep-awake", ctx => Essentials(ctx, "Keep screen on", async () =>
        {
            var keepAwake = await OnMainThread(() => DeviceDisplay.Current.KeepScreenOn);
            await WebAppBridgeResults.Json(ctx, new Contracts.KeepAwakeStatus(keepAwake), Json.KeepAwakeStatus);
        }))
        .MapPut("/keep-awake", ctx => SetKeepAwakeAsync(ctx, true))
        .MapDelete("/keep-awake", ctx => SetKeepAwakeAsync(ctx, false))
        .MapGet("/clipboard", ctx => Essentials(ctx, "Clipboard", async () =>
        {
            var text = await OnMainThread(async () => Clipboard.Default.HasText ? await Clipboard.Default.GetTextAsync() : null);
            await WebAppBridgeResults.Json(ctx, new Contracts.ClipboardText(text), Json.ClipboardText);
        }))
        .MapPut("/clipboard", SetClipboardAsync)
        .MapDelete("/clipboard", ctx => Essentials(ctx, "Clipboard", async () =>
        {
            await OnMainThread(async () =>
            {
                await Clipboard.Default.SetTextAsync(null);
                return true;
            });
            await WebAppBridgeResults.NoContent(ctx);
        }));

    static async ValueTask OpenBrowserAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.OpenBrowserRequest);

        // The page can ask for web and contact links, not arbitrary schemes that open other apps.
        if (body is null
            || !Enum.IsDefined(body.LaunchMode)
            || !Uri.TryCreate(body.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "mailto" or "tel"))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"uri\": \"https://…\" } with an http, https, mailto or tel URI.");
            return;
        }

        var options = new BrowserLaunchOptions
        {
            LaunchMode = Convert<Contracts.BrowserLaunchMode, BrowserLaunchMode>(body.LaunchMode),
            TitleMode = body.ShowTitle ? BrowserTitleMode.Show : BrowserTitleMode.Hide
        };

        await Essentials(context, "Browser", async () =>
        {
            // mailto and tel are not web pages: the browser sheet cannot show them, the app that handles them can.
            var opened = uri.Scheme is "https" or "http"
                ? await OnMainThread(() => Browser.Default.OpenAsync(uri, options))
                : await OnMainThread(() => Launcher.Default.TryOpenAsync(uri));

            await Success(context, opened);
        });
    }

    static async ValueTask OpenMapAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.OpenMapRequest);
        if (body is null
            || body.Latitude is < -90 or > 90
            || body.Longitude is < -180 or > 180
            || !Enum.IsDefined(body.NavigationMode))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"latitude\": …, \"longitude\": … } within range.");
            return;
        }

        var options = new MapLaunchOptions { NavigationMode = Convert<Contracts.NavigationMode, NavigationMode>(body.NavigationMode) };

        await Essentials(context, "Maps", async () =>
        {
            var opened = await OnMainThread(() => Microsoft.Maui.ApplicationModel.Map.Default.TryOpenAsync(body.Latitude, body.Longitude, options));
            await Success(context, opened);
        });
    }

    async ValueTask ShareAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.ShareRequest);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"text\": …, \"uri\": … } or { \"files\": [{ \"root\": …, \"path\": … }] }.");
            return;
        }

        if (body.Files is { Count: > 0 } files)
        {
            if (files.Count > MaxShareFiles)
            {
                await WebAppBridgeResults.BadRequest(context, $"At most {MaxShareFiles} files can be shared at once.");
                return;
            }

            if (this.fileRoots is not { } roots)
            {
                await WebAppBridgeResults.NotSupported(context, "File sharing");
                return;
            }

            var shareFiles = new List<ShareFile>(files.Count);
            foreach (var file in files)
            {
                if (!roots.TryResolve(file.Root, file.Path, out var full))
                {
                    await WebAppBridgeResults.BadRequest(context, $"'{file.Root}/{file.Path}' is not a valid file path.");
                    return;
                }

                if (!File.Exists(full))
                {
                    await WebAppBridgeResults.NotFound(context, $"'{file.Root}/{file.Path}' does not exist.");
                    return;
                }

                shareFiles.Add(new ShareFile(full));
            }

            await Essentials(context, "Sharing", async () =>
            {
                await OnMainThread(async () =>
                {
                    await Share.Default.RequestAsync(new ShareMultipleFilesRequest(body.Title ?? String.Empty, shareFiles));
                    return true;
                });
                await WebAppBridgeResults.NoContent(context);
            });
            return;
        }

        Uri? uri = null;
        if (body.Uri is not null
            && (!Uri.TryCreate(body.Uri, UriKind.Absolute, out uri) || uri.Scheme is not ("https" or "http")))
        {
            await WebAppBridgeResults.BadRequest(context, "uri must be an absolute http or https URI.");
            return;
        }

        if (String.IsNullOrEmpty(body.Text) && uri is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected text, uri or files to share.");
            return;
        }

        await Essentials(context, "Sharing", async () =>
        {
            await OnMainThread(async () =>
            {
                await Share.Default.RequestAsync(new ShareTextRequest
                {
                    Text = body.Text,
                    Uri = uri?.AbsoluteUri,
                    Title = body.Title,
                    Subject = body.Subject
                });
                return true;
            });
            await WebAppBridgeResults.NoContent(context);
        });
    }

    static async ValueTask HapticsAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.HapticsRequest);
        if (body is null || !Enum.IsDefined(body.Type))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"type\": \"click\" | \"longPress\" }.");
            return;
        }

        await Essentials(context, "Haptic feedback", async () =>
        {
            var performed = await OnMainThread(() =>
            {
                if (!HapticFeedback.Default.IsSupported)
                    return false;

                HapticFeedback.Default.Perform(Convert<Contracts.HapticFeedbackType, HapticFeedbackType>(body.Type));
                return true;
            });

            await (performed ? WebAppBridgeResults.NoContent(context) : WebAppBridgeResults.NotSupported(context, "Haptic feedback"));
        });
    }

    static async ValueTask VibrateAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.VibrateRequest) ?? new Contracts.VibrateRequest();
        if (body.DurationMs is <= 0)
        {
            await WebAppBridgeResults.BadRequest(context, "durationMs must be positive.");
            return;
        }

        var duration = TimeSpan.FromMilliseconds(Math.Min(body.DurationMs ?? 500, MaxVibrateMs));

        await Essentials(context, "Vibration", async () =>
        {
            var vibrated = await OnMainThread(() =>
            {
                if (!Vibration.Default.IsSupported)
                    return false;

                Vibration.Default.Vibrate(duration);
                return true;
            });

            await (vibrated ? WebAppBridgeResults.NoContent(context) : WebAppBridgeResults.NotSupported(context, "Vibration"));
        });
    }

    static ValueTask SetKeepAwakeAsync(HttpContext context, bool keepOn) => Essentials(context, "Keep screen on", async () =>
    {
        // A backend without the feature takes the value and drops it rather than throwing, so read it back: a screen that
        // was asked to stay on and did not is a 501, not a success.
        var applied = await OnMainThread(() =>
        {
            DeviceDisplay.Current.KeepScreenOn = keepOn;
            return DeviceDisplay.Current.KeepScreenOn == keepOn;
        });

        await (applied ? WebAppBridgeResults.NoContent(context) : WebAppBridgeResults.NotSupported(context, "Keep screen on"));
    });

    static async ValueTask SetClipboardAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Json.ClipboardText);
        if (body?.Text is null || body.Text.Length > MaxClipboardChars)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"text\": \"…\" }} of at most {MaxClipboardChars} characters.");
            return;
        }

        await Essentials(context, "Clipboard", async () =>
        {
            await OnMainThread(async () =>
            {
                await Clipboard.Default.SetTextAsync(body.Text);
                return true;
            });
            await WebAppBridgeResults.NoContent(context);
        });
    }

    static ValueTask Success(HttpContext context, bool success)
        => WebAppBridgeResults.Json(context, new Contracts.DeviceActionResult(success), Json.DeviceActionResult);

    /// <summary>Maps the ways Essentials says "not here" to 501, and a missing permission to 403.</summary>
    static async ValueTask Essentials(HttpContext context, string feature, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PlatformNotSupportedException or NotImplementedException)
        {
            await WebAppBridgeResults.NotSupported(context, feature);
        }
        catch (PermissionException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", ex.Message);
        }
    }

    /// <summary>Requests arrive on server threads; anything that presents UI has to run on the main one.</summary>
    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    static Task<T> OnMainThread<T>(Func<T> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : Task.FromResult(action());

    /// <summary>By name: the contracts mirror the Essentials enums without referencing them.</summary>
    static TTo Convert<TFrom, TTo>(TFrom value) where TFrom : struct, Enum where TTo : struct, Enum
        => Contracts.BridgeEnum.Convert<TFrom, TTo>(value);
}

public static class DeviceBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/device</c> — the browser, maps, the share sheet, haptics, vibration, keeping the screen awake and
    /// the clipboard, over .NET MAUI Essentials. Nothing else to call; a feature the backend lacks answers 501.
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddDeviceBridge(this MauiAppDeviceBridgeBuilder bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return bridge.AddBridge<DeviceBridge>();
    }

    /// <summary>
    /// Adds <c>/_bridge/sensors</c> — accelerometer, gyroscope, magnetometer, compass, barometer, orientation and shake, as
    /// live events over .NET MAUI Essentials. A sensor runs only while a page listens to it.
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddSensorsBridge(this MauiAppDeviceBridgeBuilder bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return bridge.AddBridge<SensorsBridge>();
    }
}
