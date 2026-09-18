using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.RpiCamera.Client;
using Shiny.AppDeviceBridge.RpiCamera.Imaging;
using Shiny.AppDeviceBridge.RpiCamera.Interop;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.RpiCamera;

public static class RpiCameraBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/rpicamera</c> to the server and registers <see cref="ICameraService"/> over libcamera.
    /// <code>
    /// services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge
    ///     .Configure(o => o.AppId = "greenhouse")
    ///     .AddRpiCameraBridge(o =>
    ///     {
    ///         o.StreamWidth = 1280;
    ///         o.StreamHeight = 720;
    ///         o.Camera.NativeLibraryPath = "/opt/greenhouse/native";
    ///     })
    /// ));
    /// </code>
    /// <para>
    /// Needs no MAUI, because a camera appliance is usually a headless Pi: on Shiny.Net.HttpServer's own builder there, and on
    /// the one <c>UseAppDeviceBridge</c> hands out in an app. Safe to call anywhere: off Linux, or with the native shim missing or built against another libcamera,
    /// the camera reports itself unsupported with the reason and every other route answers 501. An
    /// <see cref="ICameraService"/> registered first is used instead.
    /// </para>
    /// </summary>
    public static TBuilder AddRpiCameraBridge<TBuilder>(this TBuilder bridge, Action<RpiCameraBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var options = new RpiCameraBridgeOptions();
        configure?.Invoke(options);
        options.Validate();

        bridge.Services.TryAddSingleton(options);
        bridge.Services.TryAddSingleton<ICameraService>(sp => CreateCameraService(options, sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        bridge.AddBridge<RpiCameraBridge>();
        return bridge;
    }

    static ICameraService CreateCameraService(RpiCameraBridgeOptions options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<LibCameraService>();

        if (!OperatingSystem.IsLinux())
            return new UnavailableCameraService("No camera backend: libcamera exists only on Linux.");

        // Applied before the first probe so the resolver sees it; the path cannot change once the library has loaded.
        if (options.Camera.NativeLibraryPath is { Length: > 0 } path)
            NativeBackend.ConfigureSearchPath(path);

        var status = NativeBackend.GetStatus();
        if (!status.IsAvailable)
        {
            // A warning, not an exception: a camera fault must not stop the device from starting, because the bridge
            // reporting the fault is how anyone finds out about it.
            logger.LogWarning("{Reason}", status.Description);
            return new UnavailableCameraService(status.Description);
        }

        logger.LogInformation("Camera backend: {Backend}", status.Description);
        return new LibCameraService(options.Camera, loggerFactory);
    }
}

/// <summary>
/// <c>/_bridge/rpicamera</c> over <see cref="ICameraService"/>.
/// <code>
/// GET    /_bridge/rpicamera                    { "supported": true, "backend": "…", "cameras": [ … ], "streams": [ … ] }
/// GET    /_bridge/rpicamera/snapshot           image/jpeg          ?camera=&amp;width=&amp;height=&amp;quality=
/// POST   /_bridge/rpicamera/capture            { "root": "data", "path": "photos/now.jpg" }  → the file
/// GET    /_bridge/rpicamera/stream             multipart/x-mixed-replace MJPEG, for an &lt;img&gt;   ?camera=&amp;width=&amp;height=&amp;quality=&amp;fps=
/// GET    /_bridge/rpicamera/controls           ?camera=
/// PUT    /_bridge/rpicamera/controls           { "values": [ { "control": "Brightness", "value": 0.2 } ] }
/// DELETE /_bridge/rpicamera/streams
/// </code>
/// <para>
/// A camera is exclusive, so every viewer of one camera shares one session and one stream: the first viewer decides the
/// size and quality, frames are encoded once, and each viewer gets the newest frame when it is ready for one rather than a
/// queue of old ones. A snapshot while a stream runs comes from the stream. The session closes when the last viewer goes.
/// </para>
/// </summary>
public sealed class RpiCameraBridge : IWebAppBridge, IAsyncDisposable
{
    const string Boundary = "appdevicebridge-frame";

    readonly ICameraService? cameras;
    readonly RpiCameraBridgeOptions options;
    readonly WebAppFileRoots? fileRoots;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, CameraFeed> feeds = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<CameraControl, double>> controls = new(StringComparer.Ordinal);
    readonly CancellationTokenSource lifetime = new();

    public RpiCameraBridge(IServiceProvider services)
    {
        this.cameras = services.GetOptionalService<ICameraService>();
        this.options = services.GetOptionalService<RpiCameraBridgeOptions>() ?? new RpiCameraBridgeOptions();
        this.fileRoots = services.GetOptionalService<WebAppFileRoots>();
        this.logger = services.GetOptionalService<ILogger<RpiCameraBridge>>() ?? (ILogger)NullLogger.Instance;
    }

    public string Name => "rpicamera";

    public bool IsSupported => this.cameras is { IsSupported: true };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapGet("/snapshot", this.SnapshotAsync)
        .MapPost("/capture", this.CaptureAsync)
        .MapGet("/stream", this.StreamAsync)
        .MapGet("/controls", this.GetControlsAsync)
        .MapPut("/controls", this.SetControlsAsync)
        .MapDelete("/streams", this.StopStreamsAsync);

    async ValueTask StatusAsync(HttpContext context)
    {
        if (this.cameras is not { IsSupported: true } service)
        {
            await WebAppBridgeResults.Json(
                context,
                new RpiCameraStatus(false, this.cameras?.BackendDescription ?? "No camera service is registered.", [], []),
                RpiCameraJsonContext.Default.RpiCameraStatus
            );
            return;
        }

        var attached = await service.GetCameras(context.RequestAborted);

        RpiCameraStream[] streams;
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            streams = [.. this.feeds.Values.Select(x => x.Describe())];
        }
        finally
        {
            this.gate.Release();
        }

        await WebAppBridgeResults.Json(
            context,
            new RpiCameraStatus(true, service.BackendDescription, [.. attached.Select(ToContract)], streams),
            RpiCameraJsonContext.Default.RpiCameraStatus
        );
    }

    async ValueTask SnapshotAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var query = context.Request.Query;
        if (!TryReadSize(query, out var width, out var height, out var quality))
        {
            await WebAppBridgeResults.BadRequest(context, "width and height must be 0 to 8192, and quality 0 to 100.");
            return;
        }

        var jpeg = await this.TakePhotographAsync(context, query["camera"].ToString(), width, height, quality);
        if (jpeg is null)
            return;

        context.Response.Headers["Cache-Control"] = "no-store";
        await Results.Bytes(jpeg, "image/jpeg").ExecuteAsync(context);
    }

    async ValueTask CaptureAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, RpiCameraJsonContext.Default.RpiCameraCapture);
        if (body is null || String.IsNullOrWhiteSpace(body.Root) || String.IsNullOrWhiteSpace(body.Path))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"root\": \"data\", \"path\": \"photos/now.jpg\" }.");
            return;
        }

        if (!IsValidSize(body.Width, body.Height, body.Quality))
        {
            await WebAppBridgeResults.BadRequest(context, "width and height must be 0 to 8192, and quality 0 to 100.");
            return;
        }

        if (this.fileRoots?.TryGet(body.Root, out var root) != true || root is null)
        {
            await WebAppBridgeResults.NotFound(context, $"There is no file root named '{body.Root}'.");
            return;
        }

        if (WebAppFilePath.Normalize(body.Path) is not { Length: > 0 } path)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "invalid_path", $"'{body.Path}' is not a valid path.");
            return;
        }

        var jpeg = await this.TakePhotographAsync(context, body.CameraId, body.Width, body.Height, body.Quality);
        if (jpeg is null)
            return;

        try
        {
            using var content = new MemoryStream(jpeg, writable: false);
            var written = await root.WriteAsync(path, content, FileWriteMode.Replace, Math.Max(jpeg.Length, 1), context.RequestAborted);
            await WebAppBridgeResults.Json(context, written.Entry, RpiCameraJsonContext.Default.FileEntry, written.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }
        catch (WebAppFileException ex)
        {
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
    }

    async ValueTask StreamAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var query = context.Request.Query;
        if (!TryReadSize(query, out var width, out var height, out var quality)
            || !TryReadInt(query["fps"].ToString(), 0, 120, out var fps))
        {
            await WebAppBridgeResults.BadRequest(context, "width and height must be 0 to 8192, quality 0 to 100, and fps 0 to 120.");
            return;
        }

        var viewer = await this.JoinFeedAsync(context, query["camera"].ToString(), width, height, quality, fps);
        if (viewer is null)
            return;

        var (feed, frames) = viewer.Value;

        using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, feed.Stopped, this.lifetime.Token);
        ended.CancelAfter(this.options.MaxStreamDuration);

        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = $"multipart/x-mixed-replace; boundary={Boundary}";
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.StartAsync(ended.Token);

            await foreach (var jpeg in frames.ReadAllAsync(ended.Token))
            {
                var header = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                await context.Response.Body.WriteAsync(header, ended.Token);
                await context.Response.Body.WriteAsync(jpeg, ended.Token);
                await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), ended.Token);
                await context.Response.Body.FlushAsync(ended.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The viewer left, the stream reached its limit, or the streams were stopped.
        }
        finally
        {
            await this.LeaveFeedAsync(feed, frames);
        }
    }

    async ValueTask GetControlsAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        if (await this.ResolveCameraAsync(context, context.Request.Query["camera"].ToString()) is not { } cameraId)
            return;

        var described = await this.WithSessionAsync(context, cameraId, session => this.DescribeControls(cameraId, session));
        if (described is not null)
            await WebAppBridgeResults.Json(context, described, RpiCameraJsonContext.Default.IReadOnlyListRpiCameraControlInfo);
    }

    async ValueTask SetControlsAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, RpiCameraJsonContext.Default.RpiCameraControlsInput);
        if (body?.Values is not { Count: > 0 } values)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"values\": [ { \"control\": \"Brightness\", \"value\": 0.2 } ] }.");
            return;
        }

        if (await this.ResolveCameraAsync(context, context.Request.Query["camera"].ToString()) is not { } cameraId)
            return;

        string? refused = null;
        var described = await this.WithSessionAsync(context, cameraId, session =>
        {
            foreach (var entry in values)
            {
                var control = BridgeEnum.Convert<RpiCameraControl, CameraControl>(entry.Control);
                if (!session.GetControlInfo(control).IsSupported)
                {
                    refused = entry.Control.ToString();
                    return null;
                }
            }

            var stored = this.ControlsFor(cameraId);
            foreach (var entry in values)
            {
                var control = BridgeEnum.Convert<RpiCameraControl, CameraControl>(entry.Control);

                if (entry.Value is { } value)
                {
                    stored[control] = value;
                    session.TrySetControl(control, value);
                }
                else
                {
                    stored.Remove(control);
                }
            }

            return this.DescribeControls(cameraId, session);
        });

        if (refused is not null)
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "unsupported_control", $"The camera has no {refused} control.");
        else if (described is not null)
            await WebAppBridgeResults.Json(context, described, RpiCameraJsonContext.Default.IReadOnlyListRpiCameraControlInfo);
    }

    async ValueTask StopStreamsAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        CameraFeed[] running;
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            running = [.. this.feeds.Values];
            this.feeds.Clear();
        }
        finally
        {
            this.gate.Release();
        }

        foreach (var feed in running)
            await feed.DisposeAsync();

        await WebAppBridgeResults.NoContent(context);
    }

    /// <summary>A JPEG, from the running stream or a session of its own. Null when a response has already been written.</summary>
    async Task<byte[]?> TakePhotographAsync(HttpContext context, string? camera, int width, int height, int quality)
    {
        if (await this.ResolveCameraAsync(context, camera) is not { } cameraId)
            return null;

        CameraFeed? feed;
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            if (!this.feeds.TryGetValue(cameraId, out feed))
            {
                try
                {
                    await using var session = await this.cameras!.Open(
                        new CameraSessionOptions
                        {
                            Width = width > 0 ? width : this.options.StillWidth,
                            Height = height > 0 ? height : this.options.StillHeight,
                            Role = CameraStreamRole.StillCapture
                        },
                        cameraId,
                        context.RequestAborted
                    );

                    this.ApplyControls(cameraId, session);
                    using var frame = await session.CaptureFrame(context.RequestAborted);
                    return this.Encode(frame, quality > 0 ? quality : this.options.StillQuality);
                }
                catch (CameraUnavailableException ex)
                {
                    await Unavailable(context, ex);
                    return null;
                }
            }
        }
        finally
        {
            this.gate.Release();
        }

        // The stream holds the camera, so the photograph comes from it, at its size.
        return await feed.NextFrameAsync(context.RequestAborted);
    }

    async Task<(CameraFeed Feed, ChannelReader<byte[]> Frames)?> JoinFeedAsync(HttpContext context, string? camera, int width, int height, int quality, int fps)
    {
        if (await this.ResolveCameraAsync(context, camera) is not { } cameraId)
            return null;

        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            if (!this.feeds.TryGetValue(cameraId, out var feed))
            {
                ICameraSession session;
                try
                {
                    session = await this.cameras!.Open(
                        new CameraSessionOptions
                        {
                            Width = width > 0 ? width : this.options.StreamWidth,
                            Height = height > 0 ? height : this.options.StreamHeight,
                            Role = CameraStreamRole.Viewfinder
                        },
                        cameraId,
                        context.RequestAborted
                    );
                }
                catch (CameraUnavailableException ex)
                {
                    await Unavailable(context, ex);
                    return null;
                }

                this.ApplyControls(cameraId, session);

                feed = new CameraFeed(
                    cameraId,
                    session,
                    quality > 0 ? quality : this.options.StreamQuality,
                    Math.Clamp(fps > 0 ? fps : this.options.MaxFps, 1, this.options.MaxFps),
                    this.Encode,
                    this.logger
                );
                this.feeds[cameraId] = feed;
            }

            return (feed, feed.Join());
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task LeaveFeedAsync(CameraFeed feed, ChannelReader<byte[]> frames)
    {
        bool last;

        await this.gate.WaitAsync();
        try
        {
            last = feed.Leave(frames) == 0 && this.feeds.TryGetValue(feed.CameraId, out var current) && ReferenceEquals(current, feed);
            if (last)
                this.feeds.Remove(feed.CameraId);
        }
        finally
        {
            this.gate.Release();
        }

        // The sensor is released as soon as nobody is watching: a warm, exclusive camera is not something to keep in reserve.
        if (last)
            await feed.DisposeAsync();
    }

    /// <summary>
    /// Runs against the stream's session when one is running, or a session opened for the call. Null when a response has
    /// already been written, or when <paramref name="action"/> returned null.
    /// </summary>
    async Task<T?> WithSessionAsync<T>(HttpContext context, string cameraId, Func<ICameraSession, T?> action) where T : class
    {
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            if (this.feeds.TryGetValue(cameraId, out var feed))
                return action(feed.Session);

            try
            {
                await using var session = await this.cameras!.Open(new CameraSessionOptions(), cameraId, context.RequestAborted);
                return action(session);
            }
            catch (CameraUnavailableException ex)
            {
                await Unavailable(context, ex);
                return null;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    IReadOnlyList<RpiCameraControlInfo> DescribeControls(string cameraId, ICameraSession session)
    {
        var stored = this.ControlsFor(cameraId);

        return
        [
            .. Enum.GetValues<RpiCameraControl>().Select(x =>
            {
                var control = BridgeEnum.Convert<RpiCameraControl, CameraControl>(x);
                var info = session.GetControlInfo(control);
                return new RpiCameraControlInfo(x, info.IsSupported, info.Minimum, info.Maximum, info.Default, stored.TryGetValue(control, out var value) ? value : null);
            })
        ];
    }

    Dictionary<CameraControl, double> ControlsFor(string cameraId)
    {
        if (!this.controls.TryGetValue(cameraId, out var stored))
            this.controls[cameraId] = stored = [];

        return stored;
    }

    void ApplyControls(string cameraId, ICameraSession session)
    {
        foreach (var (control, value) in this.ControlsFor(cameraId))
        {
            if (!session.TrySetControl(control, value))
                this.logger.LogDebug("Camera {Camera} ignored {Control} = {Value}", cameraId, control, value);
        }
    }

    byte[] Encode(CameraFrame frame, int quality)
        => frame.PixelFormat.IsCompressed ? frame.ToArray() : JpegEncoder.Encode(frame, quality, this.options.LimitedRangeInput);

    /// <summary>The camera's id: the one asked for, or the first attached. Null when a response has already been written.</summary>
    async Task<string?> ResolveCameraAsync(HttpContext context, string? requested)
    {
        var attached = await this.cameras!.GetCameras(context.RequestAborted);

        if (String.IsNullOrEmpty(requested))
        {
            if (attached.Count > 0)
                return attached[0].Id;

            await WebAppBridgeResults.Error(context, StatusCodes.Status404NotFound, "no_camera", "No camera is attached. Check the ribbon cable is seated.");
            return null;
        }

        if (attached.Any(x => x.Id == requested))
            return requested;

        await WebAppBridgeResults.NotFound(context, $"There is no camera '{requested}'.");
        return null;
    }

    async ValueTask<bool> EnsureSupportedAsync(HttpContext context)
    {
        if (this.IsSupported)
            return true;

        await WebAppBridgeResults.Error(
            context,
            StatusCodes.Status501NotImplemented,
            "not_supported",
            this.cameras?.BackendDescription ?? "The camera is not available on this platform."
        );
        return false;
    }

    static ValueTask Unavailable(HttpContext context, CameraUnavailableException ex)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "camera_unavailable", ex.Message);

    static bool TryReadSize(QueryCollection query, out int width, out int height, out int quality)
    {
        width = height = quality = 0;
        return TryReadInt(query["width"].ToString(), 0, 8192, out width)
               && TryReadInt(query["height"].ToString(), 0, 8192, out height)
               && TryReadInt(query["quality"].ToString(), 0, 100, out quality);
    }

    static bool IsValidSize(int width, int height, int quality)
        => width is >= 0 and <= 8192 && height is >= 0 and <= 8192 && quality is >= 0 and <= 100;

    static bool TryReadInt(string text, int minimum, int maximum, out int value)
    {
        value = 0;
        if (text.Length == 0)
            return true;

        return Int32.TryParse(text, out value) && value >= minimum && value <= maximum;
    }

    internal static RpiCameraInfo ToContract(CameraDescriptor camera) => new(
        camera.Id,
        camera.Model,
        camera.MaxWidth,
        camera.MaxHeight,
        camera.RotationDegrees,
        BridgeEnum.Convert<CameraLocation, RpiCameraLocation>(camera.Location)
    );

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();

        CameraFeed[] running;
        await this.gate.WaitAsync();
        try
        {
            running = [.. this.feeds.Values];
            this.feeds.Clear();
        }
        finally
        {
            this.gate.Release();
        }

        foreach (var feed in running)
            await feed.DisposeAsync();

        this.lifetime.Dispose();
    }
}

