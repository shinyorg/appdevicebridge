using System.IO.Pipelines;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.RpiCamera.Client;

namespace Shiny.AppDeviceBridge.Tests.RpiCamera;

/// <summary>A camera streamed into a pipe and read back as a viewer would, against a fake sensor producing real NV12 frames.</summary>
public class RpiCameraStreamerTests
{
    [Fact]
    public async Task Streams_framed_jpegs_until_the_duration_ceiling_and_releases_the_camera()
    {
        var camera = new FakeCameraService();
        var pipe = new Pipe();
        var statistics = new RpiCameraStreamStatistics();

        var reading = ReadAllFramesAsync(pipe.Reader.AsStream());
        var end = await camera.StreamToAsync(
            pipe.Writer.AsStream(),
            new RpiCameraStreamSettings { Width = 32, Height = 24, MaxFps = 50, MaxDuration = TimeSpan.FromMilliseconds(400) },
            statistics
        );
        await pipe.Writer.CompleteAsync();
        var frames = await reading;

        Assert.Equal(RpiCameraStreamEnd.MaxDurationReached, end);
        Assert.Equal(0, camera.LiveSessions);
        Assert.NotEmpty(frames);
        Assert.Equal(statistics.FramesSent, frames.Count);
        Assert.Equal(statistics.BytesSent, frames.Sum(x => x.Jpeg.Length + RpiCameraFrameStream.HeaderLength));

        Assert.All(frames, frame =>
        {
            Assert.Equal((32, 24), (frame.Width, frame.Height));
            Assert.Equal([0xFF, 0xD8], frame.Jpeg[..2]);
            Assert.Equal([0xFF, 0xD9], frame.Jpeg[^2..]);
        });

        // the sensor's counter, so a viewer can see gaps
        Assert.Equal(frames.Select(x => x.Sequence).Order(), frames.Select(x => x.Sequence));
    }

    [Fact]
    public async Task The_frame_rate_ceiling_drops_frames_before_encoding()
    {
        var camera = new FakeCameraService();
        var statistics = new RpiCameraStreamStatistics();

        await camera.StreamToAsync(
            Stream.Null,
            new RpiCameraStreamSettings { MaxFps = 2, MaxDuration = TimeSpan.FromMilliseconds(600) },
            statistics
        );

        Assert.InRange(statistics.FramesSent, 1, 3);
        Assert.True(statistics.FramesDropped > statistics.FramesSent, $"sent {statistics.FramesSent}, dropped {statistics.FramesDropped}");
    }

    [Fact]
    public async Task Cancelling_throws_and_releases_the_camera()
    {
        var camera = new FakeCameraService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => camera.StreamToAsync(Stream.Null, new RpiCameraStreamSettings { MaxDuration = TimeSpan.FromMinutes(1) }, cancellationToken: cts.Token)
        );
        Assert.Equal(0, camera.LiveSessions);
    }

    [Fact]
    public async Task A_viewer_that_goes_away_ends_the_stream_with_an_io_error()
    {
        var camera = new FakeCameraService();

        await Assert.ThrowsAsync<IOException>(
            () => camera.StreamToAsync(new BrokenStream(), new RpiCameraStreamSettings { MaxDuration = TimeSpan.FromMinutes(1) })
        );
        Assert.Equal(0, camera.LiveSessions);
    }

    [Fact]
    public async Task An_unsupported_camera_is_refused_up_front()
    {
        var camera = new FakeCameraService { IsSupported = false };

        var ex = await Assert.ThrowsAsync<CameraUnavailableException>(() => camera.StreamToAsync(Stream.Null));
        Assert.Equal("no camera attached", ex.Message);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(101, 10)]
    [InlineData(60, 0)]
    public async Task Settings_are_validated(int quality, int maxFps)
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new FakeCameraService().StreamToAsync(Stream.Null, new RpiCameraStreamSettings { Quality = quality, MaxFps = maxFps })
        );

    static async Task<List<RpiCameraStreamFrame>> ReadAllFramesAsync(Stream source)
    {
        var frames = new List<RpiCameraStreamFrame>();
        await foreach (var frame in source.ReadFramesAsync())
            frames.Add(frame);

        return frames;
    }

    sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("the viewer went away");
    }
}

