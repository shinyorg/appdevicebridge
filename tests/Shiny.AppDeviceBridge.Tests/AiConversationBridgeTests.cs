using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AiConversation;
using Shiny.AppDeviceBridge.AiConversation.Client;
using Shiny.AppDeviceBridge.Client;
using Native = Shiny.AiConversation;
using NativeVoice = Shiny.AiConversation.Voice;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The AI conversation bridge over the real Shiny.AiConversation, with in-memory stores and a scripted chat client in
/// place of a model: sessions, streamed turns, questions and answers, images, memory, the open-session cache, and the
/// voice loop over a fake <see cref="NativeVoice.IVoiceConversation"/>. Speech recognition and synthesis are
/// Shiny.Speech's.
/// </summary>
public class AiConversationBridgeTests
{
    [Fact]
    public async Task Answers_501_without_ai_conversations()
    {
        await using var fixture = await AiFixture.StartAsync(chat: null);

        var status = await fixture.Client.GetStatusAsync();
        Assert.False(status.Supported);
        Assert.False(status.VoiceSupported);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartSessionAsync(new AiStartSessionRequest()))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetMemoryAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AttachVoiceAsync(new AiVoiceRequest("x")))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "ai").IsSupported);
    }

    [Fact]
    public async Task Streams_a_reply_and_stores_the_conversation()
    {
        var chat = new ScriptedChatClient().Reply(new TextContent("Hello"), new TextContent(" there"));
        await using var fixture = await AiFixture.StartAsync(chat);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "ai.delta,ai.turn.ended", timeout.Token);

        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest(Title: "Greetings", Tags: ["demo"]));
        Assert.Equal("Greetings", session.Title);
        Assert.Equal(["demo"], session.Tags);
        Assert.Equal(AiSessionState.Idle, session.State);

        var sent = await fixture.Client.SendAsync(session.Id, new AiSendRequest("Hi"));
        Assert.Null(sent.Outcome);

        var text = "";
        AiTurnEnded? ended = null;
        while (ended is null)
        {
            var (name, data) = await stream.NextAsync(timeout.Token);
            if (name == "ai.delta")
            {
                var delta = Deserialize(data, AiConversationJsonContext.Default.AiTextDelta);
                Assert.Equal(session.Id, delta.SessionId);
                Assert.Equal(sent.TurnId, delta.TurnId);
                text += delta.Text;
            }
            else if (name == "ai.turn.ended")
            {
                ended = Deserialize(data, AiConversationJsonContext.Default.AiTurnEnded);
            }
        }

        Assert.Equal("Hello there", text);
        Assert.Equal(AiTurnOutcome.Completed, ended.Outcome);
        Assert.NotNull(ended.ReplyMessageId);

        var page = await fixture.Client.GetMessagesAsync(session.Id);
        Assert.Contains(page.Messages, x => x.Role == AiMessageRole.User && x.Text == "Hi" && x.Source == AiMessageSource.Typed);
        var reply = Assert.Single(page.Messages, x => x.Role == AiMessageRole.Assistant);
        Assert.Equal(ended.ReplyMessageId, reply.Id);
        Assert.Equal("Hello there", reply.Text);
        Assert.Contains(reply.Parts, x => x.Kind == AiPartKind.Text && x.Displayed && x.Text == "Hello there");
    }

    [Fact]
    public async Task Waits_for_the_reply_when_asked()
    {
        var chat = new ScriptedChatClient().Reply(new TextContent("Four."));
        await using var fixture = await AiFixture.StartAsync(chat);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var result = await fixture.Client.SendAsync(session.Id, new AiSendRequest("What is 2 + 2?", Instructions: ["Answer in one word."], Wait: true));

        Assert.Equal(AiTurnOutcome.Completed, result.Outcome);
        Assert.Equal("Four.", result.Reply?.Text);
        Assert.Empty(result.Questions!);
        Assert.Contains(chat.Requests.Single().SelectMany(x => x.Contents).OfType<TextContent>(), x => x.Text.Contains("Answer in one word."));
    }

    [Fact]
    public async Task Asks_a_question_and_takes_its_answer()
    {
        var chat = new ScriptedChatClient()
            .Reply(new TextContent("Large or small?"), ScriptedChatClient.AskUser("c1", """[{ "id": "size", "text": "Large or small?", "choices": [{ "id": "l", "label": "Large" }, { "id": "s", "label": "Small" }] }]"""))
            .Reply(new TextContent("Large it is."));
        await using var fixture = await AiFixture.StartAsync(chat);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var asked = await fixture.Client.SendAsync(session.Id, new AiSendRequest("A coffee please", Wait: true));
        Assert.Equal(AiTurnOutcome.AwaitingUser, asked.Outcome);
        var question = Assert.Single(asked.Questions!);
        Assert.Equal(["l", "s"], question.Choices!.Select(x => x.Id));
        Assert.Equal("size", Assert.Single((await fixture.Client.GetSessionAsync(session.Id)).PendingQuestions).Id);

        var badChoice = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest(Answer: new AiAnswer("size", ["xl"]))));
        Assert.Equal(HttpStatusCode.BadRequest, badChoice.StatusCode);
        var twoChoices = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest(Answer: new AiAnswer("size", ["l", "s"]))));
        Assert.Equal(HttpStatusCode.BadRequest, twoChoices.StatusCode);
        var unknown = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest(Answer: new AiAnswer("milk", ["l"]))));
        Assert.Equal("no_such_question", unknown.Code);

        var answered = await fixture.Client.SendAsync(session.Id, new AiSendRequest(Answer: new AiAnswer("size", ["l"]), Wait: true));

        Assert.Equal(AiTurnOutcome.Completed, answered.Outcome);
        Assert.Equal("Large it is.", answered.Reply?.Text);
        var answer = (await fixture.Client.GetMessagesAsync(session.Id)).Messages
            .SelectMany(x => x.Parts)
            .Single(x => x.Kind == AiPartKind.Answer);
        Assert.Equal("size", answer.QuestionId);
        Assert.Equal(["l"], answer.ChoiceIds);
        Assert.Equal("Large", answer.Text);
    }

    [Fact]
    public async Task A_turn_outlives_the_request_that_started_it()
    {
        var gate = new TaskCompletionSource();
        var chat = new ScriptedChatClient().ReplyGated(gate, [new TextContent("Thinking")], new TextContent(" done."));
        await using var fixture = await AiFixture.StartAsync(chat);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "ai.delta,ai.turn.ended", timeout.Token);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        await fixture.Client.SendAsync(session.Id, new AiSendRequest("Go"));
        await stream.NextAsync("ai.delta", timeout.Token);

        // The request has long been answered; the turn is still going.
        await Task.Delay(100, timeout.Token);
        Assert.NotEqual(AiSessionState.Idle, (await fixture.Client.GetSessionAsync(session.Id)).State);

        gate.SetResult();
        var ended = Deserialize(await stream.NextAsync("ai.turn.ended", timeout.Token), AiConversationJsonContext.Default.AiTurnEnded);
        Assert.Equal(AiTurnOutcome.Completed, ended.Outcome);
    }

    [Fact]
    public async Task Interrupts_the_running_turn()
    {
        var gate = new TaskCompletionSource();
        var chat = new ScriptedChatClient().ReplyGated(gate, [new TextContent("A long story")], new TextContent(" never told."));
        await using var fixture = await AiFixture.StartAsync(chat);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "ai.delta,ai.turn.ended", timeout.Token);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var none = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.InterruptAsync(session.Id));
        Assert.Equal("no_turn", none.Code);

        await fixture.Client.SendAsync(session.Id, new AiSendRequest("Tell me a story"));
        await stream.NextAsync("ai.delta", timeout.Token);
        await fixture.Client.InterruptAsync(session.Id);

        var ended = Deserialize(await stream.NextAsync("ai.turn.ended", timeout.Token), AiConversationJsonContext.Default.AiTurnEnded);
        Assert.Equal(AiTurnOutcome.Interrupted, ended.Outcome);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("hi", true)]
    [InlineData("   ", false)]
    public async Task Takes_text_or_an_answer_but_not_both(string? text, bool withAnswer)
    {
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient());
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest(text, withAnswer ? new AiAnswer("q", ["a"]) : null)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Sends_an_image_from_a_file_root_and_serves_it_back()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var chat = new ScriptedChatClient().Reply(new TextContent("A tiny picture."));
        await using var fixture = await AiFixture.StartAsync(chat);
        Assert.True(fixture.Roots.TryResolve("data", "photos/tiny.png", out var local));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, png);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        await fixture.Client.SendAsync(session.Id, new AiSendRequest("What is this?", Images: [new AiImageRef(new BridgeFile("data", "photos/tiny.png"), "a test image")], Wait: true));

        var sent = Assert.Single(chat.Requests.Single().SelectMany(x => x.Contents).OfType<DataContent>());
        Assert.Equal("image/png", sent.MediaType);
        Assert.Equal(png, sent.Data.ToArray());

        var image = (await fixture.Client.GetMessagesAsync(session.Id)).Messages.SelectMany(x => x.Parts).Single(x => x.Kind == AiPartKind.Image);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png, await fixture.Client.GetAttachmentAsync(image.AttachmentId!));

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest("And this?", Images: [new AiImageRef(new BridgeFile("data", "photos/none.png"))])));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await File.WriteAllTextAsync(Path.ChangeExtension(local, ".txt"), "not an image");
        var notImage = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SendAsync(session.Id, new AiSendRequest("And this?", Images: [new AiImageRef(new BridgeFile("data", "photos/tiny.txt"))])));
        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetAttachmentAsync("nope"))).StatusCode);
    }

    [Fact]
    public async Task Lists_carries_on_and_deletes_sessions()
    {
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient());

        Assert.Null(await fixture.Client.GetLatestSessionAsync());

        var first = await fixture.Client.StartSessionAsync(new AiStartSessionRequest(Profile: "kitchen"));
        var second = await fixture.Client.StartSessionAsync(new AiStartSessionRequest(Profile: "garage"));

        Assert.Equal(2, (await fixture.Client.GetSessionsAsync()).Count);
        Assert.Equal(first.Id, Assert.Single(await fixture.Client.GetSessionsAsync(profile: "kitchen")).Id);
        Assert.NotNull(await fixture.Client.GetLatestSessionAsync());

        await fixture.Client.DeleteSessionAsync(first.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetSessionAsync(first.Id))).StatusCode);
        Assert.Equal(second.Id, Assert.Single(await fixture.Client.GetSessionsAsync()).Id);
        Assert.Equal("Renamed", (await fixture.Client.SetTitleAsync(second.Id, new AiTitleRequest("Renamed"))).Title);
    }

    [Fact]
    public async Task Keeps_only_the_most_recent_sessions_open()
    {
        var chat = new ScriptedChatClient().Reply(new TextContent("Still here."));
        await using var fixture = await AiFixture.StartAsync(chat, o => o.MaxOpenSessions = 2);

        var oldest = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());
        await fixture.Client.StartSessionAsync(new AiStartSessionRequest());
        await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        Assert.Equal(2, (await fixture.Client.GetStatusAsync()).OpenSessions);

        // Closed, not gone: using it opens it again.
        var reply = await fixture.Client.SendAsync(oldest.Id, new AiSendRequest("Hello?", Wait: true));
        Assert.Equal("Still here.", reply.Reply?.Text);
        Assert.Equal(2, (await fixture.Client.GetStatusAsync()).OpenSessions);
    }

    [Fact]
    public async Task Reads_writes_and_forgets_memory()
    {
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient());

        var saved = await fixture.Client.SaveMemoryAsync(new AiMemorySaveRequest(AiMemoryScope.Global, "people/me.md", "Prefers tea.", Description: "About the user"));
        Assert.Equal(AiMemoryAuthor.User, saved.UpdatedBy);
        Assert.Equal("Prefers tea.", Assert.Single(await fixture.Client.GetMemoryAsync(AiMemoryScope.Global)).Content);

        var noKey = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SaveMemoryAsync(new AiMemorySaveRequest(AiMemoryScope.Session, "x.md", "y")));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        var escape = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SaveMemoryAsync(new AiMemorySaveRequest(AiMemoryScope.Global, "../x.md", "y")));
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);

        await fixture.Client.DeleteMemoryAsync(AiMemoryScope.Global, "people/me.md");
        Assert.Empty(await fixture.Client.GetMemoryAsync());
    }

    [Fact]
    public async Task The_voice_loop_needs_someone_listening_to_its_state()
    {
        var voice = new FakeVoice();
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient(), voice: voice);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AttachVoiceAsync(new AiVoiceRequest(session.Id)));

        Assert.Equal("not_listening", refused.Code);
        Assert.Null(voice.Session);
        Assert.Equal("not_attached", (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PushToTalkAsync())).Code);
    }

    [Fact]
    public async Task The_voice_loop_runs_while_listened_to_and_detaches_when_the_page_goes()
    {
        var voice = new FakeVoice();
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient(), voice: voice);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var states = await TestEventStream.OpenAsync(fixture.WebView, "ai.voice.state", timeout.Token);
        await using var transcripts = await TestEventStream.OpenAsync(fixture.WebView, "ai.voice.transcript", timeout.Token);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());

        var unknown = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AttachVoiceAsync(new AiVoiceRequest("nope")));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var attached = await fixture.Client.AttachVoiceAsync(new AiVoiceRequest(session.Id, WakeWord: "hey kitchen", Interruption: AiInterruptionMode.Full, Culture: "en-CA"));

        Assert.Equal(session.Id, attached.SessionId);
        Assert.Equal(session.Id, voice.Session?.SessionId);
        Assert.Equal("hey kitchen", voice.Options?.WakeWord);
        Assert.Equal(NativeVoice.InterruptionMode.Full, voice.Options?.Interruption);
        Assert.Equal("en-CA", voice.Options?.SpeechToText?.Culture?.Name);
        Assert.Equal(AiVoiceState.Listening, Deserialize(await states.NextAsync("ai.voice.state", timeout.Token), AiConversationJsonContext.Default.AiVoiceStateChanged).State);

        voice.Emit(new NativeVoice.FinalTranscript(DateTimeOffset.UtcNow, "turn on the oven"));
        var heard = Deserialize(await transcripts.NextAsync("ai.voice.transcript", timeout.Token), AiConversationJsonContext.Default.AiTranscript);
        Assert.Equal("turn on the oven", heard.Text);
        Assert.True(heard.Final);

        await fixture.Client.PushToTalkAsync();
        Assert.Equal(1, voice.PushedToTalk);

        // The page that was listening went away: nothing should be left waiting for a wake word.
        await states.DisposeAsync();
        await WaitUntil(() => voice.Session is null, timeout.Token);
        Assert.Equal("not_attached", (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PushToTalkAsync())).Code);
    }

    [Fact]
    public async Task Does_not_take_over_a_voice_loop_the_app_is_running()
    {
        var voice = new FakeVoice();
        await using var fixture = await AiFixture.StartAsync(new ScriptedChatClient(), voice: voice);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var states = await TestEventStream.OpenAsync(fixture.WebView, "ai.voice.state", timeout.Token);
        var session = await fixture.Client.StartSessionAsync(new AiStartSessionRequest());
        await voice.Attach(new FakeSession("the-apps-own"));

        var busy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.AttachVoiceAsync(new AiVoiceRequest(session.Id)));

        Assert.Equal("voice_busy", busy.Code);
        Assert.Equal("the-apps-own", voice.Session?.SessionId);
    }

    static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(20, cancellationToken);
    }

    static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(json, typeInfo)!;

    sealed class AiFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public AiConversationBridgeClient Client { get; private set; } = null!;
        public WebAppFileRoots Roots { get; private set; } = null!;

        /// <param name="chat">The model; null registers no AI conversations at all.</param>
        public static async Task<AiFixture> StartAsync(IChatClient? chat, Action<AiConversationBridgeOptions>? configure = null, NativeVoice.IVoiceConversation? voice = null)
        {
            var fixture = new AiFixture();
            var options = new AiConversationBridgeOptions();
            configure?.Invoke(options);

            var services = new ServiceCollection();
            services.AddLogging();
            if (chat is not null)
            {
                services.AddSingleton(chat);
                services.AddShinyAiConversation();
            }
            if (voice is not null)
                services.AddSingleton(voice);
            services.AddSingleton(options);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    fixture.Roots = new WebAppFileRoots(app.BridgeOptions());
                    return [new AiConversationBridge(provider, fixture.Roots)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new AiConversationBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    /// <summary>A model that plays back scripted replies in order, as Shiny.AiConversation's own tests do.</summary>
    sealed class ScriptedChatClient : IChatClient
    {
        readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> script = new();

        public List<List<ChatMessage>> Requests { get; } = [];

        public ScriptedChatClient Reply(params AIContent[] contents)
        {
            this.script.Enqueue(ct => Stream(contents, null, ct));
            return this;
        }

        /// <summary>Streams <paramref name="before"/>, waits for <paramref name="gate"/>, then streams <paramref name="after"/>.</summary>
        public ScriptedChatClient ReplyGated(TaskCompletionSource gate, AIContent[] before, params AIContent[] after)
        {
            this.script.Enqueue(ct => Stream(before, (gate, after), ct));
            return this;
        }

        public static FunctionCallContent AskUser(string callId, string questionsJson)
            => new(callId, "ask_user", new Dictionary<string, object?> { ["questions"] = JsonDocument.Parse(questionsJson).RootElement.Clone() });

        static async IAsyncEnumerable<ChatResponseUpdate> Stream(AIContent[] contents, (TaskCompletionSource Gate, AIContent[] After)? gated, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var content in contents)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { MessageId = "m" };

            if (gated is { } g)
            {
                await g.Gate.Task.WaitAsync(ct);
                foreach (var content in g.After)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { MessageId = "m" };
            }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            this.Requests.Add([.. messages]);
            if (this.script.Count == 0)
                throw new InvalidOperationException("No scripted reply left.");

            return this.script.Dequeue()(cancellationToken);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => this.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    sealed class FakeVoice : NativeVoice.IVoiceConversation
    {
        readonly Channel<NativeVoice.VoiceEvent> events = Channel.CreateUnbounded<NativeVoice.VoiceEvent>();

        public NativeVoice.VoiceState State { get; private set; }
        public Native.IAiSession? Session { get; private set; }
        public NativeVoice.VoiceOptions? Options { get; private set; }
        public Shiny.Speech.TextToSpeechOptions? TextToSpeech { get; set; }
        public int PushedToTalk { get; private set; }

        public void Emit(NativeVoice.VoiceEvent e) => this.events.Writer.TryWrite(e);

        // One reader: the bridge's pump.
        public IAsyncEnumerable<NativeVoice.VoiceEvent> GetEvents(CancellationToken ct = default) => this.events.Reader.ReadAllAsync(ct);

        public Task Attach(Native.IAiSession session, NativeVoice.VoiceOptions? options = null, CancellationToken ct = default)
        {
            this.Session = session;
            this.Options = options;
            this.Move(NativeVoice.VoiceState.Listening);
            return Task.CompletedTask;
        }

        public Task Detach()
        {
            this.Session = null;
            this.Move(NativeVoice.VoiceState.Off);
            return Task.CompletedTask;
        }

        public Task PushToTalk(CancellationToken ct = default)
        {
            this.PushedToTalk++;
            return Task.CompletedTask;
        }

        public Task StopSpeaking() => Task.CompletedTask;

        public Task Pause() => Task.CompletedTask;

        public Task Resume() => Task.CompletedTask;

        public Task<AccessState> RequestAccess() => Task.FromResult(AccessState.Available);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        void Move(NativeVoice.VoiceState state)
        {
            this.State = state;
            this.Emit(new NativeVoice.VoiceStateChanged(DateTimeOffset.UtcNow, state));
        }
    }

    /// <summary>A session the app attached the voice loop to itself — only its id matters.</summary>
    sealed class FakeSession(string id) : Native.IAiSession
    {
        public string SessionId => id;
        public string? Title => null;
        public DateTimeOffset Started => DateTimeOffset.UtcNow;
        public Native.AiSessionSettings Settings { get; } = new();
        public Native.AiSessionState State => Native.AiSessionState.Idle;
        public IReadOnlyList<Native.AiQuestion> PendingQuestions => [];
        public Native.SessionUsage Usage => Native.SessionUsage.Empty;
        public Native.ContextSnapshot Context => Native.ContextSnapshot.Empty;
        public Native.IAiTurn? ActiveTurn => null;

        public IAsyncEnumerable<Native.AiSessionEvent> GetEvents(CancellationToken ct = default) => throw new NotSupportedException();
        public Native.IAiTurn Send(Native.AiInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Native.MessagePage> GetMessages(long? beforeSequence, int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Compact(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetTitle(string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSettings(Func<Native.AiSessionSettings, Native.AiSessionSettings> update, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
