using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AiConversation.Client;

/// <summary>
/// Conversations with the app's AI model: persisted sessions that stream their replies as events, ask typed questions,
/// remember things across sessions, and can be talked to through the voice loop. The model, its key, its tools and its
/// prompts are the app's; the page never sees them.
/// <para>
/// A session's events — <see cref="OnDeltaAsync"/> and the rest — come from sessions the bridge has open. Starting,
/// reading or sending to a session opens it, and the bridge keeps the most recent few open.
/// </para>
/// </summary>
[BridgeClient("ai", typeof(AiConversationJsonContext))]
public interface IAiConversationBridge
{
    /// <summary>What the app registered.</summary>
    [BridgeGet]
    Task<AiStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    // ---- sessions

    /// <summary>Stored sessions, most recently active first, filtered by text in their messages, a tag or a profile.</summary>
    [BridgeGet("sessions")]
    Task<IReadOnlyList<AiSessionSummary>> GetSessionsAsync(string? text = null, string? tag = null, string? profile = null, int skip = 0, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Starts a session.</summary>
    [BridgePost("sessions")]
    Task<AiSession> StartSessionAsync(AiStartSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The most recently active session — to carry on where the user left off — or null when there are none.</summary>
    [BridgeGet("sessions/latest")]
    Task<AiSession?> GetLatestSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>A session. Fails with 404 when there is none.</summary>
    [BridgeGet("sessions/{id}")]
    Task<AiSession> GetSessionAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a session, its messages and its memory.</summary>
    [BridgeDelete("sessions/{id}")]
    Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Renames a session.</summary>
    [BridgePut("sessions/{id}/title")]
    Task<AiSession> SetTitleAsync(string id, AiTitleRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message, replacing any turn still running. The reply streams through <see cref="OnDeltaAsync"/> and ends
    /// with <see cref="OnTurnEndedAsync"/>; with <see cref="AiSendRequest.Wait"/>, this answers with it too.
    /// </summary>
    [BridgePost("sessions/{id}/messages")]
    Task<AiSendResult> SendAsync(string id, AiSendRequest request, CancellationToken cancellationToken = default);

    /// <summary>The stored history, newest page first: pass the oldest sequence of a page as <paramref name="before"/> for the one before it.</summary>
    [BridgeGet("sessions/{id}/messages")]
    Task<AiMessagePage> GetMessagesAsync(string id, long? before = null, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>Interrupts the running turn, keeping what was written. Fails with 409 <c>no_turn</c> when none is running.</summary>
    [BridgeDelete("sessions/{id}/turn")]
    Task InterruptAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Folds older messages into the session's summary now, rather than when the context fills.</summary>
    [BridgePost("sessions/{id}/compact")]
    Task CompactAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>An image attached to a message, by its part's <see cref="AiMessagePart.AttachmentId"/>.</summary>
    [BridgeGet("attachments")]
    Task<byte[]> GetAttachmentAsync(string id, CancellationToken cancellationToken = default);

    [BridgeEvent("ai.turn.started")]
    Task<IAsyncDisposable> OnTurnStartedAsync(Func<AiTurnStarted, Task> handler);

    [BridgeEvent("ai.delta")]
    Task<IAsyncDisposable> OnDeltaAsync(Func<AiTextDelta, Task> handler);

    [BridgeEvent("ai.tool")]
    Task<IAsyncDisposable> OnToolCallAsync(Func<AiToolCall, Task> handler);

    [BridgeEvent("ai.questions")]
    Task<IAsyncDisposable> OnQuestionsAsync(Func<AiQuestionsAsked, Task> handler);

    [BridgeEvent("ai.turn.ended")]
    Task<IAsyncDisposable> OnTurnEndedAsync(Func<AiTurnEnded, Task> handler);

    /// <summary>A message was stored or changed.</summary>
    [BridgeEvent("ai.message")]
    Task<IAsyncDisposable> OnMessageAsync(Func<AiMessageChanged, Task> handler);

    [BridgeEvent("ai.state")]
    Task<IAsyncDisposable> OnStateAsync(Func<AiStateChanged, Task> handler);

    [BridgeEvent("ai.usage")]
    Task<IAsyncDisposable> OnUsageAsync(Func<AiUsageChanged, Task> handler);

    [BridgeEvent("ai.title")]
    Task<IAsyncDisposable> OnTitleAsync(Func<AiTitleChanged, Task> handler);

    [BridgeEvent("ai.compacted")]
    Task<IAsyncDisposable> OnCompactedAsync(Func<AiCompacted, Task> handler);

    /// <summary>The assistant changed something it remembers.</summary>
    [BridgeEvent("ai.memory")]
    Task<IAsyncDisposable> OnMemoryAsync(Func<AiMemoryChanged, Task> handler);

    [BridgeEvent("ai.error")]
    Task<IAsyncDisposable> OnErrorAsync(Func<AiSessionError, Task> handler);

    // ---- memory

    /// <summary>What the assistant remembers, optionally in one scope.</summary>
    [BridgeGet("memory")]
    Task<IReadOnlyList<AiMemoryDocument>> GetMemoryAsync(AiMemoryScope? scope = null, string? scopeKey = null, CancellationToken cancellationToken = default);

    /// <summary>Writes a memory document, as the user.</summary>
    [BridgePut("memory")]
    Task<AiMemoryDocument> SaveMemoryAsync(AiMemorySaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Forgets one memory document.</summary>
    [BridgeDelete("memory")]
    Task DeleteMemoryAsync(AiMemoryScope scope, string path, string? scopeKey = null, CancellationToken cancellationToken = default);

    // ---- voice

    /// <summary>Whether the voice loop is there, and where it is.</summary>
    [BridgeGet("voice")]
    Task<AiVoiceStatus> GetVoiceAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests microphone and speech recognition permission.</summary>
    [BridgePost("voice/access")]
    Task<AiAccessResult> RequestVoiceAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches the voice loop to a session. Listen to <see cref="OnVoiceStateAsync"/> first: without a listener it fails
    /// with 409 <c>not_listening</c>, and it detaches once the last listener is gone, so a page that went away cannot
    /// leave the microphone listening for a wake word.
    /// </summary>
    [BridgePost("voice")]
    Task<AiVoiceStatus> AttachVoiceAsync(AiVoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Detaches the voice loop and releases the microphone.</summary>
    [BridgeDelete("voice")]
    Task DetachVoiceAsync(CancellationToken cancellationToken = default);

    /// <summary>Listens now, without the wake word, interrupting any reply.</summary>
    [BridgePost("voice/talk")]
    Task PushToTalkAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the reply being spoken, without listening.</summary>
    [BridgePost("voice/stop")]
    Task StopSpeakingAsync(CancellationToken cancellationToken = default);

    [BridgePost("voice/pause")]
    Task PauseVoiceAsync(CancellationToken cancellationToken = default);

    [BridgePost("voice/resume")]
    Task ResumeVoiceAsync(CancellationToken cancellationToken = default);

    [BridgeEvent("ai.voice.state")]
    Task<IAsyncDisposable> OnVoiceStateAsync(Func<AiVoiceStateChanged, Task> handler);

    [BridgeEvent("ai.voice.transcript")]
    Task<IAsyncDisposable> OnTranscriptAsync(Func<AiTranscript, Task> handler);

    [BridgeEvent("ai.voice.speaking")]
    Task<IAsyncDisposable> OnSpeakingAsync(Func<AiSpeaking, Task> handler);

    [BridgeEvent("ai.voice.interrupted")]
    Task<IAsyncDisposable> OnInterruptedAsync(Func<AiSpeechInterrupted, Task> handler);

    /// <summary>The microphone and speech levels about 20 times a second.</summary>
    [BridgeEvent("ai.voice.level")]
    Task<IAsyncDisposable> OnVoiceLevelAsync(Func<AiVoiceLevel, Task> handler);

    [BridgeEvent("ai.voice.error")]
    Task<IAsyncDisposable> OnVoiceErrorAsync(Func<AiVoiceError, Task> handler);
}
