
namespace Shiny.AppDeviceBridge.RpiCamera.Imaging;

/// <summary>
/// Turns whatever a camera produced into the tightly packed 4:2:0 Y'CbCr planes the JPEG
/// encoder wants.
/// </summary>
/// <remarks>
/// <para>
/// For the formats a Pi camera actually delivers - YUV420 and NV12 - this is a row-wise memcpy
/// and nothing more, because those already are 4:2:0 Y'CbCr. The only work is removing the
/// stride padding the pipeline adds and, for NV12, de-interleaving the chroma plane. That is
/// the whole reason the appliance encodes to JPEG rather than to anything else.
/// </para>
/// <para>
/// RGB and packed 4:2:2 sources are converted properly, which costs a multiply per pixel. They
/// exist here for USB cameras and for the odd pipeline configuration, not for the common path.
/// </para>
/// </remarks>
static class PlanarConverter
{
    /// <summary>
    /// Fills tightly packed luma and chroma planes from a frame.
    /// </summary>
    /// <param name="frame">The source frame.</param>
    /// <param name="luma">Receives width x height samples, stride equal to the width.</param>
    /// <param name="blue">Receives ceil(width/2) x ceil(height/2) Cb samples.</param>
    /// <param name="red">Receives ceil(width/2) x ceil(height/2) Cr samples.</param>
    /// <param name="limitedRange">Expand studio-swing input to full range. Ignored for RGB sources.</param>
    /// <exception cref="NotSupportedException">The frame's format is not one this understands, or its buffer is short.</exception>
    public static void Convert(CameraFrame frame, Span<byte> luma, Span<byte> blue, Span<byte> red, bool limitedRange)
    {
        var format = frame.PixelFormat;

        if (format == CameraPixelFormat.Yuv420 || format == JpegEncoder.Yv12)
        {
            // YU12 and YV12 differ only in which chroma plane comes first.
            ConvertPlanar(frame, luma, blue, red, limitedRange, swapChroma: format == JpegEncoder.Yv12);
            return;
        }

        if (format == CameraPixelFormat.Nv12 || format == JpegEncoder.Nv21)
        {
            ConvertSemiPlanar(frame, luma, blue, red, limitedRange, swapChroma: format == JpegEncoder.Nv21);
            return;
        }

        if (format == CameraPixelFormat.Yuyv)
        {
            ConvertYuyv(frame, luma, blue, red, limitedRange);
            return;
        }

        // The DRM names give the channel order from the most significant byte of a
        // little-endian word, so RG24 is B, G, R in memory and BG24 is R, G, B.
        if (format == CameraPixelFormat.Rgb888)
        {
            ConvertInterleavedRgb(frame, luma, blue, red, pixelStride: 3, redOffset: 2, greenOffset: 1, blueOffset: 0);
            return;
        }

        if (format == CameraPixelFormat.Bgr888)
        {
            ConvertInterleavedRgb(frame, luma, blue, red, pixelStride: 3, redOffset: 0, greenOffset: 1, blueOffset: 2);
            return;
        }

        if (format == CameraPixelFormat.Xrgb8888)
        {
            ConvertInterleavedRgb(frame, luma, blue, red, pixelStride: 4, redOffset: 2, greenOffset: 1, blueOffset: 0);
            return;
        }

        throw new NotSupportedException($"Cannot encode a {format} frame as a JPEG.");
    }

    // ---------------------------------------------------------------------------------
    // Planar and semi-planar 4:2:0 - the fast paths
    // ---------------------------------------------------------------------------------

    static void ConvertPlanar(
        CameraFrame frame,
        Span<byte> luma,
        Span<byte> blue,
        Span<byte> red,
        bool limitedRange,
        bool swapChroma
    )
    {
        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var lumaStride = Stride(frame, width);
        var chromaStride = Math.Max(chromaWidth, lumaStride / 2);

        var lumaSize = lumaStride * height;
        var chromaSize = chromaStride * chromaHeight;

        var source = ReadPlane(frame, 0, 0, lumaSize, "luma");
        var first = ReadPlane(frame, 1, lumaSize, chromaSize, "chroma");
        var second = ReadPlane(frame, 2, lumaSize + chromaSize, chromaSize, "chroma");

        CopyRows(source, lumaStride, luma, width, height, width);
        CopyRows(swapChroma ? second : first, chromaStride, blue, chromaWidth, chromaHeight, chromaWidth);
        CopyRows(swapChroma ? first : second, chromaStride, red, chromaWidth, chromaHeight, chromaWidth);

        if (limitedRange)
            ExpandRange(luma, blue, red);
    }

