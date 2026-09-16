#if WINDOWS
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// Windows picks with <see cref="FolderPicker"/>. An unpackaged app keeps the path; a packaged one also needs the folder in
/// the future access list to reach it after a restart, so it goes there too.
/// </summary>
static partial class FolderPlatform
{
    public static bool IsSupported => true;

    public static async Task<PickedLocation?> PickAsync(string? title, CancellationToken cancellationToken)
    {
        var folder = await OnMainThread(async () =>
        {
            var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
                ?? throw new InvalidOperationException("There is no window to show the folder picker in.");

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            return await picker.PickSingleFolderAsync();
        });

        if (folder is null || String.IsNullOrEmpty(folder.Path))
            return null;

        try
        {
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(Key(folder.Path), folder);
        }
        catch (InvalidOperationException)
        {
            // Unpackaged apps have no future access list, and need none: the path is enough.
        }

        return new PickedLocation(folder.DisplayName, PathPrefix + folder.Path);
    }

    public static WebAppFileStore? Open(string root, string token) => OpenPath(root, token);

    public static void Release(string token)
    {
        if (!token.StartsWith(PathPrefix, StringComparison.Ordinal))
            return;

        try
        {
            StorageApplicationPermissions.FutureAccessList.Remove(Key(token[PathPrefix.Length..]));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }
    }

    static string Key(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();
}
#endif
