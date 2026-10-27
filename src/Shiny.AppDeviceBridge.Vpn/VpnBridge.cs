using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Vpn.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Net.Vpn;

namespace Shiny.AppDeviceBridge.Vpn;

public static class VpnBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/vpn</c> and registers Shiny's VPN manager — there is nothing else to call in code.
    /// <code>
    /// bridge.AddVpnBridge();
    /// </code>
    /// <para>
    /// Apple needs the <i>Personal VPN</i> capability and the <c>com.apple.developer.networking.vpn.api</c> entitlement
    /// (<c>allow-vpn</c>). Android needs Android 11, and Android 13 to report the tunnel's state. Linux needs NetworkManager
    /// with its strongSwan plugin. An app that already called <c>AddVpn</c> keeps its registration. iOS, Mac Catalyst,
    /// macOS, Android and Linux; Windows answers 501.
    /// </para>
    /// <para>
    /// This hands the page the device's VPN: a page that can call it can point the device's traffic at a server of its
    /// choosing. Register it only for a web app you ship.
    /// </para>
    /// </summary>
    public static TBuilder AddVpnBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (!bridge.Services.Any(x => x.ServiceType == typeof(Native.IVpnManager)))
        {
#if ANDROID || IOS || MACCATALYST || MACOS
            bridge.Services.AddVpn();
#elif VPN_LINUX
            // Shiny.Net.Vpn has no implementation of its own for plain .NET: NetworkManager on Linux, and the bridge
            // answers 501 anywhere else.
            if (OperatingSystem.IsLinux())
                bridge.Services.AddVpn();
#endif
        }

        bridge.AddBridge<VpnBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/vpn</c> over <see cref="Native.IVpnManager"/>.
/// <code>
/// GET    /_bridge/vpn                { "state": "Connected", "capabilities": { "profile": true, "status": true, … } }
/// PUT    /_bridge/vpn/profile        { "name": "Acme", "serverAddress": "vpn.acme.com", "authentication": "UsernamePassword", … }  →  { "access": "Available" }
/// DELETE /_bridge/vpn/profile
/// POST   /_bridge/vpn/connect        { "timeoutSeconds": 30 }  →  { "state": "Connected", … }
/// POST   /_bridge/vpn/disconnect     →  { "state": "Disconnected", … }
/// event  vpn.status                  { "state": "Reconnecting" }
/// </code>
/// </summary>
public sealed class VpnBridge(IServiceProvider services) : IWebAppBridge
{
    internal const string StatusEvent = "vpn.status";
    const int MaxConnectTimeoutSeconds = 300;
    const int MaxTextLength = 1024;

    // A PKCS#12 bundle or CA certificate is a few KB; this only stops a page from handing the keystore something absurd.
    const int MaxCertificateBytes = 256 * 1024;

    readonly Native.IVpnManager? manager = services.GetOptionalService<Native.IVpnManager>();

    public string Name => "vpn";

    // Android 10 and older report no capabilities at all.
    public bool IsSupported => this.manager is { Capabilities: not Native.VpnCapabilities.None };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPut("/profile", this.SaveProfileAsync)
        .MapDelete("/profile", this.RemoveProfileAsync)
        .MapPost("/connect", this.ConnectAsync)
        .MapPost("/disconnect", this.DisconnectAsync)
        .MapEvent(StatusEvent, this.StatusChanges, VpnJsonContext.Default.VpnStatusChanged);

