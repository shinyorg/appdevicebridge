using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;

namespace Sample.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppLinuxGtk4<global::Sample.App>()
        .ConfigureSample()
        .Build();
}
