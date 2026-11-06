using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Handlers;
using Shiny.AppDeviceBridge.Desktop.Client;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>How the app's window behaves as a tray app. Everything is off until set.</summary>
public sealed class WindowBridgeOptions
{
    /// <summary>
    /// Closing the window hides it instead of quitting: the app leaves the taskbar or Dock and keeps running in the tray,
    /// and the page with it, so it still answers tray clicks and quick entry. Quit from the tray menu
    /// (<c>POST /_bridge/window/quit</c>) or the app menu.
    /// </summary>
    public bool HideOnClose { get; set; }

    /// <summary>
    /// Hide the window as soon as the app has one — for a launch at login that should land in the tray. The page still
    /// loads, so it can put its tray icon up. Pass <c>Environment.GetCommandLineArgs()</c> through your own check, such as
    /// the <c>--autostart</c> argument the app bridge's launch at login can add.
    /// </summary>
    public bool StartHidden { get; set; }

    /// <summary>
    /// Closing hides, and <c>POST /_bridge/window/hide</c> is accepted, only while a tray icon from the tray bridge is
    /// showing — otherwise nothing would be left to bring the window back, and the app would be running where nobody can
    /// see it. On by default; turn it off for an app that comes back some other way, such as a global hotkey of its own.
    /// </summary>
    public bool RequireTrayIcon { get; set; } = true;
}

public static class WindowBridgeExtensions
{
    static int hooked;

    /// <summary>
    /// Adds <c>/_bridge/window</c>: hide the app's window and show it again, and quit — so a tray app can run with no
    /// window and no taskbar or Dock entry, only its tray icon.
    /// <code>
    /// bridge
    ///     .AddTrayIconBridge()
    ///     .AddWindowBridge(o =&gt; o.HideOnClose = true);
    /// </code>
    /// <para>
    /// macOS (AppKit), Windows and Linux (GTK4). Mac Catalyst, Android and iOS report <c>{ "supported": false }</c> and
    /// answer 501. While hidden the app is off the taskbar, and on macOS out of the Dock and the app switcher.
    /// </para>
    /// </summary>
    public static MauiAppDeviceBridgeBuilder AddWindowBridge(this MauiAppDeviceBridgeBuilder bridge, Action<WindowBridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var options = new WindowBridgeOptions();
        configure?.Invoke(options);

        bridge.Services.TryAddSingleton(options);
        bridge.Services.TryAddSingleton<TrayPresence>();
        bridge.Services.TryAddSingleton<DesktopWindow>();
        bridge.Services.TryAddSingleton<IDesktopWindow>(sp => sp.GetRequiredService<DesktopWindow>());

        // Every head's window handler chains the element mapper, so this runs once for each window it creates — with the
        // platform window already made and its own close handling connected, which is what gets wrapped.
        if (Interlocked.Exchange(ref hooked, 1) == 0)
        {
            ElementHandler.ElementMapper.AppendToMapping("ShinyAppDeviceBridgeWindow", (handler, element) =>
            {
                if (element is IWindow && handler.PlatformView is { } view && handler.MauiContext?.Services.GetService<DesktopWindow>() is { } window)
                    window.Attach(view);
            });
        }

        bridge.AddBridge<WindowBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/window</c> over the app's first window.
/// <code>
/// GET  /_bridge/window          { "supported": true, "visible": true, "hideOnClose": true }
/// POST /_bridge/window/show
/// POST /_bridge/window/hide     409 no_tray_icon while no tray icon is showing
/// POST /_bridge/window/quit
///
/// events: window.visibility
/// </code>
/// </summary>
public sealed class WindowBridge : IWebAppBridge, IDisposable
{
    const string VisibilityEvent = "window.visibility";

    /// <summary>Long enough for the answer to reach the page before the app goes.</summary>
    static readonly TimeSpan QuitDelay = TimeSpan.FromMilliseconds(250);

    readonly IDesktopWindow? window;
    readonly WindowBridgeOptions options;
    readonly TrayPresence? trays;
    readonly WebAppEventSource<WindowStatus> visibility = new();

    public WindowBridge(IServiceProvider services)
    {
        this.window = services.GetOptionalService<IDesktopWindow>();
        this.options = services.GetOptionalService<WindowBridgeOptions>() ?? new WindowBridgeOptions();
        this.trays = services.GetOptionalService<TrayPresence>();

        if (this.window is not null)
            this.window.VisibilityChanged += this.OnVisibilityChanged;
    }

    public string Name => "window";

    public bool IsSupported => this.window?.IsSupported == true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent(VisibilityEvent, ct => this.visibility.ListenAsync(ct), WindowJsonContext.Default.WindowStatus)
        .MapGet("", ctx => WebAppBridgeResults.Json(ctx, this.Describe(), WindowJsonContext.Default.WindowStatus))
        .MapPost("/show", this.ShowAsync)
        .MapPost("/hide", this.HideAsync)
        .MapPost("/quit", this.QuitAsync);

    async ValueTask ShowAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        await (await this.window!.ShowAsync() ? WebAppBridgeResults.NoContent(context) : NoWindow(context));
    }

    async ValueTask HideAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        if (this.options.RequireTrayIcon && this.trays?.AnyVisible != true)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status409Conflict,
                "no_tray_icon",
                "Show a tray icon first: with none, nothing would be left to bring the window back."
            );
            return;
        }

        await (await this.window!.HideAsync() ? WebAppBridgeResults.NoContent(context) : NoWindow(context));
    }

    async ValueTask QuitAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await NotSupported(context);
            return;
        }

        await WebAppBridgeResults.NoContent(context);
        _ = this.QuitSoonAsync();
    }

    async Task QuitSoonAsync()
    {
        await Task.Delay(QuitDelay).ConfigureAwait(false);
        this.window!.Quit();
    }

    WindowStatus Describe() => new(this.IsSupported, this.IsSupported && this.window!.IsVisible, this.options.HideOnClose);

    void OnVisibilityChanged(bool visible) => this.visibility.Publish(new WindowStatus(true, visible, this.options.HideOnClose));

    static ValueTask NotSupported(HttpContext context) => WebAppBridgeResults.NotSupported(context, "Hiding the app's window");

    static ValueTask NoWindow(HttpContext context)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "no_window", "The app has no window yet.");

    public void Dispose()
    {
        if (this.window is not null)
            this.window.VisibilityChanged -= this.OnVisibilityChanged;
    }
}

/// <summary>Whether the tray bridge has an icon showing: what lets the window hide without stranding the app.</summary>
sealed class TrayPresence
{
    int visible;

    public bool AnyVisible => Volatile.Read(ref this.visible) > 0;

    internal void Set(int count) => Volatile.Write(ref this.visible, count);
}

/// <summary>The app's window, as the window bridge drives it. <see cref="DesktopWindow"/> is the real one.</summary>
interface IDesktopWindow
{
    bool IsSupported { get; }

    bool IsVisible { get; }

    event Action<bool>? VisibilityChanged;

    /// <summary>False when the app has no window yet.</summary>
    Task<bool> ShowAsync();

    /// <summary>False when the app has no window yet.</summary>
    Task<bool> HideAsync();

    void Quit();
}
