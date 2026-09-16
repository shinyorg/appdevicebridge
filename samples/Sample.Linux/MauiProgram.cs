using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Shiny.AppDeviceBridge.Desktop;

namespace Sample.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppLinuxGtk4<global::Sample.App>()
        .ConfigureSample()

        // Desktop only, so each desktop head adds them rather than Sample.App: the bridges' dependency has no
        // Android or iOS build worth dragging into those heads.
        .AddTrayIconBridge()
        .AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space")
        .Build();
}