    static void ConvertSemiPlanar(
        CameraFrame frame,
        Span<byte> luma,
        Span<byte> blue,
        Span<byte> red,
        bool limitedRange,
        bool swapChroma
    )
    {
        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var lumaStride = Stride(frame, width);
        var lumaSize = lumaStride * height;

        // The interleaved plane carries a Cb/Cr pair per two pixels, so its row is the same
        // number of bytes as a luma row.
        var chromaStride = lumaStride;
        var chromaSize = chromaStride * chromaHeight;

        var source = ReadPlane(frame, 0, 0, lumaSize, "luma");
        var interleaved = ReadPlane(frame, 1, lumaSize, chromaSize, "chroma");

        CopyRows(source, lumaStride, luma, width, height, width);

        for (var row = 0; row < chromaHeight; row++)
        {
            var sourceRow = interleaved.Slice(row * chromaStride, Math.Min(chromaStride, interleaved.Length - row * chromaStride));
            var blueRow = blue.Slice(row * chromaWidth, chromaWidth);
            var redRow = red.Slice(row * chromaWidth, chromaWidth);

            for (var column = 0; column < chromaWidth; column++)
            {
                var offset = column * 2;
                if (offset + 1 >= sourceRow.Length)
                    break;

                blueRow[column] = swapChroma ? sourceRow[offset + 1] : sourceRow[offset];
                redRow[column] = swapChroma ? sourceRow[offset] : sourceRow[offset + 1];
            }
        }

        if (limitedRange)
            ExpandRange(luma, blue, red);
    }

    // ---------------------------------------------------------------------------------
    // Packed formats
    // ---------------------------------------------------------------------------------

    static void ConvertYuyv(CameraFrame frame, Span<byte> luma, Span<byte> blue, Span<byte> red, bool limitedRange)
    {
        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var stride = Stride(frame, width * 2);
        var source = ReadPlane(frame, 0, 0, stride * height, "frame");

        for (var row = 0; row < height; row++)
        {
            var sourceRow = source.Slice(row * stride, Math.Min(stride, source.Length - row * stride));
            var lumaRow = luma.Slice(row * width, width);

            for (var column = 0; column < width; column++)
            {
                var offset = column * 2;
                lumaRow[column] = offset < sourceRow.Length ? sourceRow[offset] : (byte)0;
            }

            // 4:2:2 already has one chroma pair per two columns; going to 4:2:0 only drops
            // every second row of them.
            if (row % 2 != 0 || row / 2 >= chromaHeight)
                continue;

            var blueRow = blue.Slice(row / 2 * chromaWidth, chromaWidth);
            var redRow = red.Slice(row / 2 * chromaWidth, chromaWidth);

            for (var column = 0; column < chromaWidth; column++)
            {
                var offset = column * 4;
                blueRow[column] = offset + 1 < sourceRow.Length ? sourceRow[offset + 1] : (byte)128;
                redRow[column] = offset + 3 < sourceRow.Length ? sourceRow[offset + 3] : (byte)128;
            }
        }

        if (limitedRange)
            ExpandRange(luma, blue, red);
    }

