using System.Runtime.InteropServices;

namespace Shiny.AppDeviceBridge.RpiCamera.Interop;

/// <summary>
/// Owns the shim's camera manager. libcamera permits one per process; the shim refcounts a
/// single instance behind however many of these exist.
/// </summary>
internal sealed class CameraManagerHandle : SafeHandle
{
    CameraManagerHandle() : base(nint.Zero, ownsHandle: true) { }

    internal CameraManagerHandle(nint value) : this() => this.SetHandle(value);

    /// <inheritdoc />
    public override bool IsInvalid => this.handle == nint.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        NativeMethods.shinyrpi_camera_manager_destroy(this.handle);
        return true;
    }
}


/// <summary>
/// Owns an acquired camera. Closing it stops capture, unmaps every buffer and releases the
/// sensor back to the system.
/// </summary>
/// <remarks>
/// A <see cref="SafeHandle"/> rather than a bare pointer because the capture thread sits
/// inside a blocking native call for most of its life, and disposal can be requested from any
/// other thread at any moment. DangerousAddRef over the pump loop keeps the handle from being
/// freed underneath it, which is the one race here that would be a hard crash rather than an
/// exception.
/// </remarks>
internal sealed class CameraHandle : SafeHandle
{
    CameraHandle() : base(nint.Zero, ownsHandle: true) { }

    internal CameraHandle(nint value) : this() => this.SetHandle(value);

    /// <inheritdoc />
    public override bool IsInvalid => this.handle == nint.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        NativeMethods.shinyrpi_camera_close(this.handle);
        return true;
    }
}
