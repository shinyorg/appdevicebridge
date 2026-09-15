using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;

namespace Sample.MacOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp
        .CreateBuilder()
        .UseMauiAppMacOS<global::Sample.App>()
        .AddMacOSEssentials()
        .ConfigureSample()
        .Build();
}
