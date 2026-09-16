using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Shiny.WebAppHost.Tests;

/// <summary>
/// The rules a request from another machine meets. These run against a real listener bound past loopback and
/// reached over this machine's own LAN address, so the connection genuinely arrives from a non-loopback peer
/// — which is the thing being tested, and the thing a loopback client can never exercise.
/// </summary>
public class RemoteAccessTests
{
    [Fact]
    public async Task DeniesEveryBridgeFromTheNetworkByDefault()
    {
        await using var fixture = await RemoteFixture.StartAsync(o => o.RemoteAccess.Enabled = true);

        var bridge = await fixture.GetAsync("/_bridge/echo/ping");
        Assert.Equal(HttpStatusCode.Forbidden, bridge.StatusCode);
        Assert.Equal("remote_denied", await ErrorCodeAsync(bridge));

        // Built in or not makes no difference; nothing is remote until it is named.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/_bridge/host")).StatusCode);

        // And the web app itself is not published either.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task ServesAnAllowedBridgeAndRefusesTheRest()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.AllowBridge("echo");
        });

        // No cookie, no token — the allowlist is the whole of the authorization.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping")).StatusCode);

        var denied = await fixture.GetAsync("/_bridge/host");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("remote_denied", await ErrorCodeAsync(denied));
    }

    [Fact]
    public async Task RefusesAHostHeaderThatIsNotAnAddress()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.AllowBridge("echo");
        });

        // DNS rebinding: the request reaches this device, but under a name that is not its own.
        var rebound = await fixture.GetAsync("/_bridge/echo/ping", host: "evil.example");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, rebound.StatusCode);

        // A name the app vouches for is accepted, port and all.
        fixture.Options.RemoteAccess.AllowHost("kiosk.local");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", host: $"kiosk.local:{fixture.Port}")).StatusCode);

        // Right name, wrong port.
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.GetAsync("/_bridge/echo/ping", host: "kiosk.local:1")).StatusCode);
    }

    [Fact]
    public async Task NeverHandsOutASessionOverTheNetwork()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.AllowBridge("echo");
        });

        // Even holding the launch token, which a remote caller should never have in the first place.
        var start = await fixture.GetAsync($"{WebAppSession.StartPath}?token={fixture.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Empty(start.Headers.Where(x => x.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));

        // Ping stays open: it is how the app checks its own listener is alive.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync(WebAppSession.PingPath)).StatusCode);
    }

    [Fact]
    public async Task RunsTheAuthorizeHookBeforeAnythingIsServed()
    {
        var seen = 0;

        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.AllowBridge("echo");
            o.RemoteAccess.Authorize = ctx =>
            {
                Interlocked.Increment(ref seen);
                return ctx.Request.Headers["Authorization"].ToString() == "Bearer letmein";
            };
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.GetAsync("/_bridge/echo/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", authorization: "Bearer letmein")).StatusCode);
        Assert.Equal(2, seen);
    }

    [Fact]
    public async Task ServesTheWebAppWhenAsked()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.ServeWebApp = true;
        });

        var page = await fixture.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("version 1.0.0", await page.Content.ReadAsStringAsync());

        // Serving the pages is still not serving the device.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/_bridge/echo/ping")).StatusCode);
    }

    /// <summary>Opening the server to the network changes nothing for the WebView on this device.</summary>
    [Fact]
    public async Task LeavesTheDeviceItselfAlone()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.RemoteAccess.Enabled = true;
            o.RemoteAccess.AllowBridge("echo");
        });

        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping"))).StatusCode);

        // The same call once the WebView has traded its token for the cookie.
        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(fixture.Start);
        Assert.Equal(HttpStatusCode.NoContent, (await webView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping"))).StatusCode);

        // And a bridge nobody opened remotely is still there for the page.
        Assert.Equal(HttpStatusCode.OK, (await webView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/host"))).StatusCode);
    }

    static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    /// <summary>A host bound past loopback, plus this machine's LAN address to reach it by.</summary>
    sealed class RemoteFixture : IAsyncDisposable
    {
        TestApp app = null!;

        public WebAppHost Host { get; private set; } = null!;
        public WebAppHostOptions Options { get; private set; } = null!;
        public string Token { get; private set; } = null!;
        public Uri Start { get; private set; } = null!;
        public IPAddress Address { get; private set; } = null!;
        public int Port => this.Host.Origin!.Port;

        public static async Task<RemoteFixture> StartAsync(Action<WebAppHostOptions> configure)
        {
            var address = FindLanAddress();
            Assert.SkipWhen(address is null, "No non-loopback IPv4 address on this machine, so a remote request cannot be made.");

            var fixture = new RemoteFixture { app = new TestApp(), Address = address! };

            fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await fixture.app.StartReleaseServerAsync();

            fixture.Options = fixture.app.Options();
            configure(fixture.Options);

            var session = new WebAppSession();
            fixture.Token = session.Token;
            fixture.Host = new WebAppHost(fixture.Options, session, new WebAppEventHub(), [new EchoBridge()]);
            fixture.Start = await fixture.Host.StartAsync();

            return fixture;
        }

        /// <summary>A request that genuinely arrives from off-device, over this machine's LAN address.</summary>
        public async Task<HttpResponseMessage> GetAsync(string path, string? host = null, string? authorization = null)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{this.Address}:{this.Port}{path}"));

            if (host is not null)
                request.Headers.Host = host;

            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            return await client.SendAsync(request);
        }

        static IPAddress? FindLanAddress()
            => NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));

        public async ValueTask DisposeAsync()
        {
            if (this.Host is not null)
                await this.Host.DisposeAsync();

            if (this.app is not null)
                await this.app.DisposeAsync();
        }
    }
}

