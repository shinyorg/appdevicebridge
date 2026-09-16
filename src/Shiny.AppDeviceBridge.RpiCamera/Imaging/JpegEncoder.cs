using System.Buffers;

namespace Shiny.AppDeviceBridge.RpiCamera.Imaging;

/// <summary>
/// Encodes a camera frame as a baseline JPEG.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a camera frame is not a picture anyone can look at. libcamera hands back
/// YUV420 or NV12 - a couple of megabytes of planar samples that no phone, browser or file
/// manager will open - and moving that over a link measured in tens of kilobytes per second is
/// not a sensible thing to do even once. A 1280x720 frame is 1.4 MB as NV12 and around 90 KB as
/// a JPEG, which is the difference between a photograph arriving in seconds and in minutes.
/// </para>
/// <para>
/// It is written out rather than taken from a library because the alternatives all cost more
/// than they save on this device: ImageSharp is a large dependency for one operation, System.
/// Drawing needs a native GDI+ that a Pi OS Lite image does not have, and shelling out to
/// <c>rpicam-jpeg</c> would fork a process per frame and could not share the capture pipeline
/// this appliance already holds open. Baseline JPEG is a small, completely specified format,
/// and the camera already produces exactly the colour space it wants.
/// </para>
/// <para>
/// That last point is the reason this is cheap. JPEG stores 4:2:0 subsampled Y'CbCr, which is
/// what NV12 and YUV420 already are - so for the formats a Pi camera actually produces there is
/// no colour conversion and no resampling, just the DCT. RGB inputs are converted, and MJPEG
/// frames, when a camera produces them, are passed through untouched.
/// </para>
/// </remarks>
public static class JpegEncoder
{
    /// <summary>Quality used when a caller does not choose one. Visually clean, roughly 1 bit per pixel.</summary>
    public const int DefaultQuality = 85;

    /// <summary>True when <see cref="Encode(CameraFrame, int, bool)"/> understands this format.</summary>
    public static bool CanEncode(CameraPixelFormat format)
        => format == CameraPixelFormat.Mjpeg
           || format == CameraPixelFormat.Yuv420
           || format == Yv12
           || format == CameraPixelFormat.Nv12
           || format == Nv21
           || format == CameraPixelFormat.Yuyv
           || format == CameraPixelFormat.Rgb888
           || format == CameraPixelFormat.Bgr888
           || format == CameraPixelFormat.Xrgb8888;

    /// <summary>
    /// Encodes a frame as a JPEG.
    /// </summary>
    /// <param name="frame">The frame. Not disposed - the caller still owns it.</param>
    /// <param name="quality">1-100. Clamped.</param>
    /// <param name="limitedRange">
    /// True when the source samples are studio-swing Y'CbCr (luma 16-235) rather than full
    /// range. Frames from the Pi's ISP are normally full range and expanding them again would
    /// clip highlights, so this is off by default and only worth setting for a sensor that is
    /// visibly washed out. Ignored for RGB sources.
    /// </param>
    /// <exception cref="NotSupportedException">The frame's pixel format is not one this understands.</exception>
    public static byte[] Encode(CameraFrame frame, int quality = DefaultQuality, bool limitedRange = false)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // A camera that already produces JPEG has done this work in hardware. Re-encoding it
        // would cost time and a generation of quality to arrive at the same thing.
        if (frame.PixelFormat == CameraPixelFormat.Mjpeg)
            return frame.ToArray();

        using var buffer = new MemoryStream(EstimateSize(frame.Width, frame.Height));
        Encode(frame, buffer, quality, limitedRange);

