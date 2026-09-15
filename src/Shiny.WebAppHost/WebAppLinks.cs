using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost;

public sealed class WebAppLinkOptions
{
    /// <summary>
    /// Custom schemes the web app handles, without <c>://</c>: <c>sample</c> for <c>sample://orders/42</c>.
    /// A link with any other scheme is left to the native app.
    /// </summary>
    public ISet<string> Schemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts whose <c>https</c> links — universal links on Apple platforms, app links on Android — the web app
    /// handles: <c>app.example.com</c>.
    /// </summary>
    public ISet<string> Hosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns an accepted link into the route the page navigates to, or null to ignore it. By default a custom
    /// scheme's host is the first path segment — <c>sample://orders/42?x=1</c> is <c>/orders/42?x=1</c> — and
    /// an https link keeps its path, query and fragment. Whatever this returns is checked again: a route that is
    /// not a local path is refused.
    /// </summary>
    public Func<Uri, string?>? MapRoute { get; set; }

    /// <summary>
    /// When a link launched the app, load the web app straight at its route instead of at <c>/</c>. The link is
    /// then consumed rather than left pending, so the page does not navigate to it a second time.
    /// </summary>
    public bool NavigateOnColdStart { get; set; }
}

/// <summary>A link the web app accepted. <see cref="Route"/> is always a local path.</summary>
public sealed record WebAppLink(string Url, string Route, DateTimeOffset ReceivedAt);

/// <summary>
/// Deep links and universal links for the web app. Platform code calls <see cref="Receive"/>; the page learns
/// about the link from the <c>app.link</c> event while it is open, and from <c>/_bridge/links/pending</c> after
/// it boots.
/// <para>
/// Only the latest link is kept. A link is an instruction to show something now, and a queue of stale ones
/// replayed at the next boot would be worse than losing all but the last.
/// </para>
/// </summary>
public sealed class WebAppLinks(WebAppLinkOptions options)
{
    const int MaxRouteLength = 2048;

    readonly Lock gate = new();
    WebAppLink? pending;
    bool started;

    public WebAppLinkOptions Options { get; } = options;

    /// <summary>The link waiting for the page, if any.</summary>
    public WebAppLink? Pending
    {
        get
        {
            lock (this.gate)
                return this.pending;
        }
    }

    /// <summary>Raised for each accepted link, on the thread that received it.</summary>
    public event Action<WebAppLink>? Received;

    /// <summary>
    /// Offers an incoming link. Returns false when it is not one of the web app's — wrong scheme or host, or a
    /// route that is not a local path — so platform code can leave it to the native app.
    /// </summary>
    public bool Receive(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri || !this.Matches(uri))
            return false;

        string? route;
        try
        {
            route = this.Options.MapRoute is { } map ? map(uri) : DefaultRoute(uri);
        }
        catch (Exception)
        {
            // The mapper is app code handed a hostile URL; a mapper that throws has rejected it.
            return false;
        }

        if (route is null || !IsLocalRoute(route))
            return false;

        var link = new WebAppLink(uri.OriginalString, route, DateTimeOffset.UtcNow);

        lock (this.gate)
            this.pending = link;

