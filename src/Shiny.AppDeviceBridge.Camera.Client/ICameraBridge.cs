using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Camera.Client;

/// <summary>
/// The device's own camera, driven from wherever the page is — a laptop across the room from a phone on a mount. Not the
/// camera of the machine showing the page: that is <c>getUserMedia</c>.
/// <para>
/// The camera runs on a camera screen on the device, and only while one is open: iOS will not run a camera in the
/// background at all. <see cref="OpenAsync"/> asks the device to show one; until it does, <see cref="CameraStatus.Live"/>
/// is false and commands fail with 409 (<c>camera_not_open</c>), which a page should show as an instruction rather than an
/// error. A command the camera cannot take in its current state — stop a recording that is not running, change lens while
/// recording — fails with 422 (<c>camera_state</c>). Linux has no camera: 501.
/// </para>
/// <para>
/// The viewfinder is <c>GET {bridge}/camera/preview</c> — a <c>multipart/x-mixed-replace</c> MJPEG stream an
/// <c>&lt;img&gt;</c> plays directly. The device only encodes frames while someone is watching. Add a changing query
/// parameter each time it is shown, or a browser may reuse the previous, never-ending response.
/// </para>
/// </summary>
[BridgeClient("camera", typeof(CameraJsonContext))]
public interface ICameraBridge
{
    /// <summary>The camera's state, and whether this platform has one and the app may use it — without prompting.</summary>
    [BridgeGet]
    Task<CameraStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Prompts for camera access where the platform still will, and says where it stands.</summary>
    [BridgePost("access")]
    Task<CameraAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the device to show its camera screen. Answers straight away; the camera arrives on <see cref="OnStatusAsync"/>
    /// when it is up. Does nothing when one is already open.
    /// </summary>
    [BridgePost("open")]
    Task OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the camera screen the bridge opened. A camera screen the app opened itself is the app's to close.</summary>
    [BridgePost("close")]
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a photo at full resolution and files it.</summary>
    [BridgePost("photo")]
    Task<CameraCapture> TakePhotoAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts recording video. The camera has to be in video mode.</summary>
    [BridgePost("recording")]
    Task StartRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops recording and files the video.</summary>
    [BridgeDelete("recording")]
    Task<CameraCapture> StopRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the live camera, and answers with the state it is in afterwards.</summary>
    [BridgePut("settings")]
    Task<CameraStatus> UpdateSettingsAsync(CameraSettingsInput settings, CancellationToken cancellationToken = default);

    /// <summary>The camera changed — opened, closed, switched mode, filed a capture. Raised with the whole state.</summary>
    [BridgeEvent("camera.status")]
    Task<IAsyncDisposable> OnStatusAsync(Func<CameraStatus, Task> handler);
}
