using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Camera;

public static class CameraBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/camera</c>: the device's own camera, driven from a page anywhere — a viewfinder, the shutter, video,
    /// lens, zoom, torch and effects.
    /// <code>
    /// builder.AddCameraBridge(o => o.Folder = "photographer");
    /// </code>
    /// <para>
    /// The camera runs on a camera screen: the bridge shows its own when a page asks the device to open one, or put a
    /// <see cref="CameraBridgeView"/> on a page of yours and handle <see cref="CameraBridgeSession.OpenRequested"/>. Captures
    /// are filed into a file root (<see cref="ICameraCaptureStore"/> to file them elsewhere).
    /// </para>
    /// <para>
    /// Declare <c>NSCameraUsageDescription</c> and <c>NSMicrophoneUsageDescription</c> on Apple platforms (and the camera and
    /// audio-input entitlements where sandboxed), <c>CAMERA</c> and <c>RECORD_AUDIO</c> on Android, and the <c>webcam</c>
    /// and <c>microphone</c> capabilities on Windows. Linux has no camera: 501.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddCameraBridge(this MauiAppBuilder builder, Action<CameraBridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(CameraBridgeOptions))?.ImplementationInstance as CameraBridgeOptions;
        if (options is null)
        {
            options = new CameraBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        if (services.Any(x => x.ServiceType == typeof(CameraBridgeSession)))
            return builder;

        builder.UseShinyCamera();

        services.TryAddSingleton<ICameraCaptureStore>(sp => new FileRootCameraCaptureStore(
            sp.GetRequiredService<WebAppFileRoots>(),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System
        ));
        services.TryAddSingleton<ICameraBridgePresenter, MauiCameraBridgePresenter>();
        services.TryAddSingleton(sp => new CameraBridgeSession(
            options,
            sp.GetRequiredService<ICameraBridgePresenter>()
        ));
        services.AddWebAppBridge<CameraBridge>();

        return builder;
    }
}

/// <summary>
/// <c>/_bridge/camera</c>, over <see cref="CameraBridgeSession"/>.
/// <code>
/// GET    /_bridge/camera               the status
/// POST   /_bridge/camera/access        prompt for the camera permission
/// POST   /_bridge/camera/open          202: the device was asked to show its camera
/// POST   /_bridge/camera/close         204
/// POST   /_bridge/camera/photo         → the capture
/// POST   /_bridge/camera/recording     204
/// DELETE /_bridge/camera/recording     → the capture
/// PUT    /_bridge/camera/settings      { "videoMode": true, "facing": "Front", "zoom": 2, … } → the status
/// GET    /_bridge/camera/preview       multipart/x-mixed-replace MJPEG, for an &lt;img&gt;
/// events: camera.status
/// </code>
/// </summary>
public sealed class CameraBridge(CameraBridgeSession session) : IWebAppBridge
{
    const string Boundary = "appdevicebridge-camera";

    public string Name => "camera";

    public bool IsSupported => CameraPermission.IsSupported;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("camera.status", ct => session.Statuses.ListenAsync(ct), CameraJsonContext.Default.CameraStatus)
        .MapGet("", this.StatusAsync)
        .MapPost("/access", ctx => Guarded(ctx, () => this.RequestAccessAsync(ctx)))
        .MapPost("/open", ctx => Guarded(ctx, () => this.OpenAsync(ctx)))
        .MapPost("/close", ctx => Guarded(ctx, () => this.CloseAsync(ctx)))
        .MapPost("/photo", ctx => Guarded(ctx, () => this.CaptureAsync(ctx, (c, ct) => c.TakePhotoAsync(ct))))
        .MapPost("/recording", ctx => Guarded(ctx, () => this.StartRecordingAsync(ctx)))
        .MapDelete("/recording", ctx => Guarded(ctx, () => this.CaptureAsync(ctx, (c, ct) => c.StopRecordingAsync(ct))))
        .MapPut("/settings", ctx => Guarded(ctx, () => this.ApplyAsync(ctx)))
        .MapGet("/preview", this.PreviewAsync);

