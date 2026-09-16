using System.Buffers;

namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>Where a camera sits on the device, when the sensor reports it.</summary>
public enum CameraLocation
{
    /// <summary>Not built in - a USB camera, or a CSI module whose placement is unknown.</summary>
    External = 0,

    /// <summary>Faces the user.</summary>
    Front = 1,

    /// <summary>Faces away from the user.</summary>
    Back = 2
}


/// <summary>
/// What the stream is for. The camera pipeline picks defaults for resolution and buffering
/// from this, so it is worth setting even when the size is specified explicitly.
/// </summary>
public enum CameraStreamRole
{
    /// <summary>Low latency preview. The default, and the right choice for a live feed.</summary>
    Viewfinder = 0,

    /// <summary>A single high-resolution photograph. Slower to start, better quality.</summary>
    StillCapture = 1,

    /// <summary>Continuous recording at a steady frame rate.</summary>
    VideoRecording = 2,

    /// <summary>Unprocessed sensor data, in the sensor's own Bayer format.</summary>
    Raw = 3
}


/// <summary>
/// A pixel format, as a FourCC code. This mirrors the DRM FourCC space that libcamera uses
/// rather than inventing a parallel enumeration, because the set of formats a given sensor
/// and pipeline support is open-ended.
/// </summary>
/// <remarks>
/// The byte order of the RGB formats is the usual trap. These names follow the DRM
/// convention, where the channel order is given from the most significant byte of a
/// little-endian word - so <see cref="Rgb888"/> is B, G, R in memory, and
/// <see cref="Bgr888"/> is R, G, B. Pick by what you need in memory, not by the name.
/// </remarks>
public readonly record struct CameraPixelFormat(uint FourCc)
{
    /// <summary>Motion JPEG. One plane holding a complete JPEG per frame.</summary>
    public static CameraPixelFormat Mjpeg { get; } = FromCode("MJPG");

    /// <summary>24-bit RGB. Bytes in memory are B, G, R.</summary>
    public static CameraPixelFormat Rgb888 { get; } = FromCode("RG24");

    /// <summary>24-bit RGB. Bytes in memory are R, G, B - the one most imaging code expects.</summary>
    public static CameraPixelFormat Bgr888 { get; } = FromCode("BG24");

    /// <summary>32-bit RGBA. Bytes in memory are B, G, R, A.</summary>
    public static CameraPixelFormat Xrgb8888 { get; } = FromCode("XR24");

    /// <summary>Planar 4:2:0 YUV across three planes. The cheapest format for the sensor to produce.</summary>
    public static CameraPixelFormat Yuv420 { get; } = FromCode("YU12");

    /// <summary>Semi-planar 4:2:0 YUV: a luma plane and an interleaved chroma plane.</summary>
    public static CameraPixelFormat Nv12 { get; } = FromCode("NV12");

    /// <summary>Packed 4:2:2 YUV in a single plane.</summary>
    public static CameraPixelFormat Yuyv { get; } = FromCode("YUYV");

    /// <summary>Builds a format from its four-character code, e.g. <c>"MJPG"</c>.</summary>
    public static CameraPixelFormat FromCode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        if (code.Length != 4)
            throw new ArgumentException("A FourCC is exactly four characters.", nameof(code));

        var value = (uint)code[0]
            | ((uint)code[1] << 8)
            | ((uint)code[2] << 16)
            | ((uint)code[3] << 24);

        return new CameraPixelFormat(value);
    }

    /// <summary>True when this is a compressed format, whose frames vary in size.</summary>
    public bool IsCompressed => this == Mjpeg;

    /// <summary>The four-character code, for logs and diagnostics.</summary>
    public override string ToString()
    {
        if (this.FourCc == 0)
            return "(none)";

        Span<char> characters =
        [
            (char)(this.FourCc & 0xFF),
            (char)((this.FourCc >> 8) & 0xFF),
            (char)((this.FourCc >> 16) & 0xFF),
            (char)((this.FourCc >> 24) & 0xFF)
        ];

        return new string(characters);
    }
}


