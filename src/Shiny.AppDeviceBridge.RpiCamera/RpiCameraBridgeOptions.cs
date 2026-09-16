namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>What the camera bridge delivers, and how the libcamera binding runs.</summary>
public sealed class RpiCameraBridgeOptions
{
    /// <summary>Where the native shim is, and how the capture thread runs.</summary>
    public LibCameraOptions Camera { get; } = new();

    /// <summary>Width asked for a photograph when the caller gives none. Zero lets the pipeline choose.</summary>
    public int StillWidth { get; set; } = 1920;

    /// <summary>Height asked for a photograph when the caller gives none.</summary>
    public int StillHeight { get; set; } = 1080;

    /// <summary>JPEG quality for a photograph, 1 to 100.</summary>
    public int StillQuality { get; set; } = 90;

    /// <summary>Width asked for a live stream when the first viewer gives none.</summary>
    public int StreamWidth { get; set; } = 640;

    /// <summary>Height asked for a live stream when the first viewer gives none.</summary>
    public int StreamHeight { get; set; } = 480;

    /// <summary>
    /// JPEG quality for a live stream, 1 to 100. Lower than a photograph on purpose: each frame has to reach the viewer
    /// before the next one arrives.
    /// </summary>
    public int StreamQuality { get; set; } = 60;

    /// <summary>The most frames per second a stream delivers, whatever a viewer asks for. Surplus frames are dropped before encoding.</summary>
    public int MaxFps { get; set; } = 15;

    /// <summary>
    /// How long one viewer's stream may run before the bridge ends it. A viewer that never disconnects — a tab left open,
    /// a client that died without closing — would otherwise hold the sensor warm forever.
    /// </summary>
    public TimeSpan MaxStreamDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Treat the camera's output as studio-swing Y'CbCr and expand it to full range when encoding. Off by default: the
    /// Pi's ISP normally produces full range, and expanding it again clips highlights.
    /// </summary>
    public bool LimitedRangeInput { get; set; }

    internal void Validate()
    {
        if (this.StillQuality is < 1 or > 100 || this.StreamQuality is < 1 or > 100)
            throw new InvalidOperationException("RpiCameraBridgeOptions qualities must be between 1 and 100.");

        if (this.MaxFps is < 1 or > 120)
            throw new InvalidOperationException("RpiCameraBridgeOptions.MaxFps must be between 1 and 120.");

        if (this.MaxStreamDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("RpiCameraBridgeOptions.MaxStreamDuration must be positive.");
    }
}
