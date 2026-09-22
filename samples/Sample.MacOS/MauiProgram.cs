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

            // HttpClient's own TLS on macOS stops at 1.2, and FOSSGIS's Valhalla only takes 1.3. iOS and Mac Catalyst
            // use NSURLSession already.
            .AddMapsBridge(o => o.HttpMessageHandlerFactory = () => new NSUrlSessionHandler()))
#if DEBUG
        .AddMauiDevFlowAgent()
        .AddDevFlowWebView()
#endif
        .Build();
}
