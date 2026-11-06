using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Desktop.Client;

/// <summary>
/// The app's own window on the desktop: hide it and show it again, and quit — what a tray app needs to run with no window
/// and no taskbar or Dock entry, only its tray icon. The page keeps running while the window is hidden, so it still
/// answers tray clicks and quick entry. macOS (AppKit), Windows and Linux; Mac Catalyst, Android and iOS fail with 501.
/// </summary>
[BridgeClient("window", typeof(WindowJsonContext))]
public interface IWindowBridge
{
    /// <summary>Whether this is supported here, whether the window is showing, and what closing it does.</summary>
    [BridgeGet]
    Task<WindowStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows the window and brings the app forward, back on the taskbar or in the Dock.</summary>
    [BridgePost("show")]
    Task ShowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides the window and takes the app off the taskbar or out of the Dock; the page keeps running. Fails with 409
    /// <c>no_tray_icon</c> while no tray icon is showing, unless the app turned that check off — otherwise nothing would be
    /// left to bring the window back.
    /// </summary>
    [BridgePost("hide")]
    Task HideAsync(CancellationToken cancellationToken = default);

    /// <summary>Quits the app, shortly after answering. A tray app's Quit menu item.</summary>
    [BridgePost("quit")]
    Task QuitAsync(CancellationToken cancellationToken = default);

    /// <summary>The window was shown or hidden — by this page, by the user closing it, or by the app.</summary>
    [BridgeEvent("window.visibility")]
    Task<IAsyncDisposable> OnVisibilityAsync(Func<WindowStatus, Task> handler);
}

/// <param name="Supported">False where the window cannot be hidden: Mac Catalyst, Android, iOS.</param>
/// <param name="Visible">Whether the window is showing. False before the app has a window.</param>
/// <param name="HideOnClose">
/// Whether closing the window hides it instead of quitting — the app keeps running in the tray, and the page with it.
/// </param>
public sealed record WindowStatus(bool Supported, bool Visible, bool HideOnClose);

/// <summary>Serialization for the window contracts, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WindowStatus))]
public partial class WindowJsonContext : JsonSerializerContext;
