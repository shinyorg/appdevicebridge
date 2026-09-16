using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.RpiCamera.Interop;

namespace Shiny.AppDeviceBridge.RpiCamera;

/// <summary>
/// <see cref="ICameraService"/> over libcamera, through the native shim in
/// <c>native/shinyrpi-camera</c>.
/// </summary>
/// <remarks>
/// Frames come from the capture pipeline directly. There is no rpicam-still to launch per
/// photograph and no stdout to parse, which is what makes a continuous preview feed on a Pi
/// affordable: one process, one set of buffers, and a memcpy per frame.
///
/// The cost is that libshinyrpi_camera.so must have been built against the same libcamera the
/// device is running. When it was not, nothing here throws at construction - the service
/// reports <see cref="IsSupported"/> false with the reason, so an appliance whose camera is
/// broken still boots and can still be diagnosed over the web UI.
/// </remarks>
public sealed class LibCameraService : ICameraService, IDisposable
{
    readonly LibCameraOptions options;
    readonly ILoggerFactory loggerFactory;
    readonly ILogger<LibCameraService> logger;
    readonly SemaphoreSlim gate = new(1, 1);

    CameraManagerHandle? manager;
    bool disposed;

    /// <summary>Creates the service. Does not touch the camera stack until it is first used.</summary>
    public LibCameraService(
        LibCameraOptions options,
        ILoggerFactory loggerFactory
    )
    {
        this.options = options;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<LibCameraService>();

        if (this.options.NativeLibraryPath is { Length: > 0 } path)
            NativeBackend.ConfigureSearchPath(path);
    }

    /// <inheritdoc />
    public bool IsSupported => NativeBackend.GetStatus().IsAvailable;

