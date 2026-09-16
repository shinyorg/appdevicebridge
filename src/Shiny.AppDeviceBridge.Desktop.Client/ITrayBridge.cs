using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Desktop.Client;

/// <summary>
/// System tray and menu bar icons on Windows, macOS and Linux: create and update them, give them menus, show
/// notifications, animate them, and hear clicks and menu choices. Android and iOS fail with 501. Ids are 1–64 letters,
/// digits, <c>.</c>, <c>_</c> or <c>-</c>.
/// </summary>
[BridgeClient("tray", typeof(TrayJsonContext))]
public interface ITrayBridge
{
    /// <summary>Whether tray icons are supported here, the limit, and the icons this page created.</summary>
    [BridgeGet]
    Task<TrayStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an icon, assigning an id when <see cref="TrayIconInput.Id"/> is null. Fails with 429 past the limit.</summary>
    [BridgePost]
    Task<TrayIconInfo> CreateAsync(TrayIconInput icon, CancellationToken cancellationToken = default);

    /// <summary>Removes every icon.</summary>
    [BridgeDelete]
    Task RemoveAllAsync(CancellationToken cancellationToken = default);

    /// <summary>One icon. Fails with 404 when there is no such icon.</summary>
    [BridgeGet("{id}")]
    Task<TrayIconInfo> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the icon, or updates it: only properties that are set change, and an empty string clears
    /// <see cref="TrayIconInput.Tooltip"/>, <see cref="TrayIconInput.Title"/> or <see cref="TrayIconInput.Badge"/>. PUT
    /// by a fixed id lets a reloaded page adopt its icon rather than add another.
    /// </summary>
    [BridgePut("{id}")]
    Task<TrayIconInfo> PutAsync(string id, TrayIconInput icon, CancellationToken cancellationToken = default);

    /// <summary>Removes an icon.</summary>
    [BridgeDelete("{id}")]
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Replaces an icon's menu.</summary>
    [BridgePut("{id}/menu")]
    Task<TrayIconInfo> SetMenuAsync(string id, TrayMenuInput menu, CancellationToken cancellationToken = default);

    /// <summary>Removes an icon's menu.</summary>
    [BridgeDelete("{id}/menu")]
    Task ClearMenuAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Opens an icon's menu, as though it had been clicked.</summary>
    [BridgePost("{id}/menu/show")]
    Task ShowMenuAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Shows a notification from the icon.</summary>
    [BridgePost("{id}/notification")]
    Task NotifyAsync(string id, TrayNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Cycles the icon through two or more frames.</summary>
    [BridgePut("{id}/animation")]
    Task<TrayIconInfo> StartAnimationAsync(string id, TrayAnimation animation, CancellationToken cancellationToken = default);

    /// <summary>Stops animating and restores the icon.</summary>
    [BridgeDelete("{id}/animation")]
    Task StopAnimationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>An icon was clicked. Also delivered to background.js when no page is listening.</summary>
    [BridgeEvent("tray.click")]
    Task<IAsyncDisposable> OnClickAsync(Func<TrayClick, Task> handler);

    /// <summary>A menu item was chosen. Also delivered to background.js when no page is listening.</summary>
    [BridgeEvent("tray.menu")]
    Task<IAsyncDisposable> OnMenuAsync(Func<TrayMenuSelection, Task> handler);
}
