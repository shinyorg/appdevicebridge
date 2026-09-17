using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// Who may call the bridges. The remote cases run against a real listener bound past loopback and reached over this
/// machine's own LAN address, so the connection genuinely arrives from a non-loopback peer — which is the thing being
/// tested, and the thing a loopback client can never exercise.
/// </summary>
public class BridgeSecurityTests
{
    const string Key = "kiosk-key";

    [Fact]
    public async Task DeniesEveryBridgeFromTheNetworkByDefault()
    {
        await using var fixture = await RemoteFixture.StartAsync();

        AssertDenied(await fixture.GetAsync("/_bridge/echo/ping"));

        // Built in or not makes no difference.
        AssertDenied(await fixture.GetAsync("/_bridge/host"));

        // And the web app itself is not published either.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/")).StatusCode);
    }

    /// <summary>The debug default: any caller, so a browser or script on the development machine can reach a device.</summary>
    [Fact]
    public async Task LetsAnyCallerInADebugBuild()
    {
        await using var fixture = await RemoteFixture.StartAsync(o => o.IsDebug = true);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/_bridge/host")).StatusCode);
    }

    [Fact]
    public async Task KeepsTheDeviceOnlyRuleInADebugBuildWhenAsked()
    {
        await using var fixture = await RemoteFixture.StartAsync(o =>
        {
            o.IsDebug = true;
            o.AllowAnyCallerInDebug = false;
        });

        AssertDenied(await fixture.GetAsync("/_bridge/echo/ping"));
    }

    /// <summary>The app decides how a remote caller authenticates; the policy it gives replaces the default entirely.</summary>
    [Fact]
    public async Task LetsTheAppDecideWhoMayCall()
    {
        await using var fixture = await RemoteFixture.StartAsync(
            o => o.AuthorizeBridges(p => p.RequireRole("kiosk")),
            http: h => h.AddAuthentication().AddApiKey(k => k.AddKey(Key, "kiosk", "kiosk"))
        );

        AssertDenied(await fixture.GetAsync("/_bridge/echo/ping"));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", key: Key)).StatusCode);

        // Replaced, not added to: the WebView's session authenticates, but it is no longer what the bridges ask for.
        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(fixture.Start);
        AssertDenied(await webView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping")));
    }

    [Fact]
    public async Task RefusesAHostNameItDoesNotAnswerTo()
    {
        await using var fixture = await RemoteFixture.StartAsync(o => o.IsDebug = true);

        // DNS rebinding: the request reaches this device, but under a name that is not its own.
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await fixture.GetAsync("/_bridge/echo/ping", host: "evil.example")).StatusCode);

