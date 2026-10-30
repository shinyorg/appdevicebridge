using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AiConversation.Client;

public enum AiSessionState { Idle, Thinking, Responding, AwaitingUser, Compacting }

/// <summary>How a turn ended.</summary>
public enum AiTurnOutcome
{
    Completed,

    /// <summary>The AI asked something; <see cref="AiSession.PendingQuestions"/> holds it.</summary>
    AwaitingUser,

    /// <summary>Interrupted part way; what was heard is kept.</summary>
    Interrupted,

    /// <summary>A newer message replaced it before it finished.</summary>
    Superseded,

    Failed
}

public enum AiMessageRole { User, Assistant, Tool, System, Summary }

public enum AiMessageSource { Typed, Voice, ChoiceAnswer, Camera, Interceptor, Compaction, MemoryFlush, Model }

public enum AiMessageStatus { Complete, Streaming, Interrupted, Failed }

public enum AiPartKind { Text, ToolCall, ToolResult, Answer, Image }

public enum AiContextPressure { Normal, Warning, Critical }

/// <summary>What happens to an image after the turn that sent it.</summary>
public enum AiImageRetention
{
    /// <summary>Stored with the message, but not sent to the model again. The default.</summary>
    DropAfterTurn,

    /// <summary>Stored, and sent with every later turn.</summary>
    KeepInContext,

    /// <summary>Sent once and never stored.</summary>
    DontStore
}

/// <summary>Whose memory a document is: one session's, one profile's, or every session's.</summary>
public enum AiMemoryScope { Session, Profile, Global }

public enum AiMemoryAuthor { Assistant, User, App }

public enum AiMemoryChange { Created, Updated, Deleted, Renamed }

public enum AiVoiceState { Off, WaitingForWakeWord, Listening, FollowUp, Thinking, Speaking, Paused }

/// <summary>How the user can talk over a reply.</summary>
public enum AiInterruptionMode
{
    /// <summary>They cannot; the microphone is muted while the AI speaks.</summary>
    Off,

    /// <summary>Only a quiet word ("stop", "cancel") said on its own. The default.</summary>
    QuietWords,

    /// <summary>Anything they say.</summary>
    Full
}

/// <summary>What the voice loop does to acknowledge it heard the user.</summary>
public enum AiAcknowledgement { None, AudioBlip, LessWordy, Full }

public enum AiInterruptionReason { QuietWord, Answer, NewSpeech, PushToTalk, Stop }

/// <param name="Supported">Whether the app registered AI conversations.</param>
/// <param name="VoiceSupported">Whether the app registered the voice loop.</param>
/// <param name="OpenSessions">Sessions the bridge holds open, and raises events for.</param>
public sealed record AiStatus(bool Supported, bool VoiceSupported, int OpenSessions);

/// <summary>A new session. Every field is optional.</summary>
/// <param name="Profile">Selects the app's tools, memory and prompts for a kind of conversation.</param>
/// <param name="ExcludeFromSearch">Keep it out of conversation search and long-term memory.</param>
public sealed record AiStartSessionRequest(
    string? Title = null,
    string? Profile = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool ExcludeFromSearch = false
);

/// <summary>Something the AI asked, with the answers to pick from when it gave some.</summary>
public sealed record AiQuestion(string Id, string Text, IReadOnlyList<AiChoice>? Choices = null, bool AllowMultiple = false);

public sealed record AiChoice(string Id, string Label);

/// <summary>Everything billed in a session.</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, long CachedInputTokens, long ReasoningTokens, long TotalTokens, int RequestCount);

/// <summary>How full the model's context window is.</summary>
/// <param name="IsEstimate">True when counted locally rather than reported by the model.</param>
public sealed record AiContext(long CurrentTokens, long? MaxTokens, double? PercentUsed, AiContextPressure? Pressure, bool IsEstimate);

/// <summary>A session as it is right now.</summary>
public sealed record AiSession(
    string Id,
    string? Title,
    DateTimeOffset Started,
    AiSessionState State,
    IReadOnlyList<AiQuestion> PendingQuestions,
    AiUsage Usage,
    AiContext Context,
    string? Profile,
    IReadOnlyList<string> Tags
);

/// <summary>A stored session, without loading it.</summary>
public sealed record AiSessionSummary(
    string Id,
    string? Title,
    DateTimeOffset Started,
    DateTimeOffset LastActivity,
    int MessageCount,
    string? Profile,
    IReadOnlyList<string> Tags,
    AiUsage Usage
);

public sealed record AiTitleRequest(string Title);

