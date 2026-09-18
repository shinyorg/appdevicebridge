using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// A group of native endpoints the page can call, mounted under <c>/_bridge/{Name}</c>.
/// <para>
/// Every bridge route requires <see cref="AppDeviceBridgePolicies.Bridges"/>: a caller the policy refuses never
/// reaches it. A bridge only has to validate its own input.
/// </para>
/// <code>
/// public sealed class ClipboardBridge(IClipboard clipboard) : IWebAppBridge
/// {
///     public string Name => "clipboard";
///     public bool IsSupported => true;
///
///     public void Map(WebAppBridgeRoutes routes) => routes
///         .MapGet("", async ctx => await WebAppBridgeResults.Json(ctx, new Text(await clipboard.GetTextAsync()), MyJson.Default.Text));
/// }
///
/// http.AddAppDeviceBridge(bridge => bridge.AddBridge&lt;ClipboardBridge&gt;());
/// </code>
/// </summary>
public interface IWebAppBridge
{
    /// <summary>The route segment: lowercase letters, digits and <c>-</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the native service is present on this platform. Reported to the page by
    /// <c>GET /_bridge/capabilities</c>, so it can hide what it cannot use rather than calling it to
    /// find out.
    /// </summary>
    bool IsSupported { get; }

    void Map(WebAppBridgeRoutes routes);
}

/// <summary>
/// Maps a bridge's routes under its prefix.
/// <para>
/// Every route requires <see cref="AppDeviceBridgePolicies.Bridges"/> — only callers on this device by default — and
/// nothing else: the fallback policy your own endpoints get does not apply to bridges, so a policy written for those
/// can never loosen or tighten device access.
/// </para>
/// </summary>
public sealed class WebAppBridgeRoutes
{
    readonly HttpServer server;

    internal WebAppBridgeRoutes(HttpServer server, string bridgePrefix, string name, WebAppEventHub events)
    {
        this.server = server;
        this.BridgePrefix = bridgePrefix;
        this.Prefix = $"{bridgePrefix}/{name}";
        this.Events = events;
    }

    /// <summary>
    /// Where the bridges are mounted — <c>/_bridge</c> by default, and with the app's base path already on the
    /// front. A bridge that hands the page a URL has to build it from this rather than assume the default.
    /// </summary>
    public string BridgePrefix { get; }

    /// <summary><c>{BridgePrefix}/{name}</c>.</summary>
    public string Prefix { get; }

    /// <summary>The page's event stream. See <see cref="WebAppEventHub"/>.</summary>
    public WebAppEventHub Events { get; }

    /// <summary>
    /// Maps an event on the page's one stream. <paramref name="source"/> is enumerated once per page stream that asks for
    /// <paramref name="eventName"/>, and its token is cancelled when that stream drops the event or disconnects — so a
    /// source that hooks a native event in its body and unhooks it in <c>finally</c> never outlives the page.
    /// See <see cref="WebAppEventStream.FromEvent{T}(Func{Action{T}, Action}, CancellationToken, int)"/>.
    /// </summary>
    public WebAppBridgeRoutes MapEvent<T>(string eventName, Func<CancellationToken, IAsyncEnumerable<T>> source, JsonTypeInfo<T> typeInfo)
    {
        this.Events.Map(eventName, source, typeInfo);
        return this;
    }

    public WebAppBridgeRoutes MapGet(string pattern, RequestDelegate handler)
    {
        this.server.MapGet(this.Combine(pattern), handler).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
        return this;
    }

    public WebAppBridgeRoutes MapPost(string pattern, RequestDelegate handler)
    {
        this.server.MapPost(this.Combine(pattern), handler).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
        return this;
    }

    public WebAppBridgeRoutes MapPut(string pattern, RequestDelegate handler)
    {
        this.server.MapPut(this.Combine(pattern), handler).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
        return this;
    }

