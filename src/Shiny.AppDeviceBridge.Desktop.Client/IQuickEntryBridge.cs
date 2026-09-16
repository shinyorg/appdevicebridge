using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Desktop.Client;

/// <summary>
/// Quick entry: a prompt window that opens over other applications — from a global hotkey, a tray click or the page —
/// in the style of Spotlight or a desktop assistant. The page configures the prompt, hears what is submitted, and writes
/// the response back, and it keeps working with the app's own window closed: submissions also go to background.js.
/// Windows, macOS and Linux; Android and iOS fail with 501.
/// </summary>
[BridgeClient("quickentry", typeof(QuickEntryJsonContext))]
public interface IQuickEntryBridge
{
    /// <summary>Whether quick entry works here, and what it is doing.</summary>
    [BridgeGet]
    Task<QuickEntryStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes how the window opens. A hotkey another application owns is reported, not an error.</summary>
    [BridgePut("options")]
    Task<QuickEntryStatus> ConfigureAsync(QuickEntryOptionsInput options, CancellationToken cancellationToken = default);

    /// <summary>Opens the window.</summary>
    [BridgePost("show")]
    Task<QuickEntryStatus> ShowAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the window.</summary>
    [BridgePost("hide")]
    Task<QuickEntryStatus> HideAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the window if it is closed, and closes it if it is open.</summary>
    [BridgePost("toggle")]
    Task<QuickEntryStatus> ToggleAsync(CancellationToken cancellationToken = default);

    /// <summary>The prompt. Fails with 409 when the app replaced the prompt with content of its own.</summary>
    [BridgeGet("prompt")]
    Task<QuickEntryPrompt> GetPromptAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the prompt: its placeholder, suggestions, busy state and response.</summary>
    [BridgePut("prompt")]
    Task<QuickEntryPrompt> SetPromptAsync(QuickEntryPromptInput prompt, CancellationToken cancellationToken = default);

    /// <summary>Clears the entry, the response and the busy state.</summary>
    [BridgePost("prompt/reset")]
    Task<QuickEntryPrompt> ResetPromptAsync(CancellationToken cancellationToken = default);

    /// <summary>Lights the glow around the screen's edge until <see cref="HideGlowAsync"/>.</summary>
    [BridgePost("glow/show")]
    Task ShowGlowAsync(CancellationToken cancellationToken = default);

    [BridgePost("glow/hide")]
    Task HideGlowAsync(CancellationToken cancellationToken = default);

    /// <summary>Lights the glow for a while, answering once it has gone out.</summary>
    [BridgePost("glow/pulse")]
    Task PulseGlowAsync(QuickEntryPulse pulse, CancellationToken cancellationToken = default);

    /// <summary>The user submitted a prompt. Also delivered to background.js when no page is listening.</summary>
    [BridgeEvent("quickentry.submitted")]
    Task<IAsyncDisposable> OnSubmittedAsync(Func<QuickEntrySubmission, Task> handler);

    /// <summary>The user chose a suggestion, just before it is submitted.</summary>
    [BridgeEvent("quickentry.suggestion")]
    Task<IAsyncDisposable> OnSuggestionAsync(Func<QuickEntrySubmission, Task> handler);

    /// <summary>The user pressed Escape in the prompt.</summary>
    [BridgeEvent("quickentry.cancelled")]
    Task<IAsyncDisposable> OnCancelledAsync(Func<QuickEntryPromptEvent, Task> handler);

    /// <summary>The user pressed the microphone button.</summary>
    [BridgeEvent("quickentry.microphone")]
    Task<IAsyncDisposable> OnMicrophoneAsync(Func<QuickEntryPromptEvent, Task> handler);

    /// <summary>The window opened, however it was opened.</summary>
    [BridgeEvent("quickentry.opened")]
    Task<IAsyncDisposable> OnOpenedAsync(Func<QuickEntryStatus, Task> handler);

    /// <summary>The window closed, however it was dismissed.</summary>
    [BridgeEvent("quickentry.closed")]
    Task<IAsyncDisposable> OnClosedAsync(Func<QuickEntryStatus, Task> handler);
}
