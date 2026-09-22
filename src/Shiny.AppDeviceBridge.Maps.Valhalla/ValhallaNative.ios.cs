#if IOS
using System.Runtime.InteropServices;

namespace Shiny.AppDeviceBridge.Maps.Valhalla;

/// <summary>
/// native/shiny-valhalla's C surface, statically linked into the app with valhalla-mobile's library. The shim turns every
/// C++ exception into a null actor or valhalla-mobile's error envelope, so nothing throws across this boundary.
/// </summary>
static partial class ValhallaNative
{
    // Bumped together with shiny_valhalla_abi_version in native/shiny-valhalla whenever the C surface changes.
    const int ExpectedAbiVersion = 2;

    static bool checkedAbi;

    public static nint Create(string configPath)
    {
        if (!checkedAbi)
        {
            var version = shiny_valhalla_abi_version();
            if (version != ExpectedAbiVersion)
                throw new InvalidOperationException($"The linked Valhalla shim is version {version}; this package needs {ExpectedAbiVersion}.");

            checkedAbi = true;
        }

        var actor = shiny_valhalla_create(configPath);
        if (actor == 0)
        {
            var error = shiny_valhalla_last_error();
            try
            {
                throw new InvalidOperationException($"Valhalla could not start: {Marshal.PtrToStringUTF8(error)}");
            }
            finally
            {
                shiny_valhalla_free(error);
            }
        }

        return actor;
    }

    public static string Route(nint actor, string request)
    {
        var result = shiny_valhalla_route(actor, request);
        if (result == 0)
            throw new OutOfMemoryException("Valhalla's answer could not be allocated.");

        try
        {
            return Marshal.PtrToStringUTF8(result) ?? String.Empty;
        }
        finally
        {
            shiny_valhalla_free(result);
        }
    }

    public static void Delete(nint actor) => shiny_valhalla_delete(actor);

    [LibraryImport("__Internal")]
    private static partial int shiny_valhalla_abi_version();

    [LibraryImport("__Internal", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shiny_valhalla_create(string configPath);

    [LibraryImport("__Internal")]
    private static partial void shiny_valhalla_delete(nint actor);

    [LibraryImport("__Internal")]
    private static partial nint shiny_valhalla_last_error();

    [LibraryImport("__Internal", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shiny_valhalla_route(nint actor, string request);

    [LibraryImport("__Internal")]
    private static partial void shiny_valhalla_free(nint value);
}
#endif
