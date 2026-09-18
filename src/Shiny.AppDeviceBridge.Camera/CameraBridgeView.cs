using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Maui.Controls.Camera;
using NativeFacing = Shiny.Controls.Camera.CameraFacing;
using NativeFilter = Shiny.Controls.Camera.CameraFilter;
using PreviewScaleMode = Shiny.Controls.Camera.PreviewScaleMode;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// A camera that the bridge drives: Shiny's <see cref="CameraView"/>, attached to <see cref="CameraBridgeSession"/> while it
/// is on screen. Put it on a camera page of your own, with whatever controls you like bound to its properties — the buttons
/// on that page and the requests from a page elsewhere run the same methods, so the two cannot drift.
/// <code>
/// &lt;ContentPage xmlns:camera="clr-namespace:Shiny.AppDeviceBridge.Camera;assembly=Shiny.AppDeviceBridge.Camera"&gt;
///     &lt;camera:CameraBridgeView x:Name="Camera" /&gt;
/// &lt;/ContentPage&gt;
/// </code>
/// <para>
/// It attaches when it gets a handler and detaches when it loses one, and turns the camera on as it attaches. Call
/// <see cref="Start"/> and <see cref="Stop"/> yourself from a page that stays alive off screen. Captures are filed through
/// <see cref="ICameraCaptureStore"/>.
/// </para>
/// </summary>
public class CameraBridgeView : ContentView, ICameraBridgeController
{
    const int CameraReadAttempts = 6;
    static readonly TimeSpan CameraReadInterval = TimeSpan.FromMilliseconds(400);

    readonly CameraView camera;
    CameraBridgeSession? session;
    ICameraCaptureStore? store;
    CameraPreviewAnalyzer? analyzer;
    IDisposable? attachment;
    CancellationTokenSource? reading;

    bool videoMode;
    bool recording;
    bool busy;
    bool active;
    bool torchOn;
    Client.CameraFacing facing = Client.CameraFacing.Back;
    string? cameraId;
    Client.CameraFilter filter = Client.CameraFilter.None;
    IReadOnlyList<CameraDevice> cameras = [];
    CameraCapture? lastCapture;
    string? message;