    public WebAppBridgeRoutes MapDelete(string pattern, RequestDelegate handler)
    {
        this.server.MapDelete(this.Combine(pattern), handler).RequireAuthorization(AppDeviceBridgePolicies.Bridges);
        return this;
    }

    string Combine(string pattern) => pattern switch
    {
        "" or "/" => this.Prefix,
        _ when pattern.StartsWith('/') => this.Prefix + pattern,
        _ => this.Prefix + "/" + pattern
    };
}

/// <summary>An error the page can switch on: a stable <see cref="Code"/> and a message for people.</summary>
public sealed record WebAppBridgeError(string Code, string Message);

/// <summary>Responses bridges share, so every bridge fails the same way.</summary>
public static class WebAppBridgeResults
{
    public static ValueTask Json<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
        => Results.Json(value, typeInfo, statusCode).ExecuteAsync(context);

    public static ValueTask NoContent(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return ValueTask.CompletedTask;
    }

    public static ValueTask Error(HttpContext context, int statusCode, string code, string message)
        => Json(context, new WebAppBridgeError(code, message), WebAppBridgeJsonContext.Default.WebAppBridgeError, statusCode);

    /// <summary>501: the service does not exist on this platform, or the app did not register it.</summary>
    public static ValueTask NotSupported(HttpContext context, string feature)
        => Error(context, StatusCodes.Status501NotImplemented, "not_supported", $"{feature} is not available on this platform.");

    public static ValueTask BadRequest(HttpContext context, string message)
        => Error(context, StatusCodes.Status400BadRequest, "bad_request", message);

    public static ValueTask NotFound(HttpContext context, string message)
        => Error(context, StatusCodes.Status404NotFound, "not_found", message);

    /// <summary>The request body, or null when it is absent or malformed.</summary>
    public static ValueTask<T?> ReadBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        => context.Request.ReadJsonAsync(typeInfo, context.RequestAborted);
}