        return buffer.ToArray();
    }

    /// <summary>Encodes a frame as a JPEG straight into a stream.</summary>
    /// <inheritdoc cref="Encode(CameraFrame, int, bool)" path="/param"/>
    /// <param name="output">Where the JPEG is written. Not flushed or closed.</param>
    public static void Encode(CameraFrame frame, Stream output, int quality = DefaultQuality, bool limitedRange = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);

        if (frame.PixelFormat == CameraPixelFormat.Mjpeg)
        {
            output.Write(frame.Data);
            return;
        }

        if (frame.Width <= 0 || frame.Height <= 0)
            throw new NotSupportedException("The frame has no dimensions.");

        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var luma = ArrayPool<byte>.Shared.Rent(width * height);
        var blue = ArrayPool<byte>.Shared.Rent(chromaWidth * chromaHeight);
        var red = ArrayPool<byte>.Shared.Rent(chromaWidth * chromaHeight);
        try
        {
            PlanarConverter.Convert(frame, luma, blue, red, limitedRange);

            EncodeYuv420(
                luma.AsSpan(0, width * height),
                width,
                blue.AsSpan(0, chromaWidth * chromaHeight),
                red.AsSpan(0, chromaWidth * chromaHeight),
                chromaWidth,
                width,
                height,
                output,
                quality
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(luma);
            ArrayPool<byte>.Shared.Return(blue);
            ArrayPool<byte>.Shared.Return(red);
        }
    }

    /// <summary>
    /// Encodes already-planar 4:2:0 Y'CbCr as a baseline JPEG.
    /// </summary>
    /// <remarks>
    /// The tight path: this is the shape JPEG stores natively, so nothing here resamples or
    /// converts. Exposed separately because it is also the only part worth testing without a
    /// camera attached.
    /// </remarks>
    /// <param name="luma">Luma samples, full resolution.</param>
    /// <param name="lumaStride">Bytes per luma row, padding included.</param>
    /// <param name="blue">Cb samples, half resolution in both directions.</param>
    /// <param name="red">Cr samples, half resolution in both directions.</param>
    /// <param name="chromaStride">Bytes per chroma row, padding included.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="output">Where the JPEG is written.</param>
    /// <param name="quality">1-100. Clamped.</param>
    public static void EncodeYuv420(
        ReadOnlySpan<byte> luma,
        int lumaStride,
        ReadOnlySpan<byte> blue,
        ReadOnlySpan<byte> red,
        int chromaStride,
        int width,
        int height,
        Stream output,
        int quality = DefaultQuality
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(lumaStride, width);
        ArgumentOutOfRangeException.ThrowIfLessThan(chromaStride, (width + 1) / 2);

        quality = Math.Clamp(quality, 1, 100);

        var lumaQuant = JpegTables.ScaleQuantization(JpegTables.LuminanceQuantization, quality);
        var chromaQuant = JpegTables.ScaleQuantization(JpegTables.ChrominanceQuantization, quality);

        WriteHeaders(output, width, height, lumaQuant, chromaQuant);
        WriteScan(
            output,
            luma, lumaStride,
            blue, red, chromaStride,
            width, height,
            lumaQuant, chromaQuant
        );

        // EOI.
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
    }

    // ---------------------------------------------------------------------------------
    // Markers
    // ---------------------------------------------------------------------------------

    static void WriteHeaders(Stream output, int width, int height, ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> chroma)
    {
        Span<byte> segment = stackalloc byte[JpegTables.BlockSize * 2 + 4];

        // SOI.
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        // APP0/JFIF. Not strictly required, but a file without it is rejected by enough
        // consumer software that omitting it is a false economy.
        WriteMarker(output, 0xE0, 16);
        output.Write("JFIF\0"u8);
        output.Write([0x01, 0x02]);   // version 1.02
        output.WriteByte(0x00);       // no density units
        output.Write([0x00, 0x01, 0x00, 0x01]);
        output.Write([0x00, 0x00]);   // no thumbnail

        // DQT, both tables in one segment, each in zig-zag order as the format requires.
        WriteMarker(output, 0xDB, 2 + 65 + 65);
        WriteQuantizationTable(output, segment, 0, luma);
        WriteQuantizationTable(output, segment, 1, chroma);

        // SOF0: baseline, three components, luma at 2x2 sampling and chroma at 1x1 - which is
        // what makes this 4:2:0.
        WriteMarker(output, 0xC0, 8 + 3 * 3);
        output.WriteByte(8);
        WriteUInt16(output, (ushort)height);
        WriteUInt16(output, (ushort)width);
        output.WriteByte(3);
        output.Write([1, 0x22, 0]);
        output.Write([2, 0x11, 1]);
        output.Write([3, 0x11, 1]);

        WriteHuffmanTable(output, 0x00, JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
        WriteHuffmanTable(output, 0x10, JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
        WriteHuffmanTable(output, 0x01, JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
        WriteHuffmanTable(output, 0x11, JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);

        // SOS.
        WriteMarker(output, 0xDA, 6 + 2 * 3);
        output.WriteByte(3);
        output.Write([1, 0x00]);
        output.Write([2, 0x11]);
        output.Write([3, 0x11]);
        output.Write([0x00, 0x3F, 0x00]);
    }

    static void WriteQuantizationTable(Stream output, Span<byte> scratch, byte id, ReadOnlySpan<ushort> table)
    {
        scratch[0] = id;    // 8-bit precision in the high nibble, which is zero
        for (var i = 0; i < JpegTables.BlockSize; i++)
            scratch[1 + i] = (byte)table[JpegTables.ZigZag[i]];

        output.Write(scratch[..(1 + JpegTables.BlockSize)]);
    }

    static void WriteHuffmanTable(Stream output, byte id, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        WriteMarker(output, 0xC4, 2 + 1 + 16 + values.Length);
        output.WriteByte(id);
        output.Write(bits);
        output.Write(values);
    }

    static void WriteMarker(Stream output, byte marker, int length)
    {
        output.WriteByte(0xFF);
        output.WriteByte(marker);
        WriteUInt16(output, (ushort)length);
    }

    static void WriteUInt16(Stream output, ushort value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)(value & 0xFF));
    }

    // ---------------------------------------------------------------------------------
    // Entropy-coded scan
    // ---------------------------------------------------------------------------------

    static void WriteScan(
        Stream output,
        ReadOnlySpan<byte> luma,
        int lumaStride,
        ReadOnlySpan<byte> blue,
        ReadOnlySpan<byte> red,
        int chromaStride,
        int width,
        int height,
        ReadOnlySpan<ushort> lumaQuant,
        ReadOnlySpan<ushort> chromaQuant
    )
    {
        var writer = new JpegBitWriter(output);

        Span<float> block = stackalloc float[JpegTables.BlockSize];
        Span<int> coefficients = stackalloc int[JpegTables.BlockSize];

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        // One MCU covers 16x16 pixels: four luma blocks and one of each chroma block. Partial
        // MCUs at the right and bottom edges are filled by replicating the edge pixel, which
        // costs nothing and avoids the ringing a black fill would produce along the border.
        var mcusAcross = (width + 15) / 16;
        var mcusDown = (height + 15) / 16;

        var previousLumaDc = 0;
        var previousBlueDc = 0;
        var previousRedDc = 0;

        for (var mcuY = 0; mcuY < mcusDown; mcuY++)
        {
            for (var mcuX = 0; mcuX < mcusAcross; mcuX++)
            {
                for (var subY = 0; subY < 2; subY++)
                {
                    for (var subX = 0; subX < 2; subX++)
                    {
                        ExtractBlock(luma, lumaStride, width, height, mcuX * 16 + subX * 8, mcuY * 16 + subY * 8, block);
                        Transform(block, lumaQuant, coefficients);
                        WriteBlock(writer, coefficients, ref previousLumaDc, luminance: true);
                    }
                }

                ExtractBlock(blue, chromaStride, chromaWidth, chromaHeight, mcuX * 8, mcuY * 8, block);
                Transform(block, chromaQuant, coefficients);
                WriteBlock(writer, coefficients, ref previousBlueDc, luminance: false);

                ExtractBlock(red, chromaStride, chromaWidth, chromaHeight, mcuX * 8, mcuY * 8, block);
                Transform(block, chromaQuant, coefficients);
                WriteBlock(writer, coefficients, ref previousRedDc, luminance: false);
            }
        }

        writer.Flush();
    }

    /// <summary>
    /// Copies one 8x8 block out of a plane, level-shifted into the signed range the DCT wants.
    /// </summary>
    /// <remarks>
    /// Coordinates outside the plane clamp to its edge. That covers the partial MCUs at the
    /// right and bottom of any image whose dimensions are not a multiple of 16, which is most
    /// of them.
    /// </remarks>
    static void ExtractBlock(
        ReadOnlySpan<byte> plane,
        int stride,
        int planeWidth,
        int planeHeight,
        int originX,
        int originY,
        Span<float> block
    )
    {
        for (var y = 0; y < 8; y++)
        {
            var row = Math.Min(originY + y, planeHeight - 1) * stride;
            for (var x = 0; x < 8; x++)
            {
                var column = Math.Min(originX + x, planeWidth - 1);
                block[y * 8 + x] = plane[row + column] - 128f;
            }
        }
    }

    /// <summary>Forward DCT followed by quantization.</summary>
    static void Transform(ReadOnlySpan<float> block, ReadOnlySpan<ushort> quantization, Span<int> coefficients)
    {
        Span<float> rows = stackalloc float[JpegTables.BlockSize];
        Span<float> transformed = stackalloc float[JpegTables.BlockSize];

        // Separable: a 1-D DCT along each row, then along each column of the result. The naive
        // form is 1024 multiply-adds a block, which on a Pi 4 encodes 720p faster than the link
        // it is going out over could ever carry it - so the clarity is free.
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var x = 0; x < 8; x++)
                    sum += block[y * 8 + x] * JpegTables.Cosines[u * 8 + x];

                rows[y * 8 + u] = sum;
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++)
                    sum += rows[y * 8 + u] * JpegTables.Cosines[v * 8 + y];

                transformed[v * 8 + u] = sum;
            }
        }

        for (var i = 0; i < JpegTables.BlockSize; i++)
            coefficients[i] = (int)MathF.Round(transformed[i] / quantization[i]);
    }

    /// <summary>Huffman-codes one quantized block: the DC difference, then the AC run/size pairs.</summary>
    static void WriteBlock(JpegBitWriter writer, ReadOnlySpan<int> coefficients, ref int previousDc, bool luminance)
    {
        var dcTable = luminance ? JpegTables.DcLuminance : JpegTables.DcChrominance;
        var acTable = luminance ? JpegTables.AcLuminance : JpegTables.AcChrominance;

        // DC is coded as the difference from the previous block of the same component, which is
        // where most of JPEG's compression of flat areas comes from.
        var difference = coefficients[0] - previousDc;
        previousDc = coefficients[0];

        var magnitude = MagnitudeCategory(difference);
        writer.WriteCode(dcTable, magnitude);
        if (magnitude > 0)
            writer.WriteValue(difference, magnitude);

        var runLength = 0;
        for (var i = 1; i < JpegTables.BlockSize; i++)
        {
            var value = coefficients[JpegTables.ZigZag[i]];
            if (value == 0)
            {
                runLength++;
                continue;
            }

            // Runs longer than 15 zeros need an explicit ZRL for each full 16.
            while (runLength > 15)
            {
                writer.WriteCode(acTable, 0xF0);
                runLength -= 16;
            }

            var size = MagnitudeCategory(value);
            writer.WriteCode(acTable, (runLength << 4) | size);
            writer.WriteValue(value, size);
            runLength = 0;
        }

        // Trailing zeros are not coded at all; end-of-block says the rest of the block is zero.
        if (runLength > 0)
            writer.WriteCode(acTable, 0x00);
    }

    /// <summary>Bits needed to represent the magnitude of a coefficient.</summary>
    static int MagnitudeCategory(int value)
    {
        var magnitude = Math.Abs(value);
        var bits = 0;
        while (magnitude > 0)
        {
            bits++;
            magnitude >>= 1;
        }
        return bits;
    }

    static int EstimateSize(int width, int height)
        // Roughly one bit per pixel at default quality, plus headers. Only a starting capacity.
        => Math.Max(4096, width * height / 8 + 1024);

    /// <summary>YV12 - the same planar 4:2:0 as YU12 with the chroma planes the other way round.</summary>
    internal static CameraPixelFormat Yv12 { get; } = CameraPixelFormat.FromCode("YV12");

    /// <summary>NV21 - the same semi-planar 4:2:0 as NV12 with the chroma samples interleaved the other way round.</summary>
    internal static CameraPixelFormat Nv21 { get; } = CameraPixelFormat.FromCode("NV21");
}
