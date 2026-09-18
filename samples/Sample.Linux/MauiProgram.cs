using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Shiny.AppDeviceBridge.AppSupport.Linux;
using Shiny.AppDeviceBridge.Desktop;

namespace Sample.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppLinuxGtk4<global::Sample.App>()
        .AddLinuxGtk4Essentials()
        .ConfigureSample()

        // The battery from UPower and power-profiles-daemon: the GTK4 Essentials one never raises its change events.
        .AddAppSupportLinux()

        // Desktop only, so each desktop head adds them rather than Sample.App: the bridges' dependency has no
        // Android or iOS build worth dragging into those heads.
        .AddTrayIconBridge()
        .AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space")
        .Build();
}
