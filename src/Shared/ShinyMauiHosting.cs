using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Shiny.AppDeviceBridge;

/// <summary>Compiled into Shiny.AppDeviceBridge.Maui for its platform targets, where Shiny's MAUI host exists.</summary>
static class ShinyMauiHosting
{
    /// <summary>
    /// Calls <c>UseShiny()</c> unless something already has. It registers an initialization service
    /// every time it is called, so an app that calls it itself would otherwise start Shiny twice.
    /// </summary>
    public static MauiAppBuilder EnsureShiny(this MauiAppBuilder builder)
    {
        var registered = builder.Services.Any(x =>
            x.ServiceType == typeof(IMauiInitializeService)
            && x.ImplementationType?.Name == "ShinyMauiInitializationService"
        );

        if (!registered)
            builder.UseShiny();

        return builder;
    }
}
