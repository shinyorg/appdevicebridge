using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Speech.Client;

/// <summary>Why dictation ended.</summary>
public enum SpeechEndReason
{
    /// <summary>The page stopped it.</summary>
    Stopped,

    /// <summary>The page stopped listening to events, so nothing was left to receive results.</summary>
    PageClosed,

    /// <summary>The recognizer gave up after repeated or unrecoverable failures.</summary>
    Error
}

public enum SpeechErrorSource { Recognition, Synthesis }

public sealed record SpeechStatus(bool RecognitionSupported, bool SynthesisSupported, bool Listening, bool Recognizing, bool Speaking);

public sealed record SpeechAccessResult(AccessState Access);

/// <param name="Culture">A culture name such as <c>en-US</c>; the device's when null.</param>
/// <param name="SilenceTimeoutMs">How much quiet ends it, 500–10,000 ms.</param>
/// <param name="TimeoutMs">The longest it waits in all, 1,000–60,000 ms; 15 seconds when null.</param>
/// <param name="PreferOnDevice">Keep audio on the device where the platform allows.</param>
/// <param name="Keywords">At most 32, each at most 64 characters.</param>
public sealed record RecognizeRequest(
    string? Culture = null,
    int? SilenceTimeoutMs = null,
    int? TimeoutMs = null,
    bool? PreferOnDevice = null,
    IReadOnlyList<string>? Keywords = null
);

/// <param name="Text">What was heard; null when nothing was.</param>
public sealed record RecognizeResult(string? Text);

/// <param name="Culture">A culture name such as <c>en-US</c>; the device's when null.</param>
/// <param name="SilenceTimeoutMs">How much quiet ends a phrase, 500–10,000 ms.</param>
/// <param name="PreferOnDevice">Keep audio on the device where the platform allows.</param>
/// <param name="Keywords">Words that raise <see cref="ISpeechBridge.OnKeywordAsync"/>. At most 32, each at most 64 characters.</param>
public sealed record SpeechListenerRequest(
    string? Culture = null,
    int? SilenceTimeoutMs = null,
    bool? PreferOnDevice = null,
    IReadOnlyList<string>? Keywords = null
);

public sealed record SpeechListener(string? Culture, IReadOnlyList<string>? Keywords);

/// <param name="Text">At most 4,000 characters.</param>
/// <param name="Culture">A culture name such as <c>en-US</c>.</param>
/// <param name="Voice">A voice's <see cref="SpeechVoice.Id"/>.</param>
/// <param name="Pitch">0.5–2.</param>
/// <param name="Rate">0.25–4.</param>
/// <param name="Volume">0–1.</param>
/// <param name="Wait">Return once speech ends rather than straight away.</param>
public sealed record SpeakRequest(
    string Text,
    string? Culture = null,
    string? Voice = null,
    float? Pitch = null,
    float? Rate = null,
    float? Volume = null,
    bool Wait = true
);

/// <param name="Completed">False when it was interrupted.</param>
public sealed record SpeakResult(bool Completed);

public sealed record SpeechVoice(string Id, string Name, string? Culture);

/// <param name="Confidence">0–1, where the platform reports one.</param>
public sealed record SpeechRecognized(string Text, float? Confidence);

public sealed record SpeechKeyword(string Keyword);

public sealed record SpeechEnded(SpeechEndReason Reason);

public sealed record SpeechError(SpeechErrorSource Source, string Message);

/// <summary>Serialization for every speech contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SpeechStatus))]
[JsonSerializable(typeof(SpeechAccessResult))]
[JsonSerializable(typeof(RecognizeRequest))]
[JsonSerializable(typeof(RecognizeResult))]
[JsonSerializable(typeof(SpeechListenerRequest))]
[JsonSerializable(typeof(SpeechListener))]
[JsonSerializable(typeof(SpeakRequest))]
[JsonSerializable(typeof(SpeakResult))]
[JsonSerializable(typeof(IReadOnlyList<SpeechVoice>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(SpeechRecognized))]
[JsonSerializable(typeof(SpeechKeyword))]
[JsonSerializable(typeof(SpeechEnded))]
[JsonSerializable(typeof(SpeechError))]
public partial class SpeechJsonContext : JsonSerializerContext;
