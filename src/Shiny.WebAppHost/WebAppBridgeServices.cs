using Microsoft.Extensions.DependencyInjection;

namespace Shiny.WebAppHost;

public static class WebAppBridgeServices
{
    /// <summary>
    /// The service, or null when it is not registered — or when it is registered but cannot be constructed.
    /// <para>
    /// For bridges resolving their native service. Resolving the host constructs every bridge, and a native
    /// registration that is incomplete on one platform (a dependency the head was expected to provide, say) would
    /// otherwise take the whole web app down with it instead of reporting one bridge as unsupported.
    /// </para>
    /// </summary>
    public static T? GetOptionalService<T>(this IServiceProvider services) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            return services.GetService<T>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"{typeof(T).FullName} could not be created, so its bridge reports itself unsupported: {ex.GetBaseException().Message}");
            return null;
        }
    }
}