/// <summary>One camera attached to the device.</summary>
/// <param name="Id">
/// Stable identifier from the camera stack. On a Pi this encodes the media device path and
/// sensor, so it survives a reboot but not a move to a different CSI port.
/// </param>
/// <param name="Model">Sensor model, e.g. <c>imx708</c>. Empty when the sensor does not report one.</param>
/// <param name="MaxWidth">Full width of the sensor's pixel array.</param>
/// <param name="MaxHeight">Full height of the sensor's pixel array.</param>
/// <param name="RotationDegrees">How the sensor is mounted relative to the device.</param>
/// <param name="Location">Physical placement, when reported.</param>
public record CameraDescriptor(
    string Id,
    string Model,
    int MaxWidth,
    int MaxHeight,
    int RotationDegrees,
    CameraLocation Location
);


/// <summary>What to ask the camera for when opening a session.</summary>
public class CameraSessionOptions
{
    /// <summary>Requested width in pixels. Leave at zero to take the pipeline's default for the role.</summary>
    public int Width { get; set; }

    /// <summary>Requested height in pixels. Leave at zero to take the pipeline's default for the role.</summary>
    public int Height { get; set; }

    /// <summary>Requested pixel format. Leave at default to take the pipeline's choice for the role.</summary>
    public CameraPixelFormat PixelFormat { get; set; }

    /// <summary>What the stream is for.</summary>
    public CameraStreamRole Role { get; set; } = CameraStreamRole.Viewfinder;

    /// <summary>
    /// Capture buffers to allocate. More buffers absorb a slower reader at the cost of memory;
    /// four is a reasonable floor for a preview stream. Zero takes the pipeline's default.
    /// </summary>
    public int BufferCount { get; set; }

    /// <summary>
    /// How many completed frames may wait for the reader. Beyond this the oldest are recycled
    /// and counted as dropped, which is what a live feed wants - a stale frame is worth less
    /// than a current one. Capped so that two buffers always stay with the sensor.
    /// </summary>
    public int QueueDepth { get; set; } = 1;
}


/// <summary>
/// The stream as the camera actually configured it. The pipeline is free to adjust anything it
/// cannot honour exactly, so this is what to trust, not <see cref="CameraSessionOptions"/>.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="PixelFormat">The format frames arrive in.</param>
/// <param name="Stride">Bytes per row of the first plane, including any padding.</param>
/// <param name="BufferCount">Capture buffers the pipeline allocated.</param>
/// <param name="QueueDepth">Completed frames that may wait for the reader.</param>
public record CameraStreamInfo(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    int Stride,
    int BufferCount,
    int QueueDepth
);


/// <summary>Controls that can be adjusted on a running camera.</summary>
/// <remarks>
/// Not every sensor implements every control - a Camera Module 3 has autofocus and a v2
/// module does not. <see cref="ICameraSession.TrySetControl"/> reports that rather than
/// throwing, so code can offer what the attached hardware supports.
/// </remarks>
public enum CameraControl
{
    /// <summary>Automatic exposure. Turn off before setting exposure or gain by hand.</summary>
    AutoExposure = 1,

    /// <summary>Shutter time in microseconds. Only honoured with <see cref="AutoExposure"/> off.</summary>
    ExposureTime = 2,

    /// <summary>Sensor gain, as a multiplier. Only honoured with <see cref="AutoExposure"/> off.</summary>
    AnalogueGain = 3,

    /// <summary>Automatic white balance.</summary>
    AutoWhiteBalance = 4,

    /// <summary>White balance preset, when automatic white balance is on.</summary>
    WhiteBalanceMode = 5,

    /// <summary>Brightness, -1.0 to 1.0, where 0.0 is neutral.</summary>
    Brightness = 6,

    /// <summary>Contrast, from 0.0 upwards, where 1.0 is neutral.</summary>
    Contrast = 7,

    /// <summary>Saturation, from 0.0 upwards, where 1.0 is neutral and 0.0 is monochrome.</summary>
    Saturation = 8,

    /// <summary>Sharpening, from 0.0 upwards, where 1.0 is neutral and 0.0 is none.</summary>
    Sharpness = 9,

    /// <summary>Exposure compensation in stops, applied on top of automatic exposure.</summary>
    ExposureValue = 10,

    /// <summary>Shortest permitted frame interval in nanoseconds - the ceiling on frame rate.</summary>
    FrameDurationMin = 11,

    /// <summary>Longest permitted frame interval in nanoseconds - the floor on frame rate.</summary>
    FrameDurationMax = 12,

    /// <summary>Autofocus mode: 0 manual, 1 single-shot, 2 continuous.</summary>
    AutoFocusMode = 13,

