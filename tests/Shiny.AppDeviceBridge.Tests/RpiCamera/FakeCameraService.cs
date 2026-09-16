using System.Buffers;
using System.Runtime.CompilerServices;
using Shiny.AppDeviceBridge.RpiCamera;

namespace Shiny.AppDeviceBridge.Tests.RpiCamera;

/// <summary>
/// A camera that produces synthetic NV12 frames.
/// </summary>
/// <remarks>
/// NV12 because that is what a Pi camera's viewfinder stream actually delivers, so the encode
/// path these tests exercise is the one that runs on the device rather than a convenient
/// substitute. The picture is a moving gradient: every frame differs, which is what makes a
/// stream that silently repeats one frame visible as a failure.
/// </remarks>
sealed class FakeCameraService(int width = 64, int height = 48) : ICameraService
{
    /// <summary>How many sessions have been opened, so exclusivity can be asserted.</summary>
    public int OpenCount { get; private set; }

    /// <summary>Every session opened, in order.</summary>
    public List<FakeCameraSession> Sessions { get; } = [];

    /// <summary>Sessions opened and not yet disposed. Must never exceed one.</summary>
    public int LiveSessions { get; private set; }

    public bool IsSupported { get; set; } = true;

    public string BackendDescription => this.IsSupported ? "fake camera" : "no camera attached";

    public Task<IReadOnlyList<CameraDescriptor>> GetCameras(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CameraDescriptor>>(this.IsSupported
            ? [new CameraDescriptor("fake0", "imx-test", width, height, 0, CameraLocation.Back)]
            : []
        );

    public Task<ICameraSession> Open(
        CameraSessionOptions? options = null,
        string? cameraId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!this.IsSupported)
            throw new CameraUnavailableException(this.BackendDescription);

        if (this.LiveSessions > 0)
            throw new CameraUnavailableException("The camera is held by another session.");

        this.OpenCount++;
        this.LiveSessions++;

        var requested = options ?? new CameraSessionOptions();

        // Read back rather than echo, as a real pipeline does: it adjusts anything it cannot
        // deliver exactly, and here that means clamping to the sensor's size.
        var actualWidth = requested.Width > 0 ? Math.Min(requested.Width, width) : width;
        var actualHeight = requested.Height > 0 ? Math.Min(requested.Height, height) : height;

        var session = new FakeCameraSession(
            new CameraDescriptor("fake0", "imx-test", width, height, 0, CameraLocation.Back),
            new CameraStreamInfo(actualWidth, actualHeight, CameraPixelFormat.Nv12, actualWidth, 4, 1),
            () => this.LiveSessions--
        );
        this.Sessions.Add(session);

        return Task.FromResult<ICameraSession>(session);
    }
}


sealed class FakeCameraSession(CameraDescriptor camera, CameraStreamInfo stream, Action onDispose) : ICameraSession
{
    // The one control this sensor has, so control handling can be exercised both ways.
    public static readonly CameraControlInfo BrightnessInfo = new(true, -1, 1, 0);

    ulong sequence;
    int disposed;

    public CameraDescriptor Camera { get; } = camera;
    public CameraStreamInfo Stream { get; } = stream;

    public async IAsyncEnumerable<CameraFrame> ReadFrames(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return this.Produce();

            try
            {
                // A sensor interval, roughly. Short enough that a test does not wait on it,
                // long enough that the frame ceiling has something to drop.
                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public Task<CameraFrame> CaptureFrame(CancellationToken cancellationToken = default)
        => Task.FromResult(this.Produce());

    CameraFrame Produce()
    {
        var width = this.Stream.Width;
        var height = this.Stream.Height;
        var chromaHeight = (height + 1) / 2;

        var lumaSize = width * height;
        var chromaSize = width * chromaHeight;

        var buffer = ArrayPool<byte>.Shared.Rent(lumaSize + chromaSize);
        var frameNumber = (int)this.sequence;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                buffer[y * width + x] = (byte)((x + y + frameNumber * 8) & 0xFF);
        }

        for (var i = 0; i < chromaSize; i++)
            buffer[lumaSize + i] = (byte)(128 + ((i + frameNumber) % 32) - 16);

        return new CameraFrame(
            buffer,
            [0, lumaSize],
            [lumaSize, chromaSize],
            width,
            height,
            width,
            CameraPixelFormat.Nv12,
            this.sequence++,
            TimeSpan.FromMilliseconds(frameNumber * 33)
        );
    }

    /// <summary>What was set on this session, in order.</summary>
    public List<(CameraControl Control, double Value)> Applied { get; } = [];

    public bool TrySetControl(CameraControl control, double value)
    {
        if (control != CameraControl.Brightness)
            return false;

        this.Applied.Add((control, value));
        return true;
    }

    public CameraControlInfo GetControlInfo(CameraControl control)
        => control == CameraControl.Brightness ? BrightnessInfo : CameraControlInfo.Unsupported;

    public CameraStatistics GetStatistics() => new((long)this.sequence, (long)this.sequence, 0, 0, 0);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            onDispose();

        return ValueTask.CompletedTask;
    }
}