public static class AppDeviceBridgeHttpServerBuilderExtensions
{
    /// <summary>
    /// Puts the bridges on the app's Shiny.Net.HttpServer, with the built-in settings, files and native-call bridges, and
    /// hands <paramref name="bridge"/> the <see cref="AppDeviceBridgeBuilder"/> the bridges are registered on. Call it as
    /// often as you like — every call configures the same <see cref="AppDeviceBridgeOptions"/>, which are validated when the
    /// server is built. Bridge packages add their bridges with an extension of their own on the builder.
    /// <code>
    /// services.AddShinyHttpServer(http =>
    /// {
    ///     http.Options.Address = IPAddress.Any;
    ///     http.AddAuthentication().AddApiKey(k => k.AddKey(key, "kiosk"));
    ///     http.AddAppDeviceBridge(bridge => bridge
    ///         .Configure(o =>
    ///         {
    ///             o.AppId = "field-app";
    ///             o.AllowedHosts.Add("kiosk.local");
    ///             o.AuthorizeBridges(p => p.RequireAssertion(ctx => BridgeCallers.IsOnDevice(ctx.HttpContext) || ctx.User.Identity?.IsAuthenticated == true));
    ///         })
    ///         .AddRpiCameraBridge());
    ///     http.Configure(server => server.MapGet("/api/orders", ...));
    /// });
    /// </code>
    /// <para>
    /// The bridges enforce <see cref="AppDeviceBridgePolicies.Bridges"/> themselves, so they are protected whether or not
    /// the app puts <c>UseAuthentication</c> and <c>UseAuthorization</c> in its pipeline — which the app's own endpoints
    /// need, and which the bridges leave to the app.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddAppDeviceBridge(this ShinyHttpServerBuilder http, Action<AppDeviceBridgeBuilder>? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        var builder = new AppDeviceBridgeBuilder(http);
        bridge?.Invoke(builder);
        return http;
    }

    /// <summary>Registers the bridge server once, and returns the one options instance every call configures.</summary>
    internal static AppDeviceBridgeOptions Register(ShinyHttpServerBuilder http)
    {
        var services = http.Services;
        var options = GetOrAddOptions(services);

        if (services.Any(x => x.ServiceType == typeof(AppDeviceBridgeRegistration)))
            return options;

        services.AddSingleton(new AppDeviceBridgeRegistration());
        services.TryAddSingleton<WebAppEventHub>();
        services.TryAddSingleton<WebAppFileRoots>();
        services.TryAddSingleton<IWebAppMainThread, InlineWebAppMainThread>();
        services.TryAddSingleton(sp => new AppDeviceBridgeServer(
            sp.GetRequiredService<AppDeviceBridgeOptions>(),
            sp.GetServices<IWebAppBridge>(),
            sp.GetRequiredService<WebAppEventHub>(),
            sp,
            sp.GetService<ILoggerFactory>()
        ));

        // One instance serving two roles: the bridge the page answers calls through, and the service native
        // delegates call into.
        services.TryAddSingleton(sp => new WebAppInvoker(
            sp.GetRequiredService<AppDeviceBridgeOptions>(),
            () => sp.GetService<IWebAppBackgroundInvoker>(),
            sp.GetService<ILoggerFactory>()
        ));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebAppBridge, WebAppInvoker>(sp => sp.GetRequiredService<WebAppInvoker>()));

        // Registered either way and left unmapped when switched off, so a later configure call can still change its mind.
        services.AddShinyStores();
        global::Shiny.Json.AddContext(WebAppStoreJsonContext.Default);
        services.AddWebAppBridge<WebAppSettingsBridge>();
        services.AddWebAppBridge<WebAppFilesBridge>();

        // The schemes are the app's; this only makes sure the machinery is there for the bridge policy to evaluate against.
        http.AddAuthentication();
        http.AddAuthorization(o => o.AddPolicy(AppDeviceBridgePolicies.Bridges, p =>
        {
            // Read when the policies are built, after every configure call has had its say.
            if (options.BridgePolicy is { } custom)
            {
                custom(p);
                return;
            }

            p.RequireAssertion(
                ctx => ctx.HttpContext.GetRequiredService<AppDeviceBridgeServer>().IsDefaultBridgeCaller(ctx.HttpContext),
                "a caller on this device"
            );
        }));

        // Runs as the container builds the server, so the bridges are on it before it can serve a request.
        http.Configure(server => server.Services!.GetRequiredService<AppDeviceBridgeServer>().Compose(server));

        return options;
    }

    /// <summary>The one options instance every <see cref="AddAppDeviceBridge"/> call configures. An instance registered beforehand is used.</summary>
    static AppDeviceBridgeOptions GetOrAddOptions(IServiceCollection services)
    {
        if (services.FirstOrDefault(x => x.ServiceType == typeof(AppDeviceBridgeOptions))?.ImplementationInstance is AppDeviceBridgeOptions existing)
            return existing;

        var options = new AppDeviceBridgeOptions();
        services.AddSingleton(options);
        return options;
    }

    /// <summary>Marks the collection as having the bridge server, so a second call only configures.</summary>
    sealed class AppDeviceBridgeRegistration;
}

static class AppDeviceBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers a bridge. Its routes are mapped when the server is built, so the order relative to the bridge server's
    /// registration does not matter, and adding the same bridge twice is harmless. <see cref="AppDeviceBridgeBuilder.AddBridge{TBridge}"/>
    /// is the public way in.
    /// </summary>
    public static IServiceCollection AddWebAppBridge<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBridge>(
        this IServiceCollection services
    ) where TBridge : class, IWebAppBridge
    {
        // Registered here as well: services a bridge registers alongside itself — a geofence delegate, say — publish
        // through the hub, whichever call came first.
        services.TryAddSingleton<WebAppEventHub>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebAppBridge, TBridge>());

        return services;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebAppBridgeError))]
[JsonSerializable(typeof(WebAppPathsResponse))]
partial class WebAppBridgeJsonContext : JsonSerializerContext;