    static void ConvertInterleavedRgb(
        CameraFrame frame,
        Span<byte> luma,
        Span<byte> blue,
        Span<byte> red,
        int pixelStride,
        int redOffset,
        int greenOffset,
        int blueOffset
    )
    {
        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var stride = Stride(frame, width * pixelStride);
        var source = ReadPlane(frame, 0, 0, stride * height, "frame");

        // Chroma is accumulated over each 2x2 block and averaged, rather than point-sampled.
        // Averaging costs nothing here and avoids the colour crawl that sampling one corner of
        // each block produces on fine detail.
        Span<int> blueSums = chromaWidth <= 1024 ? stackalloc int[chromaWidth] : new int[chromaWidth];
        Span<int> redSums = chromaWidth <= 1024 ? stackalloc int[chromaWidth] : new int[chromaWidth];
        Span<int> counts = chromaWidth <= 1024 ? stackalloc int[chromaWidth] : new int[chromaWidth];

        for (var row = 0; row < height; row++)
        {
            if (row % 2 == 0)
            {
                blueSums.Clear();
                redSums.Clear();
                counts.Clear();
            }

            var sourceRow = source.Slice(row * stride, Math.Min(stride, source.Length - row * stride));
            var lumaRow = luma.Slice(row * width, width);

            for (var column = 0; column < width; column++)
            {
                var offset = column * pixelStride;
                if (offset + pixelStride > sourceRow.Length)
                    break;

                int r = sourceRow[offset + redOffset];
                int g = sourceRow[offset + greenOffset];
                int b = sourceRow[offset + blueOffset];

                // BT.601 as JFIF defines it, in fixed point with 16 fractional bits.
                lumaRow[column] = (byte)Math.Clamp((19595 * r + 38470 * g + 7471 * b + 32768) >> 16, 0, 255);

                var chromaColumn = column / 2;
                blueSums[chromaColumn] += Math.Clamp(((-11059 * r - 21709 * g + 32768 * b + 32768) >> 16) + 128, 0, 255);
                redSums[chromaColumn] += Math.Clamp(((32768 * r - 27439 * g - 5329 * b + 32768) >> 16) + 128, 0, 255);
                counts[chromaColumn]++;
            }

            // Flushed on the odd row of each pair, and on the final row when the height is odd.
            if (row % 2 == 0 && row + 1 < height)
                continue;

            var chromaRow = row / 2;
            if (chromaRow >= chromaHeight)
                continue;

            var blueOut = blue.Slice(chromaRow * chromaWidth, chromaWidth);
            var redOut = red.Slice(chromaRow * chromaWidth, chromaWidth);

            for (var column = 0; column < chromaWidth; column++)
            {
                var count = Math.Max(1, counts[column]);
                blueOut[column] = (byte)(blueSums[column] / count);
                redOut[column] = (byte)(redSums[column] / count);
            }
        }
    }

    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads a plane, whether the camera reported it as its own plane or packed everything
    /// into one buffer.
    /// </summary>
    /// <remarks>
    /// Both happen. libcamera exposes YUV420 as three planes on some pipelines and as a single
    /// allocation on others, and a caller that assumed either one would break on the other
    /// device.
    /// </remarks>
    static ReadOnlySpan<byte> ReadPlane(CameraFrame frame, int index, int fallbackOffset, int length, string what)
    {
        if (index < frame.PlaneCount)
        {
            var plane = frame.GetPlane(index);
            if (plane.Length >= length)
                return plane;

            // A short dedicated plane is a real inconsistency rather than a packing difference.
            throw new NotSupportedException(
                $"The camera reported a {what} plane of {plane.Length} bytes where {length} were needed."
            );
        }

        var combined = frame.GetPlane(0);
        if (fallbackOffset + length > combined.Length)
        {
            throw new NotSupportedException(
                $"The frame is {combined.Length} bytes, too short for its {what} plane at {frame.Width}x{frame.Height}."
            );
        }

        return combined.Slice(fallbackOffset, length);
    }

    static int Stride(CameraFrame frame, int minimum)
        => frame.Stride >= minimum ? frame.Stride : minimum;

    static void CopyRows(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int rows,
        int bytesPerRow
    )
    {
        for (var row = 0; row < rows; row++)
        {
            var start = row * sourceStride;

            // The final row of a plane is sometimes only as long as the image is wide, with no
            // padding after it, so the count is clamped rather than assumed.
            var count = Math.Min(bytesPerRow, source.Length - start);
            if (count <= 0)
                break;

            source.Slice(start, count).CopyTo(destination.Slice(row * destinationStride, count));
        }
    }

    /// <summary>
    /// Expands studio-swing Y'CbCr - luma 16-235, chroma 16-240 - to the full range JPEG stores.
    /// </summary>
    static void ExpandRange(Span<byte> luma, Span<byte> blue, Span<byte> red)
    {
        for (var i = 0; i < luma.Length; i++)
            luma[i] = LumaExpansion[luma[i]];

        for (var i = 0; i < blue.Length; i++)
            blue[i] = ChromaExpansion[blue[i]];

        for (var i = 0; i < red.Length; i++)
            red[i] = ChromaExpansion[red[i]];
    }

    static readonly byte[] LumaExpansion = BuildLumaExpansion();
    static readonly byte[] ChromaExpansion = BuildChromaExpansion();

    static byte[] BuildLumaExpansion()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)Math.Clamp((i - 16) * 255 / 219, 0, 255);

        return table;
    }

    static byte[] BuildChromaExpansion()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)Math.Clamp((i - 128) * 255 / 224 + 128, 0, 255);

        return table;
    }
}
