using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Speech.Client;
using Shiny.Net.HttpServer;
using Shiny.Speech;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Speech;

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
    /// bridge.AddSpeechBridge();
    /// </code>
    /// <para>
    /// Platform setup: <c>RECORD_AUDIO</c> on Android; <c>NSSpeechRecognitionUsageDescription</c> and
    /// <c>NSMicrophoneUsageDescription</c> on Apple platforms, plus the <c>com.apple.security.device.audio-input</c>
    /// entitlement where the app is sandboxed. Linux has no OS speech engine, so its endpoints answer 501.
    /// </para>
    /// </summary>
    public static TBuilder AddSpeechBridge<TBuilder>(this TBuilder bridge, Action<WebAppSpeechOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var options = new WebAppSpeechOptions();
        configure?.Invoke(options);

#if MACOS
        bridge.Services.EnsureShinyCore();
#endif

#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
        if (options.RegisterSpeechServices)
            bridge.Services.AddSpeechServices();
#endif

        bridge.AddBridge<SpeechBridge>();
        return bridge;
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
/// answers 409 <c>microphone_busy</c>. The listener belongs to whoever listens for <c>speech.result</c> or
/// <c>speech.partial</c>: it needs one to start, and stops once the last of them is gone, so a page that went away
/// cannot leave the microphone open.
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
    readonly WebAppEventSource<SpeechRecognized> results = new();
    readonly WebAppEventSource<SpeechRecognized> partials = new();
    readonly WebAppEventSource<SpeechKeyword> keywords = new();
    readonly WebAppEventSource<SpeechEnded> ended = new();
    readonly WebAppEventSource<SpeakResult> spoken = new();
    readonly WebAppEventSource<SpeechError> errors = new();
    readonly Lock gate = new();

    volatile MicrophoneUse use;
    SpeechListener? listener;
    CancellationTokenSource? utterance;

    readonly IWebAppMainThread mainThread;

    public SpeechBridge(IServiceProvider services)
    {
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
        this.stt = services.GetOptionalService<ISpeechToTextService>();
        this.tts = services.GetOptionalService<ITextToSpeechService>();

        if (this.stt is not null)
        {
            this.stt.ResultReceived += this.OnResult;
            this.stt.KeywordHeard += this.OnKeyword;
            this.stt.Error += this.OnRecognitionError;
        }
    }

    public string Name => "speech";

    public bool IsSupported => this.stt?.IsSupported == true || this.tts?.IsSupported == true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("speech.result", ct => this.results.ListenAsync(this.OnDictationListenerStopped, ct), SpeechJsonContext.Default.SpeechRecognized)
        .MapEvent("speech.partial", ct => this.partials.ListenAsync(this.OnDictationListenerStopped, ct), SpeechJsonContext.Default.SpeechRecognized)
        .MapEvent("speech.keyword", this.keywords.ListenAsync, SpeechJsonContext.Default.SpeechKeyword)
        .MapEvent("speech.ended", this.ended.ListenAsync, SpeechJsonContext.Default.SpeechEnded)
        .MapEvent("speech.spoken", this.spoken.ListenAsync, SpeechJsonContext.Default.SpeakResult)
        .MapEvent("speech.error", this.errors.ListenAsync, SpeechJsonContext.Default.SpeechError)
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
        new SpeechStatus(
            this.stt?.IsSupported == true,
            this.tts?.IsSupported == true,
            this.use == MicrophoneUse.Listening,
            this.use == MicrophoneUse.Recognizing,
            this.tts?.IsSpeaking == true
        ),
        SpeechJsonContext.Default.SpeechStatus
    );

    async ValueTask RequestAccessAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        var access = await OnMainThread(s.RequestAccess);
        await WebAppBridgeResults.Json(context, new SpeechAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)), SpeechJsonContext.Default.SpeechAccessResult);
    }

    async ValueTask RecognizeAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, SpeechJsonContext.Default.RecognizeRequest);
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

            await WebAppBridgeResults.Json(context, new RecognizeResult(text), SpeechJsonContext.Default.RecognizeResult);
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
        SpeechListener? current;
        lock (this.gate)
            current = this.listener;

        return current is null
            ? WebAppBridgeResults.NoContent(context)
            : WebAppBridgeResults.Json(context, current, SpeechJsonContext.Default.SpeechListener);
    }

    async ValueTask StartListenerAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        if (!this.HasDictationListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen for speech.result or speech.partial first: dictation results arrive as events.");
            return;
        }

        var (valid, body) = await ReadOptionalBodyAsync(context, SpeechJsonContext.Default.SpeechListenerRequest);
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

        var response = new SpeechListener(options.Culture?.Name, options.Keywords);
        lock (this.gate)
            this.listener = response;

        // The last listener may have gone between the check above and now, and then nothing would ever close the microphone.
        if (!this.HasDictationListeners)
            await this.EndListenerAsync(SpeechEndReason.PageClosed);

        await WebAppBridgeResults.Json(context, response, SpeechJsonContext.Default.SpeechListener);
    }

    async ValueTask StopListenerAsync(HttpContext context)
    {
        if (this.stt is not { IsSupported: true })
        {
            await WebAppBridgeResults.NotSupported(context, "Speech recognition");
            return;
        }

        await this.EndListenerAsync(SpeechEndReason.Stopped);
        await WebAppBridgeResults.NoContent(context);
    }

    async Task EndListenerAsync(SpeechEndReason reason)
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
            this.PublishError(SpeechErrorSource.Recognition, ex.Message);
        }
        finally
        {
            this.Release(MicrophoneUse.Stopping);
            this.ended.Publish(new SpeechEnded(reason));
        }
    }

    void OnResult(object? sender, SpeechRecognitionResult result)
    {
        if (this.use != MicrophoneUse.Listening)
            return;

        (result.IsFinal ? this.results : this.partials).Publish(new SpeechRecognized(result.Text, result.Confidence));
    }

    void OnKeyword(object? sender, string keyword)
    {
        if (this.use == MicrophoneUse.Listening)
            this.keywords.Publish(new SpeechKeyword(keyword));
    }

    void OnRecognitionError(object? sender, SpeechRecognitionError error)
    {
        if (this.use != MicrophoneUse.Listening)
            return;

        this.PublishError(SpeechErrorSource.Recognition, error.Message);

        // The service retries transient failures itself and gives up after repeated or unrecoverable ones. When it
        // has given up, the page hears that as the end of the session.
        if (this.stt?.IsListening == false)
            _ = this.EndListenerAsync(SpeechEndReason.Error);
    }

    bool HasDictationListeners => this.results.HasListeners || this.partials.HasListeners;

    /// <summary>Dictation is for whoever hears its results; with the last of them gone, the microphone closes.</summary>
    void OnDictationListenerStopped(int remaining)
    {
        if (remaining == 0 && !this.HasDictationListeners && this.use == MicrophoneUse.Listening)
            _ = this.EndListenerAsync(SpeechEndReason.PageClosed);
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

    async Task<bool> EnsureAccessAsync(HttpContext context, ISpeechToTextService service)
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
    static string? CreateRecognitionOptions(string? cultureName, int? silenceTimeoutMs, bool? preferOnDevice, IReadOnlyList<string>? keywords, out SpeechRecognitionOptions options)
    {
        options = new SpeechRecognitionOptions();

        if (!TryGetCulture(cultureName, out var culture))
            return "Unknown culture. Expected a name such as \"en-US\".";

        if (keywords is not null
            && (keywords.Count > MaxKeywords || keywords.Any(x => String.IsNullOrWhiteSpace(x) || x.Length > MaxKeywordLength)))
            return $"Expected at most {MaxKeywords} keywords of at most {MaxKeywordLength} characters each.";

        options = options with
        {
            Culture = culture ?? options.Culture,
            SilenceTimeout = silenceTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 500, 10_000)) : options.SilenceTimeout,
            PreferOnDevice = preferOnDevice ?? options.PreferOnDevice,
            Keywords = keywords is null ? options.Keywords : [.. keywords]
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

        var body = await WebAppBridgeResults.ReadBodyAsync(context, SpeechJsonContext.Default.SpeakRequest);
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

        if (!body.Wait)
        {
            await WebAppBridgeResults.NoContent(context);
            return;
        }

        // A page that navigates away mid-sentence should not leave the device talking.
        SpeakOutcome outcome;
        using (context.RequestAborted.Register(cancellation.Cancel))
            outcome = await speaking;

        if (outcome.Error is { } error)
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "speech_failed", error);
        else
            await WebAppBridgeResults.Json(context, new SpeakResult(outcome.Completed), SpeechJsonContext.Default.SpeakResult);
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
            this.PublishError(SpeechErrorSource.Synthesis, ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref this.utterance, null, cancellation);
            this.spoken.Publish(new SpeakResult(completed));
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
        IReadOnlyList<SpeechVoice> list = [.. voices.Select(x => new SpeechVoice(x.Id, x.Name, x.Culture?.Name))];

        await WebAppBridgeResults.Json(context, list, SpeechJsonContext.Default.IReadOnlyListSpeechVoice);
    }

    async ValueTask CulturesAsync(HttpContext context)
    {
        if (this.tts is not { IsSupported: true } t)
        {
            await WebAppBridgeResults.NotSupported(context, "Text-to-speech");
            return;
        }

        var voices = await t.GetVoicesAsync(null, context.RequestAborted);
        IReadOnlyList<string> cultures =
        [
            .. voices
                .Select(x => x.Culture?.Name)
                .OfType<string>()
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        await WebAppBridgeResults.Json(context, cultures, SpeechJsonContext.Default.IReadOnlyListString);
    }

    // ---- helpers

    void PublishError(SpeechErrorSource source, string message)
        => this.errors.Publish(new SpeechError(source, message));

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
    Task<T> OnMainThread<T>(Func<Task<T>> action)
        => this.mainThread.InvokeAsync(action);

    Task OnMainThread(Func<Task> action)
        => this.mainThread.InvokeAsync(action);

    public void Dispose()
    {
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
