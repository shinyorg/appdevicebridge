#if FOLDERS_LINUX
namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// The net10.0 build: GTK's file dialog on Linux, which the maui-labs GTK head runs; anywhere else there is no picker. A
/// Linux folder is a plain path.
/// </summary>
static partial class FolderPlatform
{
    public static bool IsSupported => OperatingSystem.IsLinux();

    public static async Task<PickedLocation?> PickAsync(string? title, CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return null;

        var path = await OnMainThread(async () =>
        {
            using var dialog = Gtk.FileDialog.New();
            dialog.SetModal(true);

            if (title is not null)
                dialog.SetTitle(title);

            var parent = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Gtk.Window;

            try
            {
                using var folder = await dialog.SelectFolderAsync(parent);
                return folder?.GetPath();
            }
            catch (GLib.GException)
            {
                // Dismissing the dialog is reported as an error.
                return null;
            }
        });

        return path is { Length: > 0 }
            ? new PickedLocation(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path, PathPrefix + path)
            : null;
    }

    public static WebAppFileStore? Open(string root, string token) => IsSupported ? OpenPath(root, token) : null;

    public static void Release(string token)
    {
    }

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();
}
#endif
