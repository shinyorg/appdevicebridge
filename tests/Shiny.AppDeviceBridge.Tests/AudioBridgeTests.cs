using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shiny.AppDeviceBridge.Audio;
using Shiny.AppDeviceBridge.Audio.Client;
using Shiny.AppDeviceBridge.Client;
using Native = Shiny.Audio;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The audio bridge over fake Shiny.Audio services: playing clips from a URL or a file root and hearing them end, the
/// volume, recording to a WAV filed into a file root, the time limit, routes, and the monitor that belongs to whoever
/// listens to its level. AVAudioEngine, AAudio, WASAPI and PulseAudio themselves are Shiny.Audio's and need real hardware.
/// </summary>
public class AudioBridgeTests
{
    static readonly Native.AudioDevice BuiltInMic = new("mic-1", "iPhone Microphone", Native.AudioDeviceIo.Input, Native.AudioDeviceType.BuiltInMic, true);
    static readonly Native.AudioDevice Headset = new("bt-1", "AirPods", Native.AudioDeviceIo.Input, Native.AudioDeviceType.Bluetooth, false);
    static readonly Native.AudioDevice Speaker = new("spk-1", "Speaker", Native.AudioDeviceIo.Output, Native.AudioDeviceType.BuiltInSpeaker, true);

    [Fact]
    public async Task Answers_501_without_audio_services()
    {
        await using var fixture = await AudioFixture.StartAsync(new AudioServices());

        var status = await fixture.Client.GetStatusAsync();
        Assert.False(status.PlaybackSupported);
        Assert.False(status.RecordingSupported);
        Assert.False(status.DevicesSupported);
        Assert.False(status.MonitorSupported);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PlayAsync(new AudioPlayRequest(Url: "https://example.com/a.mp3")))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync(new AudioRecordingRequest()))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetDevicesAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitorAsync(new AudioMonitorRequest()))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync())).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "audio").IsSupported);
    }

    [Fact]
    public async Task Plays_a_url_and_says_when_it_ends()
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.playback.ended", timeout.Token);

        var playing = await fixture.Client.PlayAsync(new AudioPlayRequest(Url: "https://example.com/ding.mp3"));

        Assert.Equal("https://example.com/ding.mp3", Assert.Single(player.Sources));
        Assert.Equal("https://example.com/ding.mp3", playing.Url);
        Assert.Null(playing.File);

        // Still playing once the request that started it has finished.
        await Task.Delay(100, timeout.Token);
        Assert.True(player.Started.Single().IsPlaying);
        Assert.Equal(playing, Assert.Single(await fixture.Client.GetPlaybackAsync()));
        Assert.Equal(1, (await fixture.Client.GetStatusAsync()).Playing);

        player.Started.Single().Finish();

        var ended = Deserialize(await stream.NextAsync("audio.playback.ended", timeout.Token), AudioJsonContext.Default.AudioPlaybackEnded);
        Assert.Equal(playing.Id, ended.Id);
        Assert.False(ended.Stopped);
        Assert.Null(ended.Error);
        Assert.Empty(await fixture.Client.GetPlaybackAsync());
    }

    [Fact]
    public async Task Plays_a_file_from_a_file_root()
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });

        Assert.True(fixture.Roots.TryResolve("data", "sounds/ding.wav", out var local));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, [1, 2, 3]);

        var playing = await fixture.Client.PlayAsync(new AudioPlayRequest(File: new BridgeFile("data", "sounds/ding.wav")));

        Assert.Equal(local, Assert.Single(player.Sources));
        Assert.Equal(new BridgeFile("data", "sounds/ding.wav"), playing.File);
        Assert.Null(playing.Url);

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PlayAsync(new AudioPlayRequest(File: new BridgeFile("data", "sounds/nope.wav"))));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var noRoot = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PlayAsync(new AudioPlayRequest(File: new BridgeFile("elsewhere", "ding.wav"))));
        Assert.Equal(HttpStatusCode.NotFound, noRoot.StatusCode);

        var escape = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PlayAsync(new AudioPlayRequest(File: new BridgeFile("data", "../ding.wav"))));
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://example.com/a.mp3", true)]
    [InlineData("ftp://example.com/a.mp3", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/sounds/a.mp3", false)]
    public async Task Takes_exactly_one_source_and_only_http_urls(string? url, bool withFile)
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PlayAsync(new AudioPlayRequest(url, withFile ? new BridgeFile("data", "a.mp3") : null)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(player.Sources);
    }

    [Fact]
    public async Task Stops_one_clip_and_leaves_the_others()
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.playback.ended", timeout.Token);

        var music = await fixture.Client.PlayAsync(new AudioPlayRequest(Url: "https://example.com/music.mp3"));
        var effect = await fixture.Client.PlayAsync(new AudioPlayRequest(Url: "https://example.com/effect.mp3"));

        await fixture.Client.StopPlaybackAsync(effect.Id);

        var ended = Deserialize(await stream.NextAsync("audio.playback.ended", timeout.Token), AudioJsonContext.Default.AudioPlaybackEnded);
        Assert.Equal(effect.Id, ended.Id);
        Assert.True(ended.Stopped);
        Assert.Equal(music, Assert.Single(await fixture.Client.GetPlaybackAsync()));

        var unknown = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopPlaybackAsync(effect.Id));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await fixture.Client.StopAllPlaybackAsync();
        var last = Deserialize(await stream.NextAsync("audio.playback.ended", timeout.Token), AudioJsonContext.Default.AudioPlaybackEnded);
        Assert.Equal(music.Id, last.Id);
        Assert.True(last.Stopped);
    }

    [Fact]
    public async Task Reports_a_clip_that_failed()
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.playback.ended", timeout.Token);

        await fixture.Client.PlayAsync(new AudioPlayRequest(Url: "https://example.com/broken.mp3"));
        player.Started.Single().Fail("Unsupported format");

        var ended = Deserialize(await stream.NextAsync("audio.playback.ended", timeout.Token), AudioJsonContext.Default.AudioPlaybackEnded);
        Assert.Equal("Unsupported format", ended.Error);
    }

    [Fact]
    public async Task Sets_the_volume_only_where_the_platform_can()
    {
        var player = new FakePlayer { VolumeSettable = true };
        await using (var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player }))
        {
            Assert.True((await fixture.Client.GetStatusAsync()).VolumeControlSupported);
            Assert.Equal(0.25f, (await fixture.Client.SetVolumeAsync(new AudioVolumeRequest(0.25f))).Volume);
            Assert.Equal(0.25f, player.Volume);

            var outOfRange = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetVolumeAsync(new AudioVolumeRequest(1.5f)));
            Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        }

        await using (var apple = await AudioFixture.StartAsync(new AudioServices { Player = new FakePlayer { VolumeSettable = false } }))
        {
            var status = await apple.Client.GetStatusAsync();
            Assert.False(status.VolumeControlSupported);
            Assert.NotNull(status.Volume);
            Assert.True((await Assert.ThrowsAsync<BridgeException>(() => apple.Client.SetVolumeAsync(new AudioVolumeRequest(0.5f)))).IsNotSupported);
        }
    }

    [Fact]
    public async Task Forwards_volume_changes()
    {
        var player = new FakePlayer();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Player = player });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.volume", timeout.Token);

        player.RaiseVolume(0.4f);

        Assert.Equal(0.4f, Deserialize(await stream.NextAsync("audio.volume", timeout.Token), AudioJsonContext.Default.AudioVolume).Volume);
    }

    [Fact]
    public async Task Records_with_an_effect_and_files_both_takes()
    {
        var recorder = new FakeRecorder();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = recorder, Devices = new FakeDevices() });

        var started = await fixture.Client.StartRecordingAsync(new AudioRecordingRequest(
            AudioRecordMode.Both,
            AudioEffectPreset.Robot,
            "bt-1",
            new AudioProcessing(NoiseSuppression: true)
        ));
        Assert.True(started.Recording);

        var options = Assert.Single(recorder.Starts);
        Assert.Equal(Native.AudioRecordMode.Both, options.Mode);
        Assert.NotNull(options.Effects);
        Assert.NotEmpty(options.Effects!.Effects);
        Assert.Equal("bt-1", options.InputDevice?.Id);
        Assert.True(options.Processing?.NoiseSuppression);
        Assert.False(options.Processing?.EchoCancellation);

        var recording = await fixture.Client.StopRecordingAsync();

        Assert.Equal(new BridgeFile("data", "audio-recordings/REC_20261030-101502.wav"), recording.File);
        Assert.Equal(new BridgeFile("data", "audio-recordings/REC_20261030-101502-dry.wav"), recording.DryFile);
        Assert.Equal("audio/wav", recording.MimeType);
        Assert.Equal((16000, 1), (recording.SampleRate, recording.Channels));
        Assert.Equal(FakeRecorder.Wet.Length, recording.Size);

        Assert.True(fixture.Roots.TryResolve("data", recording.File.Path, out var wet));
        Assert.Equal(FakeRecorder.Wet, await File.ReadAllBytesAsync(wet));
        Assert.True(fixture.Roots.TryResolve("data", recording.DryFile!.Path, out var dry));
        Assert.Equal(FakeRecorder.Dry, await File.ReadAllBytesAsync(dry));
        Assert.False(File.Exists(options.Path), "the temporary take is deleted once filed");

        Assert.False((await fixture.Client.GetStatusAsync()).Recording);
    }

    [Fact]
    public async Task Two_recordings_in_the_same_second_do_not_overwrite_each_other()
    {
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = new FakeRecorder() });

        await fixture.Client.StartRecordingAsync(new AudioRecordingRequest());
        var first = await fixture.Client.StopRecordingAsync();
        await fixture.Client.StartRecordingAsync(new AudioRecordingRequest());
        var second = await fixture.Client.StopRecordingAsync();

        Assert.Equal("audio-recordings/REC_20261030-101502.wav", first.File.Path);
        Assert.Equal("audio-recordings/REC_20261030-101502-2.wav", second.File.Path);
        Assert.Null(first.DryFile);
    }

    [Fact]
    public async Task Refuses_an_unknown_input_and_a_denied_microphone()
    {
        var recorder = new FakeRecorder();
        await using (var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = recorder, Devices = new FakeDevices() }))
        {
            var unknown = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync(new AudioRecordingRequest(InputDeviceId: "nope")));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

            var output = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync(new AudioRecordingRequest(InputDeviceId: "spk-1")));
            Assert.Equal(HttpStatusCode.BadRequest, output.StatusCode);
        }

        await using (var denied = await AudioFixture.StartAsync(new AudioServices { Recorder = new FakeRecorder { Access = AccessState.Denied } }))
        {
            var refused = await Assert.ThrowsAsync<BridgeException>(() => denied.Client.StartRecordingAsync(new AudioRecordingRequest()));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("permission_denied", refused.Code);
            Assert.False((await denied.Client.GetStatusAsync()).Recording);
        }

        Assert.Empty(recorder.Starts);
    }

    [Fact]
    public async Task One_microphone_between_recording_and_monitor()
    {
        var monitor = new FakeMonitor();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = new FakeRecorder(), Monitor = monitor });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.monitor.level", timeout.Token);

        await fixture.Client.StartRecordingAsync(new AudioRecordingRequest());

        var again = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync(new AudioRecordingRequest()));
        Assert.Equal("microphone_busy", again.Code);

        var monitorBusy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitorAsync(new AudioMonitorRequest()));
        Assert.Equal(HttpStatusCode.Conflict, monitorBusy.StatusCode);
        Assert.Equal("microphone_busy", monitorBusy.Code);
        Assert.False(monitor.IsMonitoring);

        await fixture.Client.StopRecordingAsync();
        await fixture.Client.StartMonitorAsync(new AudioMonitorRequest());

        var recordingBusy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync(new AudioRecordingRequest()));
        Assert.Equal("microphone_busy", recordingBusy.Code);
    }

    [Fact]
    public async Task Stops_and_files_the_recording_when_the_time_limit_runs_out()
    {
        await using var fixture = await AudioFixture.StartAsync(
            new AudioServices { Recorder = new FakeRecorder() },
            o => o.MaxRecordingDuration = TimeSpan.FromSeconds(30)
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.recording.ended", timeout.Token);

        // Asking for longer than the app allows gets the app's limit.
        var started = await fixture.Client.StartRecordingAsync(new AudioRecordingRequest(MaxDurationSeconds: 600));
        Assert.Equal(30, started.MaxRecordingSeconds);

        fixture.Time.Advance(TimeSpan.FromSeconds(30));

        var ended = Deserialize(await stream.NextAsync("audio.recording.ended", timeout.Token), AudioJsonContext.Default.AudioRecordingEnded);
        Assert.Equal(AudioRecordingEndReason.MaxDurationReached, ended.Reason);
        Assert.Equal("audio-recordings/REC_20261030-101532.wav", ended.Recording?.File.Path);
        Assert.False((await fixture.Client.GetStatusAsync()).Recording);
    }

    [Fact]
    public async Task Cancelling_keeps_nothing()
    {
        var recorder = new FakeRecorder();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = recorder });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.recording.ended", timeout.Token);

        await fixture.Client.StartRecordingAsync(new AudioRecordingRequest());
        await fixture.Client.CancelRecordingAsync();

        var ended = Deserialize(await stream.NextAsync("audio.recording.ended", timeout.Token), AudioJsonContext.Default.AudioRecordingEnded);
        Assert.Equal(AudioRecordingEndReason.Cancelled, ended.Reason);
        Assert.Null(ended.Recording);
        Assert.False(File.Exists(recorder.Starts.Single().Path));
        Assert.True(fixture.Roots.TryResolve("data", "audio-recordings", out var folder));
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any());

        var nothing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopRecordingAsync());
        Assert.Equal("not_recording", nothing.Code);
        Assert.Equal("not_recording", (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CancelRecordingAsync())).Code);
    }

    [Fact]
    public async Task Forwards_the_recording_level()
    {
        var recorder = new FakeRecorder();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Recorder = recorder });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.recording.level", timeout.Token);

        await fixture.Client.StartRecordingAsync(new AudioRecordingRequest());
        recorder.RaiseLevel(0.6);

        Assert.Equal(0.6, Deserialize(await stream.NextAsync("audio.recording.level", timeout.Token), AudioJsonContext.Default.AudioLevelChanged).Level);
    }

    [Fact]
    public async Task Lists_routes_and_announces_changes()
    {
        var devices = new FakeDevices();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Devices = devices });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.devices", timeout.Token);

        var listed = await fixture.Client.GetDevicesAsync();
        Assert.Equal(["mic-1", "bt-1"], listed.Inputs.Select(x => x.Id));
        Assert.Equal(AudioDeviceType.Bluetooth, listed.Inputs[1].Type);
        Assert.Equal("spk-1", listed.CurrentOutput?.Id);

        await fixture.Client.ShowOutputPickerAsync();
        Assert.Equal(1, devices.PickerShown);

        devices.Inputs.Remove(Headset);
        devices.RaiseChanged();

        var changed = Deserialize(await stream.NextAsync("audio.devices", timeout.Token), AudioJsonContext.Default.AudioDevices);
        Assert.Equal("mic-1", Assert.Single(changed.Inputs).Id);
    }

    [Fact]
    public async Task The_monitor_needs_someone_listening_to_its_level()
    {
        var monitor = new FakeMonitor();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Monitor = monitor });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitorAsync(new AudioMonitorRequest()));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_listening", refused.Code);
        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public async Task The_monitor_runs_while_listened_to_and_stops_when_the_page_goes()
    {
        var monitor = new FakeMonitor();
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Monitor = monitor, Devices = new FakeDevices() });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var ended = await TestEventStream.OpenAsync(fixture.WebView, "audio.monitor.ended", timeout.Token);
        var levels = await TestEventStream.OpenAsync(fixture.WebView, "audio.monitor.level", timeout.Token);

        var started = await fixture.Client.StartMonitorAsync(new AudioMonitorRequest(Gain: 0.8, DuckOtherAudio: true, OutputDeviceId: "spk-1", Processing: new AudioProcessing(EchoCancellation: true)));
        Assert.Equal(0.8, started.Gain);
        Assert.True(monitor.IsMonitoring);
        Assert.True(monitor.Options?.DuckOtherAudio);
        Assert.Equal("spk-1", monitor.Options?.OutputDevice?.Id);
        Assert.True(monitor.Options?.Processing?.EchoCancellation);
        Assert.True((await fixture.Client.GetStatusAsync()).Monitoring);

        monitor.RaiseLevel(0.3);
        Assert.Equal(0.3, Deserialize(await levels.NextAsync("audio.monitor.level", timeout.Token), AudioJsonContext.Default.AudioLevelChanged).Level);

        Assert.Equal(0.5, (await fixture.Client.SetMonitorGainAsync(new AudioMonitorGainRequest(0.5))).Gain);
        Assert.Equal(0.5, monitor.Gain);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetMonitorGainAsync(new AudioMonitorGainRequest(2)))).StatusCode);

        // The page that was listening went away: nobody is left to hear the microphone on the speaker.
        await levels.DisposeAsync();

        var stopped = Deserialize(await ended.NextAsync("audio.monitor.ended", timeout.Token), AudioJsonContext.Default.AudioMonitorEnded);
        Assert.Equal(AudioMonitorEndReason.PageClosed, stopped.Reason);
        Assert.False(monitor.IsMonitoring);

        var notRunning = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetMonitorGainAsync(new AudioMonitorGainRequest(0.5)));
        Assert.Equal("not_monitoring", notRunning.Code);
    }

    [Fact]
    public async Task Does_not_take_over_a_monitor_the_app_is_running()
    {
        var monitor = new FakeMonitor { IsMonitoring = true };
        await using var fixture = await AudioFixture.StartAsync(new AudioServices { Monitor = monitor });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "audio.monitor.level", timeout.Token);

        var busy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitorAsync(new AudioMonitorRequest()));

        Assert.Equal("microphone_busy", busy.Code);
        Assert.Null(monitor.Options);
    }

    static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(json, typeInfo)!;

    sealed record AudioServices
    {
        public Native.IAudioPlayer? Player { get; init; }
        public Native.IAudioRecorder? Recorder { get; init; }
        public Native.IAudioDevices? Devices { get; init; }
        public Native.IAudioMonitor? Monitor { get; init; }
    }

    sealed class AudioFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public AudioBridgeClient Client { get; private set; } = null!;
        public WebAppFileRoots Roots { get; private set; } = null!;
        public AudioBridgeOptions Options { get; } = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 30, 10, 15, 2, TimeSpan.Zero));

        public static async Task<AudioFixture> StartAsync(AudioServices audio, Action<AudioBridgeOptions>? configure = null)
        {
            var fixture = new AudioFixture();
            configure?.Invoke(fixture.Options);

            var services = new ServiceCollection();
            if (audio.Player is not null)
                services.AddSingleton(audio.Player);
            if (audio.Recorder is not null)
                services.AddSingleton(audio.Recorder);
            if (audio.Devices is not null)
                services.AddSingleton(audio.Devices);
            if (audio.Monitor is not null)
                services.AddSingleton(audio.Monitor);
            services.AddSingleton(fixture.Options);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            services.AddSingleton<TimeProvider>(fixture.Time);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    fixture.Roots = new WebAppFileRoots(app.BridgeOptions());
                    return [new AudioBridge(provider, fixture.Roots)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new AudioBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    sealed class FakePlayback(string source) : Native.IAudioPlayback
    {
        readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid Id { get; } = Guid.NewGuid();
        public string? Source { get; } = source;
        public bool IsPlaying => !this.done.Task.IsCompleted;
        public Task Completion => this.done.Task;

        public void Finish() => this.done.TrySetResult();

        public void Fail(string message) => this.done.TrySetException(new InvalidOperationException(message));

        public Task StopAsync()
        {
            this.done.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.done.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakePlayer : Native.IAudioPlayer
    {
        public List<string> Sources { get; } = [];
        public List<FakePlayback> Started { get; } = [];
        public bool VolumeSettable { get; init; } = true;

        public IReadOnlyList<Native.IAudioPlayback> Active => [.. this.Started.Where(x => x.IsPlaying)];
        public bool IsPlaying => this.Active.Count > 0;
        public bool IsPlayerAnalysisSupported => true;
        public bool IsVolumeControlSupported => this.VolumeSettable;

        float volume = 0.7f;
        public float Volume
        {
            get => this.volume;
            set
            {
                if (!this.VolumeSettable)
                    throw new NotSupportedException();

                this.volume = value;
            }
        }

        public event EventHandler<double>? AudioLevelChanged;
        public event EventHandler<float>? VolumeChanged;

        public Task<Native.IAudioPlayback> StartAsync(Stream audioStream, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The bridge plays URLs and files.");

        public Task<Native.IAudioPlayback> StartAsync(string source, CancellationToken cancellationToken = default)
        {
            this.Sources.Add(source);
            var playback = new FakePlayback(source);
            this.Started.Add(playback);

            // As Shiny.Audio does: the token a clip starts with is its lifetime, and cancelling it ends the clip.
            cancellationToken.Register(playback.Finish);
            return Task.FromResult<Native.IAudioPlayback>(playback);
        }

        public async Task StopAsync()
        {
            foreach (var playback in this.Started)
                await playback.StopAsync();
        }

        public void RaiseVolume(float value) => this.VolumeChanged?.Invoke(this, value);

        public void RaiseLevel(double value) => this.AudioLevelChanged?.Invoke(this, value);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeRecorder : Native.IAudioRecorder
    {
        public static readonly byte[] Wet = [.. "RIFFwet"u8];
        public static readonly byte[] Dry = [.. "RIFFdry"u8];

        Native.AudioRecordingOptions? current;

        public AccessState Access { get; init; } = AccessState.Available;
        public List<Native.AudioRecordingOptions> Starts { get; } = [];
        public bool IsRecording => this.current is not null;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(this.IsRecording ? 3 : 0);

        public event EventHandler<double>? InputLevelChanged;

        public Task<AccessState> RequestAccess() => Task.FromResult(this.Access);

        public async Task StartAsync(Native.AudioRecordingOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (this.current is not null)
                throw new InvalidOperationException("Already recording.");

            options ??= new Native.AudioRecordingOptions();
            this.Starts.Add(options);
            this.current = options;

            Directory.CreateDirectory(Path.GetDirectoryName(options.Path!)!);
            await File.WriteAllBytesAsync(options.Path!, Wet, cancellationToken);
            if (options.Mode == Native.AudioRecordMode.Both)
                await File.WriteAllBytesAsync(DryPath(options.Path!), Dry, cancellationToken);
        }

        public Task<Native.AudioRecording?> StopAsync()
        {
            var options = this.current;
            this.current = null;

            return Task.FromResult<Native.AudioRecording?>(options is null
                ? null
                : new Native.AudioRecording(
                    options.Path!,
                    options.Mode == Native.AudioRecordMode.Both ? DryPath(options.Path!) : null,
                    TimeSpan.FromSeconds(3),
                    16000,
                    1,
                    Wet.Length
                ));
        }

        public void RaiseLevel(double level) => this.InputLevelChanged?.Invoke(this, level);

        static string DryPath(string path) => Path.ChangeExtension(path, null) + ".dry.wav";

        // The bridge disposes each recorder it is handed; this one is shared by every recording in a test.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeDevices : Native.IAudioDevices
    {
        public List<Native.AudioDevice> Inputs { get; } = [BuiltInMic, Headset];
        public List<Native.AudioDevice> Outputs { get; } = [Speaker];
        public int PickerShown { get; private set; }

        public Native.AudioDevice? CurrentInput => this.Inputs.FirstOrDefault(x => x.IsCurrent);
        public Native.AudioDevice? CurrentOutput => this.Outputs.FirstOrDefault(x => x.IsCurrent);

        public event EventHandler? Changed;

        public IReadOnlyList<Native.AudioDevice> GetInputs() => this.Inputs;

        public IReadOnlyList<Native.AudioDevice> GetOutputs() => this.Outputs;

        public Task ShowOutputPicker()
        {
            this.PickerShown++;
            return Task.CompletedTask;
        }

        public void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    sealed class FakeMonitor : Native.IAudioMonitor
    {
        public bool IsMonitoring { get; set; }
        public double Gain { get; set; } = 1.0;
        public Native.AudioMonitorOptions? Options { get; private set; }

        public event EventHandler<double>? InputLevelChanged;

        public Task<AccessState> RequestAccess() => Task.FromResult(AccessState.Available);

        public Task Start(Native.AudioMonitorOptions? options = null, CancellationToken cancellationToken = default)
        {
            this.Options = options;
            this.Gain = options?.Gain ?? 1.0;
            this.IsMonitoring = true;
            return Task.CompletedTask;
        }

        public Task Stop()
        {
            this.IsMonitoring = false;
            return Task.CompletedTask;
        }

        public Task SetInputDevice(Native.AudioDevice? device) => Task.CompletedTask;

        public Task SetOutputDevice(Native.AudioDevice? device) => Task.CompletedTask;

        public void RaiseLevel(double level) => this.InputLevelChanged?.Invoke(this, level);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
