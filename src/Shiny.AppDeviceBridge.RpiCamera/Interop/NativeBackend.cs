using System.Reflection;
using System.Runtime.InteropServices;

namespace Shiny.AppDeviceBridge.RpiCamera.Interop;

/// <summary>The outcome of probing for the native shim, computed once per process.</summary>
/// <param name="IsAvailable">True when the shim loaded and agreed on the ABI.</param>
/// <param name="Description">One line naming the backend and its libcamera, or why there isn't one.</param>
/// <param name="LibCameraVersion">The libcamera release the shim was compiled against, when known.</param>
internal record BackendStatus(bool IsAvailable, string Description, string? LibCameraVersion);


/// <summary>
/// Finds and validates libshinyrpi_camera.so.
/// </summary>
/// <remarks>
/// Three things can go wrong before a single frame is captured, and on a headless appliance
/// they are indistinguishable unless they are told apart here:
///
///   the .so is missing        - it was never built, or the publish did not include it
///   the .so will not load     - it was built against a different libcamera than is installed
///   the .so disagrees on ABI  - it is from a different revision of this repository
///
/// Each produces its own message naming the fix. This is the price of binding a library with
/// no stable ABI, and it is cheaper to pay here than in a support conversation.
/// </remarks>
internal static class NativeBackend
{
    static readonly Lock Gate = new();
    static BackendStatus? status;
    static string? configuredPath;
    static bool resolverRegistered;

    /// <summary>
    /// Points the loader at a specific file, for a device that keeps the shim somewhere other
    /// than beside the assemblies. Must be called before the first use of the backend.
    /// </summary>
    public static void ConfigureSearchPath(string? path)
    {
        lock (Gate)
        {
            if (status is not null && !String.Equals(configuredPath, path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The native camera library has already been loaded; its path cannot be changed. " +
                    "Set Camera:NativeLibraryPath in configuration before the camera service is first used."
                );
            }

            configuredPath = path;
        }
    }

    /// <summary>Probes for the shim, caching the result. Never throws.</summary>
    public static BackendStatus GetStatus()
    {
        lock (Gate)
            return status ??= Probe();
    }

    /// <summary>Throws when there is no usable backend, with the reason.</summary>
    public static void EnsureAvailable()
    {
        var current = GetStatus();
        if (!current.IsAvailable)
            throw new CameraUnavailableException(current.Description);
    }

    static BackendStatus Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new BackendStatus(
                false,
                $"No camera backend: libcamera exists only on Linux, and this process is running on {RuntimeInformation.OSDescription}.",
                null
            );
        }

        RegisterResolver();

        try
        {
            var abi = NativeMethods.shinyrpi_camera_abi_version();

            if (abi != NativeMethods.ExpectedAbiVersion)
            {
                return new BackendStatus(
                    false,
                    $"No camera backend: libshinyrpi_camera.so implements ABI {abi} but this build of " +
                    $"Shiny.AppDeviceBridge.RpiCamera requires ABI {NativeMethods.ExpectedAbiVersion}. The shim and the " +
                    "managed binding are from different revisions - rebuild it with " +
                    "native/shinyrpi-camera/build.sh.",
                    null
                );
            }

            var libCameraVersion = NativeMethods.LibCameraVersion();
            return new BackendStatus(
                true,
                $"libcamera {libCameraVersion} via libshinyrpi_camera.so (ABI {abi})",
                libCameraVersion
            );
        }
        catch (DllNotFoundException ex)
        {
            // Two very different problems land here: no file at all, and a file that will not
            // load because its libcamera is gone. The dlopen message distinguishes them and is
            // worth passing through verbatim.
            return new BackendStatus(
                false,
                "No camera backend: libshinyrpi_camera.so could not be loaded. Build it with " +
                "native/shinyrpi-camera/build.sh on the device, or install libcamera if it is " +
                $"missing. Loader said: {ex.Message}",
                null
            );
        }
        catch (EntryPointNotFoundException ex)
        {
            return new BackendStatus(
                false,
                "No camera backend: libshinyrpi_camera.so loaded but is missing entry points, so it " +
                "is an older or partial build. Rebuild it with native/shinyrpi-camera/build.sh. " +
                $"Loader said: {ex.Message}",
                null
            );
        }
        catch (BadImageFormatException ex)
        {
            return new BackendStatus(
                false,
                "No camera backend: libshinyrpi_camera.so is built for a different architecture than " +
                $"this process ({RuntimeInformation.ProcessArchitecture}). Loader said: {ex.Message}",
                null
            );
        }
    }

    static void RegisterResolver()
    {
        if (resolverRegistered)
            return;

        resolverRegistered = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeBackend).Assembly, Resolve);
    }

    static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!String.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
            return nint.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Zero hands the search back to the runtime's default probing, which covers a shim
        // installed into the system library path.
        return nint.Zero;
    }

    static IEnumerable<string> CandidatePaths()
    {
        const string fileName = "lib" + NativeMethods.LibraryName + ".so";

        if (configuredPath is { Length: > 0 })
        {
            // Accept either the file itself or the directory holding it - both are things
            // people put in a config file.
            yield return Directory.Exists(configuredPath)
                ? Path.Combine(configuredPath, fileName)
                : configuredPath;
        }

        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
        yield return "/usr/local/lib/" + fileName;
    }

    /// <summary>
    /// Turns a native result code into the exception the managed API promises, with the shim's
    /// own message attached - which is usually the libcamera error that actually explains it.
    /// </summary>
    public static Exception ToException(int result, string operation)
    {
        var detail = NativeMethods.LastError();
        var message = detail.Length > 0
            ? $"{operation} failed: {detail}"
            : $"{operation} failed with code {result}.";

        return result switch
        {
            NativeResult.Busy => new CameraUnavailableException(
                message + " Another process is using the camera - check for a running rpicam-apps " +
                "or a second instance of this appliance."
            ),
            NativeResult.NotFound => new CameraUnavailableException(message),
            NativeResult.Abi => new CameraUnavailableException(
                message + " Rebuild libshinyrpi_camera.so with native/shinyrpi-camera/build.sh."
            ),
            NativeResult.InvalidArgument => new ArgumentException(message),
            NativeResult.NoMemory => new InvalidOperationException(message),
            NativeResult.Unsupported => new NotSupportedException(message),
            _ => new InvalidOperationException(message)
        };
    }

    /// <summary>Throws unless the call succeeded.</summary>
    public static void ThrowIfFailed(int result, string operation)
    {
        if (result < NativeResult.Ok)
            throw ToException(result, operation);
    }
}
