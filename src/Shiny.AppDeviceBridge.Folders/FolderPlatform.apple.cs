#if IOS || MACCATALYST || MACOS
using System.Collections.Concurrent;
using Foundation;
#if MACOS
using AppKit;
#else
using UIKit;
using UniformTypeIdentifiers;
#endif

namespace Shiny.AppDeviceBridge.Folders;

/// <summary>
/// Apple platforms hand back a security-scoped URL. Access to it lasts only while the app holds it open, and only for
/// this launch — so the token is a bookmark, resolved and opened again on the next one.
/// </summary>
static partial class FolderPlatform
{
    const string BookmarkPrefix = "bookmark:";

#if MACOS || MACCATALYST
    const NSUrlBookmarkCreationOptions CreationOptions = NSUrlBookmarkCreationOptions.WithSecurityScope;
    const NSUrlBookmarkResolutionOptions ResolutionOptions = NSUrlBookmarkResolutionOptions.WithSecurityScope;
#else
    const NSUrlBookmarkCreationOptions CreationOptions = 0;
    const NSUrlBookmarkResolutionOptions ResolutionOptions = 0;
#endif

    // The URLs being accessed, so forgetting a folder can end its access.
    static readonly ConcurrentDictionary<string, NSUrl> open = new(StringComparer.Ordinal);

    public static bool IsSupported => true;

    public static async Task<PickedLocation?> PickAsync(string? title, CancellationToken cancellationToken)
    {
        var url = await ChooseAsync(title, cancellationToken);
        if (url?.Path is null)
            return null;

        // Access has to be open for the bookmark to carry it.
        var accessing = url.StartAccessingSecurityScopedResource();
        try
        {
            var bookmark = url.CreateBookmarkData(CreationOptions, [], null, out var error);
            var token = bookmark is null || error is not null
                ? PathPrefix + url.Path    // an app outside the sandbox gets a plain path back, which is all it needs
                : BookmarkPrefix + bookmark.GetBase64EncodedString(NSDataBase64EncodingOptions.None);

            return new PickedLocation(url.LastPathComponent ?? url.Path, token);
        }
        finally
        {
            if (accessing)
                url.StopAccessingSecurityScopedResource();
        }
    }

    public static WebAppFileStore? Open(string root, string token)
    {
        if (!token.StartsWith(BookmarkPrefix, StringComparison.Ordinal))
            return OpenPath(root, token);

        using var data = new NSData(token[BookmarkPrefix.Length..], NSDataBase64DecodingOptions.None);
        var url = NSUrl.FromBookmarkData(data, ResolutionOptions, null, out _, out var error);

        if (url?.Path is null || error is not null)
            return null;

        if (!open.ContainsKey(token))
        {
            url.StartAccessingSecurityScopedResource();
            open[token] = url;
        }

        return Directory.Exists(url.Path) ? new WebAppFileRoot(root, url.Path) : null;
    }

    public static void Release(string token)
    {
        if (open.TryRemove(token, out var url))
            url.StopAccessingSecurityScopedResource();
    }

#if MACOS
    static Task<NSUrl?> ChooseAsync(string? title, CancellationToken cancellationToken) => OnMainThread(() =>
    {
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = true;
        panel.CanChooseFiles = false;
        panel.AllowsMultipleSelection = false;
        panel.CanCreateDirectories = true;

        if (title is not null)
            panel.Message = title;

        return Task.FromResult(panel.RunModal() == 1 ? panel.Urls.FirstOrDefault() : null);
    });
#else
    static Task<NSUrl?> ChooseAsync(string? title, CancellationToken cancellationToken) => OnMainThread(async () =>
    {
        var chosen = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var picker = new UIDocumentPickerViewController([UTTypes.Folder], false) { AllowsMultipleSelection = false };

        picker.DidPickDocumentAtUrls += (_, e) => chosen.TrySetResult(e.Urls.FirstOrDefault());
        picker.WasCancelled += (_, _) => chosen.TrySetResult(null);

        if (title is not null)
            picker.Title = title;

        var presenter = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("There is no view controller to present the folder picker from.");

        presenter.PresentViewController(picker, true, null);

        await using (cancellationToken.Register(() => chosen.TrySetResult(null)))
            return await chosen.Task;
    });
#endif

    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();
}
#endif