/// <summary>The frame format, parsed from bytes the device wrote - a reader sizes a buffer from it, so a bad header is refused.</summary>
public class RpiCameraFrameStreamTests
{
    [Fact]
    public void Header_round_trips()
    {
        var header = new RpiCameraFrameHeader(123456, 42, 640, 480, RpiCameraFrameStream.MjpegFourCc, 9_876_543_210);
        var buffer = new byte[RpiCameraFrameStream.HeaderLength];

        RpiCameraFrameStream.WriteHeader(buffer, header);

        Assert.True(RpiCameraFrameStream.TryReadHeader(buffer, out var parsed));
        Assert.Equal(header, parsed);
        Assert.Equal("MJPG"u8.ToArray(), buffer[16..20]);
    }

    [Fact]
    public void A_short_header_is_rejected()
    {
        var buffer = new byte[RpiCameraFrameStream.HeaderLength];
        RpiCameraFrameStream.WriteHeader(buffer, new RpiCameraFrameHeader(10, 1, 4, 4, RpiCameraFrameStream.MjpegFourCc, 0));

        Assert.False(RpiCameraFrameStream.TryReadHeader(buffer.AsSpan(0, RpiCameraFrameStream.HeaderLength - 1), out _));
    }

    [Fact]
    public void An_empty_or_implausibly_large_frame_is_rejected()
    {
        var buffer = new byte[RpiCameraFrameStream.HeaderLength];
        Assert.False(RpiCameraFrameStream.TryReadHeader(buffer, out _));

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)(RpiCameraFrameStream.MaxFrameBytes + 1));
        Assert.False(RpiCameraFrameStream.TryReadHeader(buffer, out _));
    }

    [Fact]
    public void Writing_into_a_short_buffer_throws()
        => Assert.Throws<ArgumentException>(() => RpiCameraFrameStream.WriteHeader(
            new byte[RpiCameraFrameStream.HeaderLength - 1],
            new RpiCameraFrameHeader(1, 0, 1, 1, RpiCameraFrameStream.MjpegFourCc, 0)
        ));

    [Fact]
    public async Task Frames_round_trip_and_the_stream_ends_cleanly_between_frames()
    {
        using var buffer = new MemoryStream();
        await RpiCameraFrameStream.WriteFrameAsync(buffer, new RpiCameraStreamFrame([1, 2, 3], 7, 2, 2, 100));
        await RpiCameraFrameStream.WriteFrameAsync(buffer, new RpiCameraStreamFrame([4, 5], 8, 2, 2, 133));
        buffer.Position = 0;

        var frames = new List<RpiCameraStreamFrame>();
        await foreach (var frame in buffer.ReadFramesAsync())
            frames.Add(frame);

        Assert.Equal([7u, 8u], frames.Select(x => x.Sequence));
        Assert.Equal([1, 2, 3], frames[0].Jpeg);
        Assert.Equal(133, frames[1].TimestampMs);
    }

    [Fact]
    public async Task A_stream_that_closes_inside_a_frame_is_an_error()
    {
        using var whole = new MemoryStream();
        await RpiCameraFrameStream.WriteFrameAsync(whole, new RpiCameraStreamFrame(new byte[100], 1, 2, 2, 0));
        using var cut = new MemoryStream(whole.ToArray()[..(RpiCameraFrameStream.HeaderLength + 10)]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var _ in cut.ReadFramesAsync()) { }
        });
    }

    [Fact]
    public async Task A_malformed_header_is_an_error()
    {
        using var garbage = new MemoryStream(new byte[RpiCameraFrameStream.HeaderLength]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in garbage.ReadFramesAsync()) { }
        });
    }
}
