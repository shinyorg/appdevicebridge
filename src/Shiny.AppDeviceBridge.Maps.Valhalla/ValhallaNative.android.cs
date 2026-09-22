#if ANDROID
using System.Runtime.InteropServices;
using Android.Runtime;

namespace Shiny.AppDeviceBridge.Maps.Valhalla;

/// <summary>
/// valhalla-mobile's JNI entry points, called directly. They are written for its Kotlin class, but use neither the object
/// they are called on nor — with no HTTP client — any Java class, so .NET can call them with its own JNI environment and
/// skip the Kotlin, Moshi and model libraries the AAR would otherwise bring. Failures come back as valhalla-mobile's error
/// envelope, never as a Java exception.
/// </summary>
static partial class ValhallaNative
{
    const string Library = "valhalla-wrapper";

    public static nint Create(string configPath)
    {
        var path = JNIEnv.NewString(configPath);
        try
        {
            // valhalla-mobile builds the actor lazily when the config cannot be read yet; a failure surfaces on the first
            // route as its error envelope.
            var actor = (nint)createActor(JNIEnv.Handle, 0, path, 0);
            if (actor == 0)
                throw new InvalidOperationException("Valhalla could not start: the actor could not be allocated.");

            return actor;
        }
        finally
        {
            JNIEnv.DeleteLocalRef(path);
        }
    }

    public static string Route(nint actor, string request)
    {
        var bytes = JNIEnv.NewArray(System.Text.Encoding.UTF8.GetBytes(request));
        nint answer = 0;
        try
        {
            answer = route(JNIEnv.Handle, 0, actor, bytes);
            if (answer == 0)
                throw new OutOfMemoryException("Valhalla's answer could not be allocated.");

            var result = new byte[JNIEnv.GetArrayLength(answer)];
            JNIEnv.CopyArray(answer, result);
            return System.Text.Encoding.UTF8.GetString(result);
        }
        finally
        {
            JNIEnv.DeleteLocalRef(bytes);
            if (answer != 0)
                JNIEnv.DeleteLocalRef(answer);
        }
    }

    public static void Delete(nint actor) => deleteActor(JNIEnv.Handle, 0, actor);

    [LibraryImport(Library, EntryPoint = "Java_com_valhalla_valhalla_ValhallaKotlin_createActor")]
    private static partial long createActor(nint env, nint thiz, nint configPath, nint httpClient);

    [LibraryImport(Library, EntryPoint = "Java_com_valhalla_valhalla_ValhallaKotlin_deleteActor")]
    private static partial void deleteActor(nint env, nint thiz, long handle);

    [LibraryImport(Library, EntryPoint = "Java_com_valhalla_valhalla_ValhallaKotlin_route")]
    private static partial nint route(nint env, nint thiz, long handle, nint request);
}
#endif
