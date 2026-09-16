namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>
/// Discovers cameras and opens capture sessions.
/// </summary>
/// <remarks>
/// Platform-neutral on purpose: the appliance and its command router depend on this, while the
/// libcamera binding that implements it only loads on a Pi. A
/// workstation gets an implementation whose <see cref="IsSupported"/> is false.
/// </remarks>
public interface ICameraService
{
    /// <summary>
    /// True when a camera backend is present and usable. False on a workstation, on a device
    /// with the camera stack missing, or when the native binding failed to load - check
    /// <see cref="BackendDescription"/> for which.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// What backend is in use and which camera stack it was built against, for logs and
    /// support bundles. When <see cref="IsSupported"/> is false, this says why.
    /// </summary>
    string BackendDescription { get; }

    /// <summary>
    /// Every camera currently attached. Empty when none are, which is not an error - a
    /// ribbon cable seated badly looks exactly like this.
    /// </summary>
    Task<IReadOnlyList<CameraDescriptor>> GetCameras(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a camera and configures a stream on it.
    /// </summary>
    /// <param name="options">What to ask for. The result reports what was actually granted.</param>
    /// <param name="cameraId">
    /// Which camera, from <see cref="GetCameras"/>. Null takes the first one, which is the
    /// right answer for an appliance with a single module.
    /// </param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="CameraUnavailableException">
    /// No backend, no camera, or the camera is held by another process.
    /// </exception>
    Task<ICameraSession> Open(
        CameraSessionOptions? options = null,
        string? cameraId = null,
        CancellationToken cancellationToken = default
    );
}


/// <summary>
/// An acquired camera with a configured stream. Exclusive: while this is alive no other
/// process on the device can use the sensor, so it should be held only as long as it is
/// wanted and disposed promptly.
/// </summary>
public interface ICameraSession : IAsyncDisposable
{
    /// <summary>The camera this session holds.</summary>
    CameraDescriptor Camera { get; }

    /// <summary>The stream as it was actually configured, which may differ from what was asked for.</summary>
    CameraStreamInfo Stream { get; }

    /// <summary>
    /// Frames as they arrive, until the token is cancelled or the session is disposed.
    /// </summary>
    /// <remarks>
    /// Each frame must be disposed - ideally inside the loop body - to return its buffer to
    /// the pool. Enumerating slower than the camera produces is fine and expected: surplus
    /// frames are dropped rather than queued, so what arrives is always current. Only one
    /// enumeration may be active at a time.
    /// </remarks>
    IAsyncEnumerable<CameraFrame> ReadFrames(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for and returns a single frame. A convenience over <see cref="ReadFrames"/> for
    /// taking one photograph.
    /// </summary>
    /// <remarks>
    /// The first frame after start is usually the worst one the sensor will produce, because
    /// automatic exposure and white balance have not converged. Implementations discard a
    /// short warm-up run before returning.
    /// </remarks>
    Task<CameraFrame> CaptureFrame(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adjusts a control, taking effect on subsequent frames.
    /// </summary>
    /// <returns>
    /// False when the attached sensor does not implement the control. Not an exception,
    /// because which controls exist is a property of the hardware someone plugged in.
    /// </returns>
    bool TrySetControl(CameraControl control, double value);

    /// <summary>Whether a control exists on this sensor and what range it accepts.</summary>
    CameraControlInfo GetControlInfo(CameraControl control);

    /// <summary>Frame counters for this session.</summary>
    CameraStatistics GetStatistics();
}


/// <summary>
/// The camera could not be used. The message says which of the handful of reasons applies -
/// no backend, no camera attached, or another process holding it - because on a headless
/// device that distinction is the whole diagnosis.
/// </summary>
public class CameraUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CameraUnavailableException(string message) : base(message) { }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public CameraUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
