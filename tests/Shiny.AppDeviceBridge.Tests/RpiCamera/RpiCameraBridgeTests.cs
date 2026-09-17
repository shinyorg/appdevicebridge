using Shiny.Net.HttpServer;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.RpiCamera.Client;

namespace Shiny.AppDeviceBridge.Tests.RpiCamera;

/// <summary>The camera bridge against a fake sensor producing real NV12 frames, through the typed client.</summary>
public class RpiCameraBridgeTests
{
    [Fact]
    public async Task Reports_the_camera_and_takes_a_jpeg_snapshot()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var status = await fixture.Client.GetStatusAsync();
        Assert.True(status.Supported);
        Assert.Equal("fake camera", status.Backend);
        var camera = Assert.Single(status.Cameras);
        Assert.Equal(("fake0", RpiCameraLocation.Back), (camera.Id, camera.Location));

        var jpeg = await ReadAllAsync(await fixture.Client.SnapshotAsync(quality: 70));
        AssertJpeg(jpeg);

        // A photograph opens a session of its own and gives the sensor back straight away.
        Assert.Equal(1, fixture.Camera.OpenCount);
        Assert.Equal(0, fixture.Camera.LiveSessions);
    }

    [Fact]
    public async Task Captures_into_a_file_root_the_files_bridge_reads()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var entry = await fixture.Client.CaptureAsync(new RpiCameraCapture("data", "photos/now.jpg"));
        Assert.Equal("photos/now.jpg", entry.Path);
        Assert.True(entry.Size > 0);

        await using var stored = await new FilesBridgeClient(fixture.Transport).OpenReadAsync("data", "photos/now.jpg");
        AssertJpeg(await ReadAllAsync(stored));

        var missingRoot = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CaptureAsync(new RpiCameraCapture("nowhere", "a.jpg")));
        Assert.Equal(HttpStatusCode.NotFound, missingRoot.StatusCode);

        var badPath = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.CaptureAsync(new RpiCameraCapture("data", "../a.jpg")));
        Assert.Equal("invalid_path", badPath.Code);
    }

    [Fact]
    public async Task Refuses_an_unknown_camera_and_nonsense_sizes()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var unknown = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SnapshotAsync("fake9"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var tooBig = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SnapshotAsync(width: 100_000));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.WebView.GetAsync("/_bridge/rpicamera/stream?fps=1000")).StatusCode);
    }

    /// <summary>The camera is exclusive: every viewer shares one session, and a snapshot comes from it.</summary>
    [Fact]
    public async Task Shares_one_session_between_viewers_and_snapshots()
    {
        await using var fixture = await CameraFixture.StartAsync();

        using var first = await fixture.OpenStreamAsync();
        using var second = await fixture.OpenStreamAsync();

        AssertJpeg(await ReadPartAsync(first));
        AssertJpeg(await ReadPartAsync(second));
        AssertJpeg(await ReadAllAsync(await fixture.Client.SnapshotAsync()));

        Assert.Equal(1, fixture.Camera.OpenCount);

        var stream = Assert.Single((await fixture.Client.GetStatusAsync()).Streams);
        Assert.Equal(("fake0", 2), (stream.CameraId, stream.Viewers));
        Assert.Equal("NV12", stream.PixelFormat);
        Assert.True(stream.FramesDelivered > 0);
    }

    [Fact]
    public async Task Releases_the_camera_when_the_last_viewer_leaves()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var viewer = await fixture.OpenStreamAsync();
        AssertJpeg(await ReadPartAsync(viewer));
        Assert.Equal(1, fixture.Camera.LiveSessions);

        viewer.Dispose();
        await WaitUntilAsync(() => fixture.Camera.LiveSessions == 0);
        Assert.Empty((await fixture.Client.GetStatusAsync()).Streams);

        // And stopping the streams ends a viewer that never leaves.
        using var stubborn = await fixture.OpenStreamAsync();
        AssertJpeg(await ReadPartAsync(stubborn));
        await fixture.Client.StopStreamsAsync();
        await WaitUntilAsync(() => fixture.Camera.LiveSessions == 0);
    }

    [Fact]
    public async Task Applies_controls_now_and_to_every_later_session()
    {
        await using var fixture = await CameraFixture.StartAsync();

        var controls = await fixture.Client.SetControlsAsync(new RpiCameraControlsInput([new(RpiCameraControl.Brightness, 0.25)]));
        var brightness = Assert.Single(controls, x => x.Control == RpiCameraControl.Brightness);
        Assert.Equal((true, -1d, 1d, 0.25d), (brightness.Supported, brightness.Minimum, brightness.Maximum, brightness.Value!.Value));
        Assert.False(Assert.Single(controls, x => x.Control == RpiCameraControl.LensPosition).Supported);

        await fixture.Client.SnapshotAsync();
        Assert.Contains((CameraControl.Brightness, 0.25), fixture.Camera.Sessions[^1].Applied);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetControlsAsync(new RpiCameraControlsInput([new(RpiCameraControl.LensPosition, 2)])));
        Assert.Equal("unsupported_control", refused.Code);

        var cleared = await fixture.Client.SetControlsAsync(new RpiCameraControlsInput([new(RpiCameraControl.Brightness, null)]));
        Assert.Null(Assert.Single(cleared, x => x.Control == RpiCameraControl.Brightness).Value);
    }

    [Fact]
    public async Task Says_why_there_is_no_camera_and_answers_501()
    {
        await using var fixture = await CameraFixture.StartAsync(new FakeCameraService { IsSupported = false });

        var status = await fixture.Client.GetStatusAsync();
        Assert.False(status.Supported);
        Assert.Equal("no camera attached", status.Backend);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SnapshotAsync());
        Assert.True(refused.IsNotSupported);
    }

    [Fact]
    public void Is_unavailable_with_a_reason_off_linux()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "On Linux the native shim decides, which depends on the machine.");

        var collection = new ServiceCollection();
        collection.AddShinyHttpServer(http => http.AddRpiCameraBridge(), autoStart: false);
        var services = collection.BuildServiceProvider();
        var cameras = services.GetRequiredService<ICameraService>();

        Assert.False(cameras.IsSupported);
        Assert.Contains("only on Linux", cameras.BackendDescription);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Refuses_options_it_cannot_honour(int quality)
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddShinyHttpServer(http => http.AddRpiCameraBridge(o => o.StreamQuality = quality), autoStart: false));

    [Fact]
    public void Maps_every_contract_enum()
    {
        foreach (var control in Enum.GetValues<RpiCameraControl>())
            BridgeEnum.Convert<RpiCameraControl, CameraControl>(control);

        foreach (var location in Enum.GetValues<CameraLocation>())
            BridgeEnum.Convert<CameraLocation, RpiCameraLocation>(location);
    }

    static void AssertJpeg(byte[] bytes)
    {
        Assert.True(bytes.Length > 4, "Expected a JPEG, got nothing.");
        Assert.Equal([0xFF, 0xD8], bytes[..2]);
        Assert.Equal([0xFF, 0xD9], bytes[^2..]);
    }

    static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>One part of the multipart stream: the headers, then exactly Content-Length bytes.</summary>
    static async Task<byte[]> ReadPartAsync(HttpResponseMessage response)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = await response.Content.ReadAsStreamAsync(timeout.Token);

        var headers = new StringBuilder();
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var one = new byte[1];
            if (await body.ReadAsync(one, timeout.Token) == 0)
                throw new EndOfStreamException("The stream ended before a frame.");

            headers.Append((char)one[0]);
        }

        Assert.Contains("--appdevicebridge-frame", headers.ToString());
        Assert.Contains("Content-Type: image/jpeg", headers.ToString());

        var length = Int32.Parse(headers.ToString().Split("Content-Length: ")[1].Split("\r\n")[0]);
        var jpeg = new byte[length];
        await body.ReadExactlyAsync(jpeg, timeout.Token);
        return jpeg;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    sealed class CameraFixture(BuiltInClientTests.HostFixture host, HttpClient webView, FakeCameraService camera) : IAsyncDisposable
    {
        public FakeCameraService Camera => camera;

        public IBridgeTransport Transport => host.Transport;

        public HttpClient WebView => webView;

        public RpiCameraBridgeClient Client { get; } = new(host.Transport);

        public static async Task<CameraFixture> StartAsync(FakeCameraService? camera = null)
        {
            camera ??= new FakeCameraService();
            HttpClient? webView = null;

            var host = await BuiltInClientTests.HostFixture.StartAsync(app =>
            {
                var options = app.BridgeOptions();
                var roots = new WebAppFileRoots(options);
                var services = new ServiceCollection()
                    .AddSingleton<ICameraService>(camera)
                    .AddSingleton(roots)
                    .AddSingleton(new RpiCameraBridgeOptions { MaxFps = 60 })
                    .BuildServiceProvider();

                return [new RpiCameraBridge(services), new WebAppFilesBridge(roots, options, new WebAppEventHub())];
            }, onStarted: client => webView = client);

            return new CameraFixture(host, webView!, camera);
        }

        /// <summary>A viewer: the response headers are in, the frames follow.</summary>
        public async Task<HttpResponseMessage> OpenStreamAsync()
        {
            var response = await webView.GetAsync("/_bridge/rpicamera/stream", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("multipart/x-mixed-replace", response.Content.Headers.ContentType?.ToString());
            return response;
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
