using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;
using Shiny.AppDeviceBridge.AppLinks;

namespace Sample.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    // App links bridge: the link that launches the app arrives before MauiProgram has registered the bridge, so it
    // is handed over here. Once registered, the bridge takes later links itself.
    public override void OpenUrls(NSApplication application, NSUrl[] urls)
    {
        foreach (var url in urls)
            if (url.AbsoluteString is { } value)
                AppLinks.Receive(new Uri(value));
    }
}
