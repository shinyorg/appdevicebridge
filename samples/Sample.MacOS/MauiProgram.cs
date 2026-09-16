using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Shiny.AppDeviceBridge.TrayIcon;

namespace Sample.MacOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppMacOS<global::Sample.App>()
        .AddMacOSEssentials()
        .ConfigureSample()

        // Desktop only, so each desktop head adds it rather than Sample.App: the bridge's dependency has no
        // Android or iOS build worth dragging into those heads.
        .AddTrayIconBridge()
        .Build();
}
