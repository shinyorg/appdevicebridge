using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Speech;

namespace Shiny.WebAppHost.Bridge.Speech;

public sealed class WebAppSpeechOptions
{
    /// <summary>
    /// Registers Shiny.Speech's on-device recognizer and synthesizer. Turn off to register your own instead — a cloud
    /// provider, or Whisper on Linux, which has no OS speech engine. The bridge uses whichever
    /// <see cref="ISpeechToTextService"/> and <see cref="ITextToSpeechService"/> are registered.
    /// </summary>
    public bool RegisterSpeechServices { get; set; } = true;
}

public static class SpeechBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/speech</c> and registers Shiny.Speech's on-device services for the platform — there is nothing
    /// else to call.
    /// <code>
    /// builder.AddSpeechBridge();
    /// </code>
    /// <para>
    /// Platform setup: <c>RECORD_AUDIO</c> on Android; <c>NSSpeechRecognitionUsageDescription</c> and
    /// <c>NSMicrophoneUsageDescription</c> on Apple platforms, plus the <c>com.apple.security.device.audio-input</c>
    /// entitlement where the app is sandboxed. Linux has no OS speech engine, so its endpoints answer 501.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddSpeechBridge(this MauiAppBuilder builder, Action<WebAppSpeechOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WebAppSpeechOptions();
        configure?.Invoke(options);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        // Android's microphone permission request goes through the AndroidPlatform that UseShiny registers.
        builder.EnsureShiny();
#elif MACOS
        builder.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        if (options.RegisterSpeechServices)
            builder.Services.AddSpeechServices();
#endif

        builder.Services.AddWebAppBridge<SpeechBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/speech</c> over <see cref="ISpeechToTextService"/> and <see cref="ITextToSpeechService"/>.
/// <code>
/// GET    /_bridge/speech/status
/// POST   /_bridge/speech/access      requests microphone and speech recognition permission
/// POST   /_bridge/speech/recognize   { "culture": "en-US", "silenceTimeoutMs": 2000, "timeoutMs": 15000 } → { "text": "…" }
/// GET    /_bridge/speech/listener    204 when not listening
/// POST   /_bridge/speech/listener    { "culture": "en-US", "keywords": ["stop"] } — results arrive as events
/// DELETE /_bridge/speech/listener
/// POST   /_bridge/speech/speak       { "text": "…", "culture", "voice", "pitch", "rate", "volume", "wait": true }
/// DELETE /_bridge/speech/speak
/// GET    /_bridge/speech/voices?culture=en-US
/// GET    /_bridge/speech/cultures    the cultures voices exist for
///
/// events: speech.partial, speech.result, speech.keyword, speech.ended, speech.spoken, speech.error
/// </code>
/// <para>
/// There is one microphone. A one-shot recognition and the listener exclude each other, and whichever comes second
/// answers 409 <c>microphone_busy</c>. The listener belongs to the page that started it and stops when that page's
/// last event stream closes, so a page that went away cannot leave the microphone open.
/// </para>
/// <para>
/// A new utterance interrupts the one playing rather than queueing behind it.
/// </para>
/// </summary>
public sealed class SpeechBridge : IWebAppBridge, IDisposable
{
    // Android's TextToSpeech.getMaxSpeechInputLength(); the other engines accept more, but a page should not depend on it.
    const int MaxTextLength = 4000;
    const int MaxKeywords = 32;
    const int MaxKeywordLength = 64;

    readonly ISpeechToTextService? stt;
    readonly ITextToSpeechService? tts;
    readonly WebAppEventHub events;
    readonly Lock gate = new();

    volatile MicrophoneUse use;
    ListenerResponse? listener;
    CancellationTokenSource? utterance;

    public SpeechBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.stt = services.GetOptionalService<ISpeechToTextService>();
        this.tts = services.GetOptionalService<ITextToSpeechService>();
        this.events = events;

        if (this.stt is not null)
        {
            this.stt.ResultReceived += this.OnResult;
            this.stt.KeywordHeard += this.OnKeyword;
            this.stt.Error += this.OnRecognitionError;
        }

