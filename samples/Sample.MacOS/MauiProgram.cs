using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Shiny.AppDeviceBridge.Desktop;

namespace Sample.MacOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppMacOS<global::Sample.App>()
        .AddMacOSEssentials()
        // Desktop only, so each desktop head adds them rather than Sample.App: the bridges' dependency has no
        // Android or iOS build worth dragging into those heads.
        .ConfigureSample(bridge => bridge
            .AddTrayIconBridge()
            .AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space"))
        .Build();
}
