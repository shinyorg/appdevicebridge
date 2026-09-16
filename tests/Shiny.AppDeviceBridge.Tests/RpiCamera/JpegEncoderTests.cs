using System.Buffers.Binary;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.RpiCamera.Imaging;

namespace Shiny.AppDeviceBridge.Tests.RpiCamera;

/// <summary>
/// The encoder that turns a camera frame into something anyone can look at.
/// </summary>
/// <remarks>
/// <para>
/// There is no JPEG decoder in this repo to compare against, so these check the two things that
/// can be checked without one: that the file is structurally a JPEG a decoder will accept - the
/// right markers, in the right order, with correct segment lengths and no unescaped 0xFF in the
/// scan - and that the encoder agrees with itself across the input formats that should produce
/// identical output.
/// </para>
/// <para>
/// The structural checks are the ones that matter, because the failure they catch is the one
/// that does not show up locally: a file that opens in a permissive decoder and is rejected by
/// a strict one.
/// </para>
/// </remarks>
public class JpegEncoderTests
{
    static CameraFrame Frame(byte[] data, int width, int height, int stride, CameraPixelFormat format)
        => new(data, [0], [data.Length], width, height, stride, format, 0, TimeSpan.Zero);

    /// <summary>A recognisable test picture: colour bars, a luma ramp, and a hard checkerboard.</summary>
    static byte[] TestCard(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        (byte R, byte G, byte B)[] bars =
        [
            (255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
            (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)
        ];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;

                if (y < height / 2)
                {
                    var bar = bars[Math.Min(bars.Length - 1, x * bars.Length / width)];
                    (rgb[i], rgb[i + 1], rgb[i + 2]) = (bar.R, bar.G, bar.B);
                }
                else if (y < height * 3 / 4)
                {
                    var v = (byte)(x * 255 / Math.Max(1, width - 1));
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
                }
                else
                {
                    // A hard checkerboard is what catches a block-ordering or zig-zag mistake:
                    // it puts energy in the high frequencies where such a bug is visible.
                    var on = (x / 8 + y / 8) % 2 == 0;
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = (byte)(on ? 255 : 0);
                }
            }
        }

