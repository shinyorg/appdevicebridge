using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Tunnel;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// Traffic through a tunnel. A tunnel hands the server its requests from the local end of the forward, so every one of them
/// arrives from 127.0.0.1 — and every header on it is written by a caller somewhere on the internet. These tests send real
/// HTTP down connections shaped exactly that way.
/// </summary>
public class TunnelTests
{
    const string PublicHost = "abc123.a.free.pinggy.link";

    [Fact]
    public async Task A_tunneled_caller_is_never_on_the_device()
    {
        await using var fixture = await TunnelFixture.StartAsync();

        // Through the tunnel, by the tunnel's own name: a remote caller, so the default policy refuses it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);

        // Claiming to be loopback changes nothing. The address was loopback all along; the name is the caller's to write.
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.TunnelAsync("/_bridge/echo/ping", host: $"127.0.0.1:{fixture.Port}")).StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.TunnelAsync("/_bridge/echo/ping", host: "localhost")).StatusCode);

        // The same bridge from this device, with the WebView's session, still works.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.WebView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping"))).StatusCode);
    }

    [Fact]
    public async Task A_tunneled_caller_never_gets_the_session()
    {
        await using var fixture = await TunnelFixture.StartAsync(web: o => o.ServeWebAppRemotely = true);

        // Even holding the launch token.
        var start = await fixture.TunnelAsync($"{fixture.Host.Paths.Start}?token={fixture.Session.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.False(start.Headers.Contains("Set-Cookie"));

        // Or the cookie itself, replayed from outside: it proves nothing about who sent it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping", cookie: fixture.Session.Token)).StatusCode);

        // The whole disguise — a loopback name and the device's own cookie, from a loopback address — is still not the device.
        var disguised = await fixture.TunnelAsync("/_bridge/echo/ping", host: $"127.0.0.1:{fixture.Port}", cookie: fixture.Session.Token);
        Assert.NotEqual(HttpStatusCode.NoContent, disguised.StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, disguised.StatusCode);
    }

    /// <summary>The debug allowance is for a browser on the development machine, not for the internet.</summary>
    [Fact]
    public async Task The_debug_allowance_stops_at_the_tunnel()
    {
        await using var fixture = await TunnelFixture.StartAsync(o => o.IsDebug = true);

        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.NoContent, (await stranger.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.TunnelAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Serves_the_web_app_through_the_tunnel_only_when_asked()
    {
        await using (var closed = await TunnelFixture.StartAsync())
            Assert.Equal(HttpStatusCode.Forbidden, (await closed.TunnelAsync("/")).StatusCode);

        await using var open = await TunnelFixture.StartAsync(web: o => o.ServeWebAppRemotely = true);
        var page = await open.TunnelAsync("/");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("version 1.0.0", await page.Content.ReadAsStringAsync());
    }

    /// <summary>The app's policy decides for a tunneled caller exactly as it would for any remote one.</summary>
    [Fact]
    public async Task Lets_the_apps_policy_admit_a_tunneled_caller()
    {
        await using var fixture = await TunnelFixture.StartAsync(
            o => o.AuthorizeBridges(p => p.RequireRole("kiosk")),
            http: h => h.AddAuthentication().AddApiKey(k => k.AddKey("kiosk-key", "kiosk", "kiosk"))
        );

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.TunnelAsync("/_bridge/echo/ping", key: "kiosk-key")).StatusCode);
    }

    [Fact]
    public async Task Opens_and_closes_without_touching_the_listener()
    {
        await using var fixture = await TunnelFixture.StartAsync(startServer: false);

        Assert.Null(fixture.Bridge.Origin);
        Assert.Equal(TunnelState.Stopped, fixture.Tunnel.State);

        var url = await fixture.Tunnel.StartAsync("token-123");

        Assert.Equal(new Uri($"https://{PublicHost}"), url);
        Assert.Equal(TunnelState.Connected, fixture.Tunnel.State);
        Assert.True(fixture.Tunnel.IsOn);
        Assert.Equal("token-123", fixture.Ssh.Subdomain);
        Assert.Contains(fixture.Tunnel, fixture.Bridge.Tunnels);

        // Serving through the tunnel, while the server listens on nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);
        Assert.Null(fixture.Bridge.Origin);

        await fixture.Tunnel.StopAsync();

        Assert.Equal(TunnelState.Stopped, fixture.Tunnel.State);
        Assert.Null(fixture.Tunnel.PublicUrl);
        Assert.True(fixture.Ssh.Disposed);

        // Its name is not an answer to anything once it is closed.
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);
    }

    [Fact]
    public async Task Follows_the_address_across_a_reconnect()
    {
        await using var fixture = await TunnelFixture.StartAsync(startServer: false);
        var changes = new List<string?>();
        fixture.Tunnel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await fixture.Tunnel.StartAsync();

        fixture.Ssh.Report(TunnelState.Reconnecting, null);
        Assert.Equal(TunnelState.Reconnecting, fixture.Tunnel.State);
        Assert.Null(fixture.Tunnel.PublicUrl);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);

        fixture.Ssh.Report(TunnelState.Connected, "https://xyz789.a.free.pinggy.link");
        Assert.Equal(TunnelState.Connected, fixture.Tunnel.State);
        Assert.Equal("xyz789.a.free.pinggy.link", fixture.Tunnel.PublicUrl?.Host);

        // The new name is accepted, the old one is not.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.TunnelAsync("/_bridge/echo/ping", host: "xyz789.a.free.pinggy.link")).StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.TunnelAsync("/_bridge/echo/ping")).StatusCode);

        Assert.Contains(nameof(AppDeviceBridgeTunnel.PublicUrl), changes);
        Assert.Contains(nameof(AppDeviceBridgeTunnel.IsOn), changes);
    }

    [Fact]
    public async Task Reports_why_it_could_not_open()
    {
        await using (var throwing = await TunnelFixture.StartAsync(startServer: false, open: _ => throw new InvalidOperationException("ssh refused")))
        {
            Assert.Null(await throwing.Tunnel.StartAsync());
            Assert.Equal(TunnelState.Failed, throwing.Tunnel.State);
            Assert.Equal("ssh refused", throwing.Tunnel.LastError);
            Assert.False(throwing.Tunnel.IsOn);
            Assert.True(throwing.Ssh.Disposed);
        }

        await using var addressless = await TunnelFixture.StartAsync(startServer: false, open: _ => null);
        Assert.Null(await addressless.Tunnel.StartAsync());
        Assert.Equal(TunnelState.Failed, addressless.Tunnel.State);
        Assert.Contains("never reported a public address", addressless.Tunnel.LastError);

        // And it can be tried again.
        addressless.Ssh.Answer = _ => $"https://{PublicHost}";
        Assert.NotNull(await addressless.Tunnel.StartAsync());
        Assert.Equal(TunnelState.Connected, addressless.Tunnel.State);
        Assert.Null(addressless.Tunnel.LastError);
    }

    [Fact]
    public async Task Registers_on_the_servers_builder()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http
                .AddAppDeviceBridge(o => o.AppId = TestApp.AppId)
                .AddAppDeviceBridgeTunnel(o => o.Host = QuickTunnelHost.Serveo),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var tunnel = provider.GetRequiredService<AppDeviceBridgeTunnel>();

        Assert.Same(tunnel, provider.GetServices<IAppDeviceBridgeTunnel>().Single());
        Assert.Equal(QuickTunnelHost.Serveo, provider.GetRequiredService<AppDeviceBridgeTunnelOptions>().Host);

        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;
        Assert.Same(tunnel, server.Tunnels.Single());
    }

    /// <summary>A host with the tunnel registered as an app registers it, and a way to send requests down a tunneled connection.</summary>
    sealed class TunnelFixture : IAsyncDisposable
    {
        TestApp app = null!;

        public WebAppHost Host { get; private set; } = null!;
        public AppDeviceBridgeServer Bridge => this.Host.Server;
        public AppDeviceBridgeTunnel Tunnel { get; private set; } = null!;
        /// <summary>The SSH side, faked: what the tunnel opens.</summary>
        public FakeSession Ssh { get; private set; } = null!;
        public WebAppSession Session { get; } = new();
        public HttpClient WebView { get; } = new(new HttpClientHandler { CookieContainer = new CookieContainer() });
        public int Port => this.Host.Origin?.Port ?? 80;

        public static async Task<TunnelFixture> StartAsync(
            Action<AppDeviceBridgeOptions>? configure = null,
            Action<WebAppHostOptions>? web = null,
            Action<ShinyHttpServerBuilder>? http = null,
            bool startServer = true,
            Func<string?, string?>? open = null
        )
        {
            var fixture = new TunnelFixture { app = new TestApp() };
            fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await fixture.app.StartReleaseServerAsync();

            var options = fixture.app.Options();
            web?.Invoke(options);

            fixture.Ssh = new FakeSession(open ?? (_ => $"https://{PublicHost}"));
            fixture.Host = fixture.app.CreateHost(
                options,
                fixture.app.BridgeOptions(configure),
                null,
                [new EchoBridge()],
                h =>
                {
                    h.Services.AddSingleton(sp => new AppDeviceBridgeTunnel(sp.GetRequiredService<AppDeviceBridgeServer>(), (_, subdomain) =>
                    {
                        fixture.Ssh.Subdomain = subdomain;
                        return fixture.Ssh;
                    }));
                    h.Services.AddSingleton<IAppDeviceBridgeTunnel>(sp => sp.GetRequiredService<AppDeviceBridgeTunnel>());
                    http?.Invoke(h);
                },
                fixture.Session
            );

            fixture.Tunnel = fixture.Bridge.Http.Services!.GetRequiredService<AppDeviceBridgeTunnel>();

            if (startServer)
            {
                await fixture.WebView.GetStringAsync(await fixture.Host.StartAsync());
                await fixture.Tunnel.StartAsync();
            }

            return fixture;
        }

        /// <summary>
        /// A request down a fresh tunneled connection: from loopback, like an SSH forward, but marked tunneled by the
        /// transport. The caller controls every header, <c>Host</c> included.
        /// </summary>
        public async Task<HttpResponseMessage> TunnelAsync(string path, string host = PublicHost, string? cookie = null, string? key = null)
        {
            var server = this.Bridge.Http;
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectCallback = (_, _) =>
                {
                    var connection = new DuplexPipeConnection(
                        Guid.NewGuid().ToString("n"),
                        remoteEndPoint: new IPEndPoint(IPAddress.Loopback, 40000),
                        isTunneled: true
                    );
                    _ = server.ServeAsync(connection);
                    return ValueTask.FromResult<Stream>(new DuplexStream(connection.TransportReader, connection.TransportWriter));
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://tunnel.invalid{path}"));
            request.Headers.Host = host;

            if (cookie is not null)
                request.Headers.Add("Cookie", $"{WebAppSession.CookieName}={cookie}");

            if (key is not null)
                request.Headers.Add("X-API-Key", key);

            var response = await client.SendAsync(request);
            await response.Content.LoadIntoBufferAsync();
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            this.WebView.Dispose();
            await this.app.DisposeAsync();
        }
    }

    internal sealed class FakeSession(Func<string?, string?> answer) : ITunnelSession
    {
        public Func<string?, string?> Answer { get; set; } = answer;

        public string? Subdomain { get; set; }

        public bool Disposed { get; private set; }

        public string? PublicUrl { get; private set; }

        public TunnelState State { get; private set; } = TunnelState.Stopped;

        public string? LastError { get; private set; }

        public event EventHandler? Changed;

        public Task<string?> StartAsync(CancellationToken cancellationToken)
        {
            this.Disposed = false;
            this.PublicUrl = this.Answer(this.Subdomain);
            this.State = this.PublicUrl is null ? TunnelState.Failed : TunnelState.Connected;
            return Task.FromResult(this.PublicUrl);
        }

        /// <summary>What the SSH layer reports on its own: a drop, a reconnect at a new address.</summary>
        public void Report(TunnelState state, string? url)
        {
            this.State = state;
            this.PublicUrl = url;
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stream over the transport ends of a duplex pipe: what an HTTP client writes, the server reads.</summary>
    sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        readonly Stream input = reader.AsStream();
        readonly Stream output = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => this.input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => this.input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => this.output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => this.output.WriteAsync(buffer, cancellationToken);
        public override void Flush() => this.output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => this.output.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.output.Dispose();
                this.input.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
