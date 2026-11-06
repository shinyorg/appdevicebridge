using Microsoft.Maui.Dispatching;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>
/// The app's first window on the desktop heads: hides and shows it, takes the app off the taskbar or out of the Dock while
/// it is hidden, and — with <see cref="WindowBridgeOptions.HideOnClose"/> — turns closing it into hiding it. Each head's
/// half is in <c>DesktopWindow.{platform}.cs</c>; the rest answer unsupported.
/// </summary>
sealed partial class DesktopWindow(WindowBridgeOptions options, TrayPresence trays) : IDesktopWindow
{
    bool attached;
    int visible;

    public bool IsSupported => PlatformSupported;

    public bool IsVisible => Volatile.Read(ref this.visible) == 1;

    public event Action<bool>? VisibilityChanged;

    /// <summary>From the window handler's mapping, on the main thread, once the platform window exists.</summary>
    internal void Attach(object platformView)
    {
        // The first window is the app's; a second one (a modal, another window the app opens) is left alone.
        if (this.attached || !PlatformSupported || !this.AttachPlatform(platformView, options.HideOnClose))
            return;

        this.attached = true;
        this.SetVisible(true);

        // After the head has shown it: hidden from inside the mapping, it would be shown again a moment later.
        if (options.StartHidden)
            this.Dispatch(() => this.HideNow());
    }

    /// <summary>Whether closing should hide: asked when the user closes the window.</summary>
    bool ShouldHideOnClose => options.HideOnClose && (!options.RequireTrayIcon || trays.AnyVisible);

    public Task<bool> ShowAsync() => OnMain(() =>
    {
        if (!this.attached)
            return false;

        this.ShowPlatform();
        this.SetVisible(true);
        return true;
    });

    public Task<bool> HideAsync() => OnMain(() => this.attached && this.HideNow());

    public void Quit() => this.Dispatch(this.QuitPlatform);

    bool HideNow()
    {
        this.HidePlatform();
        this.SetVisible(false);
        return true;
    }

    void SetVisible(bool value)
    {
        var state = value ? 1 : 0;
        if (Interlocked.Exchange(ref this.visible, state) != state)
            this.VisibilityChanged?.Invoke(value);
    }

    // The app's dispatcher, as the tray bridge uses: a dispatcher captured from the thread the mapping ran on is not
    // available on every head, and AppKit refuses these calls from any other thread.
    static IDispatcher? MainDispatcher => Microsoft.Maui.Controls.Application.Current?.Dispatcher;

    static Task<bool> OnMain(Func<bool> action)
        => MainDispatcher is { IsDispatchRequired: true } main ? main.DispatchAsync(action) : Task.FromResult(action());

    void Dispatch(Action action)
    {
        if (MainDispatcher is { } main)
            main.Dispatch(action);
        else
            action();
    }
}

#if !MACOS && !WINDOWS && !DESKTOP_LINUX
sealed partial class DesktopWindow
{
    // Mac Catalyst has no reach into NSApplication to leave the Dock; Android and iOS have no such window.
    static bool PlatformSupported => false;

    bool AttachPlatform(object platformView, bool hideOnClose) => false;

    void ShowPlatform()
    {
    }

    void HidePlatform()
    {
    }

    void QuitPlatform()
    {
    }
}
#endif