    /// <inheritdoc />
    public string BackendDescription => NativeBackend.GetStatus().Description;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CameraDescriptor>> GetCameras(CancellationToken cancellationToken = default)
    {
        if (!this.IsSupported)
            return [];

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var handle = this.EnsureManager();

            NativeBackend.ThrowIfFailed(
                NativeMethods.shinyrpi_camera_manager_count(handle.DangerousGetHandle(), out var count),
                "Counting cameras"
            );

            var cameras = new List<CameraDescriptor>(count);
            for (var index = 0; index < count; index++)
            {
                var info = NativeCameraInfo.Create();
                var result = NativeMethods.shinyrpi_camera_manager_info(handle.DangerousGetHandle(), index, ref info);

                if (result < NativeResult.Ok)
                {
                    // One unreadable camera should not hide the others - a Pi with two modules
                    // where one has a bad cable is a real situation.
                    this.logger.LogWarning("Could not read camera {Index}: {Error}", index, NativeMethods.LastError());
                    continue;
                }

                cameras.Add(ToDescriptor(ref info));
            }

            return cameras;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ICameraSession> Open(
        CameraSessionOptions? options = null,
        string? cameraId = null,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        NativeBackend.EnsureAvailable();

        var sessionOptions = options ?? new CameraSessionOptions();

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var managerHandle = this.EnsureManager();

            NativeBackend.ThrowIfFailed(
                NativeMethods.shinyrpi_camera_open(managerHandle.DangerousGetHandle(), cameraId, out var raw),
                cameraId is null ? "Opening the first camera" : $"Opening camera '{cameraId}'"
            );

            var handle = new CameraHandle(raw);
            try
            {
                var descriptor = this.DescribeOpened(managerHandle, cameraId);
                var stream = Configure(handle, sessionOptions);

                NativeBackend.ThrowIfFailed(
                    NativeMethods.shinyrpi_camera_start(handle.DangerousGetHandle()),
                    "Starting the camera"
                );

                this.logger.LogInformation(
                    "Camera {Model} ({Id}) streaming {Width}x{Height} {Format}, {Buffers} buffers",
                    descriptor.Model,
                    descriptor.Id,
                    stream.Width,
                    stream.Height,
                    stream.PixelFormat,
                    stream.BufferCount
                );

                return new LibCameraSession(
                    handle,
                    descriptor,
                    stream,
                    this.options,
                    this.loggerFactory.CreateLogger<LibCameraSession>()
                );
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.manager?.Dispose();
        this.gate.Dispose();
    }

    CameraManagerHandle EnsureManager()
    {
        if (this.manager is { IsInvalid: false })
            return this.manager;

        NativeBackend.EnsureAvailable();

        NativeBackend.ThrowIfFailed(
            NativeMethods.shinyrpi_camera_manager_create(out var raw),
            "Starting the camera manager"
        );

        this.manager = new CameraManagerHandle(raw);
        this.logger.LogDebug("Camera backend: {Backend}", NativeBackend.GetStatus().Description);

        return this.manager;
    }

    /// <summary>
    /// Finds the descriptor for the camera that was just opened. The shim's open call does not
    /// return one, so it is matched back out of the enumeration - by id when one was asked for,
    /// otherwise the first, which is what open itself picked.
    /// </summary>
    CameraDescriptor DescribeOpened(CameraManagerHandle managerHandle, string? cameraId)
    {
        var count = 0;
        if (NativeMethods.shinyrpi_camera_manager_count(managerHandle.DangerousGetHandle(), out count) < NativeResult.Ok)
            count = 0;

        for (var index = 0; index < count; index++)
        {
            var info = NativeCameraInfo.Create();
            if (NativeMethods.shinyrpi_camera_manager_info(managerHandle.DangerousGetHandle(), index, ref info) < NativeResult.Ok)
                continue;

            var descriptor = ToDescriptor(ref info);
            if (cameraId is null || String.Equals(descriptor.Id, cameraId, StringComparison.Ordinal))
                return descriptor;
        }

        return new CameraDescriptor(cameraId ?? "", "", 0, 0, 0, CameraLocation.External);
    }

    static CameraStreamInfo Configure(CameraHandle handle, CameraSessionOptions options)
    {
        var config = NativeStreamConfig.Create();
        config.Width = (uint)Math.Max(0, options.Width);
        config.Height = (uint)Math.Max(0, options.Height);
        config.PixelFormat = options.PixelFormat.FourCc;
        config.BufferCount = (uint)Math.Max(0, options.BufferCount);
        config.Role = (uint)options.Role;
        config.QueueDepth = (uint)Math.Max(1, options.QueueDepth);

        NativeBackend.ThrowIfFailed(
            NativeMethods.shinyrpi_camera_configure(handle.DangerousGetHandle(), ref config),
            "Configuring the camera stream"
        );

        // Read back rather than echo the request: the pipeline silently adjusts anything it
        // cannot deliver exactly, and a caller that assumed otherwise would misread every frame.
        return new CameraStreamInfo(
            (int)config.Width,
            (int)config.Height,
            new CameraPixelFormat(config.PixelFormat),
            (int)config.Stride,
            (int)config.BufferCount,
            (int)config.QueueDepth
        );
    }

    static unsafe CameraDescriptor ToDescriptor(ref NativeCameraInfo info)
    {
        static string Read(byte* start, int capacity)
        {
            var span = new ReadOnlySpan<byte>(start, capacity);
            var end = span.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
        }

        fixed (byte* id = info.Id)
        fixed (byte* model = info.Model)
        {
            return new CameraDescriptor(
                Read(id, NativeCameraInfo.IdLength),
                Read(model, NativeCameraInfo.ModelLength),
                (int)info.MaxWidth,
                (int)info.MaxHeight,
                info.RotationDegrees,
                (CameraLocation)info.Location
            );
        }
    }
}


/// <summary>
/// The <see cref="ICameraService"/> registered when there is no camera backend - a workstation,
/// or a device with the native shim missing.
/// </summary>
/// <remarks>
/// Registered rather than omitted so that code depending on a camera still resolves and still
/// runs. An appliance whose camera is broken should boot, serve its web UI and say what is
/// wrong; it should not fail to start.
/// </remarks>
public sealed class UnavailableCameraService(string reason) : ICameraService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string BackendDescription { get; } = reason;

    /// <inheritdoc />
    public Task<IReadOnlyList<CameraDescriptor>> GetCameras(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);

    /// <inheritdoc />
    public Task<ICameraSession> Open(
        CameraSessionOptions? options = null,
        string? cameraId = null,
        CancellationToken cancellationToken = default
    ) => throw new CameraUnavailableException(this.BackendDescription);
}
