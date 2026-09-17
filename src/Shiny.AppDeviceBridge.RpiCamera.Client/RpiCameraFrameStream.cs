using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Shiny.AppDeviceBridge.RpiCamera.Client;

/// <summary>One frame of a camera stream read with <see cref="RpiCameraFrameStream.ReadFramesAsync"/>.</summary>
/// <param name="Jpeg">The encoded frame, ready for an image decoder or a file.</param>
/// <param name="Sequence">
/// The sensor's frame counter. Gaps are frames the device dropped - to stay inside the frame rate, or because the link
/// could not keep up - so a widening gap is the signal to ask for a smaller or slower stream.
/// </param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="TimestampMs">Capture time on the device's monotonic clock, for pacing and latency. Not wall-clock time.</param>
public sealed record RpiCameraStreamFrame(byte[] Jpeg, uint Sequence, int Width, int Height, long TimestampMs);


/// <summary>The fixed header in front of every frame in a camera stream.</summary>
/// <param name="PayloadLength">Bytes of encoded frame following the header.</param>
/// <param name="Sequence">The sensor's frame counter.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="PixelFormat">The payload's FourCC - <see cref="RpiCameraFrameStream.MjpegFourCc"/>.</param>
/// <param name="TimestampMs">Capture time on the device's monotonic clock.</param>
public readonly record struct RpiCameraFrameHeader(
    int PayloadLength,
    uint Sequence,
    int Width,
    int Height,
    uint PixelFormat,
    long TimestampMs
);


/// <summary>
/// A live camera feed as a byte stream: each JPEG preceded by a small header, one after another until the stream closes.
/// </summary>
/// <remarks>
/// <para>
/// For a transport that is just a pipe - a Bluetooth LE L2CAP channel above all, where there is no HTTP to carry
/// <c>multipart/x-mixed-replace</c> - but anything that is a <see cref="Stream"/> works. The device side writes it with
/// <c>ICameraService.StreamToAsync</c> in Shiny.AppDeviceBridge.RpiCamera; a viewer reads it with
/// <see cref="ReadFramesAsync"/>.
/// </para>
/// <code>
/// [payloadLength:u32] [sequence:u32] [width:u32] [height:u32] [fourcc:u32] [timestampMs:i64]   little endian, 28 bytes
/// [payload: payloadLength bytes of JPEG]
/// </code>
/// <para>
/// The stream ends when it closes between frames. A header that claims an empty or implausibly large frame is
/// rejected rather than believed, because a reader sizes a buffer from it.
/// </para>
/// </remarks>
public static class RpiCameraFrameStream
{
    /// <summary>The size of a frame header in bytes.</summary>
    public const int HeaderLength = 28;

    /// <summary>The largest frame a header may claim.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>The FourCC of a JPEG payload: <c>MJPG</c>.</summary>
    public const uint MjpegFourCc = 'M' | ('J' << 8) | ('P' << 16) | ((uint)'G' << 24);

    /// <summary>Writes a header into <paramref name="destination"/>, which must be at least <see cref="HeaderLength"/> bytes.</summary>
    public static void WriteHeader(Span<byte> destination, in RpiCameraFrameHeader header)
    {
        if (destination.Length < HeaderLength)
            throw new ArgumentException($"A frame header needs {HeaderLength} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], (uint)header.PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), header.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), (uint)header.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), (uint)header.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), header.PixelFormat);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(20, 8), header.TimestampMs);
    }

    /// <summary>Parses a header.</summary>
    /// <returns>False when the header is short or claims an empty or implausibly large frame.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, out RpiCameraFrameHeader header)
    {
        header = default;
        if (source.Length < HeaderLength)
            return false;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        if (length is 0 or > MaxFrameBytes)
            return false;

        header = new RpiCameraFrameHeader(
            (int)length,
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(16, 4)),
            BinaryPrimitives.ReadInt64LittleEndian(source.Slice(20, 8))
        );
        return true;
    }

    /// <summary>Writes one JPEG frame - header then payload - to <paramref name="destination"/>.</summary>
    public static async ValueTask WriteFrameAsync(Stream destination, RpiCameraStreamFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Jpeg.Length is 0 or > MaxFrameBytes)
            throw new ArgumentException($"A frame must be 1 to {MaxFrameBytes} bytes.", nameof(frame));

        var header = new byte[HeaderLength];
        WriteHeader(header, new RpiCameraFrameHeader(frame.Jpeg.Length, frame.Sequence, frame.Width, frame.Height, MjpegFourCc, frame.TimestampMs));

        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(frame.Jpeg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads frames from <paramref name="source"/> until it closes between frames.
    /// </summary>
    /// <exception cref="InvalidDataException">A header was malformed.</exception>
    /// <exception cref="EndOfStreamException">The stream closed part way through a frame.</exception>
    public static async IAsyncEnumerable<RpiCameraStreamFrame> ReadFramesAsync(
        this Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new byte[HeaderLength];
        while (true)
        {
            // closing between frames is how a stream ends, so zero bytes here is not an error - a partial header is
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                filled += read;
            }

            if (filled == 0)
                yield break;

            if (filled < buffer.Length)
                throw new EndOfStreamException("The camera stream closed part way through a frame header.");

            if (!TryReadHeader(buffer, out var header))
                throw new InvalidDataException("The camera stream sent a malformed frame header.");

            var jpeg = new byte[header.PayloadLength];
            await source.ReadExactlyAsync(jpeg, cancellationToken).ConfigureAwait(false);

            yield return new RpiCameraStreamFrame(jpeg, header.Sequence, header.Width, header.Height, header.TimestampMs);
        }
    }
}
