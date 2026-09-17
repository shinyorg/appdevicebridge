using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The app's own endpoints on the same server as the bridges, secured the app's way — its schemes, its policies, its fallback
/// — with the WebView's session as one more scheme among them.
/// </summary>
public class CustomEndpointTests
{
    const string ReaderKey = "reader-key";
    const string AdminKey = "admin-key";

    /// <summary>The fallback is the app's choice, on the app's server; the bridges do not impose one.</summary>
    [Fact]
    public async Task UsesTheAppsOwnFallbackPolicy()
    {
        await using var fixture = await Fixture.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/orders", key: ReaderKey)).StatusCode);
    }

    /// <summary>The page is just another caller: its session authenticates like any other scheme.</summary>
    [Fact]
    public async Task AcceptsTheWebViewsSessionAsAScheme()
    {
        await using var fixture = await Fixture.StartAsync();

        var response = await fixture.GetAsync("/api/orders", webView: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("orders for webview", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LetsAnEndpointOptOut()
        => Assert.Equal(HttpStatusCode.OK, (await (await Fixture.StartAsync()).GetAsync("/api/health")).StatusCode);

    /// <summary>
    /// The app, the bridges and the WebView host each add authorization on the one builder. Every call has to land — the
    /// bridges' policy beside the app's, not instead of it.
    /// </summary>
    [Fact]
    public async Task AppliesPoliciesFromEveryRegistration()
    {
        await using var fixture = await Fixture.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/api/admin", key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/admin", key: AdminKey)).StatusCode);
    }

    [Fact]
    public async Task CanDemandTheWebViewSpecifically()
    {
        await using var fixture = await Fixture.StartAsync();

        // A perfectly valid admin key is still not the WebView.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/api/page-only", key: AdminKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/page-only", webView: true)).StatusCode);
    }

