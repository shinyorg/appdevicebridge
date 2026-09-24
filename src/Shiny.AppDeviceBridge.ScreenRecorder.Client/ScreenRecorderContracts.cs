using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.ScreenRecorder.Client;

/// <summary>
/// What this device's recorder can do. A request that asks for something missing here answers 501 rather than recording
/// without it.
/// </summary>
public enum ScreenRecorderCapability
{
    /// <summary>It can record at all.</summary>
    Recording,

    /// <summary><see cref="IScreenRecorderBridge.PauseAsync"/> and <see cref="IScreenRecorderBridge.ResumeAsync"/> work.</summary>
    PauseResume,

    /// <summary><see cref="ScreenRecordingRequest.IncludeMicrophone"/> is honoured.</summary>
    Microphone,

    /// <summary>
    /// <see cref="ScreenRecordingRequest.IncludeSystemAudio"/> is honoured: everything the machine plays on macOS and Linux,
    /// only the app's own audio on iOS and Android.
    /// </summary>
    SystemAudio,

    /// <summary><see cref="ScreenRecordingRequest.ShowCursor"/> can be turned off.</summary>
    CursorToggle,

    /// <summary><see cref="ScreenRecordingRequest.FrameRate"/> is honoured.</summary>
    FrameRateControl,

    /// <summary><see cref="ScreenRecordingRequest.VideoBitrate"/> is honoured.</summary>
    BitrateControl,

    /// <summary><see cref="ScreenRecordingRequest.MaxWidth"/> is honoured.</summary>
    Downscaling
}

/// <summary>Where the recorder is.</summary>
public enum ScreenRecorderState
{
    Idle,

    /// <summary>Waiting on the user — a consent dialog, a compositor's picker — or on the platform to start writing frames.</summary>
    Starting,

    Recording,

    Paused,

    /// <summary>Finishing the file.</summary>
    Stopping
}

/// <summary>Why a recording ended.</summary>
public enum ScreenRecordingEndReason
{
    /// <summary>The platform gave no reason.</summary>
    Unknown,

    /// <summary>Stopped through the bridge. The recording was filed.</summary>
    Stopped,

    /// <summary>Cancelled through the bridge. Nothing was kept.</summary>
    Cancelled,

    /// <summary>
    /// <see cref="ScreenRecordingRequest.MaxDurationSeconds"/>, or the app's own limit, ran out. The recording was filed.
    /// </summary>
    MaxDurationReached,

    /// <summary>The user stopped it outside the page — Android's notification, the macOS menu bar, the compositor.</summary>
    RevokedByUser,

    /// <summary>The OS ended it: a call on iOS, the screen locking, Android's foreground-service limit.</summary>
    InterruptedBySystem,

    /// <summary>The display went away.</summary>
    TargetLost,

    /// <summary>The encoder failed, or the finished file could not be filed. There is rarely anything to keep.</summary>
    EncoderFailed
}

/// <summary>A recording, filed into a file root and read through the files bridge.</summary>
/// <param name="File">Where it was filed.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Width">Encoded width in pixels.</param>
/// <param name="Height">Encoded height in pixels.</param>
/// <param name="DurationSeconds">How long it runs, without any paused span.</param>
/// <param name="MimeType">What was written — <c>video/mp4</c> on every native platform.</param>
public sealed record ScreenRecording(
    BridgeFile File,
    long Size,
    int Width,
    int Height,
    double DurationSeconds,
    string MimeType
);

/// <summary>The recorder as it is right now.</summary>
/// <param name="Supported">Whether this device can record its screen at all.</param>
/// <param name="Capabilities">What it can do.</param>
/// <param name="State">Where it is.</param>
/// <param name="ElapsedSeconds">How much has been recorded, without any paused span. Null when nothing is recording.</param>
/// <param name="MaxDurationSeconds">The longest a recording may run on this device; a longer request is shortened to it.</param>
/// <param name="LastRecording">The most recent recording the bridge filed.</param>
public sealed record ScreenRecorderStatus(
    bool Supported,
    IReadOnlyList<ScreenRecorderCapability> Capabilities,
    ScreenRecorderState State,
    double? ElapsedSeconds = null,
    double? MaxDurationSeconds = null,
    ScreenRecording? LastRecording = null
);

/// <summary>
/// What to record. Every setting but the audio is optional, and each one needs its
/// <see cref="ScreenRecorderCapability"/>: a request for something this device cannot do answers 501.
/// </summary>
/// <param name="IncludeMicrophone">Mix in the microphone. Needs <see cref="ScreenRecorderCapability.Microphone"/>.</param>
/// <param name="IncludeSystemAudio">Capture what the device is playing. Needs <see cref="ScreenRecorderCapability.SystemAudio"/>.</param>
/// <param name="ShowCursor">Draw the pointer. Null or true draws it; false needs <see cref="ScreenRecorderCapability.CursorToggle"/>.</param>
/// <param name="FrameRate">A ceiling, 1–240. Needs <see cref="ScreenRecorderCapability.FrameRateControl"/>.</param>
/// <param name="VideoBitrate">Bits a second. Needs <see cref="ScreenRecorderCapability.BitrateControl"/>.</param>
/// <param name="MaxWidth">Downscale to at most this many pixels wide. Needs <see cref="ScreenRecorderCapability.Downscaling"/>.</param>
/// <param name="MaxDurationSeconds">
/// Stop and file it after this long. Never longer than <see cref="ScreenRecorderStatus.MaxDurationSeconds"/>, which also
/// applies when this is null.
/// </param>
public sealed record ScreenRecordingRequest(
    bool IncludeMicrophone = false,
    bool IncludeSystemAudio = false,
    bool? ShowCursor = null,
    int? FrameRate = null,
    int? VideoBitrate = null,
    int? MaxWidth = null,
    double? MaxDurationSeconds = null
);

/// <summary>Which audio to ask for along with the screen. Only the microphone has a permission of its own.</summary>
public sealed record ScreenRecorderAccessRequest(bool IncludeMicrophone = false, bool IncludeSystemAudio = false);

/// <param name="Access">
/// Where recording stands after asking. Android cannot grant the screen ahead of time, so it reports
/// <see cref="AccessState.Unknown"/> there and asks when a recording starts: anything but
/// <see cref="AccessState.Denied"/> is worth trying.
/// </param>
public sealed record ScreenRecorderAccessResult(AccessState Access);

/// <summary>A recording ended — stopped, cancelled, or ended by the device.</summary>
/// <param name="Reason">Why.</param>
/// <param name="Recording">The recording, when there was one to file.</param>
/// <param name="Message">What went wrong, when something did.</param>
public sealed record ScreenRecordingEnded(ScreenRecordingEndReason Reason, ScreenRecording? Recording = null, string? Message = null);

/// <summary>Serialization for every screen recorder contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ScreenRecorderStatus))]
[JsonSerializable(typeof(ScreenRecordingRequest))]
[JsonSerializable(typeof(ScreenRecording))]
[JsonSerializable(typeof(ScreenRecorderAccessRequest))]
[JsonSerializable(typeof(ScreenRecorderAccessResult))]
[JsonSerializable(typeof(ScreenRecordingEnded))]
public partial class ScreenRecorderJsonContext : JsonSerializerContext;
