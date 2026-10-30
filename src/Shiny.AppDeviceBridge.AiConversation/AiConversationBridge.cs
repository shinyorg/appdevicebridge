using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AiConversation.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.AiConversation;
using NativeVoice = Shiny.AiConversation.Voice;

namespace Shiny.AppDeviceBridge.AiConversation;

public sealed class AiConversationBridgeOptions
{
    /// <summary>
    /// Registers Shiny.AiConversation with the configuration passed to
    /// <see cref="AiConversationBridgeExtensions.AddAiConversationBridge{TBuilder}"/>. Turn off when the app registers it
    /// itself.
    /// </summary>
    public bool RegisterAiConversation { get; set; } = true;

    /// <summary>
    /// Registers the voice loop, and Shiny.Speech's on-device recognizer and synthesizer for it. Off by default: it is a
    /// microphone the page can hold open, and an app that only chats in text has no use for it.
    /// </summary>
    public bool Voice { get; set; }

    /// <summary>
    /// How many sessions the bridge keeps open, raising their events. Past this, the least recently used one that is not
    /// running a turn or attached to the voice loop is closed; using it again reopens it. 8 by default.
    /// </summary>
    public int MaxOpenSessions { get; set; } = 8;

    internal void Validate()
    {
        if (this.MaxOpenSessions < 1)
            throw new InvalidOperationException("AiConversationBridgeOptions.MaxOpenSessions is at least 1.");
    }
}

public static class AiConversationBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/ai</c> and registers Shiny.AiConversation. The model is the app's: register an <c>IChatClient</c>,
    /// or set a provider here.
    /// <code>
    /// bridge.AddAiConversationBridge(ai => ai
    ///     .SetChatClientProvider&lt;MyChatClientProvider&gt;()
    ///     .AddSessionTitles()
    /// );
    /// bridge.AddAiConversationBridge(ai => ai.SetChatClientProvider&lt;MyChatClientProvider&gt;(), o => o.Voice = true);
    /// </code>
    /// <para>
    /// Every tool the app registers runs on the page's say-so: the page writes the prompt, and the model decides what to
    /// call. Register only tools the web app may use.
    /// </para>
    /// <para>
    /// Voice platform setup: <c>RECORD_AUDIO</c> on Android; <c>NSSpeechRecognitionUsageDescription</c> and
    /// <c>NSMicrophoneUsageDescription</c> on Apple platforms, plus the <c>com.apple.security.device.audio-input</c>
    /// entitlement where the app is sandboxed.
    /// </para>
    /// </summary>
    public static TBuilder AddAiConversationBridge<TBuilder>(
        this TBuilder bridge,
        Action<AiConversationOptions>? configure = null,
        Action<AiConversationBridgeOptions>? options = null
    ) where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var existing = services.FirstOrDefault(x => x.ServiceType == typeof(AiConversationBridgeOptions))?.ImplementationInstance as AiConversationBridgeOptions;
        var bridgeOptions = existing ?? new AiConversationBridgeOptions();
        options?.Invoke(bridgeOptions);
        bridgeOptions.Validate();

        if (existing is not null)
            return bridge;

        services.AddSingleton(bridgeOptions);

        if (bridgeOptions.RegisterAiConversation)
            services.AddShinyAiConversation(configure);

        if (bridgeOptions.Voice)
        {
#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
            services.AddSpeechServices();
#endif
            services.AddShinyAiConversationVoice();
        }

        bridge.AddBridge<AiConversationBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/ai</c> over <see cref="Native.IAiSessionManager"/> and, when registered,
/// <see cref="NativeVoice.IVoiceConversation"/>.
/// <code>
/// GET    /_bridge/ai
/// GET    /_bridge/ai/sessions?text=&amp;tag=&amp;profile=&amp;skip=0&amp;take=50
/// POST   /_bridge/ai/sessions                  { "title": "…", "profile": "…" }
/// GET    /_bridge/ai/sessions/latest           204 when there are none
/// GET    /_bridge/ai/sessions/{id}
/// DELETE /_bridge/ai/sessions/{id}
/// PUT    /_bridge/ai/sessions/{id}/title       { "title": "…" }
/// POST   /_bridge/ai/sessions/{id}/messages    { "text": "…" } or { "answer": { "questionId", "choiceIds" } }, "wait": false
/// GET    /_bridge/ai/sessions/{id}/messages?before=&amp;count=50
/// DELETE /_bridge/ai/sessions/{id}/turn        interrupts the running turn
/// POST   /_bridge/ai/sessions/{id}/compact
/// GET    /_bridge/ai/attachments?id=…
/// GET    /_bridge/ai/memory?scope=&amp;scopeKey=
/// PUT    /_bridge/ai/memory                    { "scope": "Global", "path": "…", "content": "…" }
/// DELETE /_bridge/ai/memory?scope=&amp;path=&amp;scopeKey=
/// GET    /_bridge/ai/voice
/// POST   /_bridge/ai/voice/access
/// POST   /_bridge/ai/voice                     { "sessionId": "…", "wakeWord": "hey app" }
/// DELETE /_bridge/ai/voice
/// POST   /_bridge/ai/voice/{talk,stop,pause,resume}
///
/// events: ai.turn.started, ai.delta, ai.tool, ai.questions, ai.turn.ended, ai.message, ai.state, ai.usage, ai.title,
///         ai.compacted, ai.memory, ai.error, ai.voice.state, ai.voice.transcript, ai.voice.speaking,
///         ai.voice.interrupted, ai.voice.level, ai.voice.error
/// </code>
/// <para>
/// The bridge holds one instance of each session it uses — the library's instances each raise their own events and
/// would conflict saving the same session — and pumps its events to the page. A turn belongs to its session, not to the
/// request that started it: it runs on when the request ends, and a newer message supersedes it.
/// </para>
/// <para>
/// The voice loop belongs to whoever listens for <c>ai.voice.state</c>: it needs one to attach, and detaches once the
/// last is gone, so a page that went away cannot leave the microphone waiting for a wake word.
/// </para>
/// </summary>
public sealed class AiConversationBridge : IWebAppBridge, IDisposable
{
    const int MaxTextLength = 32_000;
    const int MaxInstructions = 8;
    const int MaxInstructionLength = 4_000;
    const int MaxImages = 4;
    const long MaxImageBytes = 10 * 1024 * 1024;
    const int MaxTitleLength = 200;
    const int MaxMemoryPathLength = 200;
    const int MaxMemoryContentLength = 64 * 1024;
    const int MaxQuietWords = 32;

