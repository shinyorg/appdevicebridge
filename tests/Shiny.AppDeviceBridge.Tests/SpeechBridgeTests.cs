using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Speech;
using Shiny.AppDeviceBridge.Speech.Client;
using Native = Shiny.Speech;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The speech bridge's synthesis over a fake <see cref="Native.ITextToSpeechService"/>: what a speak request becomes,
/// tone included. The OS engines themselves are Shiny.Speech's.
/// </summary>
public class SpeechBridgeTests
{
    [Fact]
    public async Task Passes_the_tone_to_the_engine()
    {
        var tts = new FakeTextToSpeech();
        await using var fixture = await SpeechFixture.StartAsync(tts);

        var result = await fixture.Client.SpeakAsync(new SpeakRequest(
            "We shipped it.",
            Rate: 1.2f,
            Tone: new SpeechTone(SpeechEmotion.Excited, 1.5f, "Like announcing good news to a friend")
        ));

        Assert.True(result?.Completed);
        var (text, options) = Assert.Single(tts.Spoken);
        Assert.Equal("We shipped it.", text);
        Assert.Equal(1.2f, options.SpeechRate);
        Assert.Equal(Native.SpeechEmotion.Excited, options.Tone?.Emotion);
        Assert.Equal(1.5f, options.Tone?.Intensity);
        Assert.Equal("Like announcing good news to a friend", options.Tone?.Instructions);
    }

    [Fact]
    public async Task Leaves_the_tone_unset_without_one_and_defaults_its_intensity()
    {
        var tts = new FakeTextToSpeech();
        await using var fixture = await SpeechFixture.StartAsync(tts);

        await fixture.Client.SpeakAsync(new SpeakRequest("Plain."));
        await fixture.Client.SpeakAsync(new SpeakRequest("Calm.", Tone: new SpeechTone(SpeechEmotion.Calm)));

        Assert.Null(tts.Spoken[0].Options.Tone);
        Assert.Equal(Native.SpeechEmotion.Calm, tts.Spoken[1].Options.Tone?.Emotion);
        Assert.Equal(1.0f, tts.Spoken[1].Options.Tone?.Intensity);
        Assert.Null(tts.Spoken[1].Options.Tone?.Instructions);
    }

    [Theory]
    [InlineData(-0.1f, 0)]
    [InlineData(2.5f, 0)]
    [InlineData(1f, 1001)]
    public async Task Refuses_a_tone_out_of_range(float intensity, int instructionsLength)
    {
        var tts = new FakeTextToSpeech();
        await using var fixture = await SpeechFixture.StartAsync(tts);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SpeakAsync(new SpeakRequest(
            "Hello",
            Tone: new SpeechTone(SpeechEmotion.Happy, intensity, instructionsLength == 0 ? null : new string('a', instructionsLength))
        )));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(tts.Spoken);
    }

    sealed class SpeechFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public SpeechBridgeClient Client { get; private set; } = null!;

        public static async Task<SpeechFixture> StartAsync(Native.ITextToSpeechService tts)
        {
            var fixture = new SpeechFixture();

            var services = new ServiceCollection();
            services.AddSingleton(tts);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new SpeechBridge(provider)]);
            fixture.Client = new SpeechBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    sealed class FakeTextToSpeech : Native.ITextToSpeechService
    {
        public List<(string Text, Native.TextToSpeechOptions Options)> Spoken { get; } = [];

        public bool IsSupported => true;
        public bool IsSpeaking => false;
        public bool CanSynthesizeToStream => false;
        public bool IsPlayerAnalysisSupported => false;

        public event EventHandler<double>? AudioLevelChanged { add { } remove { } }

        public Task<IReadOnlyList<Native.VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Native.VoiceInfo>>([]);

        public Task SpeakAsync(string text, Native.TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        {
            this.Spoken.Add((text, options ?? new Native.TextToSpeechOptions()));
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public Task<Stream> SynthesizeToStreamAsync(string text, Native.TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
