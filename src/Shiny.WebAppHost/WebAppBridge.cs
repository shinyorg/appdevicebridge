using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost;

/// <summary>
/// A group of native endpoints the page can call, mounted under <c>/_bridge/{Name}</c>.
/// <para>
/// Every bridge route sits behind the session guard: a request without the launch cookie, from a
/// foreign <c>Host</c> or a foreign <c>Origin</c>, never reaches it. A bridge only has to validate its
/// own input.
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
/// services.AddWebAppBridge&lt;ClipboardBridge&gt;();
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

/// <summary>Maps a bridge's routes under its prefix.</summary>
public sealed class WebAppBridgeRoutes
{
    readonly HttpServer server;

    internal WebAppBridgeRoutes(HttpServer server, string name, WebAppEventHub events)
    {
        this.server = server;
        this.Prefix = $"{WebAppSession.BridgePrefix}/{name}";
        this.Events = events;
    }

    /// <summary><c>/_bridge/{name}</c>.</summary>
    public string Prefix { get; }

    /// <summary>Where to publish events for the page. See <see cref="WebAppEventHub"/>.</summary>
    public WebAppEventHub Events { get; }

    public WebAppBridgeRoutes MapGet(string pattern, RequestDelegate handler)
    {
        this.server.MapGet(this.Combine(pattern), handler);
        return this;
    }

    public WebAppBridgeRoutes MapPost(string pattern, RequestDelegate handler)
    {
        this.server.MapPost(this.Combine(pattern), handler);
        return this;
    }

    public WebAppBridgeRoutes MapPut(string pattern, RequestDelegate handler)
    {
        this.server.MapPut(this.Combine(pattern), handler);
        return this;
    }

    public WebAppBridgeRoutes MapDelete(string pattern, RequestDelegate handler)
    {
        this.server.MapDelete(this.Combine(pattern), handler);
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

public static class WebAppHostServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WebAppHost"/>. The loopback server it runs is its own instance, not the
    /// container's <c>HttpServer</c>, so an app that also serves something with Shiny.Net.HttpServer
    /// keeps its own server untouched.
    /// <code>
    /// services.AddWebAppHost(o =>
    /// {
    ///     o.AppId = "field-app";
    ///     o.UpdateServer = new Uri("https://api.example.com/webapps");
    ///     o.PublicKey = """-----BEGIN PUBLIC KEY-----…""";
    ///     o.UseBaseline(typeof(App).Assembly, "webapp.zip", "1.0.0");
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppHost(this IServiceCollection services, Action<WebAppHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebAppHostOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<WebAppSession>();
        services.TryAddSingleton<WebAppEventHub>();
        services.TryAddSingleton<WebAppHost>();

        // One instance serving two roles: the bridge the page answers calls through, and the service native
        // delegates call into.
        services.TryAddSingleton<WebAppInvoker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebAppBridge, WebAppInvoker>(sp => sp.GetRequiredService<WebAppInvoker>()));

        if (options.EnableSettings)
        {
            services.AddShinyStores();
            global::Shiny.Json.AddContext(WebAppStoreJsonContext.Default);
            services.AddWebAppBridge<WebAppSettingsBridge>();
        }

        if (options.EnableFiles)
            services.AddWebAppBridge<WebAppFilesBridge>();

        return services;
    }

    /// <summary>
    /// Registers a bridge. Its routes are mapped when the host is created, so the order relative to
    /// <see cref="AddWebAppHost"/> does not matter, and adding the same bridge twice is harmless.
    /// Bridge packages wrap this in an extension of their own that also registers the native service.
    /// </summary>
    public static IServiceCollection AddWebAppBridge<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBridge>(
        this IServiceCollection services
    ) where TBridge : class, IWebAppBridge
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered here as well as in AddWebAppHost: services a bridge registers alongside itself —
        // a geofence delegate, say — publish through the hub, whichever call came first.
        services.TryAddSingleton<WebAppEventHub>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebAppBridge, TBridge>());

        return services;
    }
}

public sealed record WebAppBridgeCapability(string Name, bool IsSupported);

public sealed record WebAppHostInfo(
    string AppId,
    string HostVersion,
    string Platform,
    string? WebAppVersion,
    WebAppPackageOrigin? WebAppOrigin,
    string? PendingVersion,
    IReadOnlyList<WebAppBridgeCapability> Bridges,
    string? DevServer
);

public sealed record WebAppApplyUpdateResult(bool Applied, string? Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebAppBridgeError))]
[JsonSerializable(typeof(WebAppHostInfo))]
[JsonSerializable(typeof(WebAppApplyUpdateResult))]
partial class WebAppBridgeJsonContext : JsonSerializerContext;