    // Hooked only while a page listens, so the OS is watched only then; Shiny hands each new subscriber the current state.
    IAsyncEnumerable<VpnStatusChanged> StatusChanges(CancellationToken cancellationToken)
    {
        if (!this.IsSupported)
            return AsyncEnumerable.Empty<VpnStatusChanged>();

        var m = this.manager!;
        return WebAppEventStream.FromEvent<VpnStatusChanged>(emit =>
        {
            EventHandler<Native.VpnConnectionState> handler = (_, state) => emit(new VpnStatusChanged(ToContract(state)));
            m.StatusChanged += handler;
            return () => m.StatusChanged -= handler;
        }, cancellationToken);
    }

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, m => this.WriteStatusAsync(context, m));

    ValueTask SaveProfileAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, VpnJsonContext.Default.VpnProfileRequest);
        if (ToProfile(body, out var problem) is not { } profile)
        {
            await WebAppBridgeResults.BadRequest(context, problem!);
            return;
        }

        // The first save shows the OS consent prompt.
        var access = await services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => m.SaveProfile(profile, context.RequestAborted));

        await WebAppBridgeResults.Json(
            context,
            new VpnProfileResult(BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access)),
            VpnJsonContext.Default.VpnProfileResult
        );
    });

    ValueTask RemoveProfileAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await m.RemoveProfile(context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ConnectAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, VpnJsonContext.Default.VpnConnectRequest) ?? new VpnConnectRequest();
        if (body.TimeoutSeconds is < 1 or > MaxConnectTimeoutSeconds)
        {
            await WebAppBridgeResults.BadRequest(context, $"timeoutSeconds is 1 to {MaxConnectTimeoutSeconds}.");
            return;
        }

        await m.Connect(body.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null, context.RequestAborted);
        await this.WriteStatusAsync(context, m);
    });

    ValueTask DisconnectAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await m.Disconnect(context.RequestAborted);
        await this.WriteStatusAsync(context, m);
    });

    async Task WriteStatusAsync(HttpContext context, Native.IVpnManager m)
    {
        var capabilities = m.Capabilities;

        // GetStatus answers Unknown without the Status capability, but on some platforms only after asking the OS.
        var state = capabilities.HasFlag(Native.VpnCapabilities.Status)
            ? await m.GetStatus(context.RequestAborted)
            : Native.VpnConnectionState.Unknown;

        await WebAppBridgeResults.Json(context, new VpnStatus(ToContract(state), ToContract(capabilities)), VpnJsonContext.Default.VpnStatus);
    }

    /// <summary>The profile the page described, or null with the reason it is not one.</summary>
    internal static Native.VpnProfile? ToProfile(VpnProfileRequest? body, out string? problem)
    {
        problem = body switch
        {
            null => "Expected { \"name\": \"…\", \"serverAddress\": \"…\", \"authentication\": \"SharedSecret | UsernamePassword | Certificate\", … }.",
            { Name: var n } when String.IsNullOrWhiteSpace(n) || n.Length > MaxTextLength => $"name is required, at most {MaxTextLength} characters.",
            { ServerAddress: var a } when String.IsNullOrWhiteSpace(a) || a.Length > MaxTextLength => $"serverAddress is required, at most {MaxTextLength} characters.",
            { Authentication: var t } when !Enum.IsDefined(t) => "authentication is SharedSecret, UsernamePassword or Certificate.",
            { Authentication: VpnAuthenticationType.SharedSecret, Secret: var s } when String.IsNullOrEmpty(s) => "A SharedSecret profile needs secret.",
            { Authentication: VpnAuthenticationType.UsernamePassword } x when String.IsNullOrEmpty(x.Username) || String.IsNullOrEmpty(x.Password)
                => "A UsernamePassword profile needs username and password.",
            { Authentication: VpnAuthenticationType.Certificate, Pkcs12: var p } when p is not { Length: > 0 } => "A Certificate profile needs pkcs12, the base64 .p12 bundle.",
            { Pkcs12.Length: > MaxCertificateBytes } or { ServerRootCertificate.Length: > MaxCertificateBytes } => $"Certificates are at most {MaxCertificateBytes / 1024} KB.",
            { RemoteIdentifier.Length: > MaxTextLength } or { LocalIdentifier.Length: > MaxTextLength } => $"Identifiers are at most {MaxTextLength} characters.",
            _ => null
        };
        if (problem is not null)
            return null;

        return new Native.VpnProfile
        {
            Name = body!.Name,
            ServerAddress = body.ServerAddress,
            Authentication = body.Authentication switch
            {
                VpnAuthenticationType.SharedSecret => Native.VpnAuthentication.SharedSecret(body.Secret!),
                VpnAuthenticationType.UsernamePassword => Native.VpnAuthentication.UsernamePassword(body.Username!, body.Password!),
                _ => Native.VpnAuthentication.Certificate(body.Pkcs12!, body.Password)
            },
            RemoteIdentifier = body.RemoteIdentifier,
            LocalIdentifier = body.LocalIdentifier,
            ServerRootCertificate = body.ServerRootCertificate is { Length: > 0 } root ? root : null
        };
    }

    /// <summary>501 where there is no VPN client to drive; the OS's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.IVpnManager, Task> action)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "The VPN");
            return;
        }

        try
        {
            await action(this.manager!);
        }
        catch (Native.VpnNotSupportedException ex)
        {
            // A profile option or operation this platform lacks: retrying never helps.
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
        }
        catch (Native.VpnPermissionException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "vpn_permission", ex.Message);
        }
        catch (Native.VpnConnectionException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "vpn_connection_failed", ex.Message);
        }
        catch (Native.VpnException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "vpn_failed", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    internal static VpnConnectionState ToContract(Native.VpnConnectionState state)
        => BridgeEnum.Convert<Native.VpnConnectionState, VpnConnectionState>(state);

    internal static VpnCapabilityInfo ToContract(Native.VpnCapabilities c) => new(
        c.HasFlag(Native.VpnCapabilities.Profile),
        c.HasFlag(Native.VpnCapabilities.Connect),
        c.HasFlag(Native.VpnCapabilities.Disconnect),
        c.HasFlag(Native.VpnCapabilities.Status),
        c.HasFlag(Native.VpnCapabilities.SharedSecretAuthentication),
        c.HasFlag(Native.VpnCapabilities.UsernamePasswordAuthentication),
        c.HasFlag(Native.VpnCapabilities.CertificateAuthentication),
        c.HasFlag(Native.VpnCapabilities.ServerRootCertificate),
        c.HasFlag(Native.VpnCapabilities.RemoteIdentifier)
    );
}