/// <summary>An image to send, from a file root — one the page wrote, a photo, a camera capture.</summary>
/// <param name="Description">What it shows, for models and history that cannot see it.</param>
public sealed record AiImageRef(BridgeFile File, string? Description = null, AiImageRetention Retention = AiImageRetention.DropAfterTurn);

/// <summary>An answer to one of <see cref="AiSession.PendingQuestions"/> by its choices.</summary>
public sealed record AiAnswer(string QuestionId, IReadOnlyList<string> ChoiceIds);

/// <summary>
/// A message to the AI: <see cref="Text"/>, or an <see cref="Answer"/> to a question with choices. A question without
/// choices is answered with text.
/// </summary>
/// <param name="Text">At most 32,000 characters.</param>
/// <param name="Images">At most 4, each a JPEG, PNG, WebP, HEIC or GIF of at most 10 MB.</param>
/// <param name="Instructions">Extra direction for this turn only, not shown in the history.</param>
/// <param name="Wait">Answer once the turn ends, with its reply, rather than as soon as it starts.</param>
public sealed record AiSendRequest(
    string? Text = null,
    AiAnswer? Answer = null,
    IReadOnlyList<AiImageRef>? Images = null,
    IReadOnlyList<string>? Instructions = null,
    bool Wait = false
);

/// <summary>One part of a message. Which fields are set depends on <see cref="Kind"/>.</summary>
/// <param name="Displayed">Shown in the conversation.</param>
/// <param name="Spoken">Read aloud by the voice loop.</param>
/// <param name="Text">The text; a tool call's arguments as JSON; a tool's result; an answer's labels.</param>
/// <param name="AttachmentId">An image's id for <see cref="IAiConversationBridge.GetAttachmentAsync"/>.</param>
public sealed record AiMessagePart(
    AiPartKind Kind,
    bool Displayed,
    bool Spoken,
    string? Text = null,
    string? Name = null,
    string? CallId = null,
    bool IsError = false,
    string? AttachmentId = null,
    string? MediaType = null,
    string? QuestionId = null,
    IReadOnlyList<string>? ChoiceIds = null
);

/// <param name="Text">What to show.</param>
/// <param name="SpokenText">What was actually heard, when it was spoken and interrupted.</param>
/// <param name="Compacted">Folded into a summary; no longer sent to the model.</param>
public sealed record AiMessage(
    string Id,
    string SessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    AiMessageRole Role,
    AiMessageSource Source,
    AiMessageStatus Status,
    string? Text,
    string? SpokenText,
    IReadOnlyList<AiMessagePart> Parts,
    bool Compacted
);

/// <param name="Messages">Oldest first.</param>
/// <param name="HasMore">Older messages remain; ask again with the first one's sequence.</param>
public sealed record AiMessagePage(IReadOnlyList<AiMessage> Messages, bool HasMore);

/// <summary>A turn that started, or with <see cref="AiSendRequest.Wait"/>, one that ended.</summary>
/// <param name="Outcome">Null when it is still running.</param>
public sealed record AiSendResult(
    string TurnId,
    AiTurnOutcome? Outcome = null,
    AiMessage? Reply = null,
    IReadOnlyList<AiQuestion>? Questions = null,
    string? Error = null
);

public sealed record AiTurnStarted(string SessionId, string TurnId, AiMessage UserMessage);

/// <summary>The next piece of the reply as the model writes it.</summary>
public sealed record AiTextDelta(string SessionId, string TurnId, string Text);

/// <summary>The model called one of the app's tools, or one finished.</summary>
/// <param name="Silent">A tool the app marked as not worth showing.</param>
public sealed record AiToolCall(string SessionId, string TurnId, string CallId, string Name, bool Completed, bool IsError, bool Silent);

public sealed record AiQuestionsAsked(string SessionId, string TurnId, IReadOnlyList<AiQuestion> Questions);

/// <param name="SpokenText">What was heard before an interruption.</param>
public sealed record AiTurnEnded(string SessionId, string TurnId, AiTurnOutcome Outcome, string? ReplyMessageId = null, string? SpokenText = null, string? Error = null);

/// <param name="Updated">True when an existing message changed — finished streaming, interrupted, compacted.</param>
public sealed record AiMessageChanged(string SessionId, AiMessage Message, bool Updated);

public sealed record AiStateChanged(string SessionId, AiSessionState State);

public sealed record AiUsageChanged(string SessionId, AiUsage Usage, AiContext Context);

public sealed record AiTitleChanged(string SessionId, string? Title);

/// <param name="WasHardTrim">Messages were dropped without a summary, because summarizing failed or there was no room.</param>
public sealed record AiCompacted(string SessionId, int CompactedCount, bool WasHardTrim);

