#if WINDOWS
using Shiny.AppDeviceBridge.Desktop;
#endif

namespace Sample.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiApp<global::Sample.App>()
        .ConfigureSample()

        // The tray bridge is desktop only, and Windows is the only desktop this head builds.
#if WINDOWS
        .AddTrayIconBridge()
#endif
        .Build();
}
