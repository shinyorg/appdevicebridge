using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shiny.AppDeviceBridge.RpiCamera.Interop;

/// <summary>
/// Return codes from the native shim. Anything below zero is a failure; one is a timeout,
/// which is ordinary.
/// </summary>
internal static class NativeResult
{
    public const int Ok = 0;
    public const int Timeout = 1;
    public const int InvalidArgument = -1;
    public const int NotFound = -2;
    public const int Busy = -3;
    public const int InvalidState = -4;
    public const int NoMemory = -5;
    public const int Configuration = -6;
    public const int Io = -7;
    public const int Abi = -8;
    public const int Unsupported = -9;
    public const int Unknown = -99;
}


/// <summary>
/// Mirrors <c>shinyrpi_camera_info</c>.
/// </summary>
/// <remarks>
/// Fixed-size buffers rather than marshalled arrays, so the struct is blittable and
/// <c>LibraryImport</c> can pass it without any runtime marshalling.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCameraInfo
{
    public const int IdLength = 256;
    public const int ModelLength = 128;

    public uint StructSize;
    public uint MaxWidth;
    public uint MaxHeight;
    public int RotationDegrees;
    public uint Location;
    public uint Reserved0;
    public fixed byte Id[IdLength];
    public fixed byte Model[ModelLength];

    public static NativeCameraInfo Create() => new() { StructSize = (uint)Unsafe.SizeOf<NativeCameraInfo>() };
}


/// <summary>Mirrors <c>shinyrpi_stream_config</c>. Read back after configure - the pipeline adjusts.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStreamConfig
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public uint PixelFormat;
    public uint BufferCount;
    public uint Role;
    public uint QueueDepth;
    public uint Stride;

    public static NativeStreamConfig Create() => new() { StructSize = (uint)Unsafe.SizeOf<NativeStreamConfig>() };
}


/// <summary>
/// Mirrors <c>shinyrpi_frame</c>.
/// </summary>
/// <remarks>
/// The plane pointers and lengths are three separate fields rather than fixed buffers because
/// a fixed buffer cannot have element type <see cref="nint"/>, and hard-coding eight bytes per
/// pointer would silently produce the wrong layout on a 32-bit ARM build. Declared this way it
/// is correct on both, and the struct_size handshake catches it if it ever is not.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrame
{
    public uint StructSize;
    public uint PlaneCount;
    public ulong FrameId;
    public ulong Sequence;
    public long TimestampNs;

    public nint PlaneData0;
    public nint PlaneData1;
    public nint PlaneData2;

    public ulong PlaneLength0;
    public ulong PlaneLength1;
    public ulong PlaneLength2;

    public uint Width;
    public uint Height;
    public uint PixelFormat;
    public uint Stride;

    public static NativeFrame Create() => new() { StructSize = (uint)Unsafe.SizeOf<NativeFrame>() };

    public readonly nint PlaneData(int index) => index switch
    {
        0 => this.PlaneData0,
        1 => this.PlaneData1,
        2 => this.PlaneData2,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public readonly int PlaneLength(int index) => index switch
    {
        0 => (int)this.PlaneLength0,
        1 => (int)this.PlaneLength1,
        2 => (int)this.PlaneLength2,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}


/// <summary>Mirrors <c>shinyrpi_stats</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStats
{
    public uint StructSize;
    public uint QueueLength;
    public uint CheckedOut;
    public uint Reserved0;
    public ulong FramesCompleted;
    public ulong FramesDelivered;
    public ulong FramesDropped;
    public ulong FramesErrored;

    public static NativeStats Create() => new() { StructSize = (uint)Unsafe.SizeOf<NativeStats>() };
}
