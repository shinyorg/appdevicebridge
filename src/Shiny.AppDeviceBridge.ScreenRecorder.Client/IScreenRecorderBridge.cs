using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.ScreenRecorder.Client;

/// <summary>
/// Records the device's screen to a video filed into a file root. Not the screen of the machine showing the page — in a
/// browser that is <c>getDisplayMedia</c>, which the embedded WebViews on iOS and Android do not offer.
/// <para>
/// What ends up in the file differs by platform. Android, macOS, Windows and Linux record the whole screen, other apps
/// included; iOS and Mac Catalyst record only this app. Every platform but Windows asks the user first — a consent dialog,
/// a system permission or the compositor's picker — so <see cref="StartAsync"/> can take as long as the user takes to
/// answer.
/// </para>
/// <para>
/// One recording at a time: a second start answers 409 (<c>recording_busy</c>), and a stop, pause or resume with nothing
/// recording answers 409 (<c>not_recording</c>). A setting the device cannot honour answers 501, as does a device that
/// cannot record at all; a declined consent answers 403 (<c>permission_denied</c>). The device can end a recording on its
/// own, so listen to <see cref="OnEndedAsync"/> rather than assuming a stop will find one running.
/// </para>
/// </summary>
[BridgeClient("screenrecorder", typeof(ScreenRecorderJsonContext))]
public interface IScreenRecorderBridge
{
    /// <summary>What the recorder can do and where it is, without prompting.</summary>
    [BridgeGet]
    Task<ScreenRecorderStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks for what recording needs, where the platform lets it be asked ahead of time.</summary>
    [BridgePost("access")]
    Task<ScreenRecorderAccessResult> RequestAccessAsync(ScreenRecorderAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts recording and answers once frames are being written — after any consent the platform asks for — with the
    /// state it is in.
    /// </summary>
    [BridgePost("recording")]
    Task<ScreenRecorderStatus> StartAsync(ScreenRecordingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops recording, finishes the file and files it. Finishing takes a moment on a long recording.</summary>
    [BridgeDelete("recording")]
    Task<ScreenRecording> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends the recording and keeps nothing.</summary>
    [BridgePost("recording/cancel")]
    Task CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops writing frames without ending the recording. Needs <see cref="ScreenRecorderCapability.PauseResume"/>.</summary>
    [BridgePost("recording/pause")]
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Carries on after <see cref="PauseAsync"/>.</summary>
    [BridgePost("recording/resume")]
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>The recorder changed state — starting, recording, paused, stopping, idle. Raised with the whole status.</summary>
    [BridgeEvent("screenrecorder.status")]
    Task<IAsyncDisposable> OnStatusAsync(Func<ScreenRecorderStatus, Task> handler);

    /// <summary>A recording ended, however it ended, with the recording when one was filed.</summary>
    [BridgeEvent("screenrecorder.ended")]
    Task<IAsyncDisposable> OnEndedAsync(Func<ScreenRecordingEnded, Task> handler);
}
