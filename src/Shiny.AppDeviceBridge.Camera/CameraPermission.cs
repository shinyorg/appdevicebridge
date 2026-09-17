#if IOS || MACCATALYST || MACOS
using AVFoundation;
#endif
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// Whether this build has a camera, and whether the app may use it. Asked for rather than assumed: on Apple platforms
/// touching a capture session without the grant does not fail, it terminates the app.
/// </summary>
static class CameraPermission
{
#if ANDROID || IOS || MACCATALYST || MACOS || WINDOWS
    public static bool IsSupported => true;
#else
    /// <summary>The Linux head: the camera control has no handler there.</summary>
    public static bool IsSupported => false;
#endif

#if IOS || MACCATALYST || MACOS
    // AVFoundation directly rather than Essentials' permission: the AppKit head's Essentials does not implement the camera
    // permission and would answer "unknown" on a Mac that has granted it. AVCaptureDevice is what the session consults.
    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(
        AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video) switch
        {
            AVAuthorizationStatus.Authorized => ContractAccess.Available,
            AVAuthorizationStatus.Denied => ContractAccess.Denied,
            AVAuthorizationStatus.Restricted => ContractAccess.Restricted,
            _ => ContractAccess.Unknown
        }
    );

    public static async Task<ContractAccess> RequestAccessAsync()
    {
        // Prompts only while undecided; after a refusal it answers without showing anything.
        await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);
        return await GetAccessAsync();
    }
#elif ANDROID
    public static async Task<ContractAccess> GetAccessAsync() => Map(await Permissions.CheckStatusAsync<Permissions.Camera>());

    public static async Task<ContractAccess> RequestAccessAsync() => Map(await Permissions.RequestAsync<Permissions.Camera>());

    static ContractAccess Map(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted => ContractAccess.Available,
        PermissionStatus.Denied => ContractAccess.Denied,
        PermissionStatus.Restricted or PermissionStatus.Limited => ContractAccess.Restricted,
        PermissionStatus.Disabled => ContractAccess.Disabled,
        _ => ContractAccess.Unknown
    };
#elif WINDOWS
    // Windows grants the camera through the app's declared capability and the privacy settings, not a prompt the app can
    // raise; a refusal surfaces when the capture session starts, as a camera error on the screen.
    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(ContractAccess.Available);

    public static Task<ContractAccess> RequestAccessAsync() => GetAccessAsync();
#else
    public static Task<ContractAccess> GetAccessAsync() => Task.FromResult(ContractAccess.NotSupported);

    public static Task<ContractAccess> RequestAccessAsync() => GetAccessAsync();
#endif
}
