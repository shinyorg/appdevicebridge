namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>How the libcamera binding finds its native shim and runs its capture thread.</summary>
public class LibCameraOptions
{
    /// <summary>
    /// Where to find <c>libshinyrpi_camera.so</c>, as either a file or the directory holding
    /// it. Leave null to look beside the assemblies, then in the runtime's native folder, then
    /// in <c>/usr/local/lib</c>, then on the system library path.
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>
    /// How long a single blocking read waits for a frame before looping. This is not a frame
    /// timeout - it is how often the capture thread checks whether it has been asked to stop,
    /// so it only needs to be short enough that shutdown feels immediate.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Frames to discard before returning one from <c>CaptureFrame</c>. The first frames after
    /// a start are captured while automatic exposure and white balance are still converging,
    /// and look visibly wrong. Five is enough for a viewfinder stream; a still in changing
    /// light wants more.
    /// </summary>
    public int WarmUpFrames { get; set; } = 5;

    /// <summary>
    /// How long to wait for the capture thread to unwind during disposal before giving up on
    /// it. Reaching this means a native call is wedged, which is worth a log line.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
