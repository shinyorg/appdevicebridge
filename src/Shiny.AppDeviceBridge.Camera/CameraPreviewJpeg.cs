using Shiny.Controls.Camera;
#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
using Shiny.Maui.Controls.Camera;
#endif
#if IOS || MACCATALYST || MACOS
using CoreGraphics;
using Foundation;
using ImageIO;
#elif ANDROID
using Android.Graphics;
#elif WINDOWS
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
#endif

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// Encodes one camera frame as a viewfinder JPEG.
/// <para>
/// Per platform, because a camera frame is a native buffer with no cross-platform way to read it: Apple hands back a
/// <c>CGImage</c> over the capture buffer, CameraX a planar YUV <c>ImageProxy</c>, Windows a BGRA array. Called on the
/// capture thread with a borrowed buffer, so it allocates little and fails by returning null rather than throwing.
/// </para>
/// </summary>
static class CameraPreviewJpeg
{
    /// <summary>A JPEG no longer than <paramref name="maxEdge"/> on its longest side, or null.</summary>
    public static ValueTask<byte[]?> EncodeAsync(CameraFrame frame, int maxEdge, int quality)
    {
#if IOS || MACCATALYST || MACOS
        return ValueTask.FromResult(EncodeApple(frame, maxEdge, quality / 100f));
#elif ANDROID
        return ValueTask.FromResult(EncodeAndroid(frame, quality));
#elif WINDOWS
        return EncodeWindowsAsync(frame, maxEdge, quality / 100f);
#else
        // Linux has no camera handler, so there is never a frame to encode.
        _ = frame;
        _ = maxEdge;
        _ = quality;
        return ValueTask.FromResult<byte[]?>(null);
#endif
    }

    /// <summary>The size to encode at, or null when the frame is already within <paramref name="maxEdge"/>.</summary>
    internal static (int Width, int Height)? Fit(int width, int height, int maxEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxEdge || longest == 0)
            return null;

        var scale = (double)maxEdge / longest;
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

#if IOS || MACCATALYST || MACOS
    static byte[]? EncodeApple(CameraFrame frame, int maxEdge, float quality)
    {
        if (frame is not AppleCameraFrame apple)
            return null;

        // Straight off the capture buffer rather than through Bgra, which materializes a full-frame managed copy this
        // path would throw away.
        using var source = apple.ToCGImage();
        if (source is null)
            return null;

        if (Fit((int)source.Width, (int)source.Height, maxEdge) is not { } target)
            return EncodeCGImage(source, quality);

        using var scaled = Scale(source, target.Width, target.Height);
        return EncodeCGImage(scaled ?? source, quality);
    }

    /// <summary>
    /// Redraws the frame smaller. Explicitly, rather than by asking ImageIO for a maximum pixel size: that option applies
    /// when a destination copies from an image source, and is ignored for a CGImage added directly — which would silently
    /// stream full-resolution frames.
    /// </summary>
    static CGImage? Scale(CGImage source, int width, int height)
    {
        try
        {
            using var space = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(
                null,
                width,
                height,
                8,
                width * 4,
                space,
                CGImageAlphaInfo.NoneSkipFirst | (CGImageAlphaInfo)CGBitmapFlags.ByteOrder32Little
            );
            context.InterpolationQuality = CGInterpolationQuality.Medium;
            context.DrawImage(new CGRect(0, 0, width, height), source);
            return context.ToImage();
        }
        catch (Exception)
        {
            return null;
        }
    }

    static byte[]? EncodeCGImage(CGImage image, float quality)
    {
        using var data = new NSMutableData();
        using var destination = CGImageDestination.Create(data, "public.jpeg", 1);
        if (destination is null)
            return null;

        destination.AddImage(image, new CGImageDestinationOptions { LossyCompressionQuality = quality });
        return destination.Close() ? data.ToArray() : null;
    }
#endif

#if ANDROID
    /// <summary>
    /// CameraX delivers planar YUV, and the platform's one JPEG encoder that takes YUV without a trip through a Bitmap is
    /// <see cref="YuvImage"/>, which wants NV21 — so the planes are interleaved into that first. No scaling: CameraX sizes
    /// the analysis stream near 640×480 on its own, and scaling further would mean decoding and re-encoding every frame.
    /// </summary>
    static byte[]? EncodeAndroid(CameraFrame frame, int quality)
    {
        if (frame is not AndroidCameraFrame android || android.Proxy.Image is not { } image)
            return null;

        var planes = image.GetPlanes();
        if (planes is null || planes.Length < 3)
            return null;

        var width = image.Width;
        var height = image.Height;
        var nv21 = new byte[width * height * 3 / 2];

        CopyPlane(planes[0], width, height, nv21, 0, 1);

        // NV21 is Y then interleaved VU: the V plane leads by a byte, both written at a stride of two.
        var chroma = width * height;
        CopyPlane(planes[2], width / 2, height / 2, nv21, chroma, 2);
        CopyPlane(planes[1], width / 2, height / 2, nv21, chroma + 1, 2);

        using var yuv = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
        using var stream = new MemoryStream();
        using var rect = new Android.Graphics.Rect(0, 0, width, height);

        return yuv.CompressToJpeg(rect, quality, stream) ? stream.ToArray() : null;
    }

    /// <summary>
    /// Copies one plane honouring its strides. A YUV_420_888 plane is not densely packed — rows are padded, and chroma is
    /// often already interleaved at a pixel stride of two — so a wholesale copy produces a diagonally smeared picture.
    /// </summary>
    static void CopyPlane(Android.Media.Image.Plane plane, int width, int height, byte[] destination, int offset, int destinationStride)
    {
        if (plane.Buffer is not { } buffer)
            return;

        var rowStride = plane.RowStride;
        var pixelStride = plane.PixelStride;
        var row = new byte[rowStride];
        var position = offset;

        buffer.Rewind();
        for (var y = 0; y < height; y++)
        {
            var remaining = buffer.Remaining();
            if (remaining <= 0)
                return;

            var length = Math.Min(rowStride, remaining);
            buffer.Get(row, 0, length);

            if (pixelStride == 1 && destinationStride == 1)
            {
                Array.Copy(row, 0, destination, position, Math.Min(width, length));
                position += width;
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var source = x * pixelStride;
                if (source >= length)
                    break;

                destination[position] = row[source];
                position += destinationStride;
            }
        }
    }
#endif

#if WINDOWS
    static async ValueTask<byte[]?> EncodeWindowsAsync(CameraFrame frame, int maxEdge, float quality)
    {
        if (frame is not WindowsCameraFrame windows)
            return null;

        var properties = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) }
        };

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)windows.Width, (uint)windows.Height, 96, 96, windows.Bgra);

        // The encoder scales as it writes.
        if (Fit(windows.Width, windows.Height, maxEdge) is { } target)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)target.Width;
            encoder.BitmapTransform.ScaledHeight = (uint)target.Height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
        }

        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
#endif
}