    public CameraBridgeView()
    {
        this.camera = new CameraView
        {
            IsActive = false,
            ScaleMode = PreviewScaleMode.AspectFill,
            IsPinchToZoomEnabled = true,

            // Full sensor resolution, stated rather than relied on: a changed default is a 2MP photo from a 12MP sensor
            // with nothing to say so.
            PhotoQuality = PhotoQuality.Highest,

            // The analyzer here is the viewfinder, a second pair of eyes: a watching laptop should see the effect this
            // screen shows and the captures will carry.
            AnalyzerSeesEffects = true
        };

        this.camera.CameraError += (_, e) => this.Message = e.Message;
        this.camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CameraView.Zoom) or nameof(CameraView.MinZoom) or nameof(CameraView.MaxZoom))
                this.Changed();
        };

        this.Content = this.camera;
    }

    /// <summary>The camera control itself, for anything the bridge does not set — scale mode, video quality, overlays.</summary>
    public CameraView Camera => this.camera;

    /// <summary>Whether this view is the camera the bridge drives right now.</summary>
    public bool IsAttached => this.attachment is not null;

    public bool IsVideoMode
    {
        get => this.videoMode;
        private set => this.Set(ref this.videoMode, value);
    }

    public bool IsRecording
    {
        get => this.recording;
        private set => this.Set(ref this.recording, value);
    }

    /// <summary>A capture is in progress. A full-resolution photo takes long enough to press twice — from two screens.</summary>
    public bool IsBusy
    {
        get => this.busy;
        private set => this.Set(ref this.busy, value);
    }

    /// <summary>Whether the camera is on. Off keeps the screen but frees the sensor, the encoder and the battery.</summary>
    public bool IsCameraActive
    {
        get => this.active;
        private set
        {
            if (this.Set(ref this.active, value))
                this.camera.IsActive = value;
        }
    }

    public Client.CameraFacing Facing
    {
        get => this.facing;
        private set
        {
            if (!this.Set(ref this.facing, value))
                return;

            this.camera.Facing = BridgeEnum.Convert<Client.CameraFacing, NativeFacing>(value);
            this.OnPropertyChanged(nameof(this.TorchAvailable));

            if (!this.TorchAvailable)
                this.IsTorchOn = false;
        }
    }

    public string? CameraId
    {
        get => this.cameraId;
        private set
        {
            if (this.Set(ref this.cameraId, value))
                this.camera.CameraId = value;
        }
    }

    public bool IsTorchOn
    {
        get => this.torchOn;
        private set
        {
            if (this.Set(ref this.torchOn, value))
                this.camera.IsTorchOn = value;
        }
    }

    public Client.CameraFilter Filter
    {
        get => this.filter;
        private set
        {
            if (this.Set(ref this.filter, value))
                this.camera.Filter = BridgeEnum.Convert<Client.CameraFilter, NativeFilter>(value);
        }
    }

    public IReadOnlyList<CameraDevice> Cameras
    {
        get => this.cameras;
        private set
        {
            if (this.Set(ref this.cameras, value))
                this.OnPropertyChanged(nameof(this.ChoosesCamera));
        }
    }

    public CameraCapture? LastCapture
    {
        get => this.lastCapture;
        private set => this.Set(ref this.lastCapture, value);
    }

    /// <summary>What the camera is doing, or why it is not — a failure from the control, a filed capture.</summary>
    public string? Message
    {
        get => this.message;
        private set => this.Set(ref this.message, value);
    }

    /// <summary>
    /// Whether this device picks a camera from a list rather than flipping. A phone has a front and a back; a Mac or a PC has
    /// a built-in camera, perhaps a Continuity iPhone and any number of USB devices, none of them front or back — a flip
    /// button there has nothing to flip to.
    /// </summary>
    public bool ChoosesCamera =>
#if ANDROID || IOS
        false;
#else
        this.cameras.Count > 0;
#endif

    /// <summary>Whether there is a torch. Front lenses have none, and neither do desktop cameras.</summary>
    public bool TorchAvailable =>
#if ANDROID || IOS
        this.facing == Client.CameraFacing.Back;
#else
        false;
