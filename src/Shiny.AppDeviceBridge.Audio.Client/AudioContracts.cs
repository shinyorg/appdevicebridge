using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Audio.Client;

/// <summary>Which version of the signal a recording keeps.</summary>
public enum AudioRecordMode
{
    /// <summary>The signal after <see cref="AudioRecordingRequest.Effect"/>. The default.</summary>
    Wet,

    /// <summary>The untouched microphone, ignoring any effect.</summary>
    Dry,

    /// <summary>Both, to two files: <see cref="AudioRecording.File"/> is wet, <see cref="AudioRecording.DryFile"/> dry.</summary>
    Both
}

/// <summary>A ready-made effect applied to a recording as it is made.</summary>
public enum AudioEffectPreset
{
    /// <summary>Metallic monotone.</summary>
    Robot,

    /// <summary>Pitched well up.</summary>
    Chipmunk,

    /// <summary>Pitched down.</summary>
    DeepVoice,

    /// <summary>Long, dark reverb.</summary>
    Cathedral,

    /// <summary>Narrow band, like a phone line.</summary>
    Telephone,

    /// <summary>Distorted and band-limited, like a loud-hailer.</summary>
    Megaphone,

    /// <summary>Doubled and detuned, as though several people spoke together.</summary>
    Ensemble
}

public enum AudioDeviceIo { Input, Output }

/// <summary>What kind of route a device is, normalized across platforms.</summary>
public enum AudioDeviceType
{
    Unknown,
    BuiltInMic,
    BuiltInSpeaker,
    BuiltInReceiver,
    WiredHeadset,
    WiredHeadphones,

    /// <summary>Hands-free Bluetooth: has a microphone, at phone-call quality.</summary>
    Bluetooth,

    /// <summary>Stereo Bluetooth: output only.</summary>
    BluetoothA2dp,

    Usb,
    CarAudio,
    Hdmi,
    AirPlay
}

/// <summary>Why a recording ended.</summary>
public enum AudioRecordingEndReason
{
    /// <summary>Stopped through the bridge. The recording was filed.</summary>
    Stopped,

    /// <summary>Cancelled through the bridge. Nothing was kept.</summary>
    Cancelled,

    /// <summary><see cref="AudioRecordingRequest.MaxDurationSeconds"/>, or the app's own limit, ran out. The recording was filed.</summary>
    MaxDurationReached,

    /// <summary>Capture or filing failed. There may be nothing to keep.</summary>
    Failed
}

/// <summary>Why the monitor stopped.</summary>
public enum AudioMonitorEndReason
{
    /// <summary>The page stopped it.</summary>
    Stopped,

    /// <summary>The page stopped listening to <c>audio.monitor.level</c>, so nobody was left to hear it.</summary>
    PageClosed,

    /// <summary>The platform failed.</summary>
    Error
}

/// <summary>What this device can do, and what is running.</summary>
/// <param name="PlaybackSupported">Clips can be played.</param>
/// <param name="RecordingSupported">The microphone can be recorded.</param>
/// <param name="DevicesSupported">Routes can be listed.</param>
/// <param name="MonitorSupported">The microphone can be routed live to the output.</param>
/// <param name="VolumeControlSupported">The volume can be set. False on iOS and Mac Catalyst, which only read it.</param>
/// <param name="PlaybackLevelsSupported"><c>audio.playback.level</c> is raised.</param>
/// <param name="Volume">The system media volume, 0–1, where playback is supported.</param>
/// <param name="Playing">How many clips are playing.</param>
/// <param name="Recording">Whether a recording is running.</param>
/// <param name="RecordingElapsedSeconds">How much has been recorded, while recording.</param>
/// <param name="Monitoring">Whether the monitor is running.</param>
/// <param name="MaxRecordingSeconds">The longest a recording may run; a longer request is shortened to it.</param>
public sealed record AudioStatus(
    bool PlaybackSupported,
    bool RecordingSupported,
    bool DevicesSupported,
    bool MonitorSupported,
    bool VolumeControlSupported,
    bool PlaybackLevelsSupported,
    float? Volume,
    int Playing,
    bool Recording,
    double? RecordingElapsedSeconds,
    bool Monitoring,
    double? MaxRecordingSeconds
);

public sealed record AudioAccessResult(AccessState Access);

/// <summary>
/// The platform's voice processing, best effort and device dependent. Apple's voice-processing unit cannot switch them
/// one at a time, so any of them turns on all three there.
/// </summary>
public sealed record AudioProcessing(bool EchoCancellation = false, bool NoiseSuppression = false, bool AutomaticGainControl = false);

/// <summary>What to play: exactly one of an absolute <c>http</c>/<c>https</c> URL or a file in a file root.</summary>
/// <param name="Url">Fetched by the device, not the page.</param>
/// <param name="File">A file the page wrote, or a recording.</param>
public sealed record AudioPlayRequest(string? Url = null, BridgeFile? File = null);

/// <summary>A clip that is playing.</summary>
public sealed record AudioPlayback(string Id, string? Url, BridgeFile? File);

