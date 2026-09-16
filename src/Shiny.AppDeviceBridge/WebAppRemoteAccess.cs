using System.Net;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Opens the server past loopback, so machines other than this one can reach it.
/// <para>
/// Off by default, and deliberately so. The bridges are raw device access — location, Bluetooth, contacts,
/// the tray — reached over a loopback server whose only defence is that the caller is on this device and
/// holds the launch cookie. A caller on the network holds neither, so by default every bridge answers
/// <c>403 remote_denied</c> to anything that did not come from this device, whatever else is turned on here.
/// </para>
/// <para>
/// What you open is named one bridge at a time. The point is to serve <em>data</em> the app happens to hold —
/// files, settings, an app-specific bridge of your own — without also handing the network the device itself.
/// </para>
/// <code>
/// o.RemoteAccess.Enabled = true;
/// o.RemoteAccess.AllowBridge("files", "settings");
/// </code>
/// <para>
/// There is no credential. Anything that can reach the port can call the bridges named here, so treat
/// <see cref="AllowBridge"/> as publishing them: name only what you would put on an unauthenticated HTTP
/// endpoint. <see cref="Authorize"/> is where to add a check of your own.
/// </para>
/// </summary>
public sealed class WebAppRemoteAccessOptions
{
    /// <summary>
    /// Bind <see cref="Address"/> instead of loopback. Off by default, and while it is off nothing here
    /// applies — the listener never accepts a connection from another machine in the first place.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>What to bind when <see cref="Enabled"/>. Every interface by default.</summary>
    public IPAddress Address { get; set; } = IPAddress.Any;

    /// <summary>
    /// The bridges a caller from the network may use, by <see cref="IWebAppBridge.Name"/> — <c>files</c>,
    /// <c>settings</c>, <c>host</c>, <c>events</c> or one of your own. Empty by default: no bridge at all.
    /// An allowed bridge is fully reachable, every route and every method; there is no read-only mode, so a
    /// bridge that writes is one the network can write through.
    /// </summary>
    public ISet<string> Bridges { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Host headers to accept besides a bare IP address — an mDNS name such as <c>kiosk.local</c>, say.
    /// <para>
    /// Empty, only an IP literal is accepted, which is what stops DNS rebinding: a hostile site that points
    /// its own name at this device arrives carrying that name, and is turned away with <c>421</c>. Adding a
    /// name here accepts it, so add names you control.
    /// </para>
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serve the web app's own pages and assets to the network too. Off by default: opening a bridge for a
    /// script to read is not the same as publishing the app's UI.
    /// </summary>
    public bool ServeWebApp { get; set; }

    /// <summary>
    /// Runs before anything from the network is served, after the host check and before the bridge
    /// allowlist. Return false and the request gets <c>401</c>. This is where an API key goes:
    /// <code>
    /// o.RemoteAccess.Authorize = ctx => ctx.Request.Headers["Authorization"] == $"Bearer {key}";
    /// </code>
    /// Never called for a request from this device, which is guarded by the session instead.
    /// </summary>
    public Func<HttpContext, bool>? Authorize { get; set; }

    /// <summary>Adds to <see cref="Bridges"/>.</summary>
    public WebAppRemoteAccessOptions AllowBridge(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names)
            this.Bridges.Add(name);

        return this;
    }

    /// <summary>Adds to <see cref="AllowedHosts"/>.</summary>
    public WebAppRemoteAccessOptions AllowHost(params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        foreach (var host in hosts)
            this.AllowedHosts.Add(host);

        return this;
    }

    /// <summary>The bridge segment of <c>{prefix}/{name}/…</c>, or null when the path is not a bridge call.</summary>
    internal static string? BridgeName(string path, string bridgePrefix)
    {
        if (!path.StartsWith(bridgePrefix + "/", StringComparison.Ordinal))
            return null;

        var rest = path.AsSpan(bridgePrefix.Length + 1);
        var end = rest.IndexOf('/');
        var name = end < 0 ? rest : rest[..end];

        return name.IsEmpty ? null : name.ToString();
    }

    internal bool IsBridgeAllowed(string? name)
        => name is not null && this.Bridges.Contains(name);

    /// <summary>
    /// Whether a <c>Host</c> header from the network is one this server answers to: the right port, and either
    /// a bare IP address or a name in <see cref="AllowedHosts"/>.
    /// </summary>
    internal bool IsAllowedHost(string? host, int port)
    {
        if (String.IsNullOrEmpty(host))
            return false;

        var name = host;
        if (name.StartsWith('['))
        {
            // [::1]:5780 — the brackets keep the address's own colons apart from the port's.
            var close = name.IndexOf(']');
            if (close < 0)
                return false;

            var after = name[(close + 1)..];
            if (after.Length > 0 && (after[0] != ':' || !MatchesPort(after[1..], port)))
                return false;

            name = name[1..close];
        }
        else if (name.LastIndexOf(':') is var separator && separator >= 0)
        {
            if (!MatchesPort(name[(separator + 1)..], port))
                return false;

            name = name[..separator];
        }
        else if (port != 80)
        {
            // No port in the header means 80, which is not where this is listening.
            return false;
        }

        return IPAddress.TryParse(name, out _) || this.AllowedHosts.Contains(name);
    }

    static bool MatchesPort(string value, int port)
        => Int32.TryParse(value, out var parsed) && parsed == port;
}