    readonly AiConversationBridgeOptions options;
    readonly WebAppFileRoots roots;
    readonly IWebAppMainThread mainThread;
    readonly Native.IAiSessionManager? manager;
    readonly Native.IAiAttachmentStore? attachments;
    readonly NativeVoice.IVoiceConversation? voice;
    readonly CancellationTokenSource disposed = new();

    readonly SemaphoreSlim openGate = new(1, 1);
    readonly Dictionary<string, OpenSession> open = new(StringComparer.Ordinal);
    long useCounter;
    volatile bool voiceOwned;

    readonly WebAppEventSource<AiTurnStarted> turnStarted = new();
    readonly WebAppEventSource<AiTextDelta> deltas = new();
    readonly WebAppEventSource<AiToolCall> tools = new();
    readonly WebAppEventSource<AiQuestionsAsked> questions = new();
    readonly WebAppEventSource<AiTurnEnded> turnEnded = new();
    readonly WebAppEventSource<AiMessageChanged> messages = new();
    readonly WebAppEventSource<AiStateChanged> states = new();
    readonly WebAppEventSource<AiUsageChanged> usage = new();
    readonly WebAppEventSource<AiTitleChanged> titles = new();
    readonly WebAppEventSource<AiCompacted> compacted = new();
    readonly WebAppEventSource<AiMemoryChanged> memory = new();
    readonly WebAppEventSource<AiSessionError> errors = new();
    readonly WebAppEventSource<AiVoiceStateChanged> voiceStates = new();
    readonly WebAppEventSource<AiTranscript> transcripts = new();
    readonly WebAppEventSource<AiSpeaking> speaking = new();
    readonly WebAppEventSource<AiSpeechInterrupted> interruptions = new();
    readonly WebAppEventSource<AiVoiceLevel> voiceLevels = new();
    readonly WebAppEventSource<AiVoiceError> voiceErrors = new();

    public AiConversationBridge(IServiceProvider services, WebAppFileRoots roots)
    {
        this.roots = roots;
        this.options = services.GetOptionalService<AiConversationBridgeOptions>() ?? new AiConversationBridgeOptions();
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
        this.manager = services.GetOptionalService<Native.IAiSessionManager>();
        this.attachments = services.GetOptionalService<Native.IAiAttachmentStore>();
        this.voice = services.GetOptionalService<NativeVoice.IVoiceConversation>();

        if (this.voice is not null)
            _ = this.PumpVoiceAsync(this.voice, this.disposed.Token);
    }

    public string Name => "ai";

    public bool IsSupported => this.manager is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("ai.turn.started", this.turnStarted.ListenAsync, AiConversationJsonContext.Default.AiTurnStarted)
        .MapEvent("ai.delta", this.deltas.ListenAsync, AiConversationJsonContext.Default.AiTextDelta)
        .MapEvent("ai.tool", this.tools.ListenAsync, AiConversationJsonContext.Default.AiToolCall)
        .MapEvent("ai.questions", this.questions.ListenAsync, AiConversationJsonContext.Default.AiQuestionsAsked)
        .MapEvent("ai.turn.ended", this.turnEnded.ListenAsync, AiConversationJsonContext.Default.AiTurnEnded)
        .MapEvent("ai.message", this.messages.ListenAsync, AiConversationJsonContext.Default.AiMessageChanged)
        .MapEvent("ai.state", this.states.ListenAsync, AiConversationJsonContext.Default.AiStateChanged)
        .MapEvent("ai.usage", this.usage.ListenAsync, AiConversationJsonContext.Default.AiUsageChanged)
        .MapEvent("ai.title", this.titles.ListenAsync, AiConversationJsonContext.Default.AiTitleChanged)
        .MapEvent("ai.compacted", this.compacted.ListenAsync, AiConversationJsonContext.Default.AiCompacted)
        .MapEvent("ai.memory", this.memory.ListenAsync, AiConversationJsonContext.Default.AiMemoryChanged)
        .MapEvent("ai.error", this.errors.ListenAsync, AiConversationJsonContext.Default.AiSessionError)
        .MapEvent("ai.voice.state", ct => this.voiceStates.ListenAsync(this.OnVoiceListenerStopped, ct), AiConversationJsonContext.Default.AiVoiceStateChanged)
        .MapEvent("ai.voice.transcript", this.transcripts.ListenAsync, AiConversationJsonContext.Default.AiTranscript)
        .MapEvent("ai.voice.speaking", this.speaking.ListenAsync, AiConversationJsonContext.Default.AiSpeaking)
        .MapEvent("ai.voice.interrupted", this.interruptions.ListenAsync, AiConversationJsonContext.Default.AiSpeechInterrupted)
        .MapEvent("ai.voice.level", this.voiceLevels.ListenAsync, AiConversationJsonContext.Default.AiVoiceLevel)
        .MapEvent("ai.voice.error", this.voiceErrors.ListenAsync, AiConversationJsonContext.Default.AiVoiceError)
        .MapGet("", this.StatusAsync)
        .MapGet("/sessions", this.QuerySessionsAsync)
        .MapPost("/sessions", this.StartSessionAsync)
        .MapGet("/sessions/latest", this.LatestSessionAsync)
        .MapGet("/sessions/{id}", this.GetSessionAsync)
        .MapDelete("/sessions/{id}", this.DeleteSessionAsync)
        .MapPut("/sessions/{id}/title", this.SetTitleAsync)
        .MapPost("/sessions/{id}/messages", this.SendAsync)
        .MapGet("/sessions/{id}/messages", this.MessagesAsync)
        .MapDelete("/sessions/{id}/turn", this.InterruptAsync)
        .MapPost("/sessions/{id}/compact", this.CompactAsync)
        .MapGet("/attachments", this.AttachmentAsync)
        .MapGet("/memory", this.ListMemoryAsync)
        .MapPut("/memory", this.SaveMemoryAsync)
        .MapDelete("/memory", this.DeleteMemoryAsync)
        .MapGet("/voice", this.VoiceStatusAsync)
        .MapPost("/voice/access", this.VoiceAccessAsync)
        .MapPost("/voice", this.AttachVoiceAsync)
        .MapDelete("/voice", this.DetachVoiceAsync)
        .MapPost("/voice/talk", ctx => this.VoiceCommandAsync(ctx, v => v.PushToTalk()))
        .MapPost("/voice/stop", ctx => this.VoiceCommandAsync(ctx, v => v.StopSpeaking()))
        .MapPost("/voice/pause", ctx => this.VoiceCommandAsync(ctx, v => v.Pause()))
        .MapPost("/voice/resume", ctx => this.VoiceCommandAsync(ctx, v => v.Resume()));

