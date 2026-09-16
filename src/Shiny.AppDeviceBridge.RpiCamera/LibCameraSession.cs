using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.RpiCamera.Interop;
namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>
/// An acquired camera, streaming through the native shim.
/// </summary>
/// <remarks>
/// The shape here is dictated by the fact that the native read blocks. A dedicated thread sits
/// in it and posts frames to a one-deep channel; the async enumerator reads that channel. The
/// alternatives are worse: a threadpool thread parked in native code for half a second at a
/// time starves everything else on a four-core Pi, and a callback out of libcamera's own event
/// thread into managed code would run consumer code on the thread the pipeline needs back.
/// </remarks>
public sealed class LibCameraSession : ICameraSession
{
    readonly CameraHandle handle;
    readonly LibCameraOptions options;
    readonly ILogger logger;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim readerGate = new(1, 1);

    int disposed;

    internal LibCameraSession(
        CameraHandle handle,
        CameraDescriptor camera,
        CameraStreamInfo stream,
        LibCameraOptions options,
        ILogger logger
    )
    {
        this.handle = handle;
        this.Camera = camera;
        this.Stream = stream;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public CameraDescriptor Camera { get; }

    /// <inheritdoc />
    public CameraStreamInfo Stream { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CameraFrame> ReadFrames(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        // One reader at a time: two capture threads on one camera would interleave dequeues
        // and each see half the frames.
        if (!await this.readerGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This camera session is already being read. Only one enumeration may be active at a time.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);

        // Capacity one, dropping the old: a consumer that falls behind should see the newest
        // frame, not work through a backlog. The drop callback matters - a dropped frame still
        // owns a pooled array.
        var channel = Channel.CreateBounded<CameraFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            },
            static dropped => dropped.Dispose()
        );

        var pump = new Thread(() => this.Pump(channel.Writer, linked.Token))
        {
            IsBackground = true,
            Name = $"shinyrpi-camera:{this.Camera.Id}"
        };

        // Cancellation has to reach a thread parked inside a native call. The token alone
        // cannot; waking the shim's queue can.
        await using var wake = linked.Token.Register(this.Wake).ConfigureAwait(false);

        pump.Start();

        try
        {
            while (true)
            {
                CameraFrame frame;
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        break;

                    if (!channel.Reader.TryRead(out frame!))
                        continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                yield return frame;
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            this.Wake();

            if (!pump.Join(this.options.ShutdownTimeout))
                this.logger.LogWarning("The camera capture thread did not stop within {Timeout}", this.options.ShutdownTimeout);

            while (channel.Reader.TryRead(out var leftover))
                leftover.Dispose();

            this.readerGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CameraFrame> CaptureFrame(CancellationToken cancellationToken = default)
    {
        var discarded = 0;

        await foreach (var frame in this.ReadFrames(cancellationToken).ConfigureAwait(false))
        {
            if (discarded < this.options.WarmUpFrames)
            {
                discarded++;
                frame.Dispose();
                continue;
            }

            return frame;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new CameraUnavailableException("The camera stopped producing frames before one could be captured.");
    }

    /// <inheritdoc />
    public bool TrySetControl(CameraControl control, double value)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        var added = false;
        try
        {
            this.handle.DangerousAddRef(ref added);
            var result = NativeMethods.shinyrpi_camera_set_control(this.handle.DangerousGetHandle(), (int)control, value);

            if (result == NativeResult.NotFound)
            {
                // Which controls exist is a property of the attached sensor, so this is an
                // answer rather than a failure.
                this.logger.LogDebug("{Control} is not supported by {Model}", control, this.Camera.Model);
                return false;
            }

            NativeBackend.ThrowIfFailed(result, $"Setting {control}");
            return true;
        }
        finally
        {
            if (added)
                this.handle.DangerousRelease();
        }
    }

    /// <inheritdoc />
    public CameraControlInfo GetControlInfo(CameraControl control)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        var added = false;
        try
        {
            this.handle.DangerousAddRef(ref added);

            var result = NativeMethods.shinyrpi_camera_get_control_info(
                this.handle.DangerousGetHandle(),
                (int)control,
                out var supported,
                out var minimum,
                out var maximum,
                out var defaultValue
            );

            NativeBackend.ThrowIfFailed(result, $"Reading {control}");

            return supported == 0
                ? CameraControlInfo.Unsupported
                : new CameraControlInfo(true, minimum, maximum, defaultValue);
        }
        finally
        {
            if (added)
                this.handle.DangerousRelease();
        }
    }

    /// <inheritdoc />
    public CameraStatistics GetStatistics()
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        var added = false;
        try
        {
            this.handle.DangerousAddRef(ref added);

            var stats = NativeStats.Create();
            NativeBackend.ThrowIfFailed(
                NativeMethods.shinyrpi_camera_get_stats(this.handle.DangerousGetHandle(), ref stats),
                "Reading camera statistics"
            );

            return new CameraStatistics(
                (long)stats.FramesCompleted,
                (long)stats.FramesDelivered,
                (long)stats.FramesDropped,
                (long)stats.FramesErrored,
                (int)stats.QueueLength
            );
        }
        finally
        {
            if (added)
                this.handle.DangerousRelease();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.lifetime.CancelAsync().ConfigureAwait(false);
        this.Wake();

        // Wait out any enumeration in flight: its finally block joins the capture thread, and
        // closing the handle before that thread leaves its native call is the one thing here
        // that would take the process down rather than throw.
        try
        {
            await this.readerGate.WaitAsync(this.options.ShutdownTimeout).ConfigureAwait(false);
            this.readerGate.Release();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Timed out waiting for the camera reader to stop");
        }

        this.handle.Dispose();
        this.lifetime.Dispose();
        this.readerGate.Dispose();
    }

    void Wake()
    {
        var added = false;
        try
        {
            this.handle.DangerousAddRef(ref added);
            NativeMethods.shinyrpi_camera_wake(this.handle.DangerousGetHandle());
        }
        catch (ObjectDisposedException)
        {
            // Already gone, which is the state the wake was trying to reach.
        }
        finally
        {
            if (added)
                this.handle.DangerousRelease();
        }
    }

    /// <summary>
    /// The capture loop. Runs on its own thread for the life of one enumeration.
    /// </summary>
    /// <remarks>
    /// The handle is reference-counted once for the whole loop rather than per call, so the
    /// camera cannot be closed while this thread is inside the shim.
    /// </remarks>
    void Pump(ChannelWriter<CameraFrame> writer, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var added = false;
        var timeout = (int)this.options.ReadTimeout.TotalMilliseconds;

        try
        {
            this.handle.DangerousAddRef(ref added);
            var native = this.handle.DangerousGetHandle();

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = NativeFrame.Create();
                var result = NativeMethods.shinyrpi_camera_dequeue_frame(native, timeout, ref frame);

                if (result == NativeResult.Timeout)
                    continue;

                if (result < NativeResult.Ok)
                {
                    // A stop races with the reader by design - the shim reports the camera is
                    // no longer running and that is the expected end of the stream, not a fault.
                    if (!cancellationToken.IsCancellationRequested && result != NativeResult.InvalidState)
                        failure = NativeBackend.ToException(result, "Reading a camera frame");

                    break;
                }

                CameraFrame copied;
                try
                {
                    copied = CopyFrame(ref frame);
                }
                finally
                {
                    // Unconditional: the capture buffer must go back even if the copy threw,
                    // or the stream starves a frame at a time until it stops entirely.
                    NativeMethods.shinyrpi_camera_release_frame(native, frame.FrameId);
                }

                if (!writer.TryWrite(copied))
                    copied.Dispose();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (added)
                this.handle.DangerousRelease();

            writer.TryComplete(failure);
        }
    }

    /// <summary>
    /// Copies a frame out of the memory-mapped capture buffer into a pooled array.
    /// </summary>
    /// <remarks>
    /// The copy is what makes the resulting <see cref="CameraFrame"/> safe to hand to arbitrary
    /// consumer code. The alternative - exposing the mapping directly - would tie the sensor's
    /// throughput to how promptly a caller finished with a span.
    /// </remarks>
    static unsafe CameraFrame CopyFrame(ref NativeFrame frame)
    {
        var planeCount = (int)Math.Min(frame.PlaneCount, 3u);
        if (planeCount == 0)
            throw new InvalidOperationException("The camera returned a frame with no planes.");

        var total = 0;
        for (var i = 0; i < planeCount; i++)
            total += frame.PlaneLength(i);

        if (total <= 0)
            throw new InvalidOperationException("The camera returned an empty frame.");

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        var offsets = new int[planeCount];
        var lengths = new int[planeCount];
        var offset = 0;

        for (var i = 0; i < planeCount; i++)
        {
            var length = frame.PlaneLength(i);
            var source = frame.PlaneData(i);

            if (source != nint.Zero && length > 0)
                new ReadOnlySpan<byte>((void*)source, length).CopyTo(buffer.AsSpan(offset, length));

            offsets[i] = offset;
            lengths[i] = length;
            offset += length;
        }

        return new CameraFrame(
            buffer,
            offsets,
            lengths,
            (int)frame.Width,
            (int)frame.Height,
            (int)frame.Stride,
            new CameraPixelFormat(frame.PixelFormat),
            frame.Sequence,
            // The shim reports nanoseconds on the monotonic clock; a tick is a hundred of them.
            TimeSpan.FromTicks(frame.TimestampNs / 100)
        );
    }
}
