using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// The host's security container in front of the app's. Authentication and authorization resolve from the first;
/// an endpoint class and its dependencies fall through to the second — including per request, through a scope
/// that spans both.
/// </summary>
sealed class WebAppServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider, IServiceScopeFactory
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory) || serviceType == typeof(IServiceProvider))
            return this;

        return Resolve(primary, fallback, serviceType);
    }

    public IServiceScope CreateScope() => new Scope(primary.CreateScope(), fallback.CreateScope());

    static object? Resolve(IServiceProvider primary, IServiceProvider fallback, Type serviceType)
    {
        var found = primary.GetService(serviceType);

        // A container answers IEnumerable<T> with an empty array rather than null, which would hide every
        // registration the app has for it.
        if (found is System.Collections.ICollection { Count: 0 }
            && serviceType.IsGenericType
            && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return fallback.GetService(serviceType) ?? found;

        return found ?? fallback.GetService(serviceType);
    }

    sealed class Scope(IServiceScope primary, IServiceScope fallback) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProvider)
                ? this
                : Resolve(primary.ServiceProvider, fallback.ServiceProvider, serviceType);

        public void Dispose()
        {
            primary.Dispose();
            fallback.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (primary is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false); else primary.Dispose();
            if (fallback is IAsyncDisposable b) await b.DisposeAsync().ConfigureAwait(false); else fallback.Dispose();
        }
    }
}
