using Microsoft.Extensions.DependencyInjection;
using Shiny.Infrastructure;

namespace Shiny.WebAppHost.Bridge;

/// <summary>
/// Compiled into bridge packages for macOS, where Shiny.Hosting.Maui has no build and so nothing calls UseShiny.
/// </summary>
static class ShinyCoreHosting
{
    /// <summary>
    /// Registers Shiny's core services — IPlatform among them, which Shiny's macOS managers require — unless
    /// something already has.
    /// </summary>
    public static IServiceCollection EnsureShinyCore(this IServiceCollection services)
    {
        if (!services.Any(x => x.ServiceType == typeof(IPlatform)))
            services.AddShinyCoreServices();

        return services;
    }
}
