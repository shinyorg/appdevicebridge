using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Shiny.AppDeviceBridge.TrayIcon;

namespace Sample.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppLinuxGtk4<global::Sample.App>()
        .ConfigureSample()

        // Desktop only, so each desktop head adds it rather than Sample.App: the bridge's dependency has no
        // Android or iOS build worth dragging into those heads.
        .AddTrayIconBridge()
        .Build();
}
