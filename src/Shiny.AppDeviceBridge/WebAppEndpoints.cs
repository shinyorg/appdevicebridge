using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge;

/// <summary>Policy names the host defines, for <c>RequireAuthorization(...)</c> on your own endpoints.</summary>
public static class WebAppPolicies
{
    /// <summary>
    /// Only this device's own WebView — the caller holding the launch cookie. Every other scheme is refused,
    /// however valid its credentials, which makes this the policy for an endpoint the page uses that is not
    /// meant for anything else.
    /// </summary>
    public const string Session = "appdevicebridge:session";
}

/// <summary>
/// The WebView as an authentication scheme: the launch cookie becomes a principal, so your own endpoints treat
/// the page the same way they treat a caller presenting an API key or a token.
/// <para>
/// Only ever from this device. The cookie is the device's secret; one arriving over the network proves nothing
/// about who sent it, so a remote request never authenticates through this scheme.
/// </para>
/// </summary>
public sealed class WebAppSessionAuthenticationHandler(WebAppSession session) : IAuthenticationHandler
{
    public const string SchemeName = "WebAppSession";

    /// <summary>Carried by the WebView's principal; <see cref="WebAppPolicies.Session"/> requires it.</summary>
    public const string SessionClaim = "appdevicebridge:session";

    public string Scheme => SchemeName;

    public ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!WebAppHost.IsLocal(context.Connection.RemoteIpAddress) || !session.IsValid(context.Request.Cookies[WebAppSession.CookieName]))
            return ValueTask.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "webview"),
                new Claim(SessionClaim, "true")
            ],
            SchemeName
        );

        return ValueTask.FromResult(AuthenticateResult.Success(new ClaimsPrincipal(identity)));
    }
}

/// <summary>
/// What the app added to the host's server, gathered while the container is being built and applied when the
/// host is created.
/// <para>
/// Authentication and authorization are built into a container the host owns rather than the app's.
/// Shiny.Net.HttpServer keeps the first <c>AddAuthorization</c> it sees and drops the rest, and registers an
/// <c>HttpServer</c> of its own on the way; doing either in the app's container would quietly change an app that
/// runs a server of its own. Endpoint classes still get their dependencies from the app.
/// </para>
/// </summary>
public sealed class WebAppEndpointRegistrations
{
    internal List<Action<HttpServer, IServiceProvider>> Maps { get; } = [];
    internal List<Action<AuthenticationBuilder>> Authentication { get; } = [];
    internal List<Action<AuthorizationOptions>> Authorization { get; } = [];

    internal bool HasEndpoints => this.Maps.Count > 0;

    internal static WebAppEndpointRegistrations GetOrAdd(IServiceCollection services)
    {
        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppEndpointRegistrations))?.ImplementationInstance is WebAppEndpointRegistrations existing)
            return existing;

        var registrations = new WebAppEndpointRegistrations();
        services.AddSingleton(registrations);
        return registrations;
    }

    /// <summary>The host's private security container: the WebView scheme, the app's schemes and one set of policies.</summary>
    internal ServiceProvider BuildSecurity(WebAppSession session)
    {
        var services = new ServiceCollection();
        var http = new ShinyHttpServerBuilder(services);

        var authentication = http.AddAuthentication();
        authentication.AddScheme(_ => new WebAppSessionAuthenticationHandler(session));

        foreach (var configure in this.Authentication)
            configure(authentication);

        // One call, carrying every policy: a second AddAuthorization would be silently ignored.
        http.AddAuthorization(o =>
        {
            // Secure by default: an endpoint that says nothing needs a caller who authenticated somehow.
            o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());
            o.AddPolicy(WebAppPolicies.Session, p => p.RequireClaim(WebAppSessionAuthenticationHandler.SessionClaim));

            foreach (var configure in this.Authorization)
                configure(o);
        });

        return services.BuildServiceProvider();
    }
}

public static class WebAppEndpointServiceCollectionExtensions
{
    /// <summary>
    /// Adds your own endpoints to the host's server — raw routes, a source-generated <c>[Route]</c> class, or a
    /// module — using Shiny.Net.HttpServer's own API. Map them from the root; the host moves them under
    /// <see cref="WebAppHostOptions.BasePath"/>.
    /// <code>
    /// services.AddWebAppEndpoints(server =>
    /// {
    ///     server.MapOrderEndpoints();                                    // [Route("/api/orders")]
    ///     server.MapGet("/api/health", ctx => …).AllowAnonymous();
    ///     server.MapGet("/api/admin", ctx => …).RequireAuthorization("admin");
    /// });
    /// </code>
    /// <para>
    /// Every endpoint needs an authenticated caller unless it says otherwise — the WebView's session, or any
    /// scheme from <see cref="AddWebAppAuthentication"/>. <c>AllowAnonymous()</c> opts one out. Bridges are not
    /// affected: they stay behind the host's own guard.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWebAppEndpoints(this IServiceCollection services, Action<HttpServer> map)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(map);

        WebAppEndpointRegistrations.GetOrAdd(services).Maps.Add((server, _) => map(server));
        return services;
    }

    /// <summary>Adds an <see cref="IEndpointModule"/>, resolved from the container so it can take dependencies.</summary>
    public static IServiceCollection AddWebAppEndpoints<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>(
        this IServiceCollection services
    ) where TModule : class, IEndpointModule
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TModule>();
        WebAppEndpointRegistrations.GetOrAdd(services).Maps.Add((server, sp) => server.MapModule(sp.GetRequiredService<TModule>(), String.Empty));
        return services;
    }

    /// <summary>
    /// Adds authentication schemes for your endpoints, alongside the WebView's own session.
    /// <code>
    /// services.AddWebAppAuthentication(auth => auth.AddApiKey(o => o.AddKey(key, "kiosk")));
    /// </code>
    /// <para>
    /// These live in the host's own container, not the app's, so a scheme that resolves a dependency by type
    /// needs that dependency registered on <c>auth.Services</c> as well. Delegate-based options — an API key's
    /// <c>ValidateAsync</c>, say — can reach the app's services through <c>context.RequestServices</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWebAppAuthentication(this IServiceCollection services, Action<AuthenticationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        WebAppEndpointRegistrations.GetOrAdd(services).Authentication.Add(configure);
        return services;
    }

    /// <summary>
    /// Adds authorization policies for your endpoints. Call it as often as you like — unlike
    /// Shiny.Net.HttpServer's own <c>AddAuthorization</c>, every call applies.
    /// <code>
    /// services.AddWebAppAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("admin")));
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppAuthorization(this IServiceCollection services, Action<AuthorizationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        WebAppEndpointRegistrations.GetOrAdd(services).Authorization.Add(configure);
        return services;
    }
}