        this.Received?.Invoke(link);
        return true;
    }

    /// <summary>Removes and returns the pending link, so exactly one caller acts on it.</summary>
    public WebAppLink? Consume()
    {
        lock (this.gate)
        {
            var link = this.pending;
            this.pending = null;
            return link;
        }
    }

    /// <summary>
    /// Called once, when the host builds the first URL for the WebView. Returns the route of a link that arrived
    /// before then, when <see cref="WebAppLinkOptions.NavigateOnColdStart"/> is on.
    /// </summary>
    internal string? TakeStartRoute()
    {
        lock (this.gate)
        {
            if (this.started)
                return null;

            this.started = true;

            if (!this.Options.NavigateOnColdStart || this.pending is not { } link)
                return null;

            this.pending = null;
            return link.Route;
        }
    }

    /// <summary>
    /// A path on the web app's own origin: one leading slash, not <c>//</c> (a scheme-relative URL to another
    /// host), no backslash (which browsers read as a slash), no control characters, and nothing under the host's
    /// own <c>/_bridge</c> or <c>/_host</c>, which are not pages.
    /// </summary>
    public static bool IsLocalRoute(string? route)
    {
        if (String.IsNullOrEmpty(route) || route.Length > MaxRouteLength || route[0] != '/')
            return false;

        if (route.Length > 1 && (route[1] == '/' || route[1] == '\\'))
            return false;

        foreach (var c in route)
        {
            if (c == '\\' || Char.IsControl(c))
                return false;
        }

        return !IsUnder(route, WebAppSession.BridgePrefix) && !IsUnder(route, "/_host");
    }

    static bool IsUnder(string route, string prefix)
        => route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
           && (route.Length == prefix.Length || route[prefix.Length] is '/' or '?' or '#');

    bool Matches(Uri uri) => uri.Scheme is "https" or "http"
        ? this.Options.Hosts.Contains(uri.Host)
        : this.Options.Schemes.Contains(uri.Scheme);

    static string DefaultRoute(Uri uri)
    {
        var path = uri.AbsolutePath;

        if (uri.Scheme is not ("https" or "http") && uri.Host.Length > 0)
            path = "/" + uri.Host + (path == "/" ? String.Empty : path);

        return path + uri.Query + uri.Fragment;
    }
}

/// <summary>
/// <c>/_bridge/links</c> over <see cref="WebAppLinks"/>.
/// <code>
/// GET    /_bridge/links/pending   { "url": "sample://orders/42", "route": "/orders/42", "receivedAt": "…" }; 204 when none
/// DELETE /_bridge/links/pending   the same, and removes it — call this to act on a link
///
/// events: app.link
/// </code>
/// <para>
/// A page acts on a link by consuming it, at boot and on each <c>app.link</c> event, so a link seen live is not
/// acted on again after a reload.
/// </para>
/// </summary>
public sealed class WebAppLinksBridge : IWebAppBridge, IDisposable
{
    readonly WebAppLinks links;
    readonly WebAppEventHub events;

    public WebAppLinksBridge(WebAppLinks links, WebAppEventHub events)
    {
        this.links = links;
        this.events = events;
        this.links.Received += this.OnReceived;
    }

    public string Name => "links";

    public bool IsSupported => true;

    internal WebAppLinks Links => this.links;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/pending", ctx => Respond(ctx, this.links.Pending))
        .MapDelete("/pending", ctx => Respond(ctx, this.links.Consume()));

    static ValueTask Respond(HttpContext context, WebAppLink? link) => link is null
        ? WebAppBridgeResults.NoContent(context)
        : WebAppBridgeResults.Json(context, link, WebAppLinksJsonContext.Default.WebAppLink);

    void OnReceived(WebAppLink link)
        => this.events.Publish("app.link", link, WebAppLinksJsonContext.Default.WebAppLink);

    public void Dispose() => this.links.Received -= this.OnReceived;
}

public static class WebAppLinksServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WebAppLinks"/> and <c>/_bridge/links</c>, or returns the instance already registered.
    /// Platform packages call this and feed the instance from the OS; a head with no such package can resolve
    /// <see cref="WebAppLinks"/> and call <see cref="WebAppLinks.Receive"/> itself.
    /// </summary>
    public static WebAppLinks GetOrAddWebAppLinks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // An instance, not a factory: links arrive from platform callbacks before the container is built.
        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppLinks))?.ImplementationInstance is WebAppLinks existing)
            return existing;

        var links = new WebAppLinks(new WebAppLinkOptions());
        services.AddSingleton(links);
        services.AddWebAppBridge<WebAppLinksBridge>();
        return links;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WebAppLink))]
partial class WebAppLinksJsonContext : JsonSerializerContext;
