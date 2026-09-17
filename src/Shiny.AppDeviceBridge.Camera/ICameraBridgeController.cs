using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// A camera screen on the device, as the bridge drives it. <see cref="CameraBridgeView"/> is one; implement it yourself to
/// put a camera of your own behind the bridge, and attach it with <see cref="CameraBridgeSession.Attach"/> while it is on
/// screen.
/// <para>
/// Every member is called on the UI thread: a camera control is a view. Failures the page should hear about are
/// <see cref="CameraBridgeException"/>.
/// </para>
/// </summary>
public interface ICameraBridgeController
{
    /// <summary>
    /// The camera's state. <see cref="CameraStatus.Supported"/> and <see cref="CameraStatus.Access"/> are filled in by the
    /// session, so any value will do there.
    /// </summary>
    CameraStatus Snapshot();

    Task<CameraCapture> TakePhotoAsync(CancellationToken cancellationToken);

    Task StartRecordingAsync(CancellationToken cancellationToken);

    Task<CameraCapture> StopRecordingAsync(CancellationToken cancellationToken);

    void Apply(CameraSettingsInput settings);

    /// <summary>
    /// Starts or stops handing frames to <see cref="CameraBridgeSession.Preview"/>. Asked for when the first viewer of the
    /// viewfinder arrives and when the last one leaves, so a camera nobody is watching encodes nothing.
    /// </summary>
    void SetPreviewStreaming(bool streaming);
}

/// <summary>A camera failure the page is told about, with the status and code the camera bridge answers with.</summary>
public sealed class CameraBridgeException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    /// <summary>409: no camera screen is open on the device. A normal state — the page should say so, not retry.</summary>
    public static CameraBridgeException NotOpen() => new(
        StatusCodes.Status409Conflict,
        "camera_not_open",
        "The camera is not open on the device."
    );

    /// <summary>422: the camera is open, but not in a state where the command makes sense.</summary>
    public static CameraBridgeException WrongState(string message) => new(StatusCodes.Status422UnprocessableEntity, "camera_state", message);

    /// <summary>403: the platform has not granted the camera.</summary>
    public static CameraBridgeException AccessDenied(string message = "Camera access has not been granted.")
        => new(StatusCodes.Status403Forbidden, "access_denied", message);
}
