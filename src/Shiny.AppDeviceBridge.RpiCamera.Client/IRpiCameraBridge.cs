using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.RpiCamera.Client;

/// <summary>
/// A Raspberry Pi camera through libcamera: the cameras attached, JPEG snapshots, photographs written into a file root,
/// and sensor controls. Linux with libcamera only; everywhere else fails with 501.
/// <para>
/// The live feed is <c>GET {bridge}/rpicamera/stream</c> — a <c>multipart/x-mixed-replace</c> MJPEG stream an
/// <c>&lt;img&gt;</c> plays directly, with <c>camera</c>, <c>width</c>, <c>height</c>, <c>quality</c> and <c>fps</c> in the
/// query. Everyone watching a camera shares one stream, so the first viewer decides its size.
/// </para>
/// </summary>
[BridgeClient("rpicamera", typeof(RpiCameraJsonContext))]
public interface IRpiCameraBridge
{
    /// <summary>Whether a camera backend loaded, the cameras attached, and the streams running.</summary>
    [BridgeGet]
    Task<RpiCameraStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A JPEG from the camera. Taken from the running stream when there is one, otherwise from a session opened for the
    /// photograph. Fails with 404 for an unknown camera and 409 while another process holds the camera.
    /// </summary>
    /// <param name="camera">Null takes the first camera.</param>
    /// <param name="width">Zero takes the bridge's still size.</param>
    /// <param name="quality">JPEG quality, 1 to 100; zero takes the bridge's still quality.</param>
    [BridgeGet("snapshot")]
    Task<Stream> SnapshotAsync(string? camera = null, int width = 0, int height = 0, int quality = 0, CancellationToken cancellationToken = default);

    /// <summary>Takes a photograph and writes it into a file root, where the page, a transfer or a share can pick it up.</summary>
    [BridgePost("capture")]
    Task<FileEntry> CaptureAsync(RpiCameraCapture capture, CancellationToken cancellationToken = default);

    /// <summary>Every control, what the sensor accepts, and the value the bridge applies.</summary>
    [BridgeGet("controls")]
    Task<IReadOnlyList<RpiCameraControlInfo>> GetControlsAsync(string? camera = null, CancellationToken cancellationToken = default);

    /// <summary>Sets controls on a running stream and on every session opened later. Fails with 400 naming a control the sensor lacks.</summary>
    [BridgePut("controls")]
    Task<IReadOnlyList<RpiCameraControlInfo>> SetControlsAsync(RpiCameraControlsInput controls, string? camera = null, CancellationToken cancellationToken = default);

    /// <summary>Ends every stream, closing each viewer's response and releasing the cameras.</summary>
    [BridgeDelete("streams")]
    Task StopStreamsAsync(CancellationToken cancellationToken = default);
}
