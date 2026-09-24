using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.ScreenRecorder.Client;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.ScreenRecorder;

namespace Shiny.AppDeviceBridge.ScreenRecorder;

public sealed class ScreenRecorderBridgeOptions
{
    /// <summary>The file root recordings are filed into. <c>data</c> by default.</summary>
    public string Root { get; set; } = "data";

    /// <summary>The folder inside <see cref="Root"/>, created as needed. <c>screen-recordings</c> by default; empty files at the root.</summary>
    public string Folder { get; set; } = "screen-recordings";

    /// <summary>
    /// The longest a recording may run. A page asking for longer, or for no limit, gets this; the recording is stopped and
    /// filed when it runs out. One hour by default, so a page that went away cannot leave the screen recording until the
    /// disk fills. Null lets a recording run until it is stopped.
    /// </summary>
    public TimeSpan? MaxDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Asked on the main thread before every recording, with what the page asked for — return false to refuse it with 403
    /// (<c>declined</c>). Null by default. Worth setting on Windows, the one platform that records the whole screen without
    /// asking the user: show a confirmation of your own.
    /// </summary>
    public Func<Native.ScreenRecordingRequest, CancellationToken, Task<bool>>? ConfirmStart { get; set; }

    /// <summary>
    /// Registers Shiny.ScreenRecorder's recorder for the platform. Turn off to register your own
    /// <see cref="Native.IScreenRecorder"/> instead.
    /// </summary>
    public bool RegisterScreenRecorder { get; set; } = true;

    internal void Validate()
    {
        if (!WebAppFileStore.IsValidName(this.Root))
            throw new InvalidOperationException($"ScreenRecorderBridgeOptions.Root '{this.Root}' is not a file root name.");

        if (WebAppFilePath.Normalize(this.Folder) is null)
            throw new InvalidOperationException($"ScreenRecorderBridgeOptions.Folder '{this.Folder}' is not a relative path.");

        if (this.MaxDuration is { } max && max <= TimeSpan.Zero)
            throw new InvalidOperationException("ScreenRecorderBridgeOptions.MaxDuration is positive, or null for no limit.");
    }
}

