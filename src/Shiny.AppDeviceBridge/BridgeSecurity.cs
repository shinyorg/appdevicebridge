using System.Net;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge;

/// <summary>Policy names the bridge server defines.</summary>
public static class AppDeviceBridgePolicies
{
    /// <summary>
    /// Every bridge route requires this policy. By default it admits a caller on this device only — any caller in a debug
    /// build — and a WebView host adds its launch session to it. Replace it with
    /// <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/>.
    /// </summary>
    public const string Bridges = "appdevicebridge:bridges";
}

/// <summary>Checks for writing bridge policies of your own.</summary>
public static class BridgeCallers
{
    /// <summary>
    /// A caller on this device, reaching the server by a loopback name: the connection comes from a loopback address, the
    /// <c>Host</c> header names loopback (so a hostile site that resolves its own name to 127.0.0.1 is refused), and a
    /// browser's <c>Origin</c>, when there is one, is loopback too (so a page on another site cannot drive the bridges
    /// from inside this device's browser).
    /// </summary>
    public static bool IsOnDevice(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsLocal(context.Connection.RemoteIpAddress) || !IsLoopbackHost(context.Request.Host))
            return false;

        var origin = context.Request.Headers["Origin"].ToString();
        return origin.Length == 0
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackName(uri.Host));
    }

    /// <summary>Loopback, in either address family and through an IPv4-mapped IPv6 address.</summary>
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null)
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address);
    }

    /// <summary>Whether a <c>Host</c> header — with or without a port — names loopback.</summary>
    public static bool IsLoopbackHost(string? host) => HostName(host) is { } name && IsLoopbackName(name);

    /// <summary>The name part of a <c>Host</c> header: <c>[::1]:5780</c> is <c>::1</c>, <c>127.0.0.1:5780</c> is <c>127.0.0.1</c>.</summary>
    public static string? HostName(string? host)
    {
        if (String.IsNullOrEmpty(host))
            return null;

        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            return close < 0 ? null : host[1..close];
        }

        var separator = host.LastIndexOf(':');
        return separator >= 0 ? host[..separator] : host;
    }

    static bool IsLoopbackName(string name)
        => name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(name.Trim('[', ']'), out var address) && IsLocal(address));
}

/// <summary>
/// Something that adds to the bridge server — the WebView host is one. Called once, when the server is first started,
/// after <see cref="AppDeviceBridgeOptions.ConfigureServer"/> and before any route is mapped.
/// </summary>
public interface IAppDeviceBridgeServerExtension
{
    /// <summary>Adds authentication schemes to the server's security.</summary>
    void ConfigureAuthentication(AuthenticationBuilder authentication) { }

    /// <summary>Adds authorization policies, before the app's own.</summary>
    void ConfigureAuthorization(AuthorizationOptions authorization) { }

    /// <summary>
    /// Adds to the default bridge policy. Not called when the app replaced the policy with
    /// <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/> — that policy is the app's alone.
    /// </summary>
    void ConfigureDefaultBridgePolicy(AuthorizationPolicyBuilder policy) { }

    /// <summary>Adds middleware. Runs before routing, so it sees every request — bridges and <c>_host</c> included.</summary>
    void ConfigurePipeline(AppDeviceBridgeServer server) { }

    /// <summary>Maps routes.</summary>
    void Map(AppDeviceBridgeServer server) { }
}

/// <summary>
/// What a web app host tells the page about itself through <c>/_bridge/host</c>. Implemented by the WebView host; without
/// one, those fields are null and <c>apply-update</c> has nothing to apply.
/// </summary>
public interface IWebAppStatus
{
    string? WebAppVersion { get; }

    Client.WebAppOrigin? WebAppOrigin { get; }

    string? PendingVersion { get; }

    Uri? DevServer { get; }

    /// <summary>Starts serving a downloaded update. False when none is waiting.</summary>
    bool ApplyPendingUpdate();
}
