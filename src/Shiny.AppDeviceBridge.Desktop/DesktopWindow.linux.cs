#if DESKTOP_LINUX
namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>
/// The net10.0 build: the maui-labs GTK4 head on Linux, unsupported anywhere else. A hidden GTK window has no taskbar
/// entry, and GtkApplication keeps running while it has windows, visible or not.
/// </summary>
sealed partial class DesktopWindow
{
    Gtk.Window? window;
    ulong headCloseHandler;
    bool closing;

    static bool PlatformSupported => OperatingSystem.IsLinux();

    bool AttachPlatform(object platformView, bool hideOnClose)
    {
        if (platformView is not Gtk.Window gtkWindow)
            return false;

        this.window = gtkWindow;

        if (hideOnClose)
        {
            // The head tears the MAUI window down from its own close-request handler, which GTK runs before any connected
            // later. It is held back here, and let through for a close that should go ahead.
            var signal = GObject.Functions.SignalLookup("close-request", Gtk.Window.GetGType());
            this.headCloseHandler = GObject.Functions.SignalHandlerFind(gtkWindow, GObject.SignalMatchType.Id, signal, default, null, 0, 0);

            if (this.headCloseHandler != 0)
                GObject.Functions.SignalHandlerBlock(gtkWindow, this.headCloseHandler);

            gtkWindow.OnCloseRequest += this.OnCloseRequest;
        }

        return true;
    }

    bool OnCloseRequest(Gtk.Window sender, EventArgs args)
    {
        if (this.closing)
            return false;

        if (this.ShouldHideOnClose)
        {
            this.HideNow();
            return true;
        }

        // A close that goes ahead: the head's handler sees it on a fresh request, after this one is refused.
        this.closing = true;
        if (this.headCloseHandler != 0)
            GObject.Functions.SignalHandlerUnblock(sender, this.headCloseHandler);

        this.Dispatch(sender.Close);
        return true;
    }

    void ShowPlatform()
    {
        this.window!.SetVisible(true);
        this.window.Present();
    }

    void HidePlatform() => this.window!.SetVisible(false);

    void QuitPlatform() => Gio.Application.GetDefault()?.Quit();
}
#endif