    /// <summary>Manual focus in dioptres, where 0.0 is infinity. Needs autofocus in manual mode.</summary>
    LensPosition = 14,

    /// <summary>Noise reduction strength, as a pipeline-defined mode.</summary>
    NoiseReduction = 15
}


/// <summary>What a sensor will accept for a control.</summary>
/// <param name="IsSupported">False when the attached sensor does not implement the control at all.</param>
/// <param name="Minimum">Smallest accepted value.</param>
/// <param name="Maximum">Largest accepted value.</param>
/// <param name="Default">The value in force before anything sets it.</param>
public record CameraControlInfo(bool IsSupported, double Minimum, double Maximum, double Default)
{
    /// <summary>A control the attached sensor does not have.</summary>
    public static CameraControlInfo Unsupported { get; } = new(false, 0, 0, 0);
}


/// <summary>
/// Counters for a running session. <see cref="FramesDropped"/> is the one to watch: it counts
/// frames the camera produced and the reader was too slow to take.
/// </summary>
/// <param name="FramesCompleted">Frames the camera finished capturing.</param>
/// <param name="FramesDelivered">Frames handed to the reader.</param>
/// <param name="FramesDropped">Frames recycled because the reader was behind.</param>
/// <param name="FramesErrored">Frames the pipeline marked as failed.</param>
/// <param name="QueueLength">Completed frames waiting to be read right now.</param>
public record CameraStatistics(
    long FramesCompleted,
    long FramesDelivered,
    long FramesDropped,
    long FramesErrored,
    int QueueLength
);


/// <summary>
/// One captured frame, holding a copy of the camera's buffer.
/// </summary>
/// <remarks>
/// The copy is deliberate. The camera stack hands out a pointer into a memory-mapped capture
/// buffer that it needs back within a frame interval or the sensor stalls, which is a terrible
/// lifetime to hand to arbitrary consumer code. Copying into a pooled array costs a memcpy per
/// frame and makes the object safe to hold, queue, or hand to an async pipeline.
///
/// Dispose returns the array to the pool. Failing to dispose is not fatal - it just makes the
/// next frame allocate.
/// </remarks>
public sealed class CameraFrame : IDisposable
{
    byte[]? buffer;
    readonly int[] planeOffsets;
    readonly int[] planeLengths;

    internal CameraFrame(
        byte[] buffer,
        int[] planeOffsets,
        int[] planeLengths,
        int width,
        int height,
        int stride,
        CameraPixelFormat pixelFormat,
        ulong sequence,
        TimeSpan timestamp
    )
    {
        this.buffer = buffer;
        this.planeOffsets = planeOffsets;
        this.planeLengths = planeLengths;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.PixelFormat = pixelFormat;
        this.Sequence = sequence;
        this.Timestamp = timestamp;
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row of the first plane, including padding.</summary>
    public int Stride { get; }

    /// <summary>The format the frame data is in.</summary>
    public CameraPixelFormat PixelFormat { get; }

    /// <summary>The sensor's own frame counter. Gaps in it are dropped frames.</summary>
    public ulong Sequence { get; }

    /// <summary>
    /// When the sensor captured the frame, on the monotonic clock. Useful for measuring
    /// intervals between frames; not related to wall-clock time.
    /// </summary>
    public TimeSpan Timestamp { get; }

    /// <summary>How many planes the format uses. One for compressed and packed formats.</summary>
    public int PlaneCount => this.planeLengths.Length;

    /// <summary>
    /// The first plane, which for a compressed format such as MJPEG is the entire encoded
    /// frame - the bytes to write to a file or an HTTP response as they stand.
    /// </summary>
    public ReadOnlySpan<byte> Data => this.GetPlane(0);

    /// <summary>The bytes of one plane.</summary>
    public ReadOnlySpan<byte> GetPlane(int index)
    {
        ObjectDisposedException.ThrowIf(this.buffer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this.planeLengths.Length);

        return this.buffer.AsSpan(this.planeOffsets[index], this.planeLengths[index]);
    }

    /// <summary>Copies the frame into a new array. For callers that need to outlive the pool.</summary>
    public byte[] ToArray() => this.Data.ToArray();

    /// <inheritdoc />
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref this.buffer, null);
        if (owned is not null)
            ArrayPool<byte>.Shared.Return(owned);
    }
}
