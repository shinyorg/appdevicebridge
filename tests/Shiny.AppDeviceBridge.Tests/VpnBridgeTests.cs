using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Vpn;
using Shiny.AppDeviceBridge.Vpn.Client;
using Native = Shiny.Net.Vpn;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The VPN bridge over a fake <see cref="Native.IVpnManager"/>: the routes, profile validation and mapping, capabilities,
/// the OS's errors and the status event. The tunnels themselves are Shiny.Net.Vpn's and need a device and a server.
/// </summary>
public class VpnBridgeTests
{
    const Native.VpnCapabilities Everything =
        Native.VpnCapabilities.Profile |
        Native.VpnCapabilities.Connect |
        Native.VpnCapabilities.Disconnect |
        Native.VpnCapabilities.Status |
        Native.VpnCapabilities.SharedSecretAuthentication |
        Native.VpnCapabilities.UsernamePasswordAuthentication |
        Native.VpnCapabilities.CertificateAuthentication |
        Native.VpnCapabilities.ServerRootCertificate |
        Native.VpnCapabilities.RemoteIdentifier;

    [Fact]
    public async Task Answers_501_without_a_vpn_manager()
    {
        await using var fixture = await VpnFixture.StartAsync(null);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync())).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "vpn").IsSupported);
    }

    [Fact]
    public async Task Answers_501_where_the_os_has_no_vpn_api()
    {
        // Android 10 and older.
        await using var fixture = await VpnFixture.StartAsync(new FakeVpn { Capabilities = Native.VpnCapabilities.None });
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new VpnConnectRequest()))).IsNotSupported);
    }

    [Fact]
    public async Task Reports_the_state_and_what_the_platform_can_do()
    {
        await using var fixture = await VpnFixture.StartAsync(new FakeVpn { State = Native.VpnConnectionState.Connected });

        var status = await fixture.Client.GetStatusAsync();

        Assert.Equal(VpnConnectionState.Connected, status.State);
        Assert.Equal(new VpnCapabilityInfo(true, true, true, true, true, true, true, true, true), status.Capabilities);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "vpn").IsSupported);
    }

    [Fact]
    public async Task Reports_an_unknown_state_without_the_status_capability()
    {
        // Android 11 and 12: no state, and Android never takes a remote identifier.
        var vpn = new FakeVpn
        {
            State = Native.VpnConnectionState.Connected,
            Capabilities = Everything & ~Native.VpnCapabilities.Status & ~Native.VpnCapabilities.RemoteIdentifier
        };
        await using var fixture = await VpnFixture.StartAsync(vpn);

        var status = await fixture.Client.GetStatusAsync();

        Assert.Equal(VpnConnectionState.Unknown, status.State);
        Assert.False(status.Capabilities.Status);
        Assert.False(status.Capabilities.RemoteIdentifier);
        Assert.True(status.Capabilities.Certificate);
        Assert.Equal(0, vpn.StatusReads);
    }

    [Fact]
    public async Task Saves_a_username_and_password_profile_on_the_main_thread()
    {
        var vpn = new FakeVpn();
        await using var fixture = await VpnFixture.StartAsync(vpn);

        var root = new byte[] { 0x30, 0x82, 0x01 };
        var result = await fixture.Client.SaveProfileAsync(new VpnProfileRequest(
            "Acme",
            "vpn.acme.com",
            VpnAuthenticationType.UsernamePassword,
            Username: "allan",
            Password: "hunter2",
            RemoteIdentifier: "vpn.acme.com",
            LocalIdentifier: "allan@acme.com",
            ServerRootCertificate: root
        ));

        Assert.Equal(Client.AccessState.Available, result.Access);
        Assert.Equal(1, fixture.MainThread.Calls);

        var saved = vpn.Profile!;
        Assert.Equal("Acme", saved.Name);
        Assert.Equal("vpn.acme.com", saved.ServerAddress);
        Assert.Equal("vpn.acme.com", saved.RemoteIdentifier);
        Assert.Equal("allan@acme.com", saved.LocalIdentifier);
        Assert.Equal(root, saved.ServerRootCertificate);
        var auth = Assert.IsType<Native.UsernamePasswordAuthentication>(saved.Authentication);
        Assert.Equal(("allan", "hunter2"), (auth.Username, auth.Password));
    }

    [Fact]
    public async Task Saves_shared_secret_and_certificate_profiles()
    {
        var vpn = new FakeVpn();
        await using var fixture = await VpnFixture.StartAsync(vpn);

        await fixture.Client.SaveProfileAsync(new VpnProfileRequest("Acme", "203.0.113.5", VpnAuthenticationType.SharedSecret, Secret: "psk", LocalIdentifier: "phone"));
        Assert.Equal("psk", Assert.IsType<Native.SharedSecretAuthentication>(vpn.Profile!.Authentication).Secret);
        Assert.Null(vpn.Profile.ServerRootCertificate);

        var p12 = new byte[] { 1, 2, 3, 4 };
        await fixture.Client.SaveProfileAsync(new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.Certificate, Password: "p12pass", Pkcs12: p12));
        var cert = Assert.IsType<Native.CertificateAuthentication>(vpn.Profile!.Authentication);
        Assert.Equal(p12, cert.Pkcs12);
        Assert.Equal("p12pass", cert.Password);
    }

    [Fact]
    public async Task A_user_who_declines_the_consent_prompt_is_a_result()
    {
        await using var fixture = await VpnFixture.StartAsync(new FakeVpn { SaveAccess = Shiny.AccessState.Denied });

        var result = await fixture.Client.SaveProfileAsync(new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.SharedSecret, Secret: "psk"));
        Assert.Equal(Client.AccessState.Denied, result.Access);
    }

    public static TheoryData<VpnProfileRequest> IncompleteProfiles => new()
    {
        new VpnProfileRequest("", "vpn.acme.com", VpnAuthenticationType.SharedSecret, Secret: "psk"),
        new VpnProfileRequest("Acme", " ", VpnAuthenticationType.SharedSecret, Secret: "psk"),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.SharedSecret),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.UsernamePassword, Username: "allan"),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.UsernamePassword, Password: "hunter2"),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.Certificate, Password: "p12pass"),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.Certificate, Pkcs12: new byte[256 * 1024 + 1]),
        new VpnProfileRequest("Acme", "vpn.acme.com", (VpnAuthenticationType)42, Secret: "psk"),
        new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.SharedSecret, Secret: "psk", LocalIdentifier: new string('x', 1025))
    };

    [Theory]
    [MemberData(nameof(IncompleteProfiles))]
    public async Task Refuses_an_incomplete_profile(VpnProfileRequest request)
    {
        var vpn = new FakeVpn();
        await using var fixture = await VpnFixture.StartAsync(vpn);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SaveProfileAsync(request));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(vpn.Profile);
    }

    [Fact]
    public void A_profile_request_never_prints_its_secrets()
    {
        var text = new VpnProfileRequest("Acme", "vpn.acme.com", VpnAuthenticationType.UsernamePassword, Secret: "psk", Username: "allan", Password: "hunter2").ToString();

        Assert.Contains("vpn.acme.com", text);
        Assert.DoesNotContain("psk", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public async Task Connects_with_the_pages_timeout_and_answers_the_new_state()
    {
        var vpn = new FakeVpn { State = Native.VpnConnectionState.Disconnected };
        await using var fixture = await VpnFixture.StartAsync(vpn);

        var status = await fixture.Client.ConnectAsync(new VpnConnectRequest(45));

        Assert.Equal(TimeSpan.FromSeconds(45), vpn.ConnectTimeout);
        Assert.Equal(VpnConnectionState.Connected, status.State);

        await fixture.Client.ConnectAsync(new VpnConnectRequest());
        Assert.Null(vpn.ConnectTimeout);

        Assert.Equal(VpnConnectionState.Disconnected, (await fixture.Client.DisconnectAsync()).State);

        foreach (var timeout in new[] { 0, 301 })
        {
            var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new VpnConnectRequest(timeout)));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task Removes_the_profile()
    {
        var vpn = new FakeVpn();
        await using var fixture = await VpnFixture.StartAsync(vpn);

        await fixture.Client.RemoveProfileAsync();

        Assert.Equal(1, vpn.Removes);
        Assert.Equal(VpnConnectionState.NotConfigured, (await fixture.Client.GetStatusAsync()).State);
    }

    public static TheoryData<Exception, HttpStatusCode, string> Failures => new()
    {
        { new Native.VpnConnectionException("IKE authentication failed"), HttpStatusCode.BadGateway, "vpn_connection_failed" },
        { new Native.VpnPermissionException("No VPN profile has been saved"), HttpStatusCode.Forbidden, "vpn_permission" },
        { new Native.VpnNotSupportedException("Status is not available on this platform"), HttpStatusCode.NotImplemented, "not_supported" },
        { new Native.VpnException("NetworkManager is not running"), HttpStatusCode.BadGateway, "vpn_failed" }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Os_failures_become_errors_the_page_can_switch_on(Exception failure, HttpStatusCode status, string code)
    {
        await using var fixture = await VpnFixture.StartAsync(new FakeVpn { ConnectFailure = failure });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ConnectAsync(new VpnConnectRequest()));

        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, failed.Code);
        Assert.Contains(failure.Message, failed.Message);
    }

    [Fact]
    public async Task State_changes_reach_the_page_only_while_it_listens()
    {
        var vpn = new FakeVpn { State = Native.VpnConnectionState.Connected };
        await using var fixture = await VpnFixture.StartAsync(vpn);
        Assert.Equal(0, vpn.Subscribers);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await TestEventStream.OpenAsync(fixture.WebView, VpnBridge.StatusEvent, timeout.Token);

        // Shiny hands a new subscriber the current state, so the page knows where things stand.
        var (name, data) = await events.NextAsync(timeout.Token);
        Assert.Equal("vpn.status", name);
        Assert.Contains("\"Connected\"", data);

        vpn.RaiseStatusChanged(Native.VpnConnectionState.Reconnecting);
        Assert.Contains("\"Reconnecting\"", (await events.NextAsync(timeout.Token)).Data);
        Assert.Equal(1, vpn.Subscribers);

        await events.DisposeAsync();
        while (vpn.Subscribers > 0)
            await Task.Delay(20, timeout.Token);
    }

    sealed class VpnFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public VpnBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<VpnFixture> StartAsync(Native.IVpnManager? manager)
        {
            var fixture = new VpnFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new VpnBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new VpnBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class CountingMainThread : IWebAppMainThread
    {
        public int Calls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            this.Calls++;
            return action();
        }
    }

    sealed class FakeVpn : Native.IVpnManager
    {
        public Native.VpnCapabilities Capabilities { get; init; } = Everything;
        public Native.VpnConnectionState State { get; set; } = Native.VpnConnectionState.Disconnected;
        public Shiny.AccessState SaveAccess { get; init; } = Shiny.AccessState.Available;
        public Exception? ConnectFailure { get; init; }

        public Native.VpnProfile? Profile { get; private set; }
        public TimeSpan? ConnectTimeout { get; private set; }
        public int Removes { get; private set; }
        public int StatusReads { get; private set; }

        EventHandler<Native.VpnConnectionState>? statusChanged;
        public int Subscribers => this.statusChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<Native.VpnConnectionState>? StatusChanged
        {
            add
            {
                this.statusChanged += value;
                value?.Invoke(this, this.State);
            }
            remove => this.statusChanged -= value;
        }

        public void RaiseStatusChanged(Native.VpnConnectionState state) => this.statusChanged?.Invoke(this, state);

        public Task<Shiny.AccessState> SaveProfile(Native.VpnProfile profile, CancellationToken ct = default)
        {
            if (this.SaveAccess == Shiny.AccessState.Available)
                this.Profile = profile;
            return Task.FromResult(this.SaveAccess);
        }

        public Task RemoveProfile(CancellationToken ct = default)
        {
            this.Removes++;
            this.Profile = null;
            this.State = Native.VpnConnectionState.NotConfigured;
            return Task.CompletedTask;
        }

        public Task Connect(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (this.ConnectFailure is not null)
                return Task.FromException(this.ConnectFailure);

            this.ConnectTimeout = timeout;
            this.State = Native.VpnConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task Disconnect(CancellationToken ct = default)
        {
            this.State = Native.VpnConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<Native.VpnConnectionState> GetStatus(CancellationToken ct = default)
        {
            this.StatusReads++;
            return Task.FromResult(this.State);
        }
    }
}
