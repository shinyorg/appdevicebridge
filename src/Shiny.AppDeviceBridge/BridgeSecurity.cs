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
    /// A caller on this device, reaching the server by a loopback name: the connection is local (see
    /// <see cref="IsLocalConnection"/>), the <c>Host</c> header names loopback (so a hostile site that resolves its own
    /// name to 127.0.0.1 is refused), and a browser's <c>Origin</c>, when there is one, is loopback too (so a page on
    /// another site cannot drive the bridges from inside this device's browser).
    /// </summary>
    public static bool IsOnDevice(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsLocalConnection(context) || !IsLoopbackHost(context.Request.Host))
            return false;

        var origin = context.Request.Headers["Origin"].ToString();
        return origin.Length == 0
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackName(uri.Host));
    }

    /// <summary>
    /// A connection that started on this device: from a loopback address, and not through a tunnel.
    /// <para>
    /// The address alone is not enough. A tunnel hands its traffic to the server from the local end of the tunnel, so a
    /// caller anywhere on the internet arrives from 127.0.0.1 — and every header on that request, <c>Host</c> included, is
    /// the caller's to write. <see cref="ConnectionInfo.IsTunneled"/> is set by the transport, which the caller cannot
    /// reach, so it is what tells the two apart.
    /// </para>
    /// </summary>
    public static bool IsLocalConnection(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.Connection.IsTunneled && IsLocal(context.Connection.RemoteIpAddress);
    }

    /// <summary>
    /// Loopback, in either address family and through an IPv4-mapped IPv6 address. An address test only: to ask whether a
    /// request came from this device, use <see cref="IsLocalConnection"/>, which also refuses tunneled traffic.
    /// </summary>
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
/// Something that adds to the bridge server — the WebView host is one. Registered in the container, and called once, when
/// the server is composed onto the app's <see cref="HttpServer"/>. Authentication schemes and authorization policies are
/// not added here: they belong to the app's <see cref="ShinyHttpServerBuilder"/>, where an extension's registration adds
/// them.
/// </summary>
public interface IAppDeviceBridgeServerExtension
{
    /// <summary>
    /// Whether the default bridge policy admits this caller, on top of it being on this device. The WebView host admits
    /// only its own launch session. Not asked when the app replaced the policy with
    /// <see cref="AppDeviceBridgeOptions.AuthorizeBridges"/> — that policy is the app's alone — nor for a caller let in by
    /// <see cref="AppDeviceBridgeOptions.AllowAnyCallerInDebug"/>.
    /// </summary>
    bool AdmitsBridgeCaller(HttpContext context) => true;

    /// <summary>
    /// Adds middleware, after the bridge server's own guard. It sees every request the app's server takes, so it should
    /// pass through anything that is not its own.
    /// </summary>
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
