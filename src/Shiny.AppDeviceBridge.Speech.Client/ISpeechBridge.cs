using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Speech.Client;

/// <summary>
/// On-device speech recognition and text-to-speech. Linux has no OS speech engine, so every call there fails with 501.
/// There is one microphone: a one-shot recognition and dictation exclude each other, and whichever comes second fails
/// with 409 <c>microphone_busy</c>.
/// </summary>
[BridgeClient("speech", typeof(SpeechJsonContext))]
public interface ISpeechBridge
{
    /// <summary>What is supported, and whether the microphone or the speaker is in use.</summary>
    [BridgeGet("status")]
    Task<SpeechStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests microphone and speech recognition permission.</summary>
    [BridgePost("access")]
    Task<SpeechAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens until the speaker goes quiet and returns what was heard. <see cref="RecognizeResult.Text"/> is null when
    /// nothing was heard before the timeout.
    /// </summary>
    [BridgePost("recognize")]
    Task<RecognizeResult> RecognizeAsync(RecognizeRequest request, CancellationToken cancellationToken = default);

    /// <summary>The running dictation, or null when there is none.</summary>
    [BridgeGet("listener")]
    Task<SpeechListener?> GetListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts dictation. Results arrive through <see cref="OnPartialAsync"/>, <see cref="OnResultAsync"/> and
    /// <see cref="OnKeywordAsync"/>, so listen first; without an event listener it fails with 409. It ends when stopped
    /// or when the page stops listening to events.
    /// </summary>
    [BridgePost("listener")]
    Task<SpeechListener> StartListenerAsync(SpeechListenerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops dictation.</summary>
    [BridgeDelete("listener")]
    Task StopListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Speaks text, interrupting anything already being spoken. With <see cref="SpeakRequest.Wait"/> it returns once
    /// speech ends; without, it returns null straight away and <see cref="OnSpokenAsync"/> says when it ended.
    /// </summary>
    [BridgePost("speak")]
    Task<SpeakResult?> SpeakAsync(SpeakRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops speaking.</summary>
    [BridgeDelete("speak")]
    Task StopSpeakingAsync(CancellationToken cancellationToken = default);

    /// <summary>The voices installed, optionally for one culture such as <c>en-US</c>.</summary>
    [BridgeGet("voices")]
    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string? culture = null, CancellationToken cancellationToken = default);

    /// <summary>The cultures voices exist for.</summary>
    [BridgeGet("cultures")]
    Task<IReadOnlyList<string>> GetCulturesAsync(CancellationToken cancellationToken = default);

    /// <summary>Dictation's best guess so far, replaced as the speaker goes on.</summary>
    [BridgeEvent("speech.partial")]
    Task<IAsyncDisposable> OnPartialAsync(Func<SpeechRecognized, Task> handler);

    /// <summary>A finished phrase from dictation.</summary>
    [BridgeEvent("speech.result")]
    Task<IAsyncDisposable> OnResultAsync(Func<SpeechRecognized, Task> handler);

    /// <summary>One of the dictation's keywords was heard.</summary>
    [BridgeEvent("speech.keyword")]
    Task<IAsyncDisposable> OnKeywordAsync(Func<SpeechKeyword, Task> handler);

    /// <summary>Dictation ended.</summary>
    [BridgeEvent("speech.ended")]
    Task<IAsyncDisposable> OnEndedAsync(Func<SpeechEnded, Task> handler);

    /// <summary>An utterance finished or was interrupted.</summary>
    [BridgeEvent("speech.spoken")]
    Task<IAsyncDisposable> OnSpokenAsync(Func<SpeakResult, Task> handler);

    /// <summary>Recognition or synthesis failed.</summary>
    [BridgeEvent("speech.error")]
    Task<IAsyncDisposable> OnErrorAsync(Func<SpeechError, Task> handler);
}
