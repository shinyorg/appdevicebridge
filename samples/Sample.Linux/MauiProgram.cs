using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Shiny.AppDeviceBridge.Desktop;

namespace Sample.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppLinuxGtk4<global::Sample.App>()
        .AddLinuxGtk4Essentials()
        .ConfigureSample(bridge => bridge
            // Desktop only, so each desktop head adds them rather than Sample.App: the bridges' dependency has no
            // Android or iOS build worth dragging into those heads.
            .AddTrayIconBridge()
            .AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space")

            // A tray app: closing the window hides it (while the tray icon is up) and the page keeps running, so the tray
            // menu's Open and Quit and quick entry still work. A launch at login (--autostart) starts in the tray.
            .AddWindowBridge(o =>
            {
                o.HideOnClose = true;
                o.StartHidden = Environment.GetCommandLineArgs().Contains("--autostart");
            }))
        .Build();
}