/// <summary>
/// One camera session shared by every viewer: a pump reads frames, drops what exceeds the frame rate before paying to
/// encode it, encodes once, and offers each viewer the newest frame.
/// </summary>
sealed class CameraFeed : IAsyncDisposable
{
    readonly Func<CameraFrame, int, byte[]> encode;
    readonly ILogger logger;
    readonly CancellationTokenSource stop = new();
    readonly Lock sync = new();
    readonly List<Channel<byte[]>> viewers = [];
    readonly Task pump;
    TaskCompletionSource<byte[]> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long delivered;
    int disposed;

    public CameraFeed(string cameraId, ICameraSession session, int quality, int maxFps, Func<CameraFrame, int, byte[]> encode, ILogger logger)
    {
        this.CameraId = cameraId;
        this.Session = session;
        this.Quality = quality;
        this.MaxFps = maxFps;
        this.encode = encode;
        this.logger = logger;
        this.pump = Task.Run(this.PumpAsync);
    }

    public string CameraId { get; }

    public ICameraSession Session { get; }

    public int Quality { get; }

    public int MaxFps { get; }

    /// <summary>Cancelled when the feed ends, so every viewer's response ends with it.</summary>
    public CancellationToken Stopped => this.stop.Token;

    public ChannelReader<byte[]> Join()
    {
        // Capacity one, newest wins: a slow viewer skips frames instead of watching the past.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        lock (this.sync)
            this.viewers.Add(channel);

        return channel.Reader;
    }

