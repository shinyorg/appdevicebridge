using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shiny.AppDeviceBridge.RpiCamera.Interop;
using Shiny.AppDeviceBridge.RpiCamera;

namespace Shiny.AppDeviceBridge.Tests.RpiCamera;

/// <summary>
/// Guards the managed side of the libcamera binding.
/// </summary>
/// <remarks>
/// None of this touches a camera - there is not one on a build machine. What it does cover is
/// the part that cannot be caught by running the appliance on a Pi and looking at the picture:
/// the struct layouts that the native shim validates by size, and the FourCC arithmetic. A
/// layout that drifts produces a runtime ABI error on the device rather than a build failure,
/// so it is worth pinning the numbers here.
/// </remarks>
public class CameraInteropTests
{
    /// <summary>
    /// The sizes the C header produces on a 64-bit target, computed by hand from the field
    /// list. These are the numbers the shim compares against, so a change to either side that
    /// is not mirrored in the other shows up here rather than on a device.
    /// </summary>
    [Theory]
    [InlineData(typeof(NativeCameraInfo), 24 + 256 + 128)]
    [InlineData(typeof(NativeStreamConfig), 8 * 4)]
    [InlineData(typeof(NativeStats), 16 + 32)]
    public void NativeStructs_MatchTheCLayout(Type type, int expectedSize)
        => Assert.Equal(expectedSize, Marshal.SizeOf(type));

    [Fact]
    public void NativeFrame_MatchesTheCLayoutOn64Bit()
    {
        // 4 + 4 + 8 + 8 + 8 = 32, three pointers = 24, three lengths = 24, four uints = 16.
        // Only meaningful on a 64-bit build; on 32-bit the pointer array is narrower and the
        // shim's struct_size check is what catches a mismatch.
        if (nint.Size != 8)
            return;

        Assert.Equal(96, Unsafe.SizeOf<NativeFrame>());
    }

    [Fact]
    public void NativeStructs_CarryTheirOwnSize()
    {
        // Every Create() has to stamp struct_size, because the shim rejects a zero.
        Assert.Equal((uint)Unsafe.SizeOf<NativeCameraInfo>(), NativeCameraInfo.Create().StructSize);
        Assert.Equal((uint)Unsafe.SizeOf<NativeStreamConfig>(), NativeStreamConfig.Create().StructSize);
        Assert.Equal((uint)Unsafe.SizeOf<NativeFrame>(), NativeFrame.Create().StructSize);
        Assert.Equal((uint)Unsafe.SizeOf<NativeStats>(), NativeStats.Create().StructSize);
    }

    [Fact]
    public void ControlIds_MatchTheHeader()
    {
        // The managed enum is cast straight to the shim's control constants, so the two have to
        // agree numerically. Spot-checked at both ends of the range.
        Assert.Equal(1, (int)CameraControl.AutoExposure);
        Assert.Equal(2, (int)CameraControl.ExposureTime);
        Assert.Equal(11, (int)CameraControl.FrameDurationMin);
        Assert.Equal(14, (int)CameraControl.LensPosition);
        Assert.Equal(15, (int)CameraControl.NoiseReduction);
    }

    [Fact]
    public void StreamRoles_MatchTheHeader()
    {
        Assert.Equal(0, (int)CameraStreamRole.Viewfinder);
        Assert.Equal(1, (int)CameraStreamRole.StillCapture);
        Assert.Equal(2, (int)CameraStreamRole.VideoRecording);
        Assert.Equal(3, (int)CameraStreamRole.Raw);
    }

    [Fact]
    public void Locations_MatchTheHeader()
    {
        Assert.Equal(0, (int)CameraLocation.External);
        Assert.Equal(1, (int)CameraLocation.Front);
        Assert.Equal(2, (int)CameraLocation.Back);
    }
}


/// <summary>Covers the pixel format helper, whose byte ordering is the classic trap.</summary>
public class CameraPixelFormatTests
{
    [Fact]
    public void FromCode_PacksLittleEndian()
    {
        // 'M' | 'J' << 8 | 'P' << 16 | 'G' << 24 - the DRM FourCC convention libcamera uses.
        var expected = (uint)'M' | ((uint)'J' << 8) | ((uint)'P' << 16) | ((uint)'G' << 24);
        Assert.Equal(expected, CameraPixelFormat.FromCode("MJPG").FourCc);
    }

    [Fact]
    public void WellKnownFormats_HaveTheirDrmCodes()
    {
        Assert.Equal("MJPG", CameraPixelFormat.Mjpeg.ToString());
        Assert.Equal("RG24", CameraPixelFormat.Rgb888.ToString());
        Assert.Equal("BG24", CameraPixelFormat.Bgr888.ToString());
        Assert.Equal("YU12", CameraPixelFormat.Yuv420.ToString());
        Assert.Equal("NV12", CameraPixelFormat.Nv12.ToString());
    }