        // A name the app vouches for is accepted.
        fixture.Host.Server.Options.AllowedHosts.Add("kiosk.local");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", host: $"kiosk.local:{fixture.Port}")).StatusCode);
    }

    [Fact]
    public async Task NeverHandsOutASessionOverTheNetwork()
    {
        await using var fixture = await RemoteFixture.StartAsync(o => o.IsDebug = true);

        // Even holding the launch token, which a remote caller should never have in the first place.
        var start = await fixture.GetAsync($"{fixture.Host.Paths.Start}?token={fixture.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Empty(start.Headers.Where(x => x.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));

        // Ping stays open: it is how the app checks its own listener is alive.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync(fixture.Host.Paths.Ping)).StatusCode);
    }

    [Fact]
    public async Task ServesTheWebAppWhenAsked()
    {
        await using var fixture = await RemoteFixture.StartAsync(web: o => o.ServeWebAppRemotely = true);

        var page = await fixture.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("version 1.0.0", await page.Content.ReadAsStringAsync());

        // Serving the pages is still not serving the device.
        AssertDenied(await fixture.GetAsync("/_bridge/echo/ping"));
    }

    /// <summary>Binding past loopback changes nothing for the WebView on this device.</summary>
    [Fact]
    public async Task LeavesTheDeviceItselfAlone()
    {
        await using var fixture = await RemoteFixture.StartAsync();

        // On this device, but not the app's WebView.
        using var stranger = new HttpClient();
        AssertDenied(await stranger.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping")));

        // The same call once the WebView has traded its token for the cookie.
        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(fixture.Start);
        Assert.Equal(HttpStatusCode.NoContent, (await webView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/echo/ping"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await webView.GetAsync(new Uri(fixture.Host.Origin!, "/_bridge/host"))).StatusCode);
    }

    /// <summary>A browser page on another site, running on this device, cannot drive the bridges.</summary>
    [Fact]
    public async Task RefusesAForeignOriginOnTheDevice()
    {
        await using var app = new TestApp();
        var server = BridgesOnly(app);
        var origin = await server.StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/_bridge/echo/ping"));
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        AssertDenied(await client.SendAsync(request));
    }

    /// <summary>No WebView host: nothing adds a session, so any caller on this device may use the bridges.</summary>
    [Fact]
    public async Task ServesCallersOnTheDeviceWithoutAWebView()
    {
        await using var app = new TestApp();
        var server = BridgesOnly(app);
        var origin = await server.StartAsync();

        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(new Uri(origin, "/_bridge/echo/ping"))).StatusCode);

        // With nothing to serve at the root, it is not a web server.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(origin, "/index.html"))).StatusCode);
    }

    /// <summary>
    /// The bridges are guests on the app's server: its settings, middleware and endpoints stay the app's, and its own routes
    /// pass through the bridge server untouched — no host check, no policy of the bridges'.
    /// </summary>
    [Fact]
    public async Task SharesTheAppsServer()
    {
        await using var app = new TestApp();
        var hooked = 0;

        var server = app.CreateServer(app.BridgeOptions(), [new EchoBridge()], http =>
        {
            http.Options.Limits.MaxRequestHeaderCount = 50;
            http.Configure(s =>
            {
                s.Use(async (ctx, next) =>
                {
                    Interlocked.Increment(ref hooked);
                    ctx.Response.Headers["X-Hooked"] = "yes";
                    await next(ctx);
                });
                s.MapGet("/api/open", ctx => ctx.Response.WriteAsync("open"));
            });
        });
        var origin = await server.StartAsync();

        Assert.Equal(50, server.Http.Options.Limits.MaxRequestHeaderCount);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/api/open"));
        request.Headers.Host = "anything.example";
        var response = await client.SendAsync(request);

        Assert.Equal("open", await response.Content.ReadAsStringAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Hooked").Single());
        Assert.Equal(1, hooked);

        // The same name on a bridge is still refused.
        using var bridge = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/_bridge/echo/ping"));
        bridge.Headers.Host = "anything.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await client.SendAsync(bridge)).StatusCode);
    }

    static AppDeviceBridgeServer BridgesOnly(TestApp app)
        => app.CreateServer(app.BridgeOptions(), [new EchoBridge()]);

    static void AssertDenied(HttpResponseMessage response)
        => Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected the bridge to refuse, but it answered {(int)response.StatusCode}."
        );

    /// <summary>A host bound past loopback, plus this machine's LAN address to reach it by.</summary>
    sealed class RemoteFixture : IAsyncDisposable
    {
        TestApp app = null!;

        public WebAppHost Host { get; private set; } = null!;
        public string Token { get; private set; } = null!;
        public Uri Start { get; private set; } = null!;
        public IPAddress Address { get; private set; } = null!;
        public int Port => this.Host.Origin!.Port;

        public static async Task<RemoteFixture> StartAsync(
            Action<AppDeviceBridgeOptions>? configure = null,
            Action<WebAppHostOptions>? web = null,
            Action<ShinyHttpServerBuilder>? http = null
        )
        {
            var address = FindLanAddress();
            Assert.SkipWhen(address is null, "No non-loopback IPv4 address on this machine, so a remote request cannot be made.");

            var fixture = new RemoteFixture { app = new TestApp(), Address = address! };

            fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
            await fixture.app.StartReleaseServerAsync();

            var options = fixture.app.Options();
            web?.Invoke(options);

            var session = new WebAppSession();
            fixture.Token = session.Token;
            fixture.Host = fixture.app.CreateHost(
                options,
                fixture.app.BridgeOptions(configure),
                null,
                [new EchoBridge()],
                h =>
                {
                    h.Options.Address = IPAddress.Any;
                    http?.Invoke(h);
                },
                session
            );
            fixture.Start = await fixture.Host.StartAsync();

            return fixture;
        }

        /// <summary>A request that genuinely arrives from off-device, over this machine's LAN address.</summary>
        public async Task<HttpResponseMessage> GetAsync(string path, string? host = null, string? key = null)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{this.Address}:{this.Port}{path}"));

            if (host is not null)
                request.Headers.Host = host;

            if (key is not null)
                request.Headers.Add("X-API-Key", key);

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

/// <summary>The checks bridge policies lean on, without a socket in sight.</summary>
public class BridgeCallerTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]       // IPv4 loopback reached over a dual-stack socket
    [InlineData("192.168.1.10", false)]
    [InlineData("::ffff:192.168.1.10", false)]
    public void KnowsWhatCameFromThisDevice(string address, bool expected)
        => Assert.Equal(expected, BridgeCallers.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void TreatsAnUnknownPeerAsRemote()
        => Assert.False(BridgeCallers.IsLocal(null));

    [Theory]
    [InlineData("127.0.0.1:5780", true)]
    [InlineData("localhost:5780", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]:5780", true)]
    [InlineData("192.168.1.10:5780", false)]
    [InlineData("evil.example:5780", false)]
    [InlineData("", false)]
    [InlineData("[::1:5780", false)]             // unclosed bracket
    public void KnowsALoopbackHostHeader(string host, bool expected)
        => Assert.Equal(expected, BridgeCallers.IsLoopbackHost(host));

    [Theory]
    [InlineData("[fe80::1]:5780", "fe80::1")]
    [InlineData("kiosk.local:5780", "kiosk.local")]
    [InlineData("kiosk.local", "kiosk.local")]
    public void ReadsTheNameOutOfAHostHeader(string host, string expected)
        => Assert.Equal(expected, BridgeCallers.HostName(host));

    [Fact]
    public void DefaultsToAReleaseBuildsRulesOutsideTheDebugger()
    {
        var options = new AppDeviceBridgeOptions();

        Assert.True(options.AllowAnyCallerInDebug);
        Assert.Empty(options.AllowedHosts);
    }
}

/// <summary>The container wiring an app gets from AddAppDeviceBridge and AddWebAppHost on the server's builder.</summary>
public class RegistrationTests
{
    [Fact]
    public async Task WiresTheWebViewHostOntoTheServer()
    {
        await using var app = new TestApp();
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0", backgroundScript: """appdevicebridge.on("hello", () => "from script");"""));
        await app.StartReleaseServerAsync();

        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http =>
            {
                http.AddAppDeviceBridge(o => o.AppId = TestApp.AppId);
                http.AddWebAppHost(o =>
                {
                    var configured = app.Options();
                    o.UpdateServer = configured.UpdateServer;
                    o.PublicKey = configured.PublicKey;
                    o.InstallDirectory = configured.InstallDirectory;
                    o.HttpMessageHandlerFactory = configured.HttpMessageHandlerFactory;
                });
            },
            autoStart: false
        );

        // A second builder over the same collection, as an app configuring its server elsewhere gets: every call lands.
        services.AddShinyHttpServer(
            http =>
            {
                http.Options.Port = 0;
                http.AddAppDeviceBridge(o =>
                {
                    o.IsDebug = false;
                    o.DataDirectory = app.InstallDirectory;
                });
            },
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<WebAppHost>();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();

        Assert.Same(server, host.Server);
        Assert.Same(provider.GetRequiredService<HttpServer>(), server.Http);
        Assert.Contains(server.Bridges, x => x is WebAppSettingsBridge);
        Assert.Contains(server.Bridges, x => x is WebAppFilesBridge);

        var start = await host.StartAsync();
        Assert.Contains(host, server.Extensions);

        // The session the host adds is on the default bridge policy.
        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(new Uri(start, "/_bridge/host"))).StatusCode);

        using var webView = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
        await webView.GetStringAsync(start);
        Assert.Contains("\"webAppVersion\":\"1.0.0\"", await webView.GetStringAsync(new Uri(start, "/_bridge/host")));

        // background.js is what takes a native call no page is listening for.
        var result = await provider.GetRequiredService<WebAppInvoker>().InvokeAsync("hello", "{}");
        Assert.Equal(WebAppInvocationTarget.BackgroundScript, result.Target);
        Assert.Equal("\"from script\"", result.ResultJson);
    }

    [Fact]
    public async Task RunsWithoutAWebViewAndReportsNativeCallsUnhandled()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(o => o.AppId = "bridges-only"), autoStart: false);

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<WebAppInvoker>().InvokeAsync("job:sync", "{}");

        Assert.False(result.Handled);
        Assert.Empty(provider.GetRequiredService<AppDeviceBridgeServer>().Extensions);
    }

    [Fact]
    public void ValidatesWhenTheServerIsCreated()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(), autoStart: false);

        using var provider = services.BuildServiceProvider();
        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<HttpServer>());
        Assert.Contains("AppId", failure.Message);
    }

    /// <summary>
    /// The bridges must not depend on the app's pipeline. An app that never calls UseAuthentication or UseAuthorization —
    /// or calls them after the bridges, or twice — still gets bridges behind their policy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesTheBridgePolicyWhateverThePipeline(bool appUsesAuthorization)
    {
        await using var app = new TestApp();
        var server = app.CreateServer(
            app.BridgeOptions(o => o.AuthorizeBridges(p => p.RequireRole("kiosk"))),
            [new EchoBridge()],
            http =>
            {
                http.AddAuthentication().AddApiKey(k => k.AddKey("kiosk-key", "kiosk", "kiosk"));

                if (appUsesAuthorization)
                    http.Configure(s => s.UseAuthentication().UseAuthorization());
            }
        );
        var origin = await server.StartAsync();

        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(origin, "/_bridge/echo/ping"))).StatusCode);

        using var keyed = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/_bridge/echo/ping"));
        keyed.Headers.Add("X-API-Key", "kiosk-key");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(keyed)).StatusCode);

        // Unknown bridge routes are behind the policy too, so a refused caller cannot map what exists.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(origin, "/_bridge/nothing-here"))).StatusCode);
    }
}