    /// <summary>Removes a viewer, answering how many remain.</summary>
    public int Leave(ChannelReader<byte[]> frames)
    {
        lock (this.sync)
        {
            this.viewers.RemoveAll(x => ReferenceEquals(x.Reader, frames));
            return this.viewers.Count;
        }
    }

    public async Task<byte[]> NextFrameAsync(CancellationToken cancellationToken)
    {
        Task<byte[]> waiting;
        lock (this.sync)
            waiting = this.next.Task;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.stop.Token);
        return await waiting.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public RpiCameraStream Describe()
    {
        var stream = this.Session.Stream;
        var statistics = this.Session.GetStatistics();

        int count;
        lock (this.sync)
            count = this.viewers.Count;

        return new RpiCameraStream(
            this.CameraId,
            stream.Width,
            stream.Height,
            stream.PixelFormat.ToString(),
            this.Quality,
            this.MaxFps,
            count,
            Interlocked.Read(ref this.delivered),
            statistics.FramesDropped
        );
    }

    async Task PumpAsync()
    {
        var interval = Stopwatch.Frequency / this.MaxFps;
        var last = 0L;

        try
        {
            await foreach (var frame in this.Session.ReadFrames(this.stop.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (last != 0 && now - last < interval)
                        continue;

                    last = now;
                    var jpeg = this.encode(frame, this.Quality);
                    Interlocked.Increment(ref this.delivered);

                    TaskCompletionSource<byte[]> waiting;
                    lock (this.sync)
                    {
                        foreach (var viewer in this.viewers)
                            viewer.Writer.TryWrite(jpeg);

                        waiting = this.next;
                        this.next = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    waiting.TrySetResult(jpeg);
                }
            }
        }
        catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The stream from camera {Camera} failed", this.CameraId);
        }
        finally
        {
            // However the pump ended, the viewers' responses end with it.
            await this.stop.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        await this.stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await this.pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "The camera pump ended with an error");
        }

        await this.Session.DisposeAsync().ConfigureAwait(false);
        this.stop.Dispose();
    }
}
