#if ANDROID
using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using AndroidX.DocumentFile.Provider;
using AndroidUri = Android.Net.Uri;

namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// Android picks a folder through the Storage Access Framework: a tree URI, kept across launches by a persisted grant.
/// There is no path, so the folder becomes a <see cref="SafFileStore"/>.
/// </summary>
static partial class FolderPlatform
{
    const ActivityFlags Access = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;

    static readonly ConcurrentDictionary<int, TaskCompletionSource<AndroidUri?>> pending = new();
    static int nextRequest = 0x4F00;

    public static bool IsSupported => true;

    public static async Task<PickedLocation?> PickAsync(string? title, CancellationToken cancellationToken)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? throw new InvalidOperationException("There is no activity to start the folder picker from.");

        var request = Interlocked.Increment(ref nextRequest) & 0xFFFF;
        var chosen = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request] = chosen;

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(Access | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);

        await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(() => activity.StartActivityForResult(intent, request));

        AndroidUri? uri;
        await using (cancellationToken.Register(() => chosen.TrySetResult(null)))
            uri = await chosen.Task;

        pending.TryRemove(request, out _);

        if (uri is null)
            return null;

        activity.ContentResolver!.TakePersistableUriPermission(uri, Access);

        var name = DocumentFile.FromTreeUri(activity, uri)?.Name ?? uri.LastPathSegment ?? "Folder";
        return new PickedLocation(name, uri.ToString()!);
    }

    internal static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (pending.TryRemove(requestCode, out var chosen))
            chosen.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    public static WebAppFileStore? Open(string root, string token)
    {
        var context = Android.App.Application.Context;
        var uri = AndroidUri.Parse(token)!;

        // The grant is what makes the folder readable after a restart; without it there is nothing to open.
        var granted = context.ContentResolver?.PersistedUriPermissions.Any(x => x.Uri?.Equals(uri) == true && x.IsReadPermission && x.IsWritePermission) == true;

        return granted && DocumentFile.FromTreeUri(context, uri) is { } tree && tree.Exists()
            ? new SafFileStore(root, context, tree)
            : null;
    }

    public static void Release(string token)
    {
        // A path holds no grant to give back.
        if (token.StartsWith(PathPrefix, StringComparison.Ordinal))
            return;

        try
        {
            Android.App.Application.Context.ContentResolver?.ReleasePersistableUriPermission(AndroidUri.Parse(token)!, Access);
        }
        catch (Java.Lang.SecurityException)
        {
            // Already released, or revoked by the user.
        }
    }
}
#endif
