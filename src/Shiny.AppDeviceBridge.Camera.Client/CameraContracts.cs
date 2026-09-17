using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Camera.Client;

/// <summary>Which way a camera points. Most desktop cameras are <see cref="External"/>, which is neither front nor back.</summary>
public enum CameraFacing
{
    Back,
    Front,
    External
}

/// <summary>The camera's built-in effects, applied to the preview, the viewfinder stream and the captures alike.</summary>
public enum CameraFilter
{
    None,
    Mono,
    Noir,
    Sepia,
    Invert,
    Vivid,
    Cool,
    Warm,
    Fade,
    Chrome,
    Instant,
    Tonal
}

public enum CameraCaptureKind
{
    Photo,
    Video
}

/// <summary>One camera the device can open.</summary>
/// <param name="Id">What <see cref="CameraSettingsInput.CameraId"/> takes.</param>
/// <param name="Name">The name the platform gives it — "FaceTime HD Camera", "Back Camera".</param>
/// <param name="Facing">Which way it points.</param>
/// <param name="IsDefault">Whether the platform would choose it when nothing is pinned.</param>
public sealed record CameraDevice(string Id, string Name, CameraFacing Facing, bool IsDefault);

/// <summary>A capture, filed into a file root and read through the files bridge.</summary>
/// <param name="File">Where it was filed.</param>
/// <param name="Kind">A photo or a video.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Width">A photo's width in pixels.</param>
/// <param name="Height">A photo's height in pixels.</param>
/// <param name="DurationSeconds">A video's length, where the platform reports one.</param>
public sealed record CameraCapture(
    BridgeFile File,
    CameraCaptureKind Kind,
    long Size,
    int? Width = null,
    int? Height = null,
    double? DurationSeconds = null
);

/// <summary>
/// The camera as it is right now. Everything about the camera itself is meaningless while <see cref="Live"/> is false: no
/// camera screen is open on the device, and every command answers 409.
/// </summary>
/// <param name="Supported">Whether this platform has a camera to bridge. False on Linux.</param>
/// <param name="Access">The camera permission, without prompting.</param>
/// <param name="Live">Whether a camera screen is open on the device, so there is something to drive.</param>
/// <param name="Active">Whether that screen has the camera turned on. A live camera can be put down without closing it.</param>
/// <param name="VideoMode">Video rather than photos. The two are modes, never both at once.</param>
/// <param name="Recording">Whether a video is being recorded.</param>
/// <param name="Busy">Whether a capture is in progress.</param>
/// <param name="Facing">Which way the camera in use points.</param>
/// <param name="CameraId">The camera pinned by id, or null when the device chose by <paramref name="Facing"/>.</param>
/// <param name="Cameras">The cameras this device offers.</param>
/// <param name="ChoosesCamera">
/// Whether this device picks a camera from <paramref name="Cameras"/> rather than flipping between front and back — true on
/// desktops, whose cameras are neither.
/// </param>
/// <param name="TorchOn">Whether the torch is lit.</param>
/// <param name="TorchAvailable">Whether there is a torch to light. False on front cameras and desktops.</param>
/// <param name="Zoom">The zoom factor, between <paramref name="MinZoom"/> and <paramref name="MaxZoom"/>.</param>
/// <param name="MinZoom">The least the camera in use zooms.</param>
/// <param name="MaxZoom">The most the camera in use zooms.</param>
/// <param name="Filter">The effect in use.</param>
/// <param name="LastCapture">The most recent capture this camera filed.</param>
/// <param name="Message">A sentence for people: what the camera is doing, or why it is not.</param>
public sealed record CameraStatus(
    bool Supported,
    AccessState Access,
    bool Live,
    bool Active = false,
    bool VideoMode = false,
    bool Recording = false,
    bool Busy = false,
    CameraFacing Facing = CameraFacing.Back,
    string? CameraId = null,
    IReadOnlyList<CameraDevice>? Cameras = null,
    bool ChoosesCamera = false,
    bool TorchOn = false,
    bool TorchAvailable = false,
    double Zoom = 1,
    double MinZoom = 1,
    double MaxZoom = 1,
    CameraFilter Filter = CameraFilter.None,
    CameraCapture? LastCapture = null,
    string? Message = null
);

/// <summary>
/// A change to the live camera. Every member is optional and null leaves it alone — a patch, so two viewers of the same
/// camera do not undo each other's changes.
/// </summary>
/// <param name="VideoMode">Switch between photos and video. Refused while recording.</param>
/// <param name="Facing">Choose a camera by which way it points. Refused while recording.</param>
/// <param name="CameraId">Pin a camera by id, which wins over <paramref name="Facing"/>; empty unpins it. Refused while recording.</param>
/// <param name="Active">Turn the camera on or off without closing its screen. Refused while recording.</param>
/// <param name="Filter">The effect to apply.</param>
/// <param name="TorchOn">Light the torch, where there is one.</param>
/// <param name="Zoom">The zoom factor, clamped to the camera's range.</param>
public sealed record CameraSettingsInput(
    bool? VideoMode = null,
    CameraFacing? Facing = null,
    string? CameraId = null,
    bool? Active = null,
    CameraFilter? Filter = null,
    bool? TorchOn = null,
    double? Zoom = null
);

/// <param name="Access">Where the camera permission stands after asking.</param>
public sealed record CameraAccessResult(AccessState Access);

/// <summary>Serialization for every camera contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CameraStatus))]
[JsonSerializable(typeof(CameraSettingsInput))]
[JsonSerializable(typeof(CameraCapture))]
[JsonSerializable(typeof(CameraAccessResult))]
public partial class CameraJsonContext : JsonSerializerContext;