    ValueTask StatusAsync(HttpContext context)
    {
        int count;
        lock (this.open)
            count = this.open.Count;

        return WebAppBridgeResults.Json(context, new AiStatus(this.manager is not null, this.voice is not null, count), AiConversationJsonContext.Default.AiStatus);
    }

    // ---- sessions

    async ValueTask QuerySessionsAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        var query = context.Request.Query;
        if (!TryInt(query["skip"], 0, out var skip) || skip < 0 || !TryInt(query["take"], 50, out var take) || take is < 1 or > 200)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected skip of 0 or more and take from 1 to 200.");
            return;
        }

        var tag = NullIfEmpty(query["tag"]);
        var found = await m.Query(
            new Native.SessionQuery(
                Text: NullIfEmpty(query["text"]),
                Tags: tag is null ? null : [tag],
                Profile: NullIfEmpty(query["profile"]),
                Skip: skip,
                Take: take
            ),
            context.RequestAborted
        );

        IReadOnlyList<AiSessionSummary> list = [.. found.Select(ToSummary)];
        await WebAppBridgeResults.Json(context, list, AiConversationJsonContext.Default.IReadOnlyListAiSessionSummary);
    }

    async ValueTask StartSessionAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AiConversationJsonContext.Default.AiStartSessionRequest) ?? new AiStartSessionRequest();
        if (body.Title?.Length > MaxTitleLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected a title of at most {MaxTitleLength} characters.");
            return;
        }

        var settings = new Native.AiSessionSettings
        {
            Profile = body.Profile,
            Tags = body.Tags ?? [],
            Metadata = body.Metadata,
            ExcludeFromSearch = body.ExcludeFromSearch
        };

        // Not the request's token: a session half started is still stored.
        var session = await m.Start(settings, CancellationToken.None);
        var opened = await this.AddAsync(session);

        if (!String.IsNullOrWhiteSpace(body.Title))
            await opened.Session.SetTitle(body.Title, CancellationToken.None);

        await WebAppBridgeResults.Json(context, ToContract(opened.Session), AiConversationJsonContext.Default.AiSession);
    }

    async ValueTask LatestSessionAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        if (await m.GetLatest(context.RequestAborted) is not { } latest)
        {
            await WebAppBridgeResults.NoContent(context);
            return;
        }

        var opened = await this.AddAsync(latest);
        await WebAppBridgeResults.Json(context, ToContract(opened.Session), AiConversationJsonContext.Default.AiSession);
    }

    async ValueTask GetSessionAsync(HttpContext context)
    {
        if (await this.OpenFromRouteAsync(context) is { } opened)
            await WebAppBridgeResults.Json(context, ToContract(opened.Session), AiConversationJsonContext.Default.AiSession);
    }

    async ValueTask DeleteSessionAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        var id = RouteId(context);

        if (this.voiceOwned && this.voice?.Session?.SessionId == id)
            await this.EndVoiceAsync();

        OpenSession? closing = null;
        await this.openGate.WaitAsync();
        try
        {
            lock (this.open)
            {
                if (this.open.Remove(id, out var o))
                    closing = o;
            }
        }
        finally
        {
            this.openGate.Release();
        }

        if (closing is not null)
            await CloseAsync(closing);

        await m.Delete(id, CancellationToken.None);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask SetTitleAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, AiConversationJsonContext.Default.AiTitleRequest);
        if (body?.Title is not { } title || String.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"title\": \"…\" }} of at most {MaxTitleLength} characters.");
            return;
        }

        if (await this.OpenFromRouteAsync(context) is not { } opened)
            return;

        await opened.Session.SetTitle(title, CancellationToken.None);
        await WebAppBridgeResults.Json(context, ToContract(opened.Session), AiConversationJsonContext.Default.AiSession);
    }

    async ValueTask SendAsync(HttpContext context)
    {
        if (this.manager is null)
        {
            await NotSupported(context);
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AiConversationJsonContext.Default.AiSendRequest);
        if (body is null || (body.Text is null) == (body.Answer is null))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"text\": \"…\" } or { \"answer\": { \"questionId\": \"…\", \"choiceIds\": [\"…\"] } }.");
            return;
        }

        if (body.Text is { } t && (String.IsNullOrWhiteSpace(t) || t.Length > MaxTextLength))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected text of at most {MaxTextLength} characters.");
            return;
        }

        if (body.Instructions is { } extra && (extra.Count > MaxInstructions || extra.Any(x => String.IsNullOrWhiteSpace(x) || x.Length > MaxInstructionLength)))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected at most {MaxInstructions} instructions of at most {MaxInstructionLength} characters each.");
            return;
        }

        if (body.Images is { Count: > MaxImages })
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected at most {MaxImages} images.");
            return;
        }

        if (await this.OpenFromRouteAsync(context) is not { } opened)
            return;

        var session = opened.Session;
        Native.AiInput input;

        if (body.Answer is { } answer)
        {
            var question = session.PendingQuestions.FirstOrDefault(x => x.Id == answer.QuestionId);
            if (question is null)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "no_such_question", "That question is not waiting for an answer. GET the session for its pending questions.");
                return;
            }

            if (!question.HasChoices)
            {
                await WebAppBridgeResults.BadRequest(context, "That question has no choices; answer it with text.");
                return;
            }

            var ids = answer.ChoiceIds ?? [];
            if (ids.Count == 0
                || (!question.AllowMultiple && ids.Count > 1)
                || ids.Any(id => question.Choices!.All(c => c.Id != id)))
            {
                await WebAppBridgeResults.BadRequest(context, question.AllowMultiple
                    ? "Expected one or more of the question's choice ids."
                    : "Expected exactly one of the question's choice ids.");
                return;
            }

            input = Native.AiInput.Answer(question, [.. ids]);
        }
        else
        {
            input = Native.AiInput.Text(body.Text!);
        }

        if (body.Instructions is { Count: > 0 } instructions)
            input = input.WithInstructions([.. instructions]);

        foreach (var image in body.Images ?? [])
        {
            var (error, status, data, mediaType) = await this.ReadImageAsync(image.File, context.RequestAborted);
            if (error is not null)
            {
                await WebAppBridgeResults.Error(context, status, status == StatusCodes.Status404NotFound ? "not_found" : "bad_request", error);
                return;
            }

            input = input.WithImage(data!, mediaType!, image.Description, BridgeEnum.Convert<AiImageRetention, Native.ImageRetention>(image.Retention));
        }

        // Not the request's token: the turn is the session's, and runs on when this request has been answered.
        var turn = session.Send(input, CancellationToken.None);

        if (!body.Wait)
        {
            await WebAppBridgeResults.Json(context, new AiSendResult(turn.TurnId), AiConversationJsonContext.Default.AiSendResult);
            return;
        }

        var result = await turn.Completion.WaitAsync(context.RequestAborted);
        await WebAppBridgeResults.Json(
            context,
            new AiSendResult(
                turn.TurnId,
                BridgeEnum.Convert<Native.AiTurnOutcome, AiTurnOutcome>(result.Outcome),
                result.Reply is { } reply ? ToContract(reply) : null,
                [.. result.Questions.Select(ToContract)],
                result.Error?.Message
            ),
            AiConversationJsonContext.Default.AiSendResult
        );
    }

    async ValueTask MessagesAsync(HttpContext context)
    {
        var query = context.Request.Query;
        long? before = null;
        if (NullIfEmpty(query["before"]) is { } raw)
        {
            if (!Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            {
                await WebAppBridgeResults.BadRequest(context, "Expected before to be a message sequence.");
                return;
            }

            before = sequence;
        }

        if (!TryInt(query["count"], 50, out var count) || count is < 1 or > 200)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a count from 1 to 200.");
            return;
        }

        if (await this.OpenFromRouteAsync(context) is not { } opened)
            return;

        var page = await opened.Session.GetMessages(before, count, context.RequestAborted);
        await WebAppBridgeResults.Json(context, new AiMessagePage([.. page.Messages.Select(ToContract)], page.HasMore), AiConversationJsonContext.Default.AiMessagePage);
    }

    async ValueTask InterruptAsync(HttpContext context)
    {
        if (await this.OpenFromRouteAsync(context) is not { } opened)
            return;

        if (opened.Session.ActiveTurn is not { } turn)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "no_turn", "No turn is running.");
            return;
        }

        turn.Interrupt();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CompactAsync(HttpContext context)
    {
        if (await this.OpenFromRouteAsync(context) is not { } opened)
            return;

        await opened.Session.Compact(context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask AttachmentAsync(HttpContext context)
    {
        if (this.manager is null || this.attachments is not { } store)
        {
            await NotSupported(context);
            return;
        }

        if (NullIfEmpty(context.Request.Query["id"]) is not { } id)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected ?id= from a message part's attachmentId.");
            return;
        }

        byte[]? data;
        try
        {
            data = await store.Load(id, context.RequestAborted);
        }
        catch (ArgumentException)
        {
            data = null;
        }

        if (data is null)
        {
            await WebAppBridgeResults.NotFound(context, "No such attachment.");
            return;
        }

        await Results.Bytes(data, SniffMediaType(data)).ExecuteAsync(context);
    }

    // ---- memory

    async ValueTask ListMemoryAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        Native.MemoryScope? scope = null;
        if (NullIfEmpty(context.Request.Query["scope"]) is { } raw)
        {
            if (!Enum.TryParse<AiMemoryScope>(raw, true, out var parsed))
            {
                await WebAppBridgeResults.BadRequest(context, "Expected a scope of Session, Profile or Global.");
                return;
            }

            scope = BridgeEnum.Convert<AiMemoryScope, Native.MemoryScope>(parsed);
        }

        var documents = await m.Memory.List(scope, NullIfEmpty(context.Request.Query["scopeKey"]), context.RequestAborted);
        IReadOnlyList<AiMemoryDocument> list = [.. documents.Select(ToContract)];
        await WebAppBridgeResults.Json(context, list, AiConversationJsonContext.Default.IReadOnlyListAiMemoryDocument);
    }

    async ValueTask SaveMemoryAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AiConversationJsonContext.Default.AiMemorySaveRequest);
        if (body is null || !IsMemoryPath(body.Path) || body.Content is null || body.Content.Length > MaxMemoryContentLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"scope\": \"Global\", \"path\": \"…\", \"content\": \"…\" }} with a path of at most {MaxMemoryPathLength} characters and content of at most {MaxMemoryContentLength}.");
            return;
        }

        if (MemoryScopeError(body.Scope, body.ScopeKey) is { } scopeError)
        {
            await WebAppBridgeResults.BadRequest(context, scopeError);
            return;
        }

        try
        {
            var saved = await m.Memory.Save(BridgeEnum.Convert<AiMemoryScope, Native.MemoryScope>(body.Scope), body.ScopeKey, body.Path, body.Content, body.Description, CancellationToken.None);
            await WebAppBridgeResults.Json(context, ToContract(saved), AiConversationJsonContext.Default.AiMemoryDocument);
        }
        catch (Native.MemoryConflictException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "memory_conflict", ex.Message);
        }
    }

    async ValueTask DeleteMemoryAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await NotSupported(context);
            return;
        }

        var query = context.Request.Query;
        var path = NullIfEmpty(query["path"]);
        var scopeKey = NullIfEmpty(query["scopeKey"]);
        if (!Enum.TryParse<AiMemoryScope>(query["scope"].ToString(), true, out var scope) || !IsMemoryPath(path))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected ?scope=Session|Profile|Global&path=….");
            return;
        }

        if (MemoryScopeError(scope, scopeKey) is { } scopeError)
        {
            await WebAppBridgeResults.BadRequest(context, scopeError);
            return;
        }

        await m.Memory.Delete(BridgeEnum.Convert<AiMemoryScope, Native.MemoryScope>(scope), scopeKey, path!, CancellationToken.None);
        await WebAppBridgeResults.NoContent(context);
    }

    // ---- voice

    ValueTask VoiceStatusAsync(HttpContext context) => WebAppBridgeResults.Json(context, this.CreateVoiceStatus(), AiConversationJsonContext.Default.AiVoiceStatus);

    AiVoiceStatus CreateVoiceStatus() => this.voice is { } v
        ? new AiVoiceStatus(true, BridgeEnum.Convert<NativeVoice.VoiceState, AiVoiceState>(v.State), v.Session?.SessionId)
        : new AiVoiceStatus(false, AiVoiceState.Off, null);

    async ValueTask VoiceAccessAsync(HttpContext context)
    {
        if (this.voice is not { } v)
        {
            await NotSupported(context, "The voice loop");
            return;
        }

        var access = await this.mainThread.InvokeAsync(v.RequestAccess);
        await WebAppBridgeResults.Json(context, new AiAccessResult(BridgeEnum.Convert<AccessState, ContractAccess>(access)), AiConversationJsonContext.Default.AiAccessResult);
    }

    async ValueTask AttachVoiceAsync(HttpContext context)
    {
        if (this.voice is not { } v || this.manager is null)
        {
            await NotSupported(context, "The voice loop");
            return;
        }

        if (!this.voiceStates.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen for ai.voice.state first: the voice loop runs only while someone is listening.");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AiConversationJsonContext.Default.AiVoiceRequest);
        if (body?.SessionId is not { Length: > 0 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"sessionId\": \"…\" }.");
            return;
        }

        if (body.QuietWords is { } quiet && (quiet.Count > MaxQuietWords || quiet.Any(x => String.IsNullOrWhiteSpace(x) || x.Length > 64)))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected at most {MaxQuietWords} quiet words of at most 64 characters each.");
            return;
        }

        if (body.WakeWord?.Length > 64 || body.FollowUpTimeoutSeconds is <= 0 or > 600)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a wake word of at most 64 characters and a follow-up timeout from 0 to 600 seconds.");
            return;
        }

        CultureInfo? culture = null;
        if (body.Culture is { } cultureName)
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(cultureName, predefinedOnly: true);
            }
            catch (CultureNotFoundException)
            {
                await WebAppBridgeResults.BadRequest(context, "Unknown culture. Expected a name such as \"en-US\".");
                return;
            }
        }

        // The native app may be running the voice loop itself; the bridge does not take it over.
        if (!this.voiceOwned && v.Session is not null)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "voice_busy", "The app is running the voice loop.");
            return;
        }

        var opened = await this.OpenAsync(body.SessionId);
        if (opened is null)
        {
            await WebAppBridgeResults.NotFound(context, "No such session.");
            return;
        }

        var access = await this.mainThread.InvokeAsync(v.RequestAccess);
        if (access != AccessState.Available)
        {
            if (access == AccessState.NotSupported)
                await NotSupported(context, "Speech recognition");
            else
                await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", $"Microphone access is {access}.");

            return;
        }

        var defaults = new NativeVoice.VoiceOptions();
        var voiceOptions = defaults with
        {
            WakeWord = String.IsNullOrWhiteSpace(body.WakeWord) ? null : body.WakeWord,
            Interruption = body.Interruption is { } mode ? BridgeEnum.Convert<AiInterruptionMode, NativeVoice.InterruptionMode>(mode) : defaults.Interruption,
            QuietWords = body.QuietWords is { } words ? [.. words] : defaults.QuietWords,
            FollowUpTimeout = body.FollowUpTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : defaults.FollowUpTimeout,
            Acknowledgement = body.Acknowledgement is { } ack ? BridgeEnum.Convert<AiAcknowledgement, NativeVoice.AiAcknowledgement>(ack) : defaults.Acknowledgement,
            SpeechToText = culture is null ? defaults.SpeechToText : new Shiny.Speech.SpeechRecognitionOptions { Culture = culture },
            TextToSpeech = culture is null ? defaults.TextToSpeech : new Shiny.Speech.TextToSpeechOptions { Culture = culture }
        };

        try
        {
            await this.mainThread.InvokeAsync(() => v.Attach(opened.Session, voiceOptions, CancellationToken.None));
            this.voiceOwned = true;
        }
        catch (InvalidOperationException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "microphone_busy", "The microphone is already in use.");
            return;
        }

        // The last listener may have gone between the check above and now, and then nothing would ever detach it.
        if (!this.voiceStates.HasListeners)
            await this.EndVoiceAsync();

        await WebAppBridgeResults.Json(context, this.CreateVoiceStatus(), AiConversationJsonContext.Default.AiVoiceStatus);
    }

    async ValueTask DetachVoiceAsync(HttpContext context)
    {
        if (this.voice is null)
        {
            await NotSupported(context, "The voice loop");
            return;
        }

        await this.EndVoiceAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask VoiceCommandAsync(HttpContext context, Func<NativeVoice.IVoiceConversation, Task> command)
    {
        if (this.voice is not { } v)
        {
            await NotSupported(context, "The voice loop");
            return;
        }

        if (!this.voiceOwned || v.Session is null)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_attached", "Attach the voice loop to a session first.");
            return;
        }

        await this.mainThread.InvokeAsync(() => command(v));
        await WebAppBridgeResults.NoContent(context);
    }

    async Task EndVoiceAsync()
    {
        if (!this.voiceOwned || this.voice is not { } v)
            return;

        this.voiceOwned = false;
        try
        {
            await this.mainThread.InvokeAsync(v.Detach);
        }
        catch (Exception ex)
        {
            this.voiceErrors.Publish(new AiVoiceError(ex.Message));
        }
    }

    /// <summary>The voice loop is for whoever hears its state; with the last of them gone, the microphone closes.</summary>
    void OnVoiceListenerStopped(int remaining)
    {
        if (remaining == 0 && this.voiceOwned)
            _ = this.EndVoiceAsync();
    }

    async Task PumpVoiceAsync(NativeVoice.IVoiceConversation v, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in v.GetEvents(cancellationToken))
            {
                switch (e)
                {
                    case NativeVoice.VoiceStateChanged s:
                        this.voiceStates.Publish(new AiVoiceStateChanged(BridgeEnum.Convert<NativeVoice.VoiceState, AiVoiceState>(s.State)));
                        break;
                    case NativeVoice.InterimTranscript t:
                        this.transcripts.Publish(new AiTranscript(t.Text, false));
                        break;
                    case NativeVoice.FinalTranscript t:
                        this.transcripts.Publish(new AiTranscript(t.Text, true));
                        break;
                    case NativeVoice.SpeakingSentence s:
                        this.speaking.Publish(new AiSpeaking(s.Text));
                        break;
                    case NativeVoice.SpeechInterrupted i:
                        this.interruptions.Publish(new AiSpeechInterrupted(BridgeEnum.Convert<NativeVoice.InterruptionReason, AiInterruptionReason>(i.Reason), i.HeardText, i.SpokenText));
                        break;
                    case NativeVoice.AudioLevel l:
                        this.voiceLevels.Publish(new AiVoiceLevel(l.Input, l.Output));
                        break;
                    case NativeVoice.VoiceError err:
                        this.voiceErrors.Publish(new AiVoiceError(err.Error.Message));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- open sessions

    async Task<OpenSession?> OpenFromRouteAsync(HttpContext context)
    {
        if (this.manager is null)
        {
            await NotSupported(context);
            return null;
        }

        var opened = await this.OpenAsync(RouteId(context));
        if (opened is null)
            await WebAppBridgeResults.NotFound(context, "No such session.");

        return opened;
    }

    async Task<OpenSession?> OpenAsync(string id)
    {
        await this.openGate.WaitAsync();
        try
        {
            if (this.open.TryGetValue(id, out var existing))
            {
                existing.LastUsed = Interlocked.Increment(ref this.useCounter);
                return existing;
            }

            var session = await this.manager!.Get(id, CancellationToken.None);
            return session is null ? null : this.AddLocked(session);
        }
        finally
        {
            this.openGate.Release();
        }
    }

    /// <summary>Takes a session the manager handed over, unless the bridge already holds that one — two instances would conflict.</summary>
    async Task<OpenSession> AddAsync(Native.IAiSession session)
    {
        OpenSession? duplicate = null;
        OpenSession opened;

        await this.openGate.WaitAsync();
        try
        {
            if (this.open.TryGetValue(session.SessionId, out var existing))
            {
                existing.LastUsed = Interlocked.Increment(ref this.useCounter);
                duplicate = new OpenSession(session);
                opened = existing;
            }
            else
            {
                opened = this.AddLocked(session);
            }
        }
        finally
        {
            this.openGate.Release();
        }

        if (duplicate is not null)
            await CloseAsync(duplicate);

        return opened;
    }

    OpenSession AddLocked(Native.IAiSession session)
    {
        var opened = new OpenSession(session) { LastUsed = Interlocked.Increment(ref this.useCounter) };
        lock (this.open)
            this.open[session.SessionId] = opened;

        _ = this.PumpAsync(opened);

        while (this.open.Count > this.options.MaxOpenSessions)
        {
            var idle = this.open.Values
                .Where(x => x != opened && x.Session.ActiveTurn is null && !(this.voiceOwned && this.voice?.Session == x.Session))
                .MinBy(x => x.LastUsed);

            if (idle is null)
                break;

            lock (this.open)
                this.open.Remove(idle.Session.SessionId);

            _ = CloseAsync(idle);
        }

        return opened;
    }

    static async Task CloseAsync(OpenSession opened)
    {
        await opened.Stop.CancelAsync();
        try
        {
            await opened.Session.DisposeAsync();
        }
        catch (Exception)
        {
            // The session is in the store; an instance that fails to let go of itself has nothing left to lose.
        }
    }

    async Task PumpAsync(OpenSession opened)
    {
        var silent = new HashSet<string>(StringComparer.Ordinal);
        var id = opened.Session.SessionId;

        try
        {
            await foreach (var e in opened.Session.GetEvents(opened.Stop.Token))
            {
                switch (e)
                {
                    case Native.MessageAdded added:
                        this.messages.Publish(new AiMessageChanged(id, ToContract(added.Message), false));
                        break;
                    case Native.MessageUpdated updated:
                        this.messages.Publish(new AiMessageChanged(id, ToContract(updated.Message), true));
                        break;
                    case Native.StateChanged state:
                        this.states.Publish(new AiStateChanged(id, BridgeEnum.Convert<Native.AiSessionState, AiSessionState>(state.State)));
                        break;
                    case Native.UsageChanged changed:
                        this.usage.Publish(new AiUsageChanged(id, ToContract(changed.Usage), ToContract(changed.Context)));
                        break;
                    case Native.ContextPressureChanged pressure:
                        this.usage.Publish(new AiUsageChanged(id, ToContract(opened.Session.Usage), ToContract(pressure.Context)));
                        break;
                    case Native.TitleChanged title:
                        this.titles.Publish(new AiTitleChanged(id, title.Title));
                        break;
                    case Native.ContextCompacted c:
                        this.compacted.Publish(new AiCompacted(id, c.CompactedCount, c.WasHardTrim));
                        break;
                    case Native.MemoryChanged m:
                        this.memory.Publish(new AiMemoryChanged(id, ToContract(m.Document), BridgeEnum.Convert<Native.MemoryChange, AiMemoryChange>(m.Change)));
                        break;
                    case Native.SessionError error:
                        this.errors.Publish(new AiSessionError(id, error.Error.Message));
                        break;
                    case Native.TurnEventOccurred { Event: var turn }:
                        this.PublishTurnEvent(id, turn, silent);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    void PublishTurnEvent(string sessionId, Native.AiTurnEvent e, HashSet<string> silent)
    {
        switch (e)
        {
            case Native.TurnStarted started:
                this.turnStarted.Publish(new AiTurnStarted(sessionId, started.TurnId, ToContract(started.UserMessage)));
                break;
            case Native.TextDelta delta:
                this.deltas.Publish(new AiTextDelta(sessionId, delta.TurnId, delta.Text));
                break;
            case Native.ToolCallStarted call:
                if (call.Silent)
                    silent.Add(call.CallId);

                this.tools.Publish(new AiToolCall(sessionId, call.TurnId, call.CallId, call.Name, false, false, call.Silent));
                break;
            case Native.ToolCallCompleted done:
                this.tools.Publish(new AiToolCall(sessionId, done.TurnId, done.CallId, done.Name, true, done.IsError, silent.Remove(done.CallId)));
                break;
            case Native.QuestionsAsked asked:
                this.questions.Publish(new AiQuestionsAsked(sessionId, asked.TurnId, [.. asked.Questions.Select(ToContract)]));
                break;
            case Native.TurnInterrupted interrupted:
                this.turnEnded.Publish(Ended(sessionId, interrupted.TurnId, interrupted.Result, interrupted.SpokenText));
                break;
            case Native.TurnFailed failed:
                this.turnEnded.Publish(Ended(sessionId, failed.TurnId, failed.Result, null) with { Outcome = AiTurnOutcome.Failed, Error = failed.Error.Message });
                break;
            case Native.TurnCompleted completed:
                this.turnEnded.Publish(Ended(sessionId, completed.TurnId, completed.Result, null));
                break;
        }
    }

    static AiTurnEnded Ended(string sessionId, string turnId, Native.AiTurnResult result, string? spokenText) => new(
        sessionId,
        turnId,
        BridgeEnum.Convert<Native.AiTurnOutcome, AiTurnOutcome>(result.Outcome),
        result.Reply?.MessageId,
        spokenText,
        result.Error?.Message
    );

    // ---- mapping

    static AiSession ToContract(Native.IAiSession session) => new(
        session.SessionId,
        session.Title,
        session.Started,
        BridgeEnum.Convert<Native.AiSessionState, AiSessionState>(session.State),
        [.. session.PendingQuestions.Select(ToContract)],
        ToContract(session.Usage),
        ToContract(session.Context),
        session.Settings.Profile,
        session.Settings.Tags
    );

    static AiSessionSummary ToSummary(Native.AiSessionDocument document) => new(
        document.SessionId,
        document.Title,
        document.Started,
        document.LastActivity,
        document.MessageCount,
        document.Settings.Profile,
        document.Settings.Tags,
        ToContract(document.Usage)
    );

    static AiQuestion ToContract(Native.AiQuestion question) => new(
        question.Id,
        question.Text,
        question.Choices is { } choices ? [.. choices.Select(x => new AiChoice(x.Id, x.Label))] : null,
        question.AllowMultiple
    );

    static AiUsage ToContract(Native.SessionUsage usage) => new(
        usage.InputTokens,
        usage.OutputTokens,
        usage.CachedInputTokens,
        usage.ReasoningTokens,
        usage.TotalTokens,
        usage.RequestCount
    );

    static AiContext ToContract(Native.ContextSnapshot context) => new(
        context.CurrentTokens,
        context.MaxTokens,
        context.PercentUsed,
        context.Pressure is { } pressure ? BridgeEnum.Convert<Native.ContextPressure, AiContextPressure>(pressure) : null,
        context.IsEstimate
    );

    static AiMessage ToContract(Native.AiMessageDocument message) => new(
        message.MessageId,
        message.SessionId,
        message.Sequence,
        message.Timestamp,
        BridgeEnum.Convert<Native.MessageRole, AiMessageRole>(message.Role),
        BridgeEnum.Convert<Native.MessageSource, AiMessageSource>(message.Source),
        BridgeEnum.Convert<Native.MessageStatus, AiMessageStatus>(message.Status),
        message.Text,
        message.SpokenText,
        [.. message.Parts.Select(ToContract).OfType<AiMessagePart>()],
        message.Compacted
    );

    static AiMessagePart? ToContract(Native.MessagePart part)
    {
        var displayed = part.Audience.HasFlag(Native.Audience.Displayed);
        var spoken = part.Audience.HasFlag(Native.Audience.Spoken);

        return part switch
        {
            Native.TextPart text => new AiMessagePart(AiPartKind.Text, displayed, spoken, text.Text),
            Native.ToolCallPart call => new AiMessagePart(AiPartKind.ToolCall, displayed, spoken, call.ArgumentsJson, call.Name, call.CallId),
            Native.ToolResultPart result => new AiMessagePart(AiPartKind.ToolResult, displayed, spoken, result.Result, result.Name, result.CallId, result.IsError),
            Native.AnswerPart answer => new AiMessagePart(AiPartKind.Answer, displayed, spoken, answer.Text, QuestionId: answer.QuestionId, ChoiceIds: answer.ChoiceIds),
            Native.ImagePart image => new AiMessagePart(AiPartKind.Image, displayed, spoken, image.Description, AttachmentId: image.Attachment.AttachmentId, MediaType: image.Attachment.MediaType),
            _ => null
        };
    }

    static AiMemoryDocument ToContract(Native.AiMemoryDocument document) => new(
        document.Path,
        BridgeEnum.Convert<Native.MemoryScope, AiMemoryScope>(document.Scope),
        document.ScopeKey,
        document.Description,
        document.Content,
        document.Created,
        document.Updated,
        BridgeEnum.Convert<Native.MemoryAuthor, AiMemoryAuthor>(document.UpdatedBy),
        document.Version
    );

    // ---- helpers

    /// <summary>An image from a file root, checked: the error with its status, or the bytes and their media type.</summary>
    async Task<(string? Error, int Status, byte[]? Data, string? MediaType)> ReadImageAsync(BridgeFile? file, CancellationToken cancellationToken)
    {
        if (file is null || !this.roots.TryGet(file.Root, out var root))
            return ($"No file root '{file?.Root}'.", StatusCodes.Status404NotFound, null, null);

        if (WebAppFilePath.Normalize(file.Path) is not { Length: > 0 } path)
            return (WebAppFileException.InvalidPath().Message, StatusCodes.Status400BadRequest, null, null);

        if (root.GetLocalPath(path) is not { } local || !File.Exists(local))
            return ($"No such file '{file.Path}'.", StatusCodes.Status404NotFound, null, null);

        var mediaType = Path.GetExtension(local).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            ".gif" => "image/gif",
            _ => null
        };

        if (mediaType is null)
            return ("Expected a JPEG, PNG, WebP, HEIC or GIF image.", StatusCodes.Status400BadRequest, null, null);

        if (new FileInfo(local).Length > MaxImageBytes)
            return ($"Expected an image of at most {MaxImageBytes / (1024 * 1024)} MB.", StatusCodes.Status400BadRequest, null, null);

        return (null, 0, await File.ReadAllBytesAsync(local, cancellationToken), mediaType);
    }

    static string SniffMediaType(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => "application/octet-stream"
    };

    static bool IsMemoryPath(string? path)
        => !String.IsNullOrWhiteSpace(path) && path.Length <= MaxMemoryPathLength && !path.Contains("..", StringComparison.Ordinal);

    static string? MemoryScopeError(AiMemoryScope scope, string? scopeKey) => scope switch
    {
        AiMemoryScope.Global when scopeKey is not null => "Global memory has no scopeKey.",
        AiMemoryScope.Session or AiMemoryScope.Profile when String.IsNullOrWhiteSpace(scopeKey) => "Session and profile memory need a scopeKey: the session id or profile name.",
        _ => null
    };

    static string RouteId(HttpContext context) => context.Request.RouteValues["id"]?.ToString() ?? String.Empty;

    static string? NullIfEmpty(Microsoft.Extensions.Primitives.StringValues value)
        => String.IsNullOrEmpty(value.ToString()) ? null : value.ToString();

    static bool TryInt(Microsoft.Extensions.Primitives.StringValues value, int fallback, out int result)
    {
        result = fallback;
        return String.IsNullOrEmpty(value.ToString()) || Int32.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    static ValueTask NotSupported(HttpContext context, string feature = "AI conversations") => WebAppBridgeResults.NotSupported(context, feature);

    public void Dispose()
    {
        this.disposed.Cancel();

        if (this.voiceOwned && this.voice is { } v)
            _ = v.Detach();

        List<OpenSession> closing;
        lock (this.open)
        {
            closing = [.. this.open.Values];
            this.open.Clear();
        }

        foreach (var opened in closing)
            _ = CloseAsync(opened);
    }

    sealed class OpenSession(Native.IAiSession session)
    {
        public Native.IAiSession Session { get; } = session;

        public CancellationTokenSource Stop { get; } = new();

        public long LastUsed { get; set; }
    }
}