    async ValueTask StatusAsync(HttpContext context)
        => await WebAppBridgeResults.Json(context, await session.GetStatusAsync(), CameraJsonContext.Default.CameraStatus);

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "A camera");
            return;
        }

        // The prompt is UI.
        var access = Application.Current?.Dispatcher is { } dispatcher
            ? await dispatcher.DispatchAsync(CameraPermission.RequestAccessAsync)
            : await CameraPermission.RequestAccessAsync();

        await WebAppBridgeResults.Json(context, new CameraAccessResult(access), CameraJsonContext.Default.CameraAccessResult);
        session.NotifyChanged();
    }

    async ValueTask OpenAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "A camera");
            return;
        }

        // Accepted rather than done: whether a camera arrives shows up on camera.status a moment later.
        await session.RequestOpenAsync();
        context.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    async ValueTask CloseAsync(HttpContext context)
    {
        await session.RequestCloseAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CaptureAsync(HttpContext context, Func<ICameraBridgeController, CancellationToken, Task<CameraCapture>> capture)
    {
        var taken = await session.InvokeAsync(c => capture(c, context.RequestAborted));
        await WebAppBridgeResults.Json(context, taken, CameraJsonContext.Default.CameraCapture);
    }

    async ValueTask StartRecordingAsync(HttpContext context)
    {
        await session.InvokeAsync(async c =>
        {
            await c.StartRecordingAsync(context.RequestAborted);
            return true;
        });

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask ApplyAsync(HttpContext context)
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, CameraJsonContext.Default.CameraSettingsInput) is not { } settings)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected camera settings, such as { \"videoMode\": true }.");
            return;
        }

        if (settings.Zoom is { } zoom && (Double.IsNaN(zoom) || Double.IsInfinity(zoom)))
        {
            await WebAppBridgeResults.BadRequest(context, "zoom must be a number.");
            return;
        }

        await session.InvokeAsync(c =>
        {
            c.Apply(settings);
            return Task.FromResult(true);
        });

        await WebAppBridgeResults.Json(context, await session.GetStatusAsync(), CameraJsonContext.Default.CameraStatus);
    }

    /// <summary>
    /// The viewfinder, as <c>multipart/x-mixed-replace</c> — what an <c>&lt;img&gt;</c> renders natively. MJPEG on purpose: an
    /// encoded video stream is far more efficient per frame and far worse at what a viewfinder is for, since anything with
    /// a group of pictures buffers to decode, and half a second of delay makes framing a moving subject impossible.
    /// Independent JPEGs arrive as the camera sees them. Every part is flushed as it is written.
    /// </summary>
    async ValueTask PreviewAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Content-Type"] = $"multipart/x-mixed-replace; boundary={Boundary}";

        // A cached viewfinder is a photograph.
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        await context.Response.StartAsync(context.RequestAborted);

        // Watching is what turns frame delivery on, and leaving turns it off.
        using var viewer = session.Preview.Watch();
        var body = context.Response.Body;

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var jpeg = await viewer.NextAsync(context.RequestAborted);
                var header = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

                await body.WriteAsync(header, context.RequestAborted);
                await body.WriteAsync(jpeg, context.RequestAborted);
                await body.WriteAsync("\r\n"u8.ToArray(), context.RequestAborted);
                await body.FlushAsync(context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The viewer left.
        }
    }

    /// <summary>Turns the camera's refusals into the status and code a page switches on.</summary>
    static async ValueTask Guarded(HttpContext context, Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (CameraBridgeException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (WebAppFileException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is FeatureNotSupportedException or PlatformNotSupportedException)
        {
            await WebAppBridgeResults.NotSupported(context, "A camera");
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is PermissionException or UnauthorizedAccessException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "access_denied", ex.Message);
        }
    }
}
