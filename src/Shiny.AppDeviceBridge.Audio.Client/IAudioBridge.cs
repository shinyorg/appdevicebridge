using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Audio.Client;

/// <summary>
/// The device's speakers and microphone: play clips, read and set the system volume, record the microphone to WAV,
/// list audio routes, and route the microphone live to the output. Each part answers 501 where the platform lacks it —
/// <see cref="GetStatusAsync"/> says which.
/// <para>
/// There is one microphone. A recording and the monitor exclude each other, and whichever comes second fails with 409
/// <c>microphone_busy</c>; so does either one while the native app, or the speech bridge, holds the microphone.
/// </para>
/// </summary>
[BridgeClient("audio", typeof(AudioJsonContext))]
public interface IAudioBridge
{
    /// <summary>What is supported, and what is running.</summary>
    [BridgeGet("status")]
    Task<AudioStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests microphone permission.</summary>
    [BridgePost("access")]
    Task<AudioAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    // ---- playback

    /// <summary>The clips playing, oldest first.</summary>
    [BridgeGet("playback")]
    Task<IReadOnlyList<AudioPlayback>> GetPlaybackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a clip and answers once it is playing. Clips play together: starting one does not stop another.
    /// <see cref="OnPlaybackEndedAsync"/> says when it ends.
    /// </summary>
    [BridgePost("playback")]
    Task<AudioPlayback> PlayAsync(AudioPlayRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops one clip. Fails with 404 when it is not playing.</summary>
    [BridgeDelete("playback/{id}")]
    Task StopPlaybackAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Stops every clip.</summary>
    [BridgeDelete("playback")]
    Task StopAllPlaybackAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the system media volume. Fails with 501 where it can only be read.</summary>
    [BridgePut("volume")]
    Task<AudioVolume> SetVolumeAsync(AudioVolumeRequest request, CancellationToken cancellationToken = default);

    /// <summary>A clip ended — played out, stopped or failed.</summary>
    [BridgeEvent("audio.playback.ended")]
    Task<IAsyncDisposable> OnPlaybackEndedAsync(Func<AudioPlaybackEnded, Task> handler);

    /// <summary>The output level about 20 times a second while something plays, where <see cref="AudioStatus.PlaybackLevelsSupported"/>.</summary>
    [BridgeEvent("audio.playback.level")]
    Task<IAsyncDisposable> OnPlaybackLevelAsync(Func<AudioLevelChanged, Task> handler);

    /// <summary>The volume changed — the hardware buttons, the system's own control, or <see cref="SetVolumeAsync"/>.</summary>
    [BridgeEvent("audio.volume")]
    Task<IAsyncDisposable> OnVolumeAsync(Func<AudioVolume, Task> handler);

    // ---- recording

    /// <summary>Starts recording the microphone. Fails with 409 <c>microphone_busy</c> when it is in use.</summary>
    [BridgePost("recording")]
    Task<AudioStatus> StartRecordingAsync(AudioRecordingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops recording and files it. Fails with 409 <c>not_recording</c> when nothing is recording.</summary>
    [BridgeDelete("recording")]
    Task<AudioRecording> StopRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends the recording and keeps nothing.</summary>
    [BridgePost("recording/cancel")]
    Task CancelRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>The level of what is being written, effect included, about 20 times a second.</summary>
    [BridgeEvent("audio.recording.level")]
    Task<IAsyncDisposable> OnRecordingLevelAsync(Func<AudioLevelChanged, Task> handler);

    /// <summary>A recording ended, with the recording when one was filed.</summary>
    [BridgeEvent("audio.recording.ended")]
    Task<IAsyncDisposable> OnRecordingEndedAsync(Func<AudioRecordingEnded, Task> handler);

    // ---- routes

    /// <summary>The input and output routes the device sees.</summary>
    [BridgeGet("devices")]
    Task<AudioDevices> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows the system's output picker (AirPlay and Bluetooth on iOS). Does nothing where there is none.</summary>
    [BridgePost("devices/picker")]
    Task ShowOutputPickerAsync(CancellationToken cancellationToken = default);

    /// <summary>A route came or went, or the one in use changed. Raised with every route.</summary>
    [BridgeEvent("audio.devices")]
    Task<IAsyncDisposable> OnDevicesAsync(Func<AudioDevices, Task> handler);

    // ---- monitor

    /// <summary>
    /// Routes the microphone live to the output — a PA. Listen to <see cref="OnMonitorLevelAsync"/> first: without a
    /// listener it fails with 409 <c>not_listening</c>, and it stops once the last listener is gone, so a page that went
    /// away cannot leave the microphone on the speaker.
    /// </summary>
    [BridgePost("monitor")]
    Task<AudioMonitor> StartMonitorAsync(AudioMonitorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes the monitor's gain while it runs. Fails with 409 <c>not_monitoring</c> when it does not.</summary>
    [BridgePut("monitor")]
    Task<AudioMonitor> SetMonitorGainAsync(AudioMonitorGainRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops the monitor.</summary>
    [BridgeDelete("monitor")]
    Task StopMonitorAsync(CancellationToken cancellationToken = default);

    /// <summary>The microphone's level while monitoring.</summary>
    [BridgeEvent("audio.monitor.level")]
    Task<IAsyncDisposable> OnMonitorLevelAsync(Func<AudioLevelChanged, Task> handler);

    /// <summary>The monitor stopped.</summary>
    [BridgeEvent("audio.monitor.ended")]
    Task<IAsyncDisposable> OnMonitorEndedAsync(Func<AudioMonitorEnded, Task> handler);
}