#endif

    /// <summary>
    /// Attached and detached with the handler, because not every backend raises <c>Loaded</c> or <c>OnAppearing</c> — the
    /// maui-labs AppKit head raises neither, and a camera that waited for them never came up on a Mac.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (this.Handler is not null)
            this.Start();
        else
            this.Stop();
    }

    /// <summary>Makes this the camera the bridge drives, turns it on, and reads the device's cameras. Safe to call again.</summary>
    public void Start()
    {
        if (this.attachment is not null)
            return;

        var services = this.Handler?.MauiContext?.Services ?? IPlatformApplication.Current?.Services;
        if (services?.GetService<CameraBridgeSession>() is not { } bridge)
        {
            this.Message = "The camera bridge is not registered. Call AddCameraBridge in MauiProgram.";
            return;
        }

        this.session = bridge;
        this.store = services.GetRequiredService<ICameraCaptureStore>();
        this.analyzer ??= new CameraPreviewAnalyzer(bridge.Preview, bridge.Options);

        this.IsCameraActive = true;
        this.attachment = bridge.Attach(this);

        this.reading = new CancellationTokenSource();
        _ = this.ReadCamerasAsync(this.reading.Token);
    }

    /// <summary>Gives the camera up: detaches from the bridge, stops the viewfinder and turns the camera off.</summary>
    public void Stop()
    {
        if (this.attachment is not { } attached)
            return;

        this.reading?.Cancel();
        this.reading?.Dispose();
        this.reading = null;

        // Without the analyzer the control delivers no frames, so a viewer left watching does not keep a torn-down camera busy.
        this.camera.Analyzer = null;
        this.attachment = null;
        attached.Dispose();

        this.IsCameraActive = false;
    }

    /// <summary>
    /// Reads the cameras, asking for the permission first and asking again while the answer is empty. An early read comes
    /// back empty three ways without failing: no handler yet, the permission prompt not answered yet (AVFoundation lists no
    /// devices until it is), or a USB camera not enumerated yet. Bounded: a machine with no camera gives up and says so.
    /// </summary>
    async Task ReadCamerasAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.camera.RequestPermissionAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A backend with no permission story: the enumeration is still worth doing.
        }

        for (var attempt = 0; attempt < CameraReadAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var found = await this.camera.GetAvailableCamerasAsync(cancellationToken);

                if (found.Count > 0 || attempt == CameraReadAttempts - 1)
                {
                    this.Cameras = [.. found.Select(x => new CameraDevice(x.Id, x.Name, BridgeEnum.Convert<NativeFacing, Client.CameraFacing>(x.Facing), x.IsDefault))];

                    if (found.Count == 0)
                        this.Message = "No camera was found on this device.";

                    return;
                }

                await Task.Delay(CameraReadInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Costs the list and nothing else: the camera still opens on whatever the device chooses.
                this.Message = ex.Message;
                return;
            }
        }
    }

    /// <summary>What the shutter means now: take a photo, start recording, or stop and file the video.</summary>
    public async Task<CameraCapture?> ShutterAsync(CancellationToken cancellationToken = default)
    {
        if (!this.IsVideoMode)
            return await this.TakePhotoAsync(cancellationToken);

        if (!this.IsRecording)
        {
            await this.StartRecordingAsync(cancellationToken);
            return null;
        }

        return await this.StopRecordingAsync(cancellationToken);
    }

    public CameraStatus Snapshot() => new(
        Supported: false,
        Access: Shiny.AppDeviceBridge.Client.AccessState.Unknown,
        Live: true,
        Active: this.IsCameraActive,
        VideoMode: this.IsVideoMode,
        Recording: this.IsRecording,
        Busy: this.IsBusy,
        Facing: this.Facing,
        CameraId: this.CameraId,
        Cameras: this.Cameras,
        ChoosesCamera: this.ChoosesCamera,
        TorchOn: this.IsTorchOn,
        TorchAvailable: this.TorchAvailable,
        // Read back off the control: the device decides the range for the lens it actually opened.
        Zoom: this.camera.Zoom,
        MinZoom: this.camera.MinZoom,
        MaxZoom: this.camera.MaxZoom,
        Filter: this.Filter,
        LastCapture: this.LastCapture,
        Message: this.Message
    );

    public async Task<CameraCapture> TakePhotoAsync(CancellationToken cancellationToken)
    {
        if (this.IsVideoMode)
            throw CameraBridgeException.WrongState("The camera is in video mode.");

        if (!this.IsCameraActive)
            throw CameraBridgeException.WrongState("The camera is off.");

        if (this.IsBusy)
            throw CameraBridgeException.WrongState("A capture is already in progress.");

        this.IsBusy = true;
        try
        {
            var photo = await this.camera.CapturePhotoAsync(cancellationToken);
            await using var content = photo.OpenRead();
            return await this.FileAsync(new CameraCaptureContent(CameraCaptureKind.Photo, content, ".jpg", photo.Width, photo.Height), cancellationToken);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken)
    {
        if (!this.IsVideoMode)
            throw CameraBridgeException.WrongState("The camera is in photo mode.");

        if (!this.IsCameraActive)
            throw CameraBridgeException.WrongState("The camera is off.");

        if (this.IsRecording)
            throw CameraBridgeException.WrongState("Already recording.");

        if (this.IsBusy)
            throw CameraBridgeException.WrongState("A capture is already in progress.");

        this.IsBusy = true;
        try
        {
            // The control only touches the audio session when a recording asks for sound — the line that decides whether
            // the user's music stops.
            await this.camera.StartVideoRecordingAsync(new VideoRecordingOptions { IncludeAudio = this.session?.Options.IncludeAudio ?? true }, cancellationToken);
            this.IsRecording = true;
            this.Message = "Recording";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>
    /// Ends the recording and files the video. Not refused while <see cref="IsBusy"/>: a stop turned away is a recording
    /// that carries on after it was asked not to, which is worse than whatever overlapped it.
    /// </summary>
    public async Task<CameraCapture> StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (!this.IsRecording)
            throw CameraBridgeException.WrongState("Not recording.");

        CameraVideo video;
        try
        {
            video = await this.camera.StopVideoRecordingAsync(cancellationToken);
        }
        finally
        {
            // Whatever happened, this recording cannot be finished now; a screen still claiming to record would be lying.
            this.IsRecording = false;
        }

        try
        {
            // The extension comes off what the platform wrote: Apple records QuickTime, Android and Windows MP4.
            await using var content = video.OpenRead();
            return await this.FileAsync(
                new CameraCaptureContent(CameraCaptureKind.Video, content, Path.GetExtension(video.FilePath), Duration: video.Duration),
                cancellationToken
            );
        }
        finally
        {
            TryDelete(video.FilePath);
        }
    }

    public void Apply(CameraSettingsInput settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Everything that swaps the capture session is refused mid-recording, before anything changes, so a refused patch
        // changes nothing at all.
        if (this.IsRecording && (settings.VideoMode is { } v && v != this.IsVideoMode
                                 || settings.Facing is { } f && f != this.Facing
                                 || settings.CameraId is not null
                                 || settings.Active is { } a && a != this.IsCameraActive))
            throw CameraBridgeException.WrongState("The camera cannot be changed while recording.");

        if (settings.VideoMode is { } video)
            this.IsVideoMode = video;

        // Before Facing, because an exact camera wins over a direction. Empty unpins it.
        if (settings.CameraId is { } id)
            this.CameraId = id.Length == 0 ? null : id;

        if (settings.Facing is { } direction)
            this.Facing = direction;

        if (settings.Active is { } on)
        {
            if (!on)
                this.IsTorchOn = false;

            this.IsCameraActive = on;
        }

        if (settings.Filter is { } effect)
            this.Filter = effect;

        if (settings.TorchOn is { } torch)
            this.IsTorchOn = torch && this.TorchAvailable && this.IsCameraActive;

        if (settings.Zoom is { } zoom)
            this.camera.Zoom = Math.Clamp(zoom, this.camera.MinZoom, Math.Max(this.camera.MinZoom, this.camera.MaxZoom));

        this.Changed();
    }

    /// <summary>Toggles photo and video. Refused while recording.</summary>
    public void ToggleMode() => this.Apply(new CameraSettingsInput(VideoMode: !this.IsVideoMode));

    /// <summary>Flips between the front and back cameras.</summary>
    public void Flip() => this.Apply(new CameraSettingsInput(
        Facing: this.Facing == Client.CameraFacing.Front ? Client.CameraFacing.Back : Client.CameraFacing.Front,
        CameraId: ""
    ));

    public void SetPreviewStreaming(bool streaming) => this.camera.Analyzer = streaming ? this.analyzer : null;

    async Task<CameraCapture> FileAsync(CameraCaptureContent content, CancellationToken cancellationToken)
    {
        var filer = this.store ?? throw CameraBridgeException.NotOpen();

        try
        {
            var capture = await filer.SaveAsync(content, cancellationToken);

            // After the write, never before: every watching page refreshes on it.
            this.LastCapture = capture;
            this.Message = $"Saved {WebAppFilePath.NameOf(capture.File.Path)}";
            return capture;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Message = $"Not saved: {ex.Message}";
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The platform's own temporary file; it cleans its cache eventually.
        }
    }

    bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        this.OnPropertyChanged(name);
        this.Changed();
        return true;
    }

    /// <summary>Anything a viewer would draw differently, pushed rather than polled.</summary>
    void Changed()
    {
        if (this.attachment is not null)
            this.session?.NotifyChanged();
    }
}