    /// <summary>Two sets of rules, deliberately: a credential for the app's endpoints is not device access.</summary>
    [Fact]
    public async Task LeavesBridgesBehindTheirOwnPolicy()
    {
        await using var fixture = await Fixture.StartAsync();

        // The fallback policy does not reach the bridges — the WebView's call still goes through.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", webView: true)).StatusCode);

        // And an API key good enough for /api/admin opens nothing on the device.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/_bridge/echo/ping", key: AdminKey)).StatusCode);

        // Nor does a policy of the app's own that happens to share the fallback's opinion.
        Assert.NotEqual(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", key: ReaderKey)).StatusCode);
    }

    /// <summary>The app mapped its endpoints where it wanted them; moving the web app and the bridges leaves them there.</summary>
    [Fact]
    public async Task LeavesTheAppsEndpointsWhereTheAppMappedThem()
    {
        await using var fixture = await Fixture.StartAsync(o => o.BasePath = "/kiosk");

        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/admin", key: AdminKey)).StatusCode);

        // Under the mount point it is the web app's, which wants the WebView's session.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/kiosk/api/health")).StatusCode);
    }

    /// <summary>A generated [Route] class, with its constraints, its [Authorize] and its dependencies from the app's container.</summary>
    [Fact]
    public async Task ServesASourceGeneratedClass()
    {
        await using var fixture = await Fixture.StartAsync();

        var order = await fixture.GetAsync("/api/generated/42", key: ReaderKey);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        Assert.Equal("order 42 from the app container", await order.Content.ReadAsStringAsync());

        // The constraint turns 'abc' away, so it is no endpoint at all — just a path into the web app, which demands
        // the WebView's session as it always has. A caller without one does not learn which paths exist.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/api/generated/abc", key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/api/generated/admin", key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/api/generated/admin", key: AdminKey)).StatusCode);
    }

    [Fact]
    public async Task AnswersAWrongMethodWith405NotTheWebApp()
    {
        await using var fixture = await Fixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(fixture.Host.Origin!, "/api/health"));
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// An endpoint of the app's mapped under the bridge prefix is treated as a bridge: behind the bridge policy, whatever
    /// the app's own authorization says about it. Nothing under the prefix is ever reachable on weaker terms.
    /// </summary>
    [Fact]
    public async Task GuardsAnAppRouteUnderTheBridgePrefixAsABridge()
    {
        await using var fixture = await Fixture.StartAsync(map: server => server.MapGet("/_bridge/mine", ctx => ctx.Response.WriteAsync("mine")));

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/_bridge/mine", key: AdminKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/_bridge/mine", webView: true)).StatusCode);
    }

    [Fact]
    public async Task ServesTheNetworkWithItsOwnAuthorization()
    {
        var lan = FindLanAddress();
        Assert.SkipWhen(lan is null, "No non-loopback IPv4 address on this machine, so a remote request cannot be made.");

        await using var fixture = await Fixture.StartAsync(http: h => h.Options.Address = IPAddress.Any);
        var remote = new Uri($"http://{lan}:{fixture.Host.Origin!.Port}");

        // Their authorization is the gate.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.GetAsync(new Uri(remote, "/api/orders"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync(new Uri(remote, "/api/orders"), key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync(new Uri(remote, "/api/health"))).StatusCode);

        // The WebView's cookie is the device's secret — replayed from the network it authenticates nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.GetAsync(new Uri(remote, "/api/orders"), cookie: fixture.Session.Token)).StatusCode);

        // And the device itself is still off limits.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync(new Uri(remote, "/_bridge/echo/ping"), key: AdminKey)).StatusCode);
    }

    static IPAddress? FindLanAddress()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));

    sealed class Fixture : IAsyncDisposable
    {
        TestApp app = null!;
        CookieContainer cookies = new();

        public WebAppHost Host { get; private set; } = null!;
        public WebAppSession Session { get; private set; } = null!;

        /// <summary>What an app would register on its server: endpoints, schemes and policies, split across calls on purpose.</summary>
        static void Register(ShinyHttpServerBuilder http, Action<HttpServer>? map)
        {
            http.Services.AddSingleton<GeneratedOrderStore>();
            http.Services.AddScoped<GeneratedOrders>();

            http.AddAuthentication().AddApiKey(o => o
                .AddKey(ReaderKey, "reader")
                .AddKey(AdminKey, "admin", "admin")
            );
            http.AddAuthorization(o => o.SetFallbackPolicy(p => p.RequireAuthenticatedUser()));
            http.AddAuthorization(o => o.AddPolicy("unused", p => p.RequireRole("nobody")));
            http.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("admin")));

            http.Configure(server =>
            {
                server.UseAuthentication();
                server.UseAuthorization();

                (map ?? (s =>
                {
                    s.MapGet("/api/orders", ctx => ctx.Response.WriteAsync($"orders for {ctx.User?.Identity?.Name}"));
                    s.MapGet("/api/health", ctx => ctx.Response.WriteAsync("ok")).AllowAnonymous();
                    s.MapGet("/api/admin", ctx => ctx.Response.WriteAsync("admin")).RequireAuthorization("admin");
                    s.MapGet("/api/page-only", ctx => ctx.Response.WriteAsync("page")).RequireAuthorization(WebAppPolicies.Session);
                    s.MapGeneratedOrders();
                }))(server);
            });
        }

        public static async Task<Fixture> StartAsync(
            Action<AppDeviceBridgeOptions>? configure = null,
            Action<HttpServer>? map = null,
            Action<ShinyHttpServerBuilder>? http = null
        )
        {
            var fixture = new Fixture { app = new TestApp() };

            try
            {
                fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
                await fixture.app.StartReleaseServerAsync();

                fixture.Session = new WebAppSession();
                fixture.Host = fixture.app.CreateHost(
                    fixture.app.Options(),
                    fixture.app.BridgeOptions(configure),
                    null,
                    [new EchoBridge()],
                    h =>
                    {
                        Register(h, map);
                        http?.Invoke(h);
                    },
                    fixture.Session
                );

                var start = await fixture.Host.StartAsync();
                using var webView = new HttpClient(new HttpClientHandler { CookieContainer = fixture.cookies });
                await webView.GetAsync(start);

                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Task<HttpResponseMessage> GetAsync(string path, string? key = null, bool webView = false)
            => this.GetAsync(new Uri(this.Host.Origin!, path), key, webView ? this.cookies : null);

        public Task<HttpResponseMessage> GetAsync(Uri uri, string? key = null, string? cookie = null)
        {
            var jar = new CookieContainer();
            if (cookie is not null)
                jar.Add(uri, new Cookie(WebAppSession.CookieName, cookie));

            return this.GetAsync(uri, key, cookie is null ? null : jar);
        }

        async Task<HttpResponseMessage> GetAsync(Uri uri, string? key, CookieContainer? jar)
        {
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = jar ?? new CookieContainer(), UseCookies = true });
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            if (key is not null)
                request.Headers.Add("X-API-Key", key);

            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            if (this.Host is not null)
                await this.Host.DisposeAsync();

            await this.app.DisposeAsync();
        }
    }
}

public sealed class GeneratedOrderStore
{
    public string Describe(int id) => $"order {id} from the app container";
}

[Route("/api/generated")]
public class GeneratedOrders(GeneratedOrderStore store)
{
    [Get("/{id:int}")]
    public string Get(int id) => store.Describe(id);

    [Get("/admin"), Authorize("admin")]
    public string Admin() => "admin";
}
