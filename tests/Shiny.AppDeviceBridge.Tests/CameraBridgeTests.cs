using System.Net;
using System.Net.Http.Json;
using System.Text;
using Shiny.AppDeviceBridge.Camera;
using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Tests;

// Inside the namespace: Shiny.Core's own AccessState is otherwise found first, through the enclosing Shiny namespace.
using AccessState = Shiny.AppDeviceBridge.Client.AccessState;

/// <summary>
/// The device camera bridge, with a fake camera screen attached: everything between a request and the camera control —
/// the session, the refusals, the viewfinder stream, the events, and the filing. The control itself is platform UI, checked
/// in the sample app. This test host is the net10.0 build, which has no camera, so the platform itself reports unsupported.
/// </summary>
public class CameraBridgeTests
{
    [Fact]
    public async Task Says_there_is_no_camera_screen_open()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var status = await fixture.Client.GetStatusAsync();

        Assert.False(status.Live);
        Assert.False(status.Supported);
        Assert.Equal(AccessState.NotSupported, status.Access);
        Assert.NotNull(status.Message);
    }

    [Fact]
    public async Task Refuses_commands_with_409_until_a_camera_screen_is_open()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.TakePhotoAsync());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("camera_not_open", refused.Code);

        await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRecordingAsync());
        await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.UpdateSettingsAsync(new CameraSettingsInput(Zoom: 2)));
    }

    [Fact]
    public async Task Drives_the_attached_camera()
    {
        await using var fixture = await CameraFixture.StartAsync();
        var camera = new FakeCamera();
        using var _ = fixture.Session.Attach(camera);

        var status = await fixture.Client.GetStatusAsync();
        Assert.True(status.Live);
        Assert.True(status.Recording);
        Assert.Equal(CameraFacing.External, status.Facing);

        // What the device knows about the platform wins over whatever the screen reported.
        Assert.False(status.Supported);
        Assert.Equal(AccessState.NotSupported, status.Access);

        var photo = await fixture.Client.TakePhotoAsync();
        Assert.Equal(new BridgeFile("data", "camera/fake.jpg"), photo.File);

        await fixture.Client.StartRecordingAsync();
        Assert.Equal(CameraCaptureKind.Video, (await fixture.Client.StopRecordingAsync()).Kind);

        var after = await fixture.Client.UpdateSettingsAsync(new CameraSettingsInput(Facing: CameraFacing.Front, Filter: CameraFilter.Noir, Zoom: 2.5));
        Assert.Equal(new CameraSettingsInput(Facing: CameraFacing.Front, Filter: CameraFilter.Noir, Zoom: 2.5), camera.Applied.Single());
        Assert.True(after.Live);

        Assert.Equal(["photo", "start", "stop"], camera.Calls);
    }

    [Fact]
    public async Task Answers_422_for_a_command_the_camera_cannot_take_now()
    {
        await using var fixture = await CameraFixture.StartAsync();
        using var _ = fixture.Session.Attach(new FakeCamera { Refuse = "The camera is in video mode." });

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.TakePhotoAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("camera_state", refused.Code);
        Assert.Equal("The camera is in video mode.", refused.Message);
    }

    [Theory]
    [InlineData("{ \"facing\": \"Sideways\" }")]
    [InlineData("{ \"filter\": 42.5 }")]
    [InlineData("not json")]
    public async Task Refuses_settings_it_cannot_read(string body)
    {
        await using var fixture = await CameraFixture.StartAsync();
        var camera = new FakeCamera();
        using var _ = fixture.Session.Attach(camera);

        var response = await fixture.WebView.PutAsync("/_bridge/camera/settings", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(camera.Applied);
    }

    [Fact]
    public async Task Has_no_camera_to_open_or_prompt_for_here()
    {
        await using var fixture = await CameraFixture.StartAsync();

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.OpenAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync())).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "camera").IsSupported);
    }

    [Fact]
    public async Task Closes_the_bridges_own_camera_screen()
    {
        await using var fixture = await CameraFixture.StartAsync();
        var closed = 0;
        fixture.Session.CloseRequested += (_, _) => closed++;

        await fixture.Client.CloseAsync();

        Assert.Equal(1, closed);
        Assert.Equal(1, fixture.Presenter.Dismissed);
    }

    [Fact]
    public async Task Shows_its_own_camera_screen_only_when_nothing_else_does()
    {
        var presenter = new FakePresenter();
        var session = new CameraBridgeSession(new CameraBridgeOptions(), presenter);

        await session.RequestOpenAsync();
        Assert.Equal(1, presenter.Presented);

        // The app navigated to a camera screen of its own.
        session.OpenRequested += (_, e) => e.Handled = true;
        await session.RequestOpenAsync();
        Assert.Equal(1, presenter.Presented);

        // Already open: not even asked.
        var asked = 0;
        session.OpenRequested += (_, _) => asked++;
        using (session.Attach(new FakeCamera()))
            await session.RequestOpenAsync();
        Assert.Equal(0, asked);

        var quiet = new CameraBridgeSession(new CameraBridgeOptions { PresentWhenOpened = false }, presenter);
        await quiet.RequestOpenAsync();
        Assert.Equal(1, presenter.Presented);
    }

    [Fact]
    public void Leaves_the_live_camera_alone_when_an_older_screen_detaches()
    {
        var session = new CameraBridgeSession(new CameraBridgeOptions(), new FakePresenter());

        var first = session.Attach(new FakeCamera());
        var second = session.Attach(new FakeCamera());

        // A fast switch: the new screen attached before the old one's teardown ran.
        first.Dispose();
        Assert.True(session.IsLive);

        second.Dispose();
        Assert.False(session.IsLive);
    }

    /// <summary>A camera nobody is watching from elsewhere encodes nothing.</summary>
    [Fact]
    public void Streams_frames_only_while_someone_is_watching()
    {
        var session = new CameraBridgeSession(new CameraBridgeOptions(), new FakePresenter());
        var camera = new FakeCamera();
        using var _ = session.Attach(camera);

        var one = session.Preview.Watch();
        var two = session.Preview.Watch();
        one.Dispose();
        two.Dispose();

        Assert.Equal([true, false], camera.Streaming);

        // A viewer already waiting when the camera comes up gets frames without asking again.
        using var waiting = session.Preview.Watch();
        var late = new FakeCamera();
        using var __ = session.Attach(late);
        Assert.Equal([true], late.Streaming);
    }

    /// <summary>Falling behind is worse than dropping frames: a slow viewer gets the newest one.</summary>
    [Fact]
    public async Task Hands_a_slow_viewer_the_newest_frame()
    {
        var preview = new CameraPreview();
        using var viewer = preview.Watch();

        preview.Publish([1]);
        preview.Publish([2]);
        preview.Publish([3]);

        Assert.Equal([3], await viewer.NextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Streams_the_viewfinder_as_mjpeg()
    {
        await using var fixture = await CameraFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await fixture.WebView.GetAsync("/_bridge/camera/preview", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("multipart/x-mixed-replace", response.Content.Headers.ContentType?.MediaType);

        while (!fixture.Session.Preview.HasViewers)
            await Task.Delay(10, timeout.Token);

        var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        fixture.Session.Preview.Publish(jpeg);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var head = await ReadLineAsync(stream, timeout.Token);
        Assert.StartsWith("--", head);
        Assert.Equal("Content-Type: image/jpeg", await ReadLineAsync(stream, timeout.Token));
        Assert.Equal($"Content-Length: {jpeg.Length}", await ReadLineAsync(stream, timeout.Token));
        Assert.Equal("", await ReadLineAsync(stream, timeout.Token));

        var body = new byte[jpeg.Length];
        await stream.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal(jpeg, body);
    }

    [Fact]
    public async Task Tells_the_page_when_the_camera_changes()
    {
        await using var fixture = await CameraFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "camera.status", timeout.Token);

        using var _ = fixture.Session.Attach(new FakeCamera());

        var (name, data) = await stream.NextAsync(timeout.Token);
        Assert.Equal("camera.status", name);
        Assert.Contains("\"live\":true", data);
    }

    [Fact]
    public async Task Files_captures_into_the_root_by_when_they_were_taken()
    {
        await using var app = new TestApp();
        var options = app.BridgeOptions();
        var roots = new WebAppFileRoots(options);
        var store = new FileRootCameraCaptureStore(roots, new CameraBridgeOptions(), new FixedTime(new DateTimeOffset(2026, 9, 16, 20, 15, 2, TimeSpan.Zero)));

        var first = await store.SaveAsync(new CameraCaptureContent(CameraCaptureKind.Photo, new MemoryStream([1, 2, 3]), ".jpg", 4000, 3000), default);
        var second = await store.SaveAsync(new CameraCaptureContent(CameraCaptureKind.Photo, new MemoryStream([4]), ".JPG"), default);
        var video = await store.SaveAsync(new CameraCaptureContent(CameraCaptureKind.Video, new MemoryStream([5, 6]), ".mov", Duration: TimeSpan.FromSeconds(90)), default);
        var odd = await store.SaveAsync(new CameraCaptureContent(CameraCaptureKind.Video, new MemoryStream([7]), "no-dot"), default);

        Assert.Equal(new CameraCapture(new BridgeFile("data", "camera/IMG_20260916-201502.jpg"), CameraCaptureKind.Photo, 3, 4000, 3000), first);
        Assert.Equal("camera/IMG_20260916-201502-2.jpg", second.File.Path);
        Assert.Equal(new CameraCapture(new BridgeFile("data", "camera/VID_20260916-201502.mov"), CameraCaptureKind.Video, 2, DurationSeconds: 90), video);
        Assert.Equal("camera/VID_20260916-201502.mp4", odd.File.Path);   // an extension it cannot use falls back to the kind's

        Assert.True(roots.TryResolve("data", first.File.Path, out var written));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken));

        var nowhere = new FileRootCameraCaptureStore(roots, new CameraBridgeOptions { Root = "photos" }, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<WebAppFileException>(() => nowhere.SaveAsync(new(CameraCaptureKind.Photo, new MemoryStream([1]), ".jpg"), default));
        Assert.Equal("not_found", failure.Code);
    }

    [Theory]
    [InlineData("bad root", "camera", 12, 720, 55)]
    [InlineData("data", "../out", 12, 720, 55)]
    [InlineData("data", "camera", 0, 720, 55)]
    [InlineData("data", "camera", 12, 10, 55)]
    [InlineData("data", "camera", 12, 720, 101)]
    public void Refuses_options_it_cannot_honour(string root, string folder, int fps, int edge, int quality)
        => Assert.Throws<InvalidOperationException>(() => new CameraBridgeOptions
        {
            Root = root,
            Folder = folder,
            PreviewFramesPerSecond = fps,
            PreviewMaxEdge = edge,
            PreviewQuality = quality
        }.Validate());

    [Theory]
    [InlineData(640, 480, 720, null, null)]
    [InlineData(1920, 1080, 720, 720, 405)]
    [InlineData(1080, 1920, 720, 405, 720)]
    public void Fits_a_frame_inside_the_longest_edge(int width, int height, int maxEdge, int? expectedWidth, int? expectedHeight)
    {
        var fit = CameraPreviewJpeg.Fit(width, height, maxEdge);
        Assert.Equal(expectedWidth, fit?.Width);
        Assert.Equal(expectedHeight, fit?.Height);
    }

    static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new byte[1];

        while (await stream.ReadAsync(one, cancellationToken) == 1)
        {
            if (one[0] == '\n')
                break;

            if (one[0] != '\r')
                line.Append((char)one[0]);
        }

        return line.ToString();
    }

    sealed class CameraFixture : IAsyncDisposable
    {
        TestApp app = null!;
        BuiltInClientTests.HostFixture host = null!;

        public CameraBridgeSession Session { get; private set; } = null!;
        public FakePresenter Presenter { get; } = new();
        public WebAppEventHub Events { get; } = new();
        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public CameraBridgeClient Client { get; private set; } = null!;

        public static async Task<CameraFixture> StartAsync()
        {
            var fixture = new CameraFixture();
            fixture.Session = new CameraBridgeSession(new CameraBridgeOptions(), fixture.Presenter);

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new CameraBridge(fixture.Session)],
                fixture.Events,
                client => fixture.WebView = client
            );

            fixture.Client = new CameraBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    internal sealed class FakePresenter : ICameraBridgePresenter
    {
        public int Presented { get; private set; }

        public int Dismissed { get; private set; }

        public Task PresentAsync()
        {
            this.Presented++;
            return Task.CompletedTask;
        }

        public Task DismissAsync()
        {
            this.Dismissed++;
            return Task.CompletedTask;
        }
    }

    sealed class FakeCamera : ICameraBridgeController
    {
        public string? Refuse { get; init; }

        public List<string> Calls { get; } = [];

        public List<CameraSettingsInput> Applied { get; } = [];

        public List<bool> Streaming { get; } = [];

        public CameraStatus Snapshot() => new(true, AccessState.Available, true, Active: true, Recording: true, Facing: CameraFacing.External);

        public Task<CameraCapture> TakePhotoAsync(CancellationToken cancellationToken)
        {
            if (this.Refuse is { } reason)
                throw CameraBridgeException.WrongState(reason);

            this.Calls.Add("photo");
            return Task.FromResult(new CameraCapture(new BridgeFile("data", "camera/fake.jpg"), CameraCaptureKind.Photo, 10));
        }

        public Task StartRecordingAsync(CancellationToken cancellationToken)
        {
            this.Calls.Add("start");
            return Task.CompletedTask;
        }

        public Task<CameraCapture> StopRecordingAsync(CancellationToken cancellationToken)
        {
            this.Calls.Add("stop");
            return Task.FromResult(new CameraCapture(new BridgeFile("data", "camera/fake.mov"), CameraCaptureKind.Video, 20));
        }

        public void Apply(CameraSettingsInput settings) => this.Applied.Add(settings);

        public void SetPreviewStreaming(bool streaming) => this.Streaming.Add(streaming);
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
