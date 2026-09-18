using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;

namespace Shiny.AppDeviceBridge.AppSupport;

/// <summary>
/// The .NET MAUI Essentials half of <c>/_bridge/app</c>. Each head supplies its own platform build of Essentials,
/// and a backend without a feature throws rather than lacking the API, so every feature answers 501 on its own.
/// <code>
/// POST   /_bridge/app/share               { "text": "…", "uri": "https://…", "title": "…" }
///                                         { "files": [{ "root": "data", "path": "photos/cat.jpg" }], "title": "…" }
/// POST   /_bridge/app/haptics             { "type": "click" | "longPress" }
/// POST   /_bridge/app/vibrate             { "durationMs": 500 }     at most 5000
/// DELETE /_bridge/app/vibrate
/// GET    /_bridge/app/connectivity
/// GET    /_bridge/app/battery
/// GET    /_bridge/app/screen
/// PUT    /_bridge/app/screen/keep-awake
/// DELETE /_bridge/app/screen/keep-awake
/// GET    /_bridge/app/clipboard           { "text": null | "…" }
/// PUT    /_bridge/app/clipboard           { "text": "…" }
/// DELETE /_bridge/app/clipboard
///
/// events: app.connectivity, app.battery, app.energysaver
/// </code>
/// </summary>
public sealed partial class AppSupportBridge
{
    const int MaxShareFiles = 20;
    const int MaxVibrateMs = 5000;
    const int MaxClipboardChars = 1024 * 1024;

    void MapDevice(WebAppBridgeRoutes routes) => routes
        .MapPost("/share", this.ShareAsync)
        .MapPost("/haptics", HapticsAsync)
        .MapPost("/vibrate", VibrateAsync)
        .MapDelete("/vibrate", ctx => Essentials(ctx, "Vibration", async () =>
        {
            await ReadOnMainThread(() =>
            {
                Vibration.Default.Cancel();
                return true;
            });
            await WebAppBridgeResults.NoContent(ctx);
        }))
        .MapGet("/connectivity", ctx => Essentials(ctx, "Connectivity", async () =>
        {
            var response = await ReadOnMainThread(() => ToContract(Connectivity.Current.NetworkAccess, Connectivity.Current.ConnectionProfiles));
            await WebAppBridgeResults.Json(ctx, response, Contracts.AppJsonContext.Default.ConnectivityInfo);
        }))
        .MapGet("/battery", ctx => Essentials(ctx, "Battery", async () =>
        {
            var response = await ReadOnMainThread(() => new Contracts.BatteryInfo(
                this.battery.ChargeLevel,
                Convert<BatteryState, Contracts.BatteryState>(this.battery.State),
                Convert<BatteryPowerSource, Contracts.BatteryPowerSource>(this.battery.PowerSource),
                Convert<EnergySaverStatus, Contracts.EnergySaverStatus>(this.battery.EnergySaverStatus)
            ));
            await WebAppBridgeResults.Json(ctx, response, Contracts.AppJsonContext.Default.BatteryInfo);
        }))
        .MapGet("/screen", ctx => Essentials(ctx, "Display info", async () =>
        {
            var response = await ReadOnMainThread(() => ToContract(DeviceDisplay.Current.MainDisplayInfo, DeviceDisplay.Current.KeepScreenOn));
            await WebAppBridgeResults.Json(ctx, response, Contracts.AppJsonContext.Default.ScreenInfo);
        }))
        .MapPut("/screen/keep-awake", ctx => SetKeepAwakeAsync(ctx, true))
        .MapDelete("/screen/keep-awake", ctx => SetKeepAwakeAsync(ctx, false))
        .MapGet("/clipboard", ctx => Essentials(ctx, "Clipboard", async () =>
        {
            var text = await OnMainThread(async () => Clipboard.Default.HasText ? await Clipboard.Default.GetTextAsync() : null);
            await WebAppBridgeResults.Json(ctx, new Contracts.ClipboardText(text), Contracts.AppJsonContext.Default.ClipboardText);
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
        }))
        .MapEvent("app.connectivity", ConnectivityChanges, Contracts.AppJsonContext.Default.ConnectivityInfo)
        .MapEvent("app.battery", BatteryChanges, Contracts.AppJsonContext.Default.BatteryChanged)
        .MapEvent("app.energysaver", EnergySaverChanges, Contracts.AppJsonContext.Default.EnergySaverChanged);

