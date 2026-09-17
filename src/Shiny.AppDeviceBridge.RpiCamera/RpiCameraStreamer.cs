using System.Diagnostics;
using Shiny.AppDeviceBridge.RpiCamera.Client;
using Shiny.AppDeviceBridge.RpiCamera.Imaging;

namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>What <see cref="RpiCameraStreamer.StreamToAsync"/> is asked to deliver.</summary>
public sealed class RpiCameraStreamSettings
{
    /// <summary>The camera to open. Null takes the first one attached.</summary>
    public string? CameraId { get; set; }

    /// <summary>Width asked of the pipeline. Zero lets it choose.</summary>
    public int Width { get; set; } = 640;

    /// <summary>Height asked of the pipeline. Zero lets it choose.</summary>
    public int Height { get; set; } = 480;

    /// <summary>JPEG quality, 1 to 100. Lower than a photograph on purpose: each frame has to cross the link before the next.</summary>
    public int Quality { get; set; } = 60;

    /// <summary>The most frames per second sent. Surplus frames are dropped before they are encoded.</summary>
    public int MaxFps { get; set; } = 10;

    /// <summary>
    /// How long the stream may run before it ends itself. There is always a ceiling: a viewer that walked out of range
    /// never says stop, and a camera left streaming is a warm sensor and a flat battery.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Treat the camera's output as studio-swing Y'CbCr and expand it when encoding. See <see cref="RpiCameraBridgeOptions.LimitedRangeInput"/>.</summary>
    public bool LimitedRangeInput { get; set; }

    internal void Validate()
    {
        if (this.Width < 0 || this.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Width and Height cannot be negative.");

        if (this.Quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Quality), "Quality must be between 1 and 100.");

        if (this.MaxFps < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxFps), "MaxFps must be at least 1.");

        if (this.MaxDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxDuration), "MaxDuration must be positive.");
    }
}

/// <summary>Live counters for a stream, safe to read from another thread while it runs.</summary>
public sealed class RpiCameraStreamStatistics
{
    long framesSent;
    long framesDropped;
    long bytesSent;

    /// <summary>Frames written to the destination.</summary>
    public long FramesSent => Interlocked.Read(ref this.framesSent);

    /// <summary>Frames the sensor produced that were skipped to stay inside the frame rate.</summary>
    public long FramesDropped => Interlocked.Read(ref this.framesDropped);

    /// <summary>Bytes written to the destination, headers included.</summary>
    public long BytesSent => Interlocked.Read(ref this.bytesSent);

    internal void Sent(int bytes)
    {
        Interlocked.Increment(ref this.framesSent);
        Interlocked.Add(ref this.bytesSent, bytes);
    }

    internal void Dropped() => Interlocked.Increment(ref this.framesDropped);
}

/// <summary>Why <see cref="RpiCameraStreamer.StreamToAsync"/> returned.</summary>
public enum RpiCameraStreamEnd
{
    /// <summary>The camera stopped producing frames.</summary>
    CameraStopped,

    /// <summary>The stream ran for <see cref="RpiCameraStreamSettings.MaxDuration"/>.</summary>
    MaxDurationReached
}

/// <summary>
/// Streams a camera into any <see cref="Stream"/> in the <see cref="RpiCameraFrameStream"/> format - one JPEG per frame,
/// each behind a small header.
/// </summary>
/// <remarks>
/// <para>
/// Built for a pipe with no HTTP around it: an L2CAP channel from <c>L2CapTicketBroker</c> in
/// Shiny.BluetoothLE.Hosting, a socket, a file. The HTTP bridge's <c>/stream</c> route stays the way to watch from a page.
/// </para>
/// <para>
/// The session is opened when the stream starts and released when it ends, so call this once a viewer has actually
/// connected - a stream nobody collects then costs nothing. Frames beyond <see cref="RpiCameraStreamSettings.MaxFps"/>
/// are dropped before they are encoded, which is where a Pi's CPU goes. A slow destination backs up into the writes;
/// frames produced meanwhile are skipped by the camera pipeline rather than queued, so what arrives stays current.
/// </para>
/// </remarks>
public static class RpiCameraStreamer
{
    /// <summary>Streams until the camera stops, <see cref="RpiCameraStreamSettings.MaxDuration"/> passes, or the token is cancelled.</summary>
    /// <param name="camera">The camera service.</param>
    /// <param name="destination">Where the frames go. Not disposed.</param>
    /// <param name="settings">What to stream. Defaults apply when null.</param>
    /// <param name="statistics">Counters to update as the stream runs, for a caller that reports progress.</param>
    /// <param name="cancellationToken">Stops the stream; the method then throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="CameraUnavailableException">There is no usable camera.</exception>
    /// <exception cref="NotSupportedException">The pipeline produced a format that cannot be sent as JPEG.</exception>
    /// <exception cref="IOException">The destination failed - typically the viewer went away.</exception>
    public static async Task<RpiCameraStreamEnd> StreamToAsync(
        this ICameraService camera,
        Stream destination,
        RpiCameraStreamSettings? settings = null,
        RpiCameraStreamStatistics? statistics = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(destination);

        settings ??= new RpiCameraStreamSettings();
        settings.Validate();
        statistics ??= new RpiCameraStreamStatistics();

        if (!camera.IsSupported)
            throw new CameraUnavailableException(camera.BackendDescription);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(settings.MaxDuration);

        try
        {
            await using var session = await camera
                .Open(new CameraSessionOptions { Width = settings.Width, Height = settings.Height, Role = CameraStreamRole.Viewfinder }, settings.CameraId, lifetime.Token)
                .ConfigureAwait(false);

            var format = session.Stream.PixelFormat;
            if (!format.IsCompressed && !JpegEncoder.CanEncode(format))
            {
                throw new NotSupportedException(
                    $"The camera configured a {format} stream, which cannot be sent as JPEG. " +
                    "Ask for a different resolution, or configure the pipeline for YUV420, NV12 or MJPEG."
                );
            }

            var header = new byte[RpiCameraFrameStream.HeaderLength];
            var interval = Stopwatch.Frequency / settings.MaxFps;
            var last = 0L;

            await foreach (var frame in session.ReadFrames(lifetime.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (last != 0 && now - last < interval)
                    {
                        statistics.Dropped();
                        continue;
                    }

                    last = now;
                    var jpeg = frame.PixelFormat.IsCompressed
                        ? frame.ToArray()
                        : JpegEncoder.Encode(frame, settings.Quality, settings.LimitedRangeInput);

                    RpiCameraFrameStream.WriteHeader(header, new RpiCameraFrameHeader(
                        jpeg.Length,
                        (uint)frame.Sequence,
                        frame.Width,
                        frame.Height,
                        RpiCameraFrameStream.MjpegFourCc,
                        (long)frame.Timestamp.TotalMilliseconds
                    ));

                    await destination.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                    await destination.WriteAsync(jpeg, lifetime.Token).ConfigureAwait(false);
                    statistics.Sent(header.Length + jpeg.Length);
                }
            }

            // A session ends its enumeration quietly when cancelled rather than throwing, so which token fired decides
            // what ended the stream.
            cancellationToken.ThrowIfCancellationRequested();
            return lifetime.IsCancellationRequested ? RpiCameraStreamEnd.MaxDurationReached : RpiCameraStreamEnd.CameraStopped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            return RpiCameraStreamEnd.MaxDurationReached;
        }
    }
}