public static class ScreenRecorderBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/screenrecorder</c> and registers Shiny.ScreenRecorder's recorder for the platform.
    /// <code>
    /// bridge.AddScreenRecorderBridge();
    /// bridge.AddScreenRecorderBridge(o => o.MaxDuration = TimeSpan.FromMinutes(10));
    /// </code>
    /// <para>
    /// This is device access of the widest kind: on Android, macOS, Windows and Linux the recording is the whole screen,
    /// other apps included. Every platform but Windows asks the user before a recording starts; on Windows, set
    /// <see cref="ScreenRecorderBridgeOptions.ConfirmStart"/> to ask them yourself.
    /// </para>
    /// <para>
    /// Platform setup: <c>FOREGROUND_SERVICE</c> and <c>FOREGROUND_SERVICE_MEDIA_PROJECTION</c> on Android, plus
    /// <c>RECORD_AUDIO</c> for the microphone, and Shiny's host started by the app. <c>NSMicrophoneUsageDescription</c> on
    /// Apple platforms for the microphone; macOS asks for the Screen Recording permission itself. Packaged Windows apps
    /// declare the <c>graphicsCapture</c> capability. Linux needs xdg-desktop-portal and GStreamer or ffmpeg.
    /// </para>
    /// </summary>
    public static TBuilder AddScreenRecorderBridge<TBuilder>(this TBuilder bridge, Action<ScreenRecorderBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(ScreenRecorderBridgeOptions))?.ImplementationInstance as ScreenRecorderBridgeOptions;
        var added = options is not null;
        if (options is null)
        {
            options = new ScreenRecorderBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        if (added)
            return bridge;

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        if (options.RegisterScreenRecorder)
            services.AddScreenRecorder();
#elif SCREENRECORDER_LINUX
        // Shiny.ScreenRecorder has no recorder of its own for plain .NET: the portal on Linux, and the bridge answers 501
        // anywhere else.
        if (options.RegisterScreenRecorder && OperatingSystem.IsLinux())
            services.AddScreenRecorder();
#endif

        bridge.AddBridge<ScreenRecorderBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/screenrecorder</c> over <see cref="Native.IScreenRecorder"/>.
/// <code>
/// GET    /_bridge/screenrecorder                     { "supported": true, "capabilities": ["Recording", …], "state": "Idle", … }
/// POST   /_bridge/screenrecorder/access              { "includeMicrophone": true }  →  { "access": "Available" }
/// POST   /_bridge/screenrecorder/recording           { "includeMicrophone": true, "maxWidth": 1280, "maxDurationSeconds": 60 }
/// DELETE /_bridge/screenrecorder/recording           stops and files it  →  { "file": { "root": "data", "path": "…" }, … }
/// POST   /_bridge/screenrecorder/recording/cancel    keeps nothing
/// POST   /_bridge/screenrecorder/recording/pause
/// POST   /_bridge/screenrecorder/recording/resume
///
/// events: screenrecorder.status, screenrecorder.ended
/// </code>
/// <para>
/// One recording at a time, and the recorder is the device's rather than the page's: the device can end a recording on
/// its own — the user stopped it from the system, the OS pre-empted it, the time limit ran out — and whatever could be
/// kept is filed and announced on <c>screenrecorder.ended</c> just as a stop would have been.
/// </para>
/// <para>
/// Picking a display or window is left out on purpose: listing them would hand the page every other app's window titles.
/// A recording is of the primary display, or of the app itself on iOS and Mac Catalyst.
/// </para>
/// </summary>
public sealed class ScreenRecorderBridge : IWebAppBridge, IDisposable
{
    static readonly ScreenRecorderCapability[] AllCapabilities = Enum.GetValues<ScreenRecorderCapability>();

    readonly Native.IScreenRecorder? recorder;
    readonly WebAppFileRoots roots;
    readonly ScreenRecorderBridgeOptions options;
    readonly IWebAppMainThread mainThread;
    readonly TimeProvider time;
    readonly WebAppEventSource<ScreenRecorderStatus> statuses = new();
    readonly WebAppEventSource<ScreenRecordingEnded> endings = new();
    readonly Lock gate = new();

    // Claimed from the moment a start is accepted until the recording it made has been filed or thrown away, so a second
    // start cannot race a recording that is still starting or still being filed.
    bool claimed;
    Native.IScreenRecording? active;
    ScreenRecording? last;

    public ScreenRecorderBridge(IServiceProvider services, WebAppFileRoots roots)
    {
        this.recorder = services.GetOptionalService<Native.IScreenRecorder>();
        this.roots = roots;
        this.options = services.GetOptionalService<ScreenRecorderBridgeOptions>() ?? new ScreenRecorderBridgeOptions();
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
        this.time = services.GetOptionalService<TimeProvider>() ?? TimeProvider.System;

        if (this.recorder is not null)
            this.recorder.StateChanged += this.OnStateChanged;
    }

    public string Name => "screenrecorder";

    public bool IsSupported => this.recorder?.Capabilities.HasFlag(Native.ScreenRecorderCapabilities.Recording) == true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("screenrecorder.status", this.statuses.ListenAsync, ScreenRecorderJsonContext.Default.ScreenRecorderStatus)
        .MapEvent("screenrecorder.ended", this.endings.ListenAsync, ScreenRecorderJsonContext.Default.ScreenRecordingEnded)
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapPost("/recording", this.StartAsync)
        .MapDelete("/recording", this.StopAsync)
        .MapPost("/recording/cancel", this.CancelAsync)
        .MapPost("/recording/pause", this.PauseAsync)
        .MapPost("/recording/resume", this.ResumeAsync);

    ValueTask StatusAsync(HttpContext context)
        => WebAppBridgeResults.Json(context, this.GetStatus(), ScreenRecorderJsonContext.Default.ScreenRecorderStatus);

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, ScreenRecorderJsonContext.Default.ScreenRecorderAccessRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"includeMicrophone\": true, \"includeSystemAudio\": false }, or no body.");
            return;
        }

        var request = new Native.ScreenRecordingRequest
        {
            IncludeMicrophone = body?.IncludeMicrophone ?? false,
            IncludeSystemAudio = body?.IncludeSystemAudio ?? false
        };

        var access = await this.mainThread.InvokeAsync(() => this.recorder!.RequestAccess(request, context.RequestAborted));
        await WebAppBridgeResults.Json(
            context,
            new ScreenRecorderAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)),
            ScreenRecorderJsonContext.Default.ScreenRecorderAccessResult
        );
    }

    async ValueTask StartAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, ScreenRecorderJsonContext.Default.ScreenRecordingRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"includeMicrophone\": false, \"maxWidth\": 1280, \"maxDurationSeconds\": 60, … }, or no body.");
            return;
        }

        body ??= new ScreenRecordingRequest();
        if (body.MaxDurationSeconds is { } seconds && seconds is not (> 0 and <= 7 * 24 * 60 * 60))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected maxDurationSeconds greater than zero.");
            return;
        }

        var request = this.ToNative(body);
        try
        {
            // Checked here, before anyone is asked anything, so a request the device cannot honour never reaches a prompt.
            request.AssertValid(this.recorder!.Capabilities, "this device does not offer it");
        }
        catch (Native.ScreenRecorderNotSupportedException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
            return;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
            return;
        }

        if (!this.TryClaim())
        {
            await Busy(context);
            return;
        }

        Native.IScreenRecording recording;
        try
        {
            if (this.options.ConfirmStart is { } confirm
                && !await this.mainThread.InvokeAsync(() => confirm(request, context.RequestAborted)))
            {
                this.Release();
                await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "declined", "The app declined to record the screen.");
                return;
            }

            // A page that goes away while the user is still looking at the consent dialog tears the start down with it.
            recording = await this.mainThread.InvokeAsync(() => this.recorder!.Start(request, context.RequestAborted));
        }
        catch (Exception ex)
        {
            this.Release();

            switch (ex)
            {
                case Native.ScreenRecorderPermissionException:
                    await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", ex.Message);
                    return;

                case Native.ScreenRecorderNotSupportedException:
                    await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
                    return;

                case Native.ScreenRecorderException when this.recorder!.State != Native.ScreenRecorderState.Idle:
                    // The app itself is recording, outside the bridge.
                    await Busy(context);
                    return;

                case Native.ScreenRecorderException:
                    await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "recording_failed", ex.Message);
                    return;
            }

            throw;
        }

        lock (this.gate)
            this.active = recording;

        recording.Faulted += this.OnFaulted;
        this.statuses.Publish(this.GetStatus());

        await WebAppBridgeResults.Json(context, this.GetStatus(), ScreenRecorderJsonContext.Default.ScreenRecorderStatus);
    }

    async ValueTask StopAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        if (this.Take() is not { } recording)
        {
            await NotRecording(context);
            return;
        }

        try
        {
            // Not the request's token: a page that stops waiting must not leave a file with no index behind.
            var result = await recording.Stop(CancellationToken.None);
            var filed = await this.FileAsync(result, CancellationToken.None);

            this.Finish(new ScreenRecordingEnded(ScreenRecordingEndReason.Stopped, filed));
            await WebAppBridgeResults.Json(context, filed, ScreenRecorderJsonContext.Default.ScreenRecording);
        }
        catch (WebAppFileException ex)
        {
            this.Finish(new ScreenRecordingEnded(ScreenRecordingEndReason.EncoderFailed, null, ex.Message));
            await WebAppBridgeResults.Error(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Native.ScreenRecorderException ex)
        {
            this.Finish(new ScreenRecordingEnded(ScreenRecordingEndReason.EncoderFailed, null, ex.Message));
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "recording_failed", ex.Message);
        }
        finally
        {
            await DisposeQuietlyAsync(recording);
        }
    }

    async ValueTask CancelAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        // Cancelling nothing is done already.
        if (this.Take() is { } recording)
        {
            try
            {
                await recording.Cancel(CancellationToken.None);
            }
            finally
            {
                await DisposeQuietlyAsync(recording);
                this.Finish(new ScreenRecordingEnded(ScreenRecordingEndReason.Cancelled));
            }
        }

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask PauseAsync(HttpContext context) => this.PauseOrResumeAsync(context, (x, ct) => x.Pause(ct));

    ValueTask ResumeAsync(HttpContext context) => this.PauseOrResumeAsync(context, (x, ct) => x.Resume(ct));

    async ValueTask PauseOrResumeAsync(HttpContext context, Func<Native.IScreenRecording, CancellationToken, Task> action)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        if (!this.recorder!.Capabilities.HasFlag(Native.ScreenRecorderCapabilities.PauseResume))
        {
            await WebAppBridgeResults.NotSupported(context, "Pausing a screen recording");
            return;
        }

        Native.IScreenRecording? recording;
        lock (this.gate)
            recording = this.active;

        if (recording is null)
        {
            await NotRecording(context);
            return;
        }

        try
        {
            await action(recording, context.RequestAborted);
        }
        catch (ObjectDisposedException)
        {
            // It ended between the check and the call.
            await NotRecording(context);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    /// <summary>The device ended the recording. Whatever it salvaged is filed, exactly as a stop would have filed it.</summary>
    void OnFaulted(object? sender, Native.ScreenRecordingFaultedEventArgs args)
    {
        if (sender is not Native.IScreenRecording recording)
            return;

        lock (this.gate)
        {
            // Already taken by a stop or a cancel racing the fault; that call reports the ending.
            if (!ReferenceEquals(this.active, recording))
                return;

            this.active = null;
        }

        recording.Faulted -= this.OnFaulted;
        _ = this.FinishFaultAsync(recording, args);
    }

    async Task FinishFaultAsync(Native.IScreenRecording recording, Native.ScreenRecordingFaultedEventArgs args)
    {
        var reason = BridgeEnum.Convert<Native.ScreenRecordingFaultReason, ScreenRecordingEndReason>(args.Reason);
        ScreenRecording? filed = null;
        var message = args.Exception?.Message;

        try
        {
            if (args.Result is { } result)
                filed = await this.FileAsync(result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
        finally
        {
            await DisposeQuietlyAsync(recording);
            this.Finish(new ScreenRecordingEnded(reason, filed, message));
        }
    }

    /// <summary>Takes the running recording, so exactly one of stop, cancel or a fault reports its ending.</summary>
    Native.IScreenRecording? Take()
    {
        Native.IScreenRecording? recording;
        lock (this.gate)
        {
            recording = this.active;
            this.active = null;
        }

        if (recording is not null)
            recording.Faulted -= this.OnFaulted;

        return recording;
    }

    void Finish(ScreenRecordingEnded ended)
    {
        lock (this.gate)
        {
            if (ended.Recording is not null)
                this.last = ended.Recording;

            this.claimed = false;
        }

        this.endings.Publish(ended);
        this.statuses.Publish(this.GetStatus());
    }

    bool TryClaim()
    {
        lock (this.gate)
        {
            if (this.claimed || this.recorder!.State != Native.ScreenRecorderState.Idle)
                return false;

            this.claimed = true;
            return true;
        }
    }

    void Release()
    {
        lock (this.gate)
            this.claimed = false;
    }

    void OnStateChanged(object? sender, Native.ScreenRecorderState state)
    {
        if (this.statuses.HasListeners)
            this.statuses.Publish(this.GetStatus());
    }

    ScreenRecorderStatus GetStatus()
    {
        if (this.recorder is null)
            return new ScreenRecorderStatus(false, [], ScreenRecorderState.Idle);

        var native = this.recorder.Capabilities;
        IReadOnlyList<ScreenRecorderCapability> capabilities =
        [
            .. AllCapabilities.Where(x => Enum.TryParse<Native.ScreenRecorderCapabilities>(x.ToString(), out var flag) && native.HasFlag(flag))
        ];

        Native.IScreenRecording? recording;
        ScreenRecording? lastRecording;
        lock (this.gate)
        {
            recording = this.active;
            lastRecording = this.last;
        }

        double? elapsed = null;
        try
        {
            elapsed = recording?.Elapsed.TotalSeconds;
        }
        catch (ObjectDisposedException)
        {
        }

        return new ScreenRecorderStatus(
            native.HasFlag(Native.ScreenRecorderCapabilities.Recording),
            capabilities,
            BridgeEnum.Convert<Native.ScreenRecorderState, ScreenRecorderState>(this.recorder.State),
            elapsed,
            this.options.MaxDuration?.TotalSeconds,
            lastRecording
        );
    }

    Native.ScreenRecordingRequest ToNative(ScreenRecordingRequest body)
    {
        TimeSpan? duration = body.MaxDurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        if (this.options.MaxDuration is { } max && (duration is null || duration > max))
            duration = max;

        return new Native.ScreenRecordingRequest
        {
            IncludeMicrophone = body.IncludeMicrophone,
            IncludeSystemAudio = body.IncludeSystemAudio,
            ShowCursor = body.ShowCursor ?? true,
            FrameRate = body.FrameRate,
            VideoBitrate = body.VideoBitrate,
            MaxWidth = body.MaxWidth,
            MaxDuration = duration
        };
    }

    /// <summary>
    /// Copies the finished recording into the file root, named for when it was filed — <c>REC_20260924-201502.mp4</c> —
    /// and never over an existing file, then deletes the platform's copy. No size limit:
    /// <see cref="AppDeviceBridgeOptions.MaxFileWriteBytes"/> is for what a page uploads, and a recording is the device's own.
    /// </summary>
    async Task<ScreenRecording> FileAsync(Native.ScreenRecordingResult result, CancellationToken cancellationToken)
    {
        if (!this.roots.TryGet(this.options.Root, out var root))
            throw WebAppFileException.NotFound($"No file root '{this.options.Root}' to file the recording in.");

        var extension = result.MimeType.StartsWith("video/webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4";
        var folder = WebAppFilePath.Normalize(this.options.Folder) ?? String.Empty;
        var path = await FreePathAsync(root, folder, $"REC_{this.time.GetLocalNow():yyyyMMdd-HHmmss}", extension, cancellationToken);

        FileWriteResult written;
        await using (var content = await result.OpenRead(cancellationToken))
            written = await root.WriteAsync(path, content, FileWriteMode.CreateNew, Int64.MaxValue, cancellationToken);

        if (result.FilePath is { } source)
        {
            try
            {
                File.Delete(source);
            }
            catch (IOException)
            {
                // The platform's cache; it reclaims it in time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new ScreenRecording(
            new BridgeFile(root.Name, path),
            written.Entry.Size ?? result.ByteSize,
            result.Width,
            result.Height,
            result.Duration.TotalSeconds,
            result.MimeType
        );
    }

    /// <summary>The stamp, or the stamp with a counter when two recordings land in the same second.</summary>
    static async Task<string> FreePathAsync(WebAppFileStore root, string folder, string stamp, string extension, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = attempt == 1 ? stamp + extension : $"{stamp}-{attempt}{extension}";
            var path = folder.Length == 0 ? name : $"{folder}/{name}";

            if (await root.GetEntryAsync(path, cancellationToken) is null)
                return path;
        }

        throw WebAppFileException.Exists("Too many recordings in the same second.");
    }

    static async Task DisposeQuietlyAsync(Native.IScreenRecording recording)
    {
        try
        {
            await recording.DisposeAsync();
        }
        catch (Exception)
        {
            // A finished session has nothing left to release that a failure here would keep.
        }
    }

    static ValueTask NotSupported(HttpContext context) => WebAppBridgeResults.NotSupported(context, "Screen recording");

    static ValueTask Busy(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "recording_busy",
        "The screen is already being recorded. Stop that recording first."
    );

    static ValueTask NotRecording(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "not_recording",
        "Nothing is being recorded."
    );

    /// <summary>An absent body is valid and reads as null; a present one must parse.</summary>
    static async ValueTask<(bool Valid, T? Body)> ReadOptionalBodyAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        if (context.Request.ContentLength is 0)
            return (true, null);

        var body = await WebAppBridgeResults.ReadBodyAsync(context, typeInfo);
        return (body is not null || context.Request.ContentLength is null, body);
    }

    /// <summary>
    /// The app is going away. A recording still running is cancelled rather than left recording the screen with nobody to
    /// stop it; there is no file root left to file it in.
    /// </summary>
    public void Dispose()
    {
        if (this.recorder is not null)
            this.recorder.StateChanged -= this.OnStateChanged;

        if (this.Take() is { } recording)
            _ = DisposeQuietlyAsync(recording);
    }
}
