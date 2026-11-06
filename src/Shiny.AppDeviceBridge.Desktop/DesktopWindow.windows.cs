#if WINDOWS
using Microsoft.UI.Windowing;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>WinUI. A hidden window has no taskbar button, so hiding it is all leaving the taskbar takes.</summary>
sealed partial class DesktopWindow
{
    Microsoft.UI.Xaml.Window? window;

    static bool PlatformSupported => true;

    bool AttachPlatform(object platformView, bool hideOnClose)
    {
        if (platformView is not Microsoft.UI.Xaml.Window xamlWindow)
            return false;

        this.window = xamlWindow;

        if (hideOnClose)
            xamlWindow.AppWindow.Closing += this.OnClosing;

        return true;
    }

    void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!this.ShouldHideOnClose)
            return;

        args.Cancel = true;
        this.HideNow();
    }

    void ShowPlatform()
    {
        this.window!.AppWindow.Show();
        this.window.Activate();
    }

    void HidePlatform() => this.window!.AppWindow.Hide();

    void QuitPlatform() => Microsoft.Maui.Controls.Application.Current?.Quit();
}
#endif