        events.SubscribersChanged += this.OnSubscribersChanged;
    }

    public string Name => "speech";

    public bool IsSupported => this.stt?.IsSupported == true || this.tts?.IsSupported == true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/status", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapPost("/recognize", this.RecognizeAsync)
        .MapGet("/listener", this.ListenerAsync)
        .MapPost("/listener", this.StartListenerAsync)
        .MapDelete("/listener", this.StopListenerAsync)
        .MapPost("/speak", this.SpeakAsync)
        .MapDelete("/speak", this.StopSpeakingAsync)
        .MapGet("/voices", this.VoicesAsync)
        .MapGet("/cultures", this.CulturesAsync);

    // ---- recognition

    ValueTask StatusAsync(HttpContext context) => WebAppBridgeResults.Json(
        context,
        new SpeechStatusResponse(
            this.stt?.IsSupported == true,
            this.tts?.IsSupported == true,
            this.use == MicrophoneUse.Listening,
            this.use == MicrophoneUse.Recognizing,
            this.tts?.IsSpeaking == true
        ),
        SpeechBridgeJsonContext.Default.SpeechStatusResponse
    );

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        var access = await OnMainThread(s.RequestAccess);
        await WebAppBridgeResults.Json(context, new SpeechAccessResponse(access), SpeechBridgeJsonContext.Default.SpeechAccessResponse);
    }

    async ValueTask RecognizeAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, SpeechBridgeJsonContext.Default.RecognizeRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"culture\": \"en-US\", \"silenceTimeoutMs\": 2000, \"timeoutMs\": 15000 }, or no body.");
            return;
        }

        if (CreateRecognitionOptions(body?.Culture, body?.SilenceTimeoutMs, body?.PreferOnDevice, body?.Keywords, out var options) is { } error)
        {
            await WebAppBridgeResults.BadRequest(context, error);
            return;
        }

        if (!await EnsureAccessAsync(context, s))
            return;

        if (!this.TryClaim(MicrophoneUse.Recognizing))
        {
            await MicrophoneBusy(context);
            return;
        }

        try
        {
            // Without speech the recognizer can wait indefinitely, so the request has a ceiling of its own.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(body?.TimeoutMs ?? 15_000, 1_000, 60_000)));

            string? text;
            try
            {
                text = await OnMainThread(() => s.ListenUntilSilence(options, timeout.Token));
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                text = null;
            }
            catch (InvalidOperationException)
            {
                // Something outside the bridge — the native app — is already using the recognizer.
                await MicrophoneBusy(context);
                return;
            }

            await WebAppBridgeResults.Json(context, new RecognizeResponse(text), SpeechBridgeJsonContext.Default.RecognizeResponse);
        }
        finally
        {
            if (s.IsListening)
                await OnMainThread(s.Stop);

            this.Release(MicrophoneUse.Recognizing);
        }
    }

    ValueTask ListenerAsync(HttpContext context)
    {
        ListenerResponse? current;
        lock (this.gate)
            current = this.listener;

        return current is null
            ? WebAppBridgeResults.NoContent(context)
            : WebAppBridgeResults.Json(context, current, SpeechBridgeJsonContext.Default.ListenerResponse);
    }

    async ValueTask StartListenerAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        if (!this.events.HasSubscribers)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Open /_bridge/events first: dictation results arrive as events.");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, SpeechBridgeJsonContext.Default.ListenerRequest);
        if (!valid)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"culture\": \"en-US\", \"silenceTimeoutMs\": 2000, \"keywords\": [\"…\"] }, or no body.");
            return;
        }

        if (CreateRecognitionOptions(body?.Culture, body?.SilenceTimeoutMs, body?.PreferOnDevice, body?.Keywords, out var options) is { } error)
        {
            await WebAppBridgeResults.BadRequest(context, error);
            return;
        }

        if (!await EnsureAccessAsync(context, s))
            return;

        if (!this.TryClaim(MicrophoneUse.Listening))
        {
            await MicrophoneBusy(context);
            return;
        }

        try
        {
            await OnMainThread(() => s.Start(options));
        }
        catch (InvalidOperationException)
        {
            this.Release(MicrophoneUse.Listening);
            await MicrophoneBusy(context);
            return;
        }
        catch
        {
            this.Release(MicrophoneUse.Listening);
            throw;
        }

        var response = new ListenerResponse(options.Culture?.Name, options.Keywords);
        lock (this.gate)
            this.listener = response;

        // The page may have gone between the check above and now, and then nothing would ever close the microphone.
        if (!this.events.HasSubscribers)
            await this.EndListenerAsync("page_closed");

        await WebAppBridgeResults.Json(context, response, SpeechBridgeJsonContext.Default.ListenerResponse);
    }

    async ValueTask StopListenerAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true })
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        await this.EndListenerAsync("stopped");
        await WebAppBridgeResults.NoContent(context);
    }

    async Task EndListenerAsync(string reason)
    {
        lock (this.gate)
        {
            if (this.use != MicrophoneUse.Listening)
                return;

            // Held until the recognizer has actually stopped, so a new session cannot start into a closing one.
            this.use = MicrophoneUse.Stopping;
            this.listener = null;
        }

        try
        {
            if (this.stt is { IsListening: true } s)
                await OnMainThread(s.Stop);
        }
        catch (Exception ex)
        {
            this.PublishError("recognition", ex.Message);
        }
        finally
        {
            this.Release(MicrophoneUse.Stopping);
            this.events.Publish("speech.ended", new SpeechEndedEvent(reason), SpeechBridgeJsonContext.Default.SpeechEndedEvent);
        }
    }

    void OnResult(object? sender, SpeechRecognitionResult result)
    {
        if (this.use != MicrophoneUse.Listening)
            return;

        this.events.Publish(
            result.IsFinal ? "speech.result" : "speech.partial",
            new SpeechResultEvent(result.Text, result.Confidence),
            SpeechBridgeJsonContext.Default.SpeechResultEvent
        );
    }

    void OnKeyword(object? sender, string keyword)
    {
        if (this.use == MicrophoneUse.Listening)
            this.events.Publish("speech.keyword", new SpeechKeywordEvent(keyword), SpeechBridgeJsonContext.Default.SpeechKeywordEvent);
    }

    void OnRecognitionError(object? sender, SpeechRecognitionError error)
    {
        if (this.use != MicrophoneUse.Listening)
            return;

        this.PublishError("recognition", error.Message);

        // The service retries transient failures itself and gives up after repeated or unrecoverable ones. When it
        // has given up, the page hears that as the end of the session.
        if (this.stt?.IsListening == false)
            _ = this.EndListenerAsync("error");
    }

    void OnSubscribersChanged()
    {
        if (!this.events.HasSubscribers && this.use == MicrophoneUse.Listening)
            _ = this.EndListenerAsync("page_closed");
    }

    bool TryClaim(MicrophoneUse claim)
    {
        lock (this.gate)
        {
            if (this.use != MicrophoneUse.None)
                return false;

            this.use = claim;
            return true;
        }
    }

    void Release(MicrophoneUse claim)
    {
        lock (this.gate)
        {
            if (this.use == claim)
                this.use = MicrophoneUse.None;
        }
    }

    static async Task<bool> EnsureAccessAsync(HttpContext context, ISpeechToTextService service)
    {
        var access = await OnMainThread(service.RequestAccess);

        if (access == AccessState.Available)
            return true;

        if (access == AccessState.NotSupported)
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
        else
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", $"Speech recognition access is {access}.");

        return false;
    }

    static ValueTask MicrophoneBusy(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "microphone_busy",
        "The microphone is already in use. Stop the listener or wait for the recognition to finish."
    );

    /// <summary>The error message, or null with the options built.</summary>
    static string? CreateRecognitionOptions(string? cultureName, int? silenceTimeoutMs, bool? preferOnDevice, string[]? keywords, out SpeechRecognitionOptions options)
    {
        options = new SpeechRecognitionOptions();

        if (!TryGetCulture(cultureName, out var culture))
            return "Unknown culture. Expected a name such as \"en-US\".";

        if (keywords is not null
            && (keywords.Length > MaxKeywords || keywords.Any(x => String.IsNullOrWhiteSpace(x) || x.Length > MaxKeywordLength)))
            return $"Expected at most {MaxKeywords} keywords of at most {MaxKeywordLength} characters each.";

        options = options with
        {
            Culture = culture ?? options.Culture,
            SilenceTimeout = silenceTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 500, 10_000)) : options.SilenceTimeout,
            PreferOnDevice = preferOnDevice ?? options.PreferOnDevice,
            Keywords = keywords ?? options.Keywords
        };

        return null;
    }

    // ---- synthesis

    async ValueTask SpeakAsync(HttpContext context)
    {
        if (this.tts is not { IsSupported: true } t)
        {
            await WebAppBridgeResults.NotSupported(context, "Text-to-speech");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, SpeechBridgeJsonContext.Default.SpeakRequest);
        if (body?.Text is not { } text || String.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"text\": \"…\" }} of at most {MaxTextLength} characters.");
            return;
        }

        if (!TryGetCulture(body.Culture, out var culture))
        {
            await WebAppBridgeResults.BadRequest(context, "Unknown culture. Expected a name such as \"en-US\".");
            return;
        }

        if (body.Rate is < 0.25f or > 4f || body.Pitch is < 0.5f or > 2f || body.Volume is < 0f or > 1f)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a rate from 0.25 to 4, a pitch from 0.5 to 2 and a volume from 0 to 1.");
            return;
        }

        VoiceInfo? voice = null;
        if (body.Voice is { } voiceId)
        {
            var voices = await t.GetVoicesAsync(null, context.RequestAborted);
            voice = voices.FirstOrDefault(x => String.Equals(x.Id, voiceId, StringComparison.Ordinal));

            if (voice is null)
            {
                await WebAppBridgeResults.BadRequest(context, "Unknown voice. GET /_bridge/speech/voices lists them.");
                return;
            }
        }

        var options = new TextToSpeechOptions();
        options = options with
        {
            Culture = culture ?? options.Culture,
            Voice = voice ?? options.Voice,
            SpeechRate = body.Rate ?? options.SpeechRate,
            Pitch = body.Pitch ?? options.Pitch,
            Volume = body.Volume ?? options.Volume
        };

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref this.utterance, cancellation)?.Cancel();

        var speaking = this.SpeakUtteranceAsync(t, text, options, cancellation);

        if (body.Wait == false)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        // A page that navigates away mid-sentence should not leave the device talking.
        SpeakOutcome outcome;
        using (context.RequestAborted.Register(cancellation.Cancel))
            outcome = await speaking;

        if (outcome.Error is { } error)
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "speech_failed", error);
        else
            await WebAppBridgeResults.Json(context, new SpeakResponse(outcome.Completed), SpeechBridgeJsonContext.Default.SpeakResponse);
    }

    async Task<SpeakOutcome> SpeakUtteranceAsync(ITextToSpeechService service, string text, TextToSpeechOptions options, CancellationTokenSource cancellation)
    {
        var completed = false;
        string? error = null;

        try
        {
            if (service.IsSpeaking)
                await service.StopAsync();

            await service.SpeakAsync(text, options, cancellation.Token);
            completed = !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex.Message;
            this.PublishError("synthesis", ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref this.utterance, null, cancellation);
            this.events.Publish("speech.spoken", new SpeakResponse(completed), SpeechBridgeJsonContext.Default.SpeakResponse);
        }

        return new SpeakOutcome(completed, error);
    }

    async ValueTask StopSpeakingAsync(HttpContext context)
    {
        if (this.tts is not { IsSupported: true } t)
        {
            await WebAppBridgeResults.NotSupported(context, "Text-to-speech");
            return;
        }

        Interlocked.Exchange(ref this.utterance, null)?.Cancel();

        if (t.IsSpeaking)
            await t.StopAsync();

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask VoicesAsync(HttpContext context)
    {
        if (this.tts is not { IsSupported: true } t)
        {
            await WebAppBridgeResults.NotSupported(context, "Text-to-speech");
            return;
        }

        string? cultureName = context.Request.Query["culture"];
        if (!TryGetCulture(String.IsNullOrEmpty(cultureName) ? null : cultureName, out var culture))
        {
            await WebAppBridgeResults.BadRequest(context, "Unknown culture. Expected a name such as \"en-US\".");
            return;
        }

        var voices = await t.GetVoicesAsync(culture, context.RequestAborted);
        List<VoiceResponse> list = [.. voices.Select(x => new VoiceResponse(x.Id, x.Name, x.Culture?.Name))];

        await WebAppBridgeResults.Json(context, list, SpeechBridgeJsonContext.Default.ListVoiceResponse);
    }

    async ValueTask CulturesAsync(HttpContext context)
    {
        if (this.tts is not { IsSupported: true } t)
        {
            await WebAppBridgeResults.NotSupported(context, "Text-to-speech");
            return;
        }

        var voices = await t.GetVoicesAsync(null, context.RequestAborted);
        List<string> cultures =
        [
            .. voices
                .Select(x => x.Culture?.Name)
                .OfType<string>()
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        await WebAppBridgeResults.Json(context, cultures, SpeechBridgeJsonContext.Default.ListString);
    }

    // ---- helpers

    void PublishError(string source, string message)
        => this.events.Publish("speech.error", new SpeechErrorEvent(source, message), SpeechBridgeJsonContext.Default.SpeechErrorEvent);

    static bool TryGetCulture(string? name, out CultureInfo? culture)
    {
        culture = null;

        if (name is null)
            return true;

        if (name.Length is 0 or > 35)
            return false;

        try
        {
            culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>An absent body is valid and reads as null; a present one must parse.</summary>
    static async ValueTask<(bool Valid, T? Body)> ReadOptionalBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (context.Request.ContentLength is 0)
            return (true, null);

        var body = await WebAppBridgeResults.ReadBodyAsync(context, typeInfo);
        return (body is not null || context.Request.ContentLength is null, body);
    }

    /// <summary>Permission prompts are UI, and Android's recognizer has to be driven from the main thread.</summary>
    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    static Task OnMainThread(Func<Task> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    public void Dispose()
    {
        this.events.SubscribersChanged -= this.OnSubscribersChanged;
        Interlocked.Exchange(ref this.utterance, null)?.Cancel();

        if (this.stt is null)
            return;

        this.stt.ResultReceived -= this.OnResult;
        this.stt.KeywordHeard -= this.OnKeyword;
        this.stt.Error -= this.OnRecognitionError;

        if (this.use == MicrophoneUse.Listening)
            _ = this.stt.Stop();
    }

    enum MicrophoneUse
    {
        None,
        Recognizing,
        Listening,
        Stopping
    }

    readonly record struct SpeakOutcome(bool Completed, string? Error);
}

public sealed record SpeechStatusResponse(bool RecognitionSupported, bool SynthesisSupported, bool Listening, bool Recognizing, bool Speaking);
public sealed record SpeechAccessResponse(AccessState Access);
public sealed record RecognizeRequest(string? Culture, int? SilenceTimeoutMs, int? TimeoutMs, bool? PreferOnDevice, string[]? Keywords);
public sealed record RecognizeResponse(string? Text);
public sealed record ListenerRequest(string? Culture, int? SilenceTimeoutMs, bool? PreferOnDevice, string[]? Keywords);
public sealed record ListenerResponse(string? Culture, string[]? Keywords);
public sealed record SpeakRequest(string? Text, string? Culture, string? Voice, float? Pitch, float? Rate, float? Volume, bool? Wait);
public sealed record SpeakResponse(bool Completed);
public sealed record VoiceResponse(string Id, string Name, string? Culture);

public sealed record SpeechResultEvent(string Text, float? Confidence);
public sealed record SpeechKeywordEvent(string Keyword);
public sealed record SpeechEndedEvent(string Reason);
public sealed record SpeechErrorEvent(string Source, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SpeechStatusResponse))]
[JsonSerializable(typeof(SpeechAccessResponse))]
[JsonSerializable(typeof(RecognizeRequest))]
[JsonSerializable(typeof(RecognizeResponse))]
[JsonSerializable(typeof(ListenerRequest))]
[JsonSerializable(typeof(ListenerResponse))]
[JsonSerializable(typeof(SpeakRequest))]
[JsonSerializable(typeof(SpeakResponse))]
[JsonSerializable(typeof(List<VoiceResponse>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(SpeechResultEvent))]
[JsonSerializable(typeof(SpeechKeywordEvent))]
[JsonSerializable(typeof(SpeechEndedEvent))]
[JsonSerializable(typeof(SpeechErrorEvent))]
partial class SpeechBridgeJsonContext : JsonSerializerContext;