        return rgb;
    }

    static (byte[] Luma, byte[] Blue, byte[] Red) ToPlanes(byte[] rgb, int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var luma = new byte[width * height];
        var blue = new byte[chromaWidth * chromaHeight];
        var red = new byte[chromaWidth * chromaHeight];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                luma[y * width + x] = (byte)Math.Clamp((19595 * r + 38470 * g + 7471 * b + 32768) >> 16, 0, 255);
            }
        }

        for (var y = 0; y < chromaHeight; y++)
        {
            for (var x = 0; x < chromaWidth; x++)
            {
                int sumBlue = 0, sumRed = 0, count = 0;

                for (var dy = 0; dy < 2 && y * 2 + dy < height; dy++)
                {
                    for (var dx = 0; dx < 2 && x * 2 + dx < width; dx++)
                    {
                        var i = ((y * 2 + dy) * width + x * 2 + dx) * 3;
                        int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];

                        sumBlue += Math.Clamp(((-11059 * r - 21709 * g + 32768 * b + 32768) >> 16) + 128, 0, 255);
                        sumRed += Math.Clamp(((32768 * r - 27439 * g - 5329 * b + 32768) >> 16) + 128, 0, 255);
                        count++;
                    }
                }

                blue[y * chromaWidth + x] = (byte)(sumBlue / count);
                red[y * chromaWidth + x] = (byte)(sumRed / count);
            }
        }

        return (luma, blue, red);
    }

    // ---------------------------------------------------------------------------------
    // Structure
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(64, 64)]
    [InlineData(320, 240)]
    [InlineData(37, 23)]     // partial MCUs on both edges
    [InlineData(1, 1)]       // one pixel, which is entirely padding
    [InlineData(16, 1)]
    public void ProducesAStructurallyValidJpeg(int width, int height)
    {
        var rgb = TestCard(width, height);
        var jpeg = JpegEncoder.Encode(Frame(rgb, width, height, width * 3, CameraPixelFormat.Bgr888), 85);

        AssertValidJpeg(jpeg, width, height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(85)]
    [InlineData(100)]
    [InlineData(0)]      // clamped up
    [InlineData(500)]    // clamped down
    public void EveryQualitySettingProducesAValidFile(int quality)
    {
        var rgb = TestCard(64, 48);
        var jpeg = JpegEncoder.Encode(Frame(rgb, 64, 48, 64 * 3, CameraPixelFormat.Bgr888), quality);

        AssertValidJpeg(jpeg, 64, 48);
    }

    [Fact]
    public void HigherQualityProducesALargerFile()
    {
        var rgb = TestCard(160, 120);
        var frame = Frame(rgb, 160, 120, 160 * 3, CameraPixelFormat.Bgr888);

        var low = JpegEncoder.Encode(frame, 20).Length;
        var high = JpegEncoder.Encode(frame, 95).Length;

        Assert.True(high > low, $"quality 95 produced {high} bytes, quality 20 produced {low}");
    }

    [Fact]
    public void EveryByteOfTheScanIsEscaped()
    {
        // An unescaped 0xFF in entropy-coded data reads as a marker. Decoders differ on how
        // they recover, which is exactly the bug that opens locally and fails in the field.
        // Solid white maximises the DC coefficients and so the chance of producing one.
        var white = new byte[96 * 96 * 3];
        Array.Fill(white, (byte)255);

        var jpeg = JpegEncoder.Encode(Frame(white, 96, 96, 96 * 3, CameraPixelFormat.Bgr888), 100);
        var scanStart = FindScanStart(jpeg);

        for (var i = scanStart; i < jpeg.Length - 2; i++)
        {
            if (jpeg[i] != 0xFF)
                continue;

            var next = jpeg[i + 1];
            Assert.True(next == 0x00, $"unescaped 0xFF{next:X2} at offset {i} inside the scan");
            i++;
        }
    }

    [Fact]
    public void MjpegFramesArePassedThroughUntouched()
    {
        // A camera that already produces JPEG has done this in hardware; re-encoding would
        // cost time and a generation of quality to arrive at the same thing.
        var original = JpegEncoder.Encode(
            Frame(TestCard(32, 32), 32, 32, 32 * 3, CameraPixelFormat.Bgr888), 90
        );

        var passed = JpegEncoder.Encode(
            Frame(original, 32, 32, 32, CameraPixelFormat.Mjpeg), 10
        );

        Assert.Equal(original, passed);
    }

    // ---------------------------------------------------------------------------------
    // Formats
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PlanarSemiPlanarAndInterleavedAgree()
    {
        // YU12, NV12 and BG24 carrying the same picture must encode to the same bytes. If they
        // do not, one of the plane-extraction paths is wrong - and on a real device only one of
        // them is ever exercised, so nobody would find out.
        const int width = 64;
        const int height = 48;

        var rgb = TestCard(width, height);
        var (luma, blue, red) = ToPlanes(rgb, width, height);

        var yu12 = new byte[luma.Length + blue.Length + red.Length];
        luma.CopyTo(yu12, 0);
        blue.CopyTo(yu12, luma.Length);
        red.CopyTo(yu12, luma.Length + blue.Length);

        var nv12 = new byte[luma.Length + blue.Length * 2];
        luma.CopyTo(nv12, 0);
        for (var i = 0; i < blue.Length; i++)
        {
            nv12[luma.Length + i * 2] = blue[i];
            nv12[luma.Length + i * 2 + 1] = red[i];
        }

        var fromRgb = JpegEncoder.Encode(Frame(rgb, width, height, width * 3, CameraPixelFormat.Bgr888), 90);
        var fromYu12 = JpegEncoder.Encode(Frame(yu12, width, height, width, CameraPixelFormat.Yuv420), 90);
        var fromNv12 = JpegEncoder.Encode(Frame(nv12, width, height, width, CameraPixelFormat.Nv12), 90);

        Assert.Equal(fromRgb, fromYu12);
        Assert.Equal(fromRgb, fromNv12);
    }

    [Fact]
    public void StridePaddingIsSkipped()
    {
        // The pipeline pads rows, and a frame read as if it were tightly packed shears - each
        // row shifted a little further than the last. Padding rows with a distinctive value
        // means a shear would change the output.
        const int width = 20;
        const int height = 16;
        const int stride = 32;

        var rgb = TestCard(width, height);
        var (luma, blue, red) = ToPlanes(rgb, width, height);

        var chromaWidth = width / 2;
        var chromaHeight = height / 2;
        var chromaStride = stride / 2;

        var padded = new byte[stride * height + chromaStride * chromaHeight * 2];
        Array.Fill(padded, (byte)0x5A);

        for (var y = 0; y < height; y++)
            luma.AsSpan(y * width, width).CopyTo(padded.AsSpan(y * stride, width));

        var blueStart = stride * height;
        var redStart = blueStart + chromaStride * chromaHeight;

        for (var y = 0; y < chromaHeight; y++)
        {
            blue.AsSpan(y * chromaWidth, chromaWidth).CopyTo(padded.AsSpan(blueStart + y * chromaStride, chromaWidth));
            red.AsSpan(y * chromaWidth, chromaWidth).CopyTo(padded.AsSpan(redStart + y * chromaStride, chromaWidth));
        }

        var tight = new byte[luma.Length + blue.Length + red.Length];
        luma.CopyTo(tight, 0);
        blue.CopyTo(tight, luma.Length);
        red.CopyTo(tight, luma.Length + blue.Length);

        Assert.Equal(
            JpegEncoder.Encode(Frame(tight, width, height, width, CameraPixelFormat.Yuv420), 90),
            JpegEncoder.Encode(Frame(padded, width, height, stride, CameraPixelFormat.Yuv420), 90)
        );
    }

    [Fact]
    public void SeparatePlanesAndOneCombinedBufferAgree()
    {
        // libcamera exposes YUV420 as three planes on some pipelines and as a single allocation
        // on others. The encoder has to read both, and produce the same file from each.
        const int width = 32;
        const int height = 32;

        var (luma, blue, red) = ToPlanes(TestCard(width, height), width, height);

        var combined = new byte[luma.Length + blue.Length + red.Length];
        luma.CopyTo(combined, 0);
        blue.CopyTo(combined, luma.Length);
        red.CopyTo(combined, luma.Length + blue.Length);

        var asOnePlane = new CameraFrame(
            combined, [0], [combined.Length], width, height, width, CameraPixelFormat.Yuv420, 0, TimeSpan.Zero
        );

        var asThreePlanes = new CameraFrame(
            combined,
            [0, luma.Length, luma.Length + blue.Length],
            [luma.Length, blue.Length, red.Length],
            width, height, width, CameraPixelFormat.Yuv420, 0, TimeSpan.Zero
        );

        Assert.Equal(JpegEncoder.Encode(asOnePlane, 90), JpegEncoder.Encode(asThreePlanes, 90));
    }

    [Fact]
    public void YvuOrderingIsHonoured()
    {
        // YV12 is YU12 with the chroma planes swapped. Reading one as the other turns the
        // picture a memorable shade of wrong, and only on hardware that produces it.
        const int width = 32;
        const int height = 32;

        var (luma, blue, red) = ToPlanes(TestCard(width, height), width, height);

        var yu12 = new byte[luma.Length + blue.Length + red.Length];
        luma.CopyTo(yu12, 0);
        blue.CopyTo(yu12, luma.Length);
        red.CopyTo(yu12, luma.Length + blue.Length);

        var yv12 = new byte[yu12.Length];
        luma.CopyTo(yv12, 0);
        red.CopyTo(yv12, luma.Length);
        blue.CopyTo(yv12, luma.Length + red.Length);

        Assert.Equal(
            JpegEncoder.Encode(Frame(yu12, width, height, width, CameraPixelFormat.Yuv420), 90),
            JpegEncoder.Encode(Frame(yv12, width, height, width, CameraPixelFormat.FromCode("YV12")), 90)
        );
    }

    [Theory]
    [InlineData("MJPG")]
    [InlineData("YU12")]
    [InlineData("YV12")]
    [InlineData("NV12")]
    [InlineData("NV21")]
    [InlineData("YUYV")]
    [InlineData("RG24")]
    [InlineData("BG24")]
    [InlineData("XR24")]
    public void KnownFormatsAreAccepted(string fourCc)
        => Assert.True(JpegEncoder.CanEncode(CameraPixelFormat.FromCode(fourCc)));

    [Fact]
    public void ARawBayerFrameIsRefusedRatherThanGarbled()
    {
        var raw = CameraPixelFormat.FromCode("pRAA");
        Assert.False(JpegEncoder.CanEncode(raw));

        Assert.Throws<NotSupportedException>(
            () => JpegEncoder.Encode(Frame(new byte[64 * 64 * 2], 64, 64, 128, raw), 85)
        );
    }

    [Fact]
    public void AFrameTooShortForItsDimensionsIsRefused()
    {
        // Better a clear exception than a read past the end of a pooled buffer.
        Assert.Throws<NotSupportedException>(
            () => JpegEncoder.Encode(Frame(new byte[100], 640, 480, 640, CameraPixelFormat.Yuv420), 85)
        );
    }

    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Walks the marker structure the way a decoder does, checking that every segment length
    /// is right and that the required markers are present in a legal order.
    /// </summary>
    static void AssertValidJpeg(byte[] jpeg, int expectedWidth, int expectedHeight)
    {
        Assert.True(jpeg.Length > 4, "the file is too short to be a JPEG");
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);

        var offset = 2;
        var quantizationTables = 0;
        var huffmanTables = 0;
        var sawFrame = false;
        var sawScan = false;

        while (offset < jpeg.Length - 2)
        {
            Assert.Equal(0xFF, jpeg[offset]);

            var marker = jpeg[offset + 1];
            offset += 2;

            if (marker == 0xD9)
                break;

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset, 2));
            Assert.True(length >= 2, $"marker FF{marker:X2} declared a length of {length}");
            Assert.True(offset + length <= jpeg.Length, $"marker FF{marker:X2} runs past the end of the file");

            switch (marker)
            {
                case 0xDB:
                    // Each table is one identifier byte and 64 values.
                    quantizationTables += (length - 2) / 65;
                    break;

                case 0xC4:
                    huffmanTables++;
                    break;

                case 0xC0:
                {
                    sawFrame = true;
                    var body = jpeg.AsSpan(offset + 2);

                    Assert.Equal(8, body[0]);
                    Assert.Equal(expectedHeight, BinaryPrimitives.ReadUInt16BigEndian(body.Slice(1, 2)));
                    Assert.Equal(expectedWidth, BinaryPrimitives.ReadUInt16BigEndian(body.Slice(3, 2)));
                    Assert.Equal(3, body[5]);

                    // Luma at 2x2, chroma at 1x1 - which is what makes this 4:2:0, and what the
                    // whole plane pipeline assumes.
                    Assert.Equal(0x22, body[7]);
                    Assert.Equal(0x11, body[10]);
                    Assert.Equal(0x11, body[13]);
                    break;
                }

                case 0xDA:
                    sawScan = true;
                    break;
            }

            offset += length;

            if (marker != 0xDA)
                continue;

            // The scan runs to the end-of-image marker, so structural walking stops here.
            Assert.True(sawFrame, "the scan appeared before the frame header");
            return;
        }

        Assert.True(sawScan, "the file has no scan");
        Assert.Equal(2, quantizationTables);
        Assert.Equal(4, huffmanTables);
    }

    /// <summary>Finds the first byte of entropy-coded data, just past the SOS header.</summary>
    static int FindScanStart(byte[] jpeg)
    {
        var offset = 2;
        while (offset < jpeg.Length - 2)
        {
            var marker = jpeg[offset + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 2, 2));

            if (marker == 0xDA)
                return offset + 2 + length;

            offset += 2 + length;
        }

        throw new InvalidOperationException("no scan found");
    }
}
