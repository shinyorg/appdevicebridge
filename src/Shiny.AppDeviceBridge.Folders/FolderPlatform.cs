namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// The platform half: showing the picker, and opening a picked folder again from its token. A token is one of
/// <c>path:</c> (Windows, Linux), <c>bookmark:</c> (Apple platforms) or a <c>content://</c> tree URI (Android).
/// </summary>
static partial class FolderPlatform
{
    const string PathPrefix = "path:";

    /// <summary>A folder that is a plain directory, which is what Windows and Linux pick.</summary>
    static WebAppFileStore? OpenPath(string root, string token)
        => token.StartsWith(PathPrefix, StringComparison.Ordinal) && token[PathPrefix.Length..] is { Length: > 0 } path && Directory.Exists(path)
            ? new WebAppFileRoot(root, path)
            : null;

#if !(ANDROID || IOS || MACCATALYST || MACOS || WINDOWS || FOLDERS_LINUX)
    public static bool IsSupported => false;

    public static Task<PickedLocation?> PickAsync(string? title, CancellationToken cancellationToken) => Task.FromResult<PickedLocation?>(null);

    public static WebAppFileStore? Open(string root, string token) => null;

    public static void Release(string token)
    {
    }
#endif
}
