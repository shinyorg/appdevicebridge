using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.ScreenRecorder;
using Shiny.AppDeviceBridge.ScreenRecorder.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.ScreenRecorder;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The screen recorder bridge over a fake <see cref="Native.IScreenRecorder"/>: the routes, one recording at a time, the
/// checks made before anyone is asked anything, the app's time limit and confirmation, filing the finished video into a
/// file root, and a recording the device ended on its own. MediaProjection, ReplayKit, ScreenCaptureKit,
/// Windows.Graphics.Capture and the portal themselves are Shiny.ScreenRecorder's and need a real screen.
/// </summary>
public class ScreenRecorderBridgeTests
{
    const Native.ScreenRecorderCapabilities Desktop =
        Native.ScreenRecorderCapabilities.Recording
        | Native.ScreenRecorderCapabilities.PauseResume
        | Native.ScreenRecorderCapabilities.Microphone
        | Native.ScreenRecorderCapabilities.DisplaySelection
        | Native.ScreenRecorderCapabilities.WindowSelection
        | Native.ScreenRecorderCapabilities.FrameRateControl
        | Native.ScreenRecorderCapabilities.Downscaling;

    [Fact]
    public async Task Answers_501_without_a_recorder()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder: null);

        var status = await fixture.Client.GetStatusAsync();
        Assert.False(status.Supported);
        Assert.Empty(status.Capabilities);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest()));
        Assert.True(refused.IsNotSupported);
        await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync(new ScreenRecorderAccessRequest()));
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "screenrecorder").IsSupported);
    }

    [Fact]
    public async Task Answers_501_where_the_platform_cannot_record()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Native.ScreenRecorderCapabilities.None });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest()));

        Assert.True(refused.IsNotSupported);
        Assert.False((await fixture.Client.GetStatusAsync()).Supported);
    }

    [Fact]
    public async Task Reports_capabilities_without_display_or_window_selection()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Desktop });

        var status = await fixture.Client.GetStatusAsync();

        Assert.True(status.Supported);
        Assert.Equal(
            [
                ScreenRecorderCapability.Recording,
                ScreenRecorderCapability.PauseResume,
                ScreenRecorderCapability.Microphone,
                ScreenRecorderCapability.FrameRateControl,
                ScreenRecorderCapability.Downscaling
            ],
            status.Capabilities
        );
        Assert.Equal(ScreenRecorderState.Idle, status.State);
        Assert.Null(status.ElapsedSeconds);
        Assert.Equal(3600, status.MaxDurationSeconds);
    }

    [Fact]
    public async Task Records_stops_and_files_the_video()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var started = await fixture.Client.StartAsync(new ScreenRecordingRequest(IncludeMicrophone: true, FrameRate: 24, MaxWidth: 1280));
        Assert.Equal(ScreenRecorderState.Recording, started.State);
        Assert.NotNull(started.ElapsedSeconds);

        var request = Assert.Single(recorder.Requests);
        Assert.True(request.IncludeMicrophone);
        Assert.Equal(24, request.FrameRate);
        Assert.Equal(1280, request.MaxWidth);
        Assert.True(request.ShowCursor);

        var recording = await fixture.Client.StopAsync();

        Assert.Equal("data", recording.File.Root);
        Assert.Equal("screen-recordings/REC_20260924-201502.mp4", recording.File.Path);
        Assert.Equal(FakeScreenRecording.Content.Length, recording.Size);
        Assert.Equal((1280, 720), (recording.Width, recording.Height));
        Assert.Equal(12.5, recording.DurationSeconds);
        Assert.Equal("video/mp4", recording.MimeType);

        Assert.True(fixture.Roots.TryResolve("data", recording.File.Path, out var filed));
        Assert.Equal(FakeScreenRecording.Content, await File.ReadAllBytesAsync(filed));
        Assert.False(File.Exists(recorder.Recordings.Single().OutputPath), "the platform's copy is deleted once filed");

        var status = await fixture.Client.GetStatusAsync();
        Assert.Equal(ScreenRecorderState.Idle, status.State);
        Assert.Equal(recording, status.LastRecording);
    }

    [Fact]
    public async Task Two_recordings_in_the_same_second_do_not_overwrite_each_other()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Desktop });

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        var first = await fixture.Client.StopAsync();
        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        var second = await fixture.Client.StopAsync();

        Assert.Equal("screen-recordings/REC_20260924-201502.mp4", first.File.Path);
        Assert.Equal("screen-recordings/REC_20260924-201502-2.mp4", second.File.Path);
    }

    [Fact]
    public async Task One_recording_at_a_time()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        var busy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest()));

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("recording_busy", busy.Code);
        Assert.Single(recorder.Requests);
    }

    [Fact]
    public async Task A_recording_the_app_started_itself_makes_the_bridge_busy()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop, State = Native.ScreenRecorderState.Recording };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var busy = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest()));

        Assert.Equal("recording_busy", busy.Code);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Stop_pause_and_resume_need_a_recording_and_cancel_does_not()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Desktop });

        foreach (var call in new Func<Task>[] { () => fixture.Client.StopAsync(), () => fixture.Client.PauseAsync(), () => fixture.Client.ResumeAsync() })
        {
            var refused = await Assert.ThrowsAsync<BridgeException>(call);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("not_recording", refused.Code);
        }

        await fixture.Client.CancelAsync();
    }

    [Fact]
    public async Task A_setting_the_device_cannot_honour_is_a_501_before_anything_starts()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Native.ScreenRecorderCapabilities.Recording };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest(IncludeSystemAudio: true)));

        Assert.True(refused.IsNotSupported);
        Assert.Contains("SystemAudio", refused.Message);
        Assert.Empty(recorder.Requests);
    }

    [Theory]
    [InlineData("""{ "maxDurationSeconds": -1 }""")]
    [InlineData("""{ "frameRate": 0 }""")]
    [InlineData("""{ "maxWidth": -5 }""")]
    [InlineData("not json")]
    public async Task Refuses_bodies_it_cannot_use(string body)
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop | Native.ScreenRecorderCapabilities.BitrateControl };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var response = await fixture.WebView.PostAsync("/_bridge/screenrecorder/recording", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Starts_with_no_body()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var response = await fixture.WebView.PostAsync("/_bridge/screenrecorder/recording", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(recorder.Requests).IncludeMicrophone);
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData(60d, 60)]
    [InlineData(7200d, 3600)]
    public async Task The_apps_limit_caps_every_recording(double? requested, double applied)
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        await fixture.Client.StartAsync(new ScreenRecordingRequest(MaxDurationSeconds: requested));

        Assert.Equal(TimeSpan.FromSeconds(applied), Assert.Single(recorder.Requests).MaxDuration);
    }

    [Fact]
    public async Task No_limit_when_the_app_removes_it()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder, o => o.MaxDuration = null);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());

        Assert.Null(Assert.Single(recorder.Requests).MaxDuration);
        Assert.Null((await fixture.Client.GetStatusAsync()).MaxDurationSeconds);
    }

    [Fact]
    public async Task The_app_can_decline_a_recording()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        Native.ScreenRecordingRequest? asked = null;
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder, o => o.ConfirmStart = (request, _) =>
        {
            asked = request;
            return Task.FromResult(false);
        });

        var declined = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest(IncludeMicrophone: true)));

        Assert.Equal(HttpStatusCode.Forbidden, declined.StatusCode);
        Assert.Equal("declined", declined.Code);
        Assert.True(asked!.IncludeMicrophone);
        Assert.Empty(recorder.Requests);

        // Declining released the recorder.
        fixture.Options.ConfirmStart = null;
        await fixture.Client.StartAsync(new ScreenRecordingRequest());
    }

    [Fact]
    public async Task A_declined_consent_is_a_403_and_releases_the_recorder()
    {
        var recorder = new FakeScreenRecorder
        {
            Capabilities = Desktop,
            StartThrows = new Native.ScreenRecorderPermissionException("The user declined the capture.")
        };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var denied = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync(new ScreenRecordingRequest()));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("permission_denied", denied.Code);

        recorder.StartThrows = null;
        Assert.Equal(ScreenRecorderState.Recording, (await fixture.Client.StartAsync(new ScreenRecordingRequest())).State);
    }

    [Fact]
    public async Task Requests_access_for_the_audio_asked_for()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop, Access = Shiny.AccessState.Denied };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);

        var result = await fixture.Client.RequestAccessAsync(new ScreenRecorderAccessRequest(IncludeMicrophone: true));

        Assert.Equal(Client.AccessState.Denied, result.Access);
        Assert.True(Assert.Single(recorder.AccessRequests).IncludeMicrophone);
    }

    [Fact]
    public async Task Pauses_and_resumes_where_the_device_can()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);
        await fixture.Client.StartAsync(new ScreenRecordingRequest());

        await fixture.Client.PauseAsync();
        Assert.Equal(ScreenRecorderState.Paused, (await fixture.Client.GetStatusAsync()).State);

        await fixture.Client.ResumeAsync();
        Assert.Equal(ScreenRecorderState.Recording, (await fixture.Client.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Pausing_where_the_device_cannot_is_a_501()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Native.ScreenRecorderCapabilities.Recording });
        await fixture.Client.StartAsync(new ScreenRecordingRequest());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.PauseAsync());

        Assert.True(refused.IsNotSupported);
    }

    [Fact]
    public async Task Cancelling_keeps_nothing_and_says_so()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "screenrecorder.ended", timeout.Token);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        await fixture.Client.CancelAsync();

        var ended = Deserialize<ScreenRecordingEnded>(await stream.NextAsync("screenrecorder.ended", timeout.Token));
        Assert.Equal(ScreenRecordingEndReason.Cancelled, ended.Reason);
        Assert.Null(ended.Recording);
        Assert.True(recorder.Recordings.Single().Cancelled);
        Assert.False(File.Exists(recorder.Recordings.Single().OutputPath));
        Assert.Null((await fixture.Client.GetStatusAsync()).LastRecording);
    }

    [Fact]
    public async Task A_stop_is_announced_with_the_recording()
    {
        await using var fixture = await ScreenRecorderFixture.StartAsync(new FakeScreenRecorder { Capabilities = Desktop });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "screenrecorder.ended", timeout.Token);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        var recording = await fixture.Client.StopAsync();

        var ended = Deserialize<ScreenRecordingEnded>(await stream.NextAsync("screenrecorder.ended", timeout.Token));
        Assert.Equal(ScreenRecordingEndReason.Stopped, ended.Reason);
        Assert.Equal(recording, ended.Recording);
    }

    [Fact]
    public async Task A_recording_the_device_ended_is_filed_and_announced()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "screenrecorder.ended", timeout.Token);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        await recorder.Recordings.Single().FaultAsync(Native.ScreenRecordingFaultReason.RevokedByUser, salvage: true);

        var ended = Deserialize<ScreenRecordingEnded>(await stream.NextAsync("screenrecorder.ended", timeout.Token));
        Assert.Equal(ScreenRecordingEndReason.RevokedByUser, ended.Reason);
        Assert.NotNull(ended.Recording);
        Assert.True(fixture.Roots.TryResolve("data", ended.Recording.File.Path, out var filed));
        Assert.Equal(FakeScreenRecording.Content, await File.ReadAllBytesAsync(filed));

        // It is over: nothing left to stop, and the next recording can start.
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopAsync());
        Assert.Equal("not_recording", refused.Code);
        Assert.Equal(ended.Recording, (await fixture.Client.GetStatusAsync()).LastRecording);
        await fixture.Client.StartAsync(new ScreenRecordingRequest());
    }

    [Fact]
    public async Task A_failure_with_nothing_salvaged_is_announced_with_its_reason()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "screenrecorder.ended", timeout.Token);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        await recorder.Recordings.Single().FaultAsync(Native.ScreenRecordingFaultReason.EncoderFailed, salvage: false, new InvalidOperationException("muxer broke"));

        var ended = Deserialize<ScreenRecordingEnded>(await stream.NextAsync("screenrecorder.ended", timeout.Token));
        Assert.Equal(ScreenRecordingEndReason.EncoderFailed, ended.Reason);
        Assert.Null(ended.Recording);
        Assert.Equal("muxer broke", ended.Message);
    }

    [Fact]
    public async Task Raises_the_status_as_the_recorder_changes_state()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "screenrecorder.status", timeout.Token);

        await fixture.Client.StartAsync(new ScreenRecordingRequest());

        var status = Deserialize<ScreenRecorderStatus>(await stream.NextAsync("screenrecorder.status", timeout.Token));
        Assert.Equal(ScreenRecorderState.Recording, status.State);
    }

    [Fact]
    public async Task A_missing_file_root_fails_the_stop_rather_than_losing_it_quietly()
    {
        var recorder = new FakeScreenRecorder { Capabilities = Desktop };
        await using var fixture = await ScreenRecorderFixture.StartAsync(recorder, o => o.Root = "nowhere");

        await fixture.Client.StartAsync(new ScreenRecordingRequest());
        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopAsync());

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Equal(ScreenRecorderState.Idle, (await fixture.Client.GetStatusAsync()).State);
    }

    [Fact]
    public void Registers_once_and_answers_501_where_nothing_records()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
        {
            bridge.AddScreenRecorderBridge(o => o.Folder = "clips");
            bridge.AddScreenRecorderBridge();
        }), autoStart: false);

        using var provider = services.BuildServiceProvider();
        var bridge = Assert.Single(provider.GetServices<IWebAppBridge>().OfType<ScreenRecorderBridge>());
        Assert.Equal("clips", Assert.Single(services, x => x.ServiceType == typeof(ScreenRecorderBridgeOptions)).ImplementationInstance is ScreenRecorderBridgeOptions o ? o.Folder : null);

        // The net10.0 build registers the portal recorder on Linux and nothing anywhere else.
        Assert.Equal(OperatingSystem.IsLinux(), provider.GetService<Native.IScreenRecorder>() is not null);
        if (!OperatingSystem.IsLinux())
            Assert.False(bridge.IsSupported);
    }

    [Theory]
    [InlineData("not a root", "screen-recordings", 60)]
    [InlineData("data", "../out", 60)]
    [InlineData("data", "screen-recordings", 0)]
    public void Options_refuse_what_cannot_work(string root, string folder, int maxSeconds)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge =>
            bridge.AddScreenRecorderBridge(o =>
            {
                o.Root = root;
                o.Folder = folder;
                o.MaxDuration = TimeSpan.FromSeconds(maxSeconds);
            })), autoStart: false));
    }

    static T Deserialize<T>(string json) where T : class
        => (T)JsonSerializer.Deserialize(json, typeof(T), ScreenRecorderJsonContext.Default)!;

    sealed class ScreenRecorderFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public ScreenRecorderBridgeClient Client { get; private set; } = null!;
        public WebAppFileRoots Roots { get; private set; } = null!;
        public ScreenRecorderBridgeOptions Options { get; } = new();

        public static async Task<ScreenRecorderFixture> StartAsync(Native.IScreenRecorder? recorder, Action<ScreenRecorderBridgeOptions>? configure = null)
        {
            var fixture = new ScreenRecorderFixture();
            configure?.Invoke(fixture.Options);

            var services = new ServiceCollection();
            if (recorder is not null)
                services.AddSingleton(recorder);
            services.AddSingleton(fixture.Options);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 20, 15, 2, TimeSpan.Zero)));
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    fixture.Roots = new WebAppFileRoots(app.BridgeOptions());
                    return [new ScreenRecorderBridge(provider, fixture.Roots)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new ScreenRecorderBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    sealed class FakeScreenRecorder : Native.IScreenRecorder
    {
        public Native.ScreenRecorderCapabilities Capabilities { get; init; }
        public Native.ScreenRecorderState State { get; set; }
        public Shiny.AccessState Access { get; init; } = Shiny.AccessState.Available;
        public Exception? StartThrows { get; set; }

        public List<Native.ScreenRecordingRequest> Requests { get; } = [];
        public List<Native.ScreenRecordingRequest> AccessRequests { get; } = [];
        public List<FakeScreenRecording> Recordings { get; } = [];

        public event EventHandler<Native.ScreenRecorderState>? StateChanged;

        public void Move(Native.ScreenRecorderState state)
        {
            this.State = state;
            this.StateChanged?.Invoke(this, state);
        }

        public Task<Shiny.AccessState> RequestAccess(Native.ScreenRecordingRequest request, CancellationToken ct = default)
        {
            this.AccessRequests.Add(request);
            return Task.FromResult(this.Access);
        }

        public Task<IReadOnlyList<Native.CaptureTarget>> GetTargets(CancellationToken ct = default)
            => throw new InvalidOperationException("The bridge never lists targets.");

        public async Task<Native.IScreenRecording> Start(Native.ScreenRecordingRequest request, CancellationToken ct = default)
        {
            if (this.StartThrows is { } ex)
                throw ex;

            this.Requests.Add(request);
            var recording = new FakeScreenRecording(this);
            await File.WriteAllBytesAsync(recording.OutputPath, FakeScreenRecording.Content, ct);
            this.Recordings.Add(recording);
            this.Move(Native.ScreenRecorderState.Recording);
            return recording;
        }
    }

    sealed class FakeScreenRecording(FakeScreenRecorder recorder) : Native.IScreenRecording
    {
        public static readonly byte[] Content = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32];

        bool finished;

        public string OutputPath { get; } = Path.Combine(Path.GetTempPath(), $"screen-{Guid.NewGuid():N}.mp4");
        public bool Cancelled { get; private set; }
        public TimeSpan Elapsed => TimeSpan.FromSeconds(12.5);
        public bool IsPaused { get; private set; }

        public event EventHandler<Native.ScreenRecordingFaultedEventArgs>? Faulted;

        public Task Pause(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(this.finished, this);
            this.IsPaused = true;
            recorder.Move(Native.ScreenRecorderState.Paused);
            return Task.CompletedTask;
        }

        public Task Resume(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(this.finished, this);
            this.IsPaused = false;
            recorder.Move(Native.ScreenRecorderState.Recording);
            return Task.CompletedTask;
        }

        public Task<Native.ScreenRecordingResult> Stop(CancellationToken ct = default)
        {
            this.finished = true;
            recorder.Move(Native.ScreenRecorderState.Idle);
            return Task.FromResult(this.Result());
        }

        public Task Cancel(CancellationToken ct = default)
        {
            this.finished = true;
            this.Cancelled = true;
            File.Delete(this.OutputPath);
            recorder.Move(Native.ScreenRecorderState.Idle);
            return Task.CompletedTask;
        }

        /// <summary>What a backend does when the OS ends the recording: finish, then raise with what was salvaged.</summary>
        public Task FaultAsync(Native.ScreenRecordingFaultReason reason, bool salvage, Exception? exception = null)
        {
            this.finished = true;
            recorder.Move(Native.ScreenRecorderState.Idle);
            this.Faulted?.Invoke(this, new Native.ScreenRecordingFaultedEventArgs(reason, salvage ? this.Result() : null, exception));
            return Task.CompletedTask;
        }

        Native.ScreenRecordingResult Result() => new()
        {
            FilePath = this.OutputPath,
            Duration = this.Elapsed,
            ByteSize = Content.Length,
            Width = 1280,
            Height = 720,
            MimeType = "video/mp4"
        };

        public ValueTask DisposeAsync()
        {
            if (!this.finished)
                return new ValueTask(this.Cancel());

            return ValueTask.CompletedTask;
        }
    }
}