/// <summary>A clip ended.</summary>
/// <param name="Stopped">True when it was stopped through the bridge rather than played out.</param>
/// <param name="Error">What went wrong, when playback failed.</param>
public sealed record AudioPlaybackEnded(string Id, bool Stopped, string? Error = null);

/// <param name="Volume">0–1.</param>
public sealed record AudioVolumeRequest(float Volume);

/// <param name="Volume">The system media volume, 0–1. Android's is stepped, so it may differ slightly from what was set.</param>
public sealed record AudioVolume(float Volume);

/// <param name="Level">0 (silence) – 1, on the same perceptual scale for every meter.</param>
public sealed record AudioLevelChanged(double Level);

/// <summary>A recording to start. Every field is optional.</summary>
/// <param name="Mode">Which signal to keep.</param>
/// <param name="Effect">Applied as it is recorded. Ignored for <see cref="AudioRecordMode.Dry"/>.</param>
/// <param name="InputDeviceId">An input's <see cref="AudioDevice.Id"/>; the system default when null.</param>
/// <param name="Processing">The platform's voice processing; raw input when null.</param>
/// <param name="MaxDurationSeconds">Stop and file it after this long. Never longer than <see cref="AudioStatus.MaxRecordingSeconds"/>.</param>
public sealed record AudioRecordingRequest(
    AudioRecordMode Mode = AudioRecordMode.Wet,
    AudioEffectPreset? Effect = null,
    string? InputDeviceId = null,
    AudioProcessing? Processing = null,
    double? MaxDurationSeconds = null
);

/// <summary>A recording, filed into a file root as 16 kHz mono 16-bit WAV and read through the files bridge.</summary>
/// <param name="File">The recording — the processed take unless it was recorded <see cref="AudioRecordMode.Dry"/>.</param>
/// <param name="DryFile">The unprocessed take, for <see cref="AudioRecordMode.Both"/>.</param>
/// <param name="Size">Bytes in <paramref name="File"/>.</param>
public sealed record AudioRecording(
    BridgeFile File,
    BridgeFile? DryFile,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    long Size,
    string MimeType
);

/// <summary>A recording ended, however it ended.</summary>
public sealed record AudioRecordingEnded(AudioRecordingEndReason Reason, AudioRecording? Recording = null, string? Message = null);

/// <summary>An input or output route.</summary>
/// <param name="Id">Pass it as <see cref="AudioRecordingRequest.InputDeviceId"/> or <see cref="AudioMonitorRequest.InputDeviceId"/>.</param>
/// <param name="IsCurrent">The route in use.</param>
public sealed record AudioDevice(string Id, string Name, AudioDeviceIo Io, AudioDeviceType Type, bool IsCurrent);

public sealed record AudioDevices(
    IReadOnlyList<AudioDevice> Inputs,
    IReadOnlyList<AudioDevice> Outputs,
    AudioDevice? CurrentInput,
    AudioDevice? CurrentOutput
);

/// <summary>The monitor to start. Every field is optional.</summary>
/// <param name="Gain">0–1; 1 when null.</param>
/// <param name="DuckOtherAudio">Lower other audio while monitoring rather than interrupting it.</param>
/// <param name="InputDeviceId">An input's <see cref="AudioDevice.Id"/>; the system default when null.</param>
/// <param name="OutputDeviceId">
/// An output's <see cref="AudioDevice.Id"/>; the system default when null. Best effort: on iOS only the speaker and the
/// receiver can be chosen, and Bluetooth or AirPlay is picked through <see cref="IAudioBridge.ShowOutputPickerAsync"/>.
/// </param>
/// <param name="Processing">Echo cancellation helps against feedback.</param>
public sealed record AudioMonitorRequest(
    double? Gain = null,
    bool DuckOtherAudio = false,
    string? InputDeviceId = null,
    string? OutputDeviceId = null,
    AudioProcessing? Processing = null
);

/// <param name="Gain">0–1.</param>
public sealed record AudioMonitorGainRequest(double Gain);

/// <summary>The running monitor.</summary>
public sealed record AudioMonitor(double Gain);

public sealed record AudioMonitorEnded(AudioMonitorEndReason Reason, string? Message = null);

/// <summary>Serialization for every audio contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AudioStatus))]
[JsonSerializable(typeof(AudioAccessResult))]
[JsonSerializable(typeof(AudioPlayRequest))]
[JsonSerializable(typeof(AudioPlayback))]
[JsonSerializable(typeof(IReadOnlyList<AudioPlayback>))]
[JsonSerializable(typeof(AudioPlaybackEnded))]
[JsonSerializable(typeof(AudioVolumeRequest))]
[JsonSerializable(typeof(AudioVolume))]
[JsonSerializable(typeof(AudioLevelChanged))]
[JsonSerializable(typeof(AudioRecordingRequest))]
[JsonSerializable(typeof(AudioRecording))]
[JsonSerializable(typeof(AudioRecordingEnded))]
[JsonSerializable(typeof(AudioDevices))]
[JsonSerializable(typeof(AudioMonitorRequest))]
[JsonSerializable(typeof(AudioMonitorGainRequest))]
[JsonSerializable(typeof(AudioMonitor))]
[JsonSerializable(typeof(AudioMonitorEnded))]
public partial class AudioJsonContext : JsonSerializerContext;
