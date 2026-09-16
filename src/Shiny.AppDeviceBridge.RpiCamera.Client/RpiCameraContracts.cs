using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.RpiCamera.Client;

public enum RpiCameraLocation
{
    /// <summary>Not built in: a USB camera, or a CSI module whose placement is unknown.</summary>
    External,

    Front,

    Back
}

/// <summary>A camera attached to the device.</summary>
/// <param name="Id">Stable across reboots, but not across a move to another CSI port.</param>
/// <param name="Model">The sensor, such as <c>imx708</c>; empty when it doesn't say.</param>
/// <param name="MaxWidth">The full width of the sensor's pixel array.</param>
/// <param name="RotationDegrees">How the sensor is mounted.</param>
public sealed record RpiCameraInfo(
    string Id,
    string Model,
    int MaxWidth,
    int MaxHeight,
    int RotationDegrees,
    RpiCameraLocation Location
);

/// <summary>A live stream, shared by everyone watching it.</summary>
/// <param name="Width">What the camera actually delivers, which may differ from what was asked for.</param>
/// <param name="PixelFormat">The sensor's format as a FourCC, such as <c>NV12</c>; frames are encoded to JPEG from it.</param>
/// <param name="Viewers">Open <c>stream</c> responses.</param>
/// <param name="FramesDropped">Frames the camera produced faster than they could be sent.</param>
public sealed record RpiCameraStream(
    string CameraId,
    int Width,
    int Height,
    string PixelFormat,
    int Quality,
    int MaxFps,
    int Viewers,
    long FramesDelivered,
    long FramesDropped
);

/// <summary>What the camera can do here.</summary>
/// <param name="Supported">False off Linux, without libcamera, or with the native shim missing — <see cref="Backend"/> says which.</param>
/// <param name="Backend">The backend and the libcamera it was built against, or why there is none.</param>
/// <param name="Cameras">Every camera attached. Empty isn't an error: a badly seated ribbon cable looks like this.</param>
/// <param name="Streams">Streams running now.</param>
public sealed record RpiCameraStatus(
    bool Supported,
    string Backend,
    IReadOnlyList<RpiCameraInfo> Cameras,
    IReadOnlyList<RpiCameraStream> Streams
);

/// <summary>A photograph to take and write into a file root.</summary>
/// <param name="Root">The file root, as the files bridge names it.</param>
/// <param name="Path">Where in the root. An existing file is replaced.</param>
/// <param name="CameraId">Null takes the first camera.</param>
/// <param name="Width">Zero takes the bridge's still size.</param>
/// <param name="Quality">JPEG quality, 1 to 100; zero takes the bridge's still quality.</param>
public sealed record RpiCameraCapture(
    string Root,
    string Path,
    string? CameraId = null,
    int Width = 0,
    int Height = 0,
    int Quality = 0
);

/// <summary>A sensor control. Which ones exist depends on the module: a Camera Module 3 has autofocus, a v2 doesn't.</summary>
public enum RpiCameraControl
{
    /// <summary>1 on, 0 off. Turn off before setting exposure time or gain.</summary>
    AutoExposure,

    /// <summary>Shutter time in microseconds.</summary>
    ExposureTime,

    /// <summary>Sensor gain as a multiplier.</summary>
    AnalogueGain,

    AutoWhiteBalance,

    WhiteBalanceMode,

    /// <summary>-1 to 1, where 0 is neutral.</summary>
    Brightness,

    /// <summary>1 is neutral.</summary>
    Contrast,

    /// <summary>1 is neutral, 0 is monochrome.</summary>
    Saturation,

    /// <summary>1 is neutral, 0 is none.</summary>
    Sharpness,

    /// <summary>Exposure compensation in stops.</summary>
    ExposureValue,

    /// <summary>The shortest frame interval in nanoseconds: the ceiling on frame rate.</summary>
    FrameDurationMin,

    /// <summary>The longest frame interval in nanoseconds: the floor on frame rate.</summary>
    FrameDurationMax,

    /// <summary>0 manual, 1 single-shot, 2 continuous.</summary>
    AutoFocusMode,

    /// <summary>Manual focus in dioptres, where 0 is infinity.</summary>
    LensPosition,

    NoiseReduction
}

/// <param name="Supported">False when the attached sensor doesn't have the control.</param>
/// <param name="Value">The value the bridge applies to every session it opens; null when it leaves the sensor's own.</param>
public sealed record RpiCameraControlInfo(
    RpiCameraControl Control,
    bool Supported,
    double Minimum,
    double Maximum,
    double Default,
    double? Value
);

/// <param name="Value">Null goes back to the sensor's own value from the next session.</param>
public sealed record RpiCameraControlValue(RpiCameraControl Control, double? Value);

/// <summary>Control values to apply now, to a running stream, and to every session opened later.</summary>
public sealed record RpiCameraControlsInput(IReadOnlyList<RpiCameraControlValue> Values);

/// <summary>Serialization for every camera contract, shared by the client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RpiCameraStatus))]
[JsonSerializable(typeof(RpiCameraCapture))]
[JsonSerializable(typeof(RpiCameraControlsInput))]
[JsonSerializable(typeof(IReadOnlyList<RpiCameraControlInfo>))]
[JsonSerializable(typeof(FileEntry))]
public partial class RpiCameraJsonContext : JsonSerializerContext;
