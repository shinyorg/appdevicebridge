using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Folders.Client;

/// <summary>
/// Folders the user picks with the platform's own picker. A picked folder becomes a file root: the page reads and writes
/// it through <see cref="IFilesBridge"/> under the root name it chose, and it is still there the next time the app runs,
/// until the page forgets it. Where the platform has no folder picker, calls fail with 501.
/// </summary>
[BridgeClient("folders", typeof(FoldersJsonContext))]
public interface IFoldersBridge
{
    /// <summary>Whether folders can be picked here, and the folders picked so far.</summary>
    [BridgeGet]
    Task<FolderList> GetFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the folder picker. Null when the user cancels. Picking again under a root name already in use replaces that
    /// folder; the app's own roots (<c>data</c>, <c>cache</c>) cannot be replaced.
    /// </summary>
    [BridgePost("pick")]
    Task<PickedFolder?> PickAsync(FolderPickRequest request, CancellationToken cancellationToken = default);

    /// <summary>Forgets a picked folder: its root goes away and the app gives up its access to it.</summary>
    [BridgeDelete("{root}")]
    Task ForgetAsync(string root, CancellationToken cancellationToken = default);
}