public sealed record AiMemoryChanged(string SessionId, AiMemoryDocument Document, AiMemoryChange Change);

public sealed record AiSessionError(string SessionId, string Message);

/// <summary>Something the assistant remembers, as a markdown document at a path.</summary>
/// <param name="ScopeKey">The session id or profile name; null for <see cref="AiMemoryScope.Global"/>.</param>
public sealed record AiMemoryDocument(
    string Path,
    AiMemoryScope Scope,
    string? ScopeKey,
    string? Description,
    string Content,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    AiMemoryAuthor UpdatedBy,
    long Version
);

/// <param name="ScopeKey">The session id or profile name; null for <see cref="AiMemoryScope.Global"/>.</param>
public sealed record AiMemorySaveRequest(AiMemoryScope Scope, string Path, string Content, string? ScopeKey = null, string? Description = null);

public sealed record AiAccessResult(AccessState Access);

/// <param name="Supported">Whether the app registered the voice loop.</param>
/// <param name="SessionId">The session it is attached to.</param>
public sealed record AiVoiceStatus(bool Supported, AiVoiceState State, string? SessionId);

/// <summary>Attaches the voice loop to a session. Every field but the session is optional.</summary>
/// <param name="WakeWord">Listen for this before each conversation; without one, the loop starts on <see cref="IAiConversationBridge.PushToTalkAsync"/>.</param>
/// <param name="QuietWords">What stops a reply under <see cref="AiInterruptionMode.QuietWords"/>; at most 32.</param>
/// <param name="FollowUpTimeoutSeconds">How long it listens for an answer to a question before needing the wake word again.</param>
/// <param name="Culture">Recognition and speech, such as <c>en-US</c>.</param>
public sealed record AiVoiceRequest(
    string SessionId,
    string? WakeWord = null,
    AiInterruptionMode? Interruption = null,
    IReadOnlyList<string>? QuietWords = null,
    double? FollowUpTimeoutSeconds = null,
    AiAcknowledgement? Acknowledgement = null,
    string? Culture = null
);

public sealed record AiVoiceStateChanged(AiVoiceState State);

/// <param name="Final">False while the user is still speaking.</param>
public sealed record AiTranscript(string Text, bool Final);

/// <summary>The sentence the voice loop is speaking now.</summary>
public sealed record AiSpeaking(string Text);

public sealed record AiSpeechInterrupted(AiInterruptionReason Reason, string? HeardText, string? SpokenText);

/// <param name="Input">The microphone, 0–1.</param>
/// <param name="Output">The speech, 0–1.</param>
public sealed record AiVoiceLevel(double Input, double Output);

public sealed record AiVoiceError(string Message);

/// <summary>Serialization for every AI conversation contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AiStatus))]
[JsonSerializable(typeof(AiStartSessionRequest))]
[JsonSerializable(typeof(AiSession))]
[JsonSerializable(typeof(IReadOnlyList<AiSessionSummary>))]
[JsonSerializable(typeof(AiTitleRequest))]
[JsonSerializable(typeof(AiSendRequest))]
[JsonSerializable(typeof(AiSendResult))]
[JsonSerializable(typeof(AiMessagePage))]
[JsonSerializable(typeof(AiTurnStarted))]
[JsonSerializable(typeof(AiTextDelta))]
[JsonSerializable(typeof(AiToolCall))]
[JsonSerializable(typeof(AiQuestionsAsked))]
[JsonSerializable(typeof(AiTurnEnded))]
[JsonSerializable(typeof(AiMessageChanged))]
[JsonSerializable(typeof(AiStateChanged))]
[JsonSerializable(typeof(AiUsageChanged))]
[JsonSerializable(typeof(AiTitleChanged))]
[JsonSerializable(typeof(AiCompacted))]
[JsonSerializable(typeof(AiMemoryChanged))]
[JsonSerializable(typeof(AiSessionError))]
[JsonSerializable(typeof(AiMemoryDocument))]
[JsonSerializable(typeof(IReadOnlyList<AiMemoryDocument>))]
[JsonSerializable(typeof(AiMemorySaveRequest))]
[JsonSerializable(typeof(AiAccessResult))]
[JsonSerializable(typeof(AiVoiceStatus))]
[JsonSerializable(typeof(AiVoiceRequest))]
[JsonSerializable(typeof(AiVoiceStateChanged))]
[JsonSerializable(typeof(AiTranscript))]
[JsonSerializable(typeof(AiSpeaking))]
[JsonSerializable(typeof(AiSpeechInterrupted))]
[JsonSerializable(typeof(AiVoiceLevel))]
[JsonSerializable(typeof(AiVoiceError))]
public partial class AiConversationJsonContext : JsonSerializerContext;
