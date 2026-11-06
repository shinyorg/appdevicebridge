using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Foundation;
using Shiny.AppDeviceBridge.Desktop;
using Shiny.AppDeviceBridge.Maps;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif

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
            .AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space")

            // A tray app: closing the window hides it (while the tray icon is up) and the page keeps running, so the tray
            // menu's Open and Quit and quick entry still work. A launch at login (--autostart) starts in the tray.
            .AddWindowBridge(o =>
            {
                o.HideOnClose = true;
                o.StartHidden = Environment.GetCommandLineArgs().Contains("--autostart");
            })

            // HttpClient's own TLS on macOS stops at 1.2, and FOSSGIS's Valhalla only takes 1.3. iOS and Mac Catalyst
            // use NSURLSession already.
            .AddMapsBridge(o => o.HttpMessageHandlerFactory = () => new NSUrlSessionHandler()))
#if DEBUG
        .AddMauiDevFlowAgent()
        .AddDevFlowWebView()
#endif
        .DiagLogging()
        .Build();

    // TEMPORARY diagnostics — remove before committing.
    static MauiAppBuilder DiagLogging(this MauiAppBuilder builder)
    {
        Microsoft.Extensions.Logging.LoggingBuilderExtensions.AddProvider(builder.Logging, new DiagLoggerProvider());
        return builder;
    }
}
