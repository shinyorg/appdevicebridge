using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The app's own endpoints on the host's server, and the authentication that comes with them.</summary>
public class CustomEndpointTests
{
    const string ReaderKey = "reader-key";
    const string AdminKey = "admin-key";

    [Fact]
    public async Task NeedsAnAuthenticatedCallerByDefault()
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
    /// Shiny.Net.HttpServer keeps only the first AddAuthorization and drops the rest silently, which surfaces as a
    /// 500 the first time the missing policy is asked for. Every call here has to land.
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
    public async Task LeavesBridgesBehindTheirOwnGuard()
    {
        await using var fixture = await Fixture.StartAsync();

        // The fallback policy does not reach the bridges — the WebView's call still goes through.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.GetAsync("/_bridge/echo/ping", webView: true)).StatusCode);

        // And an API key good enough for /api/admin opens nothing on the device.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/_bridge/echo/ping", key: AdminKey)).StatusCode);
    }

    [Fact]
    public async Task MovesEndpointsUnderTheBasePath()
    {
        await using var fixture = await Fixture.StartAsync(o => o.BasePath = "/kiosk");

        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/kiosk/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/kiosk/api/admin", key: AdminKey)).StatusCode);

        // Moved, not copied.
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.GetAsync("/api/health")).StatusCode);
    }

    /// <summary>
    /// A generated [Route] class can only map at the template it was written with, so it is the case that most
    /// needs the move — with its constraints, its [Authorize] and its dependencies from the app's container intact.
    /// </summary>
    [Fact]
    public async Task MountsASourceGeneratedClass()
    {
        await using var fixture = await Fixture.StartAsync(o => o.BasePath = "/kiosk");

        var order = await fixture.GetAsync("/kiosk/api/generated/42", key: ReaderKey);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        Assert.Equal("order 42 from the app container", await order.Content.ReadAsStringAsync());

        // The constraint turns 'abc' away, so it is no endpoint at all — just a path into the web app, which demands
        // the WebView's session as it always has. A caller without one does not learn which paths exist.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/kiosk/api/generated/abc", key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.GetAsync("/kiosk/api/generated/admin", key: ReaderKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.GetAsync("/kiosk/api/generated/admin", key: AdminKey)).StatusCode);
    }

    [Fact]
    public async Task AnswersAWrongMethodWith405NotTheWebApp()
    {
        await using var fixture = await Fixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(fixture.Host.Origin!, "/api/health"));
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("/_bridge/mine")]
    [InlineData("/_host/mine")]
    public async Task RefusesAnEndpointOverWhatTheHostReserves(string path)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await Fixture.StartAsync(map: server => server.MapGet(path, ctx => ctx.Response.WriteAsync("x")));
        });

        Assert.Contains("reserves", failure.Message);
    }

    /// <summary>
    /// The host's security lives in a container of its own. Shiny.Net.HttpServer registers an HttpServer and a
    /// first-wins AuthorizationOptions when asked for auth; doing that in the app's container would change an app
    /// that runs its own server.
    /// </summary>
    [Fact]
    public void LeavesTheAppsContainerAlone()
    {
        var services = new ServiceCollection();
        Fixture.Register(services);

        Assert.DoesNotContain(services, x => x.ServiceType == typeof(HttpServer));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(AuthorizationOptions));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IAuthenticationHandler));
    }

    [Fact]
    public async Task ServesTheNetworkWithItsOwnAuthorization()
    {
        var lan = FindLanAddress();
        Assert.SkipWhen(lan is null, "No non-loopback IPv4 address on this machine, so a remote request cannot be made.");

        await using var fixture = await Fixture.StartAsync(o => o.RemoteAccess.Enabled = true);
        var remote = new Uri($"http://{lan}:{fixture.Host.Origin!.Port}");

        // No allowlist for these: their authorization is the gate.
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
        ServiceProvider services = null!;
        CookieContainer cookies = new();

        public WebAppHost Host { get; private set; } = null!;
        public WebAppSession Session { get; private set; } = null!;

        /// <summary>What an app would register: endpoints, schemes and policies, split across calls on purpose.</summary>
        public static void Register(IServiceCollection services, Action<HttpServer>? map = null)
        {
            services.AddSingleton<GeneratedOrderStore>();
            services.AddScoped<GeneratedOrders>();

            services.AddWebAppEndpoints(map ?? (server =>
            {
                server.MapGet("/api/orders", ctx => ctx.Response.WriteAsync($"orders for {ctx.User?.Identity?.Name}"));
                server.MapGet("/api/health", ctx => ctx.Response.WriteAsync("ok")).AllowAnonymous();
                server.MapGet("/api/admin", ctx => ctx.Response.WriteAsync("admin")).RequireAuthorization("admin");
                server.MapGet("/api/page-only", ctx => ctx.Response.WriteAsync("page")).RequireAuthorization(WebAppPolicies.Session);
                server.MapGeneratedOrders();
            }));

            services.AddWebAppAuthentication(auth => auth.AddApiKey(o => o
                .AddKey(ReaderKey, "reader")
                .AddKey(AdminKey, "admin", "admin")
            ));

            services.AddWebAppAuthorization(o => o.AddPolicy("unused", p => p.RequireRole("nobody")));
            services.AddWebAppAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("admin")));
        }

        public static async Task<Fixture> StartAsync(Action<WebAppHostOptions>? configure = null, Action<HttpServer>? map = null)
        {
            var fixture = new Fixture { app = new TestApp() };

            try
            {
                fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
                await fixture.app.StartReleaseServerAsync();

                var options = fixture.app.Options();
                configure?.Invoke(options);

                var services = new ServiceCollection();
                Register(services, map);
                fixture.services = services.BuildServiceProvider();

                fixture.Session = new WebAppSession();
                fixture.Host = new WebAppHost(options, fixture.Session, new WebAppEventHub(), [new EchoBridge()], services: fixture.services);

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

            if (this.services is not null)
                await this.services.DisposeAsync();

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
