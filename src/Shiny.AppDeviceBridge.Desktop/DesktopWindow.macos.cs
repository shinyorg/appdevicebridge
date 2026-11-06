#if MACOS
using AppKit;
using Foundation;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>
/// The maui-labs AppKit head. Hidden means the app hidden and switched to an accessory app — no Dock icon, no menu bar, not
/// in the app switcher — and shown means a regular app again, brought forward.
/// </summary>
sealed partial class DesktopWindow
{
    NSWindow? window;

    static bool PlatformSupported => true;

    bool AttachPlatform(object platformView, bool hideOnClose)
    {
        if (platformView is not NSWindow nsWindow)
            return false;

        this.window = nsWindow;

        // The head's own delegate tears the MAUI window down once the NSWindow closes. This one asks first, and hands the
        // head everything it handles when the close goes ahead.
        if (hideOnClose)
            nsWindow.Delegate = new HideOnCloseDelegate(this, nsWindow.Delegate as NSWindowDelegate);

        return true;
    }

    void ShowPlatform()
    {
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        app.Unhide(app);
        this.window!.MakeKeyAndOrderFront(null);
        app.Activate();
    }

    // The app is hidden rather than the window ordered out: AppKit treats ordering out the last window like closing it, and
    // an app whose delegate answers true to applicationShouldTerminateAfterLastWindowClosed — most do — would quit.
    void HidePlatform()
    {
        var app = NSApplication.SharedApplication;
        app.Hide(app);
        app.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
    }

    void QuitPlatform() => NSApplication.SharedApplication.Terminate(null);

    sealed class HideOnCloseDelegate(DesktopWindow owner, NSWindowDelegate? inner) : NSWindowDelegate
    {
        public override bool WindowShouldClose(NSObject sender)
        {
            if (!owner.ShouldHideOnClose)
                return inner?.WindowShouldClose(sender) ?? true;

            owner.HideNow();
            return false;
        }

        public override void WillClose(NSNotification notification) => inner?.WillClose(notification);
    }
}
#endif