/// <summary>The parsing and matching the remote guard leans on, without a socket in sight.</summary>
public class RemoteAccessOptionTests
{
    [Theory]
    [InlineData("192.168.1.10:5780", true)]
    [InlineData("10.0.0.4:5780", true)]
    [InlineData("[::1]:5780", true)]
    [InlineData("[fe80::1]:5780", true)]
    [InlineData("192.168.1.10:5781", false)]     // another port on this machine
    [InlineData("192.168.1.10", false)]          // no port means 80
    [InlineData("evil.example:5780", false)]     // a name resolved to this device
    [InlineData("kiosk.local:5780", false)]
    [InlineData("", false)]
    [InlineData("[::1:5780", false)]             // unclosed bracket
    public void AcceptsOnlyAddressesAtItsOwnPort(string host, bool expected)
        => Assert.Equal(expected, new WebAppRemoteAccessOptions().IsAllowedHost(host, 5780));

    [Fact]
    public void AcceptsANamedHost()
    {
        var options = new WebAppRemoteAccessOptions().AllowHost("kiosk.local");

        Assert.True(options.IsAllowedHost("kiosk.local:5780", 5780));
        Assert.True(options.IsAllowedHost("KIOSK.LOCAL:5780", 5780));
        Assert.False(options.IsAllowedHost("kiosk.local:5781", 5780));
        Assert.False(options.IsAllowedHost("other.local:5780", 5780));
    }

    [Theory]
    [InlineData("/_bridge/files/data/list", "files")]
    [InlineData("/_bridge/files", "files")]
    [InlineData("/_bridge/host", "host")]
    [InlineData("/_bridge/", null)]
    [InlineData("/_bridge", null)]
    [InlineData("/index.html", null)]
    [InlineData("/_bridgefoo/bar", null)]
    public void ReadsTheBridgeNameOutOfThePath(string path, string? expected)
        => Assert.Equal(expected, WebAppRemoteAccessOptions.BridgeName(path));

    [Fact]
    public void MatchesBridgeNamesWithoutRegardForCase()
    {
        var options = new WebAppRemoteAccessOptions().AllowBridge("Files");

        Assert.True(options.IsBridgeAllowed("files"));
        Assert.False(options.IsBridgeAllowed("settings"));
        Assert.False(options.IsBridgeAllowed(null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]       // IPv4 loopback reached over a dual-stack socket
    [InlineData("192.168.1.10", false)]
    [InlineData("::ffff:192.168.1.10", false)]
    public void KnowsWhatCameFromThisDevice(string address, bool expected)
        => Assert.Equal(expected, WebAppHost.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void TreatsAnUnknownPeerAsRemote()
        => Assert.False(WebAppHost.IsLocal(null));

    [Fact]
    public void RefusesAnAllowlistThatCannotTakeEffect()
    {
        var options = new WebAppHostOptions { AppId = "demo" };
        options.UseBaseline(typeof(RemoteAccessOptionTests).Assembly, "Shiny.WebAppHost.Tests.baseline.zip");
        options.RemoteAccess.AllowBridge("files");

        Assert.Contains("RemoteAccess.Enabled is false", Assert.Throws<InvalidOperationException>(options.Validate).Message);

        options.RemoteAccess.Enabled = true;
        options.Validate();
    }
}