    async ValueTask ShareAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.ShareRequest);
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
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.HapticsRequest);
        if (body is null || !Enum.IsDefined(body.Type))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"type\": \"click\" | \"longPress\" }.");
            return;
        }

        await Essentials(context, "Haptic feedback", async () =>
        {
            var performed = await ReadOnMainThread(() =>
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
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.VibrateRequest) ?? new Contracts.VibrateRequest();
        if (body.DurationMs is <= 0)
        {
            await WebAppBridgeResults.BadRequest(context, "durationMs must be positive.");
            return;
        }

        var duration = TimeSpan.FromMilliseconds(Math.Min(body.DurationMs ?? 500, MaxVibrateMs));

        await Essentials(context, "Vibration", async () =>
        {
            var vibrated = await ReadOnMainThread(() =>
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
        await ReadOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = keepOn);
        await WebAppBridgeResults.NoContent(context);
    });

    static async ValueTask SetClipboardAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.ClipboardText);
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

    static Task<T> ReadOnMainThread<T>(Func<T> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : Task.FromResult(action());

    // Change events hold platform listeners (a broadcast receiver, battery monitoring), so each is hooked only while a
    // stream listens to it. A backend without the feature, or without its Android permission, fails that stream alone.

    static IAsyncEnumerable<Contracts.ConnectivityInfo> ConnectivityChanges(CancellationToken cancellationToken)
        => FromMainThreadEvent<Contracts.ConnectivityInfo>(emit =>
        {
            EventHandler<ConnectivityChangedEventArgs> handler = (_, e) => emit(ToContract(e.NetworkAccess, e.ConnectionProfiles));
            Connectivity.Current.ConnectivityChanged += handler;
            return () => Connectivity.Current.ConnectivityChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<Contracts.BatteryChanged> BatteryChanges(CancellationToken cancellationToken)
        => FromMainThreadEvent<Contracts.BatteryChanged>(emit =>
        {
            EventHandler<BatteryInfoChangedEventArgs> handler = (_, e) => emit(new(
                e.ChargeLevel,
                Convert<BatteryState, Contracts.BatteryState>(e.State),
                Convert<BatteryPowerSource, Contracts.BatteryPowerSource>(e.PowerSource)
            ));
            this.battery.BatteryInfoChanged += handler;
            return () => this.battery.BatteryInfoChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<Contracts.EnergySaverChanged> EnergySaverChanges(CancellationToken cancellationToken)
        => FromMainThreadEvent<Contracts.EnergySaverChanged>(emit =>
        {
            EventHandler<EnergySaverStatusChangedEventArgs> handler = (_, e) => emit(new(Convert<EnergySaverStatus, Contracts.EnergySaverStatus>(e.EnergySaverStatus)));
            this.battery.EnergySaverStatusChanged += handler;
            return () => this.battery.EnergySaverStatusChanged -= handler;
        }, cancellationToken);

    /// <summary>An Essentials event, hooked and unhooked on the main thread: UIDevice battery monitoring is main-thread only.</summary>
    static IAsyncEnumerable<T> FromMainThreadEvent<T>(Func<Action<T>, Action> hook, CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<T>(async emit =>
        {
            var unhook = await ReadOnMainThread(() => hook(emit)).ConfigureAwait(false);
            return () => ReadOnMainThread(() =>
            {
                unhook();
                return true;
            });
        }, cancellationToken);

    static Contracts.ConnectivityInfo ToContract(NetworkAccess access, IEnumerable<ConnectionProfile> profiles)
        => new(Convert<NetworkAccess, Contracts.NetworkAccess>(access), [.. profiles.Select(x => Convert<ConnectionProfile, Contracts.ConnectionProfile>(x))]);

    static Contracts.ScreenInfo ToContract(DisplayInfo info, bool keepScreenOn) => new(
        info.Width,
        info.Height,
        info.Density,
        Convert<DisplayOrientation, Contracts.DisplayOrientation>(info.Orientation),
        Convert<DisplayRotation, Contracts.DisplayRotation>(info.Rotation),
        info.RefreshRate,
        keepScreenOn
    );
}
