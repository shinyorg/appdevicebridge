using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Shiny.WebAppHost.Bridge;

/// <summary>Compiled into each bridge package that needs Shiny's MAUI host, for its platform targets only.</summary>
static class ShinyMauiHosting
{
    /// <summary>
    /// Calls <c>UseShiny()</c> unless something already has. It registers an initialization service
    /// every time it is called, so an app that calls it itself and then adds two bridges would otherwise
    /// start Shiny three times.
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