    [Fact]
    public void ToString_RoundTripsFromCode()
    {
        foreach (var code in new[] { "MJPG", "RG24", "BG24", "XR24", "YU12", "NV12", "YUYV" })
            Assert.Equal(code, CameraPixelFormat.FromCode(code).ToString());
    }

    [Fact]
    public void Default_IsTreatedAsUnset()
    {
        // Zero means "let the pipeline choose" all the way down to the shim, so the default
        // struct value has to be zero and has to print as something readable.
        Assert.Equal(0u, default(CameraPixelFormat).FourCc);
        Assert.Equal("(none)", default(CameraPixelFormat).ToString());
    }

    [Theory]
    [InlineData("MJP")]
    [InlineData("MJPEG")]
    [InlineData("")]
    public void FromCode_RejectsAnythingButFourCharacters(string code)
        => Assert.ThrowsAny<ArgumentException>(() => CameraPixelFormat.FromCode(code));

    [Fact]
    public void OnlyMjpegIsCompressed()
    {
        Assert.True(CameraPixelFormat.Mjpeg.IsCompressed);
        Assert.False(CameraPixelFormat.Yuv420.IsCompressed);
        Assert.False(CameraPixelFormat.Bgr888.IsCompressed);
    }
}


/// <summary>Covers the frame's plane arithmetic and its pooled-buffer lifetime.</summary>
public class CameraFrameTests
{
    static CameraFrame CreateFrame(params int[] planeLengths)
    {
        var total = planeLengths.Sum();
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        var offsets = new int[planeLengths.Length];
        var offset = 0;

        for (var i = 0; i < planeLengths.Length; i++)
        {
            offsets[i] = offset;
            buffer.AsSpan(offset, planeLengths[i]).Fill((byte)(i + 1));
            offset += planeLengths[i];
        }

        return new CameraFrame(
            buffer,
            offsets,
            planeLengths,
            640,
            480,
            640,
            CameraPixelFormat.Yuv420,
            42,
            TimeSpan.FromSeconds(1)
        );
    }

    [Fact]
    public void Data_IsTheFirstPlane()
    {
        using var frame = CreateFrame(10, 4, 4);

        Assert.Equal(3, frame.PlaneCount);
        Assert.Equal(10, frame.Data.Length);
        Assert.True(frame.Data.ToArray().All(b => b == 1));
    }

    [Fact]
    public void GetPlane_SlicesEachPlaneIndependently()
    {
        using var frame = CreateFrame(10, 4, 6);

        Assert.Equal(10, frame.GetPlane(0).Length);
        Assert.Equal(4, frame.GetPlane(1).Length);
        Assert.Equal(6, frame.GetPlane(2).Length);

        // The fill values prove the offsets, not just the lengths - an off-by-one in the
        // offset arithmetic would return the right count of the wrong bytes.
        Assert.True(frame.GetPlane(1).ToArray().All(b => b == 2));
        Assert.True(frame.GetPlane(2).ToArray().All(b => b == 3));
    }

    [Fact]
    public void GetPlane_RejectsAnIndexPastTheEnd()
    {
        using var frame = CreateFrame(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetPlane(1).Length);
    }

    [Fact]
    public void AccessAfterDisposeThrowsRatherThanReadingARecycledBuffer()
    {
        var frame = CreateFrame(10);
        frame.Dispose();

        // The array is back in the pool and may already belong to someone else, so reading it
        // has to fail loudly rather than return whatever is there now.
        Assert.Throws<ObjectDisposedException>(() => frame.Data.Length);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        // Double-returning an array to the pool corrupts it for everyone, so the second dispose
        // has to be a no-op rather than a second Return.
        var frame = CreateFrame(10);
        frame.Dispose();
        frame.Dispose();
    }

    [Fact]
    public void MetadataSurvivesToTheConsumer()
    {
        using var frame = CreateFrame(8);

        Assert.Equal(640, frame.Width);
        Assert.Equal(480, frame.Height);
        Assert.Equal(640, frame.Stride);
        Assert.Equal(42UL, frame.Sequence);
        Assert.Equal(TimeSpan.FromSeconds(1), frame.Timestamp);
        Assert.Equal(CameraPixelFormat.Yuv420, frame.PixelFormat);
    }
}


/// <summary>Covers the behaviour promised when there is no camera backend at all.</summary>
public class UnavailableCameraServiceTests
{
    [Fact]
    public async Task ReportsItselfUnsupportedWithAReason()
    {
        var service = new UnavailableCameraService("no libcamera here");

        Assert.False(service.IsSupported);
        Assert.Equal("no libcamera here", service.BackendDescription);
        Assert.Empty(await service.GetCameras());
    }

    [Fact]
    public async Task OpenThrowsWithTheSameReason()
    {
        // The whole point of registering this rather than nothing: the appliance boots, and the
        // reason the camera is missing is carried all the way to whoever asks for it.
        var service = new UnavailableCameraService("shim was never built");

        var error = await Assert.ThrowsAsync<CameraUnavailableException>(() => service.Open());
        Assert.Equal("shim was never built", error.Message);
    }
}
