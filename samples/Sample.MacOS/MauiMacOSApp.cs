using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Sample.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
}
