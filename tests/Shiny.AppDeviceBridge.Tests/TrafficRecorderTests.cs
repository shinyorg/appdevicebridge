using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>What the traffic recorder keeps of each request, what it refuses to keep, and when it keeps nothing at all.</summary>
public class TrafficRecorderTests
{
    [Fact]
    public async Task RecordsTheRequestAndResponse()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/api/hello?name=world");
        Assert.Equal("hello world", await response.Content.ReadAsStringAsync());

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal("GET", exchange.Method);
        Assert.Equal("/api/hello", exchange.Path);
        Assert.Equal("/api/hello?name=world", exchange.Target);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(TrafficOrigin.Device, exchange.Origin);
        Assert.Contains(exchange.RequestHeaders, x => x.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TrafficBodyState.Captured, exchange.ResponseBody.State);
        Assert.Equal("hello world", exchange.ResponseBody.Text);
        Assert.Equal(11, exchange.ResponseBody.ByteCount);
        Assert.Null(exchange.Error);
    }

    /// <summary>The handler still reads the whole body after the recorder has read it.</summary>
    [Fact]
    public async Task KeepsTheRequestBodyAndHandsItOn()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.Client.PostAsync("/api/echo", new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal("{\"a\":1}", await response.Content.ReadAsStringAsync());

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal(TrafficBodyState.Captured, exchange.RequestBody.State);
        Assert.Equal("{\"a\":1}", exchange.RequestBody.Text);
        Assert.Equal("{\"a\":1}", exchange.ResponseBody.Text);
    }

    [Fact]
    public async Task CountsButDoesNotKeepABinaryBody()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/api/binary");
        Assert.Equal(4096, (await response.Content.ReadAsByteArrayAsync()).Length);

        var body = Assert.Single(await fixture.RecordedAsync(1)).ResponseBody;
        Assert.Equal(TrafficBodyState.Binary, body.State);
        Assert.Equal(4096, body.ByteCount);
        Assert.Null(body.Text);
    }

    [Fact]
    public async Task TruncatesTextPastTheCapAndSendsItAllAnyway()
    {
        await using var fixture = await Fixture.StartAsync(o => o.MaxBodyBytes = 10);

        using var response = await fixture.Client.PostAsync("/api/echo", new StringContent(new string('x', 50), Encoding.UTF8, "text/plain"));
        Assert.Equal(50, (await response.Content.ReadAsStringAsync()).Length);

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal(TrafficBodyState.Truncated, exchange.RequestBody.State);
        Assert.Equal(new string('x', 10), exchange.RequestBody.Text);
        Assert.Equal(50, exchange.RequestBody.ByteCount);
        Assert.Equal(TrafficBodyState.Truncated, exchange.ResponseBody.State);
        Assert.Equal(50, exchange.ResponseBody.ByteCount);
    }

    [Fact]
    public async Task RedactsARequestBodyTheAppMarks()
    {
        await using var fixture = await Fixture.StartAsync(o => o.RedactRequestBody = ctx => ctx.Request.Path == "/api/echo");

        using var response = await fixture.Client.PostAsync("/api/echo", new StringContent("password=hunter2", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal("password=hunter2", await response.Content.ReadAsStringAsync());

        var body = Assert.Single(await fixture.RecordedAsync(1)).RequestBody;
        Assert.Equal(TrafficBodyState.Redacted, body.State);
        Assert.Null(body.Text);
        Assert.Equal(16, body.ByteCount);
    }

    /// <summary>
    /// The recorder sits ahead of the bridge server's own checks. A refused bridge call is usually the thing being looked for,
    /// and a recorder that only saw what got through would never show one.
    /// </summary>
    [Fact]
    public async Task RecordsABridgeCallTheGuardRefuses()
    {
        await using var fixture = await Fixture.StartAsync(webView: true);

        using var response = await fixture.Client.GetAsync("/_bridge/echo/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal("/_bridge/echo/ping", exchange.Path);
        Assert.Equal(401, exchange.StatusCode);
    }

    [Fact]
    public async Task RecordsAFailingBridgeCall()
    {
        await using var fixture = await Fixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/_bridge/echo/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal(500, exchange.StatusCode);
        Assert.Contains("bridge_failed", exchange.ResponseBody.Text);
    }

    /// <summary>
    /// The launch token rides in the start URL, and the session cookie in every request after it. Both are the device's
    /// secret; neither may be readable from the recorder, nor from the text the detail page copies to the clipboard.
    /// </summary>
    [Fact]
    public async Task NeverKeepsTheLaunchTokenOrTheSessionCookie()
    {
        await using var fixture = await Fixture.StartAsync(webView: true);

        var start = await fixture.Host!.StartAsync();
        using (await fixture.Client.GetAsync(start)) { }
        using (await fixture.Client.GetAsync("/_bridge/echo/ping")) { }

        var exchanges = await fixture.RecordedAsync(3);
        Assert.Equal(3, exchanges.Count); // the start URL, its redirect to the page, and the bridge call

        var token = fixture.Session!.Token;
        foreach (var exchange in exchanges)
            Assert.DoesNotContain(token, TrafficText.Describe(exchange));

        var launch = exchanges.Single(x => x.Path.EndsWith("/start", StringComparison.Ordinal));
        Assert.Equal("?token=(redacted)", launch.QueryString);
        Assert.Equal("(redacted)", launch.ResponseHeaders.Single(x => x.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value);

        var call = exchanges.Single(x => x.Path == "/_bridge/echo/ping");
        Assert.Equal(204, call.StatusCode);
        Assert.Equal("(redacted)", call.RequestHeaders.Single(x => x.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value);
    }

    [Fact]
    public async Task RedactsCredentialHeaders()
    {
        await using var fixture = await Fixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/hello");
        request.Headers.Add("Authorization", "Bearer secret");
        using (await fixture.Client.SendAsync(request)) { }

        var headers = Assert.Single(await fixture.RecordedAsync(1)).RequestHeaders;
        Assert.Equal("(redacted)", headers.Single(x => x.Name == "Authorization").Value);
    }

    [Fact]
    public async Task ShowsCredentialsWhenTheAppClearsTheRedactions()
    {
        await using var fixture = await Fixture.StartAsync(o =>
        {
            o.RedactedHeaders.Clear();
            o.RedactedQueryParameters.Clear();
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/hello?token=abc");
        request.Headers.Add("Authorization", "Bearer secret");
        using (await fixture.Client.SendAsync(request)) { }

        var exchange = Assert.Single(await fixture.RecordedAsync(1));
        Assert.Equal("?token=abc", exchange.QueryString);
        Assert.Equal("Bearer secret", exchange.RequestHeaders.Single(x => x.Name == "Authorization").Value);
    }

    [Theory]
    [InlineData("?token=abc", "?token=(redacted)")]
    [InlineData("?a=1&access_token=abc&b=2", "?a=1&access_token=(redacted)&b=2")]
    [InlineData("?TOKEN=abc", "?TOKEN=(redacted)")]
    [InlineData("?tokens=abc", "?tokens=abc")]
    [InlineData("?token", "?token")]
    [InlineData("", null)]
    public void RedactsQueryParametersByName(string query, string? expected)
        => Assert.Equal(expected, new TrafficRecorder(new TrafficRecorderOptions()).RedactQuery(query));

    [Fact]
    public async Task KeepsOnlyTheNewest()
    {
        await using var fixture = await Fixture.StartAsync(o => o.MaxExchanges = 2);

        // Each waited for in turn, so they are added in the order they were made - which is what the order held is about.
        foreach (var name in new[] { "a", "b", "c" })
        {
            using (await fixture.Client.GetAsync($"/api/hello?name={name}")) { }
            await fixture.RecordedAsync(x => x.QueryString == $"?name={name}");
        }

        Assert.Equal(["?name=c", "?name=b"], fixture.Recorder.Snapshot().Select(x => x.QueryString));
    }

    [Fact]
    public async Task RecordsNothingWhileOff()
    {
        await using var fixture = await Fixture.StartAsync(o => o.RecordOnStart = false);

        using (await fixture.Client.GetAsync("/api/hello")) { }
        Assert.Empty(fixture.Recorder.Snapshot());

        fixture.Recorder.IsRecording = true;
        using (await fixture.Client.GetAsync("/api/hello")) { }
        Assert.Single(await fixture.RecordedAsync(1));
    }

    /// <summary>What was recorded is every header and body that crossed the server; off means gone.</summary>
    [Fact]
    public async Task SwitchingOffThrowsAwayWhatWasRecorded()
    {
        await using var fixture = await Fixture.StartAsync();
        using (await fixture.Client.GetAsync("/api/hello")) { }

        // Waited for before counting: an exchange still being added would raise Changed once more.
        await fixture.RecordedAsync(1);

        var changes = 0;
        fixture.Recorder.Changed += (_, _) => changes++;
        fixture.Recorder.IsRecording = false;

        Assert.Empty(fixture.Recorder.Snapshot());
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ClearsAndFindsById()
    {
        await using var fixture = await Fixture.StartAsync();
        using (await fixture.Client.GetAsync("/api/hello")) { }

        var id = Assert.Single(await fixture.RecordedAsync(1)).Id;
        Assert.NotNull(fixture.Recorder.Find(id));

        fixture.Recorder.Clear();
        Assert.Null(fixture.Recorder.Find(id));
        Assert.True(fixture.Recorder.IsRecording);
    }

    [Fact]
    public async Task IsNotInThePipelineUnlessRegistered()
    {
        await using var app = new TestApp();
        var server = app.CreateServer(app.BridgeOptions(), [new EchoBridge()]);

        Assert.Null(server.Http.Services!.GetService<TrafficRecorder>());
    }

    /// <summary>The MAUI registration, end to end: every call configures the one recorder, and it is in the pipeline.</summary>
    [Fact]
    public async Task UseTrafficMonitorRecordsTheServer()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId), startWithApp: false);
        builder.UseTrafficMonitor(o => o.MaxExchanges = 5);
        builder.UseTrafficMonitor(o => o.MaxBodyBytes = 7);
        builder.Services.AddShinyHttpServer(http => http.Options.Port = 0, autoStart: false);

        await using var services = builder.Services.BuildServiceProvider();
        var recorder = services.GetRequiredService<TrafficRecorder>();
        Assert.Equal(5, recorder.Options.MaxExchanges);
        Assert.Equal(7, recorder.Options.MaxBodyBytes);

        var server = services.GetRequiredService<AppDeviceBridgeServer>();
        var origin = await server.StartAsync();

        try
        {
            using var client = new HttpClient();
            using (await client.GetAsync(new Uri(origin, "/_bridge/host"))) { }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.Equal("/_bridge/host", Assert.Single(await recorder.WaitUntilAsync(x => x.Count >= 1, timeout.Token)).Path);
        }
        finally
        {
            await server.Http.StopAsync();
        }
    }

    [Theory]
    [InlineData("hello", 2)]
    [InlineData("POST", 1)]
    [InlineData("40", 1)]
    [InlineData("  ", 3)]
    public void FiltersByPathMethodOrStatus(string filter, int expected)
    {
        TrafficExchange[] exchanges =
        [
            Exchange("GET", "/api/hello", 200),
            Exchange("POST", "/api/hello", 201),
            Exchange("GET", "/missing", 404)
        ];

        Assert.Equal(expected, TrafficText.Filter(exchanges, filter).Count);
    }

    static TrafficExchange Exchange(string method, string path, int status) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StartedOn = DateTimeOffset.UtcNow,
        Method = method,
        Path = path,
        Origin = TrafficOrigin.Device,
        RemoteAddress = "127.0.0.1:1",
        RequestHeaders = [],
        StatusCode = status
    };

    sealed class Fixture : IAsyncDisposable
    {
        readonly TestApp app = new();

        // A recorder that never records fails the test rather than hanging it.
        readonly CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        public TrafficRecorder Recorder { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public WebAppHost? Host { get; private set; }
        public WebAppSession? Session { get; private set; }

        /// <summary>
        /// What is held once at least <paramref name="count"/> exchanges are. The recorder adds an exchange after its response
        /// has gone out, so a test reading it straight after the request has to wait for it.
        /// </summary>
        public Task<IReadOnlyList<TrafficExchange>> RecordedAsync(int count)
            => this.Recorder.WaitUntilAsync(x => x.Count >= count, this.timeout.Token);

        /// <summary>The newest exchange matching <paramref name="match"/>, once there is one.</summary>
        public Task<TrafficExchange> RecordedAsync(Func<TrafficExchange, bool> match)
            => this.Recorder.WaitForAsync(match, this.timeout.Token);

        public static async Task<Fixture> StartAsync(Action<TrafficRecorderOptions>? configure = null, bool webView = false)
        {
            var fixture = new Fixture();

            void Register(ShinyHttpServerBuilder http)
            {
                http.AddTrafficRecorder(configure);
                http.Configure(server =>
                {
                    server.MapGet("/api/hello", ctx => ctx.Response.WriteAsync($"hello {ctx.Request.Query["name"]}".TrimEnd()));
                    server.MapPost("/api/echo", async ctx =>
                    {
                        using var reader = new StreamReader(ctx.Request.Body);
                        var body = await reader.ReadToEndAsync();
                        ctx.Response.ContentType = ctx.Request.ContentType;
                        await ctx.Response.WriteAsync(body);
                    });
                    server.MapGet("/api/binary", async ctx =>
                    {
                        ctx.Response.ContentType = "application/octet-stream";
                        await ctx.Response.Body.WriteAsync(new byte[4096]);
                    });
                });
            }

            try
            {
                AppDeviceBridgeServer server;
                if (webView)
                {
                    fixture.app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
                    await fixture.app.StartReleaseServerAsync();

                    fixture.Session = new WebAppSession();
                    fixture.Host = fixture.app.CreateHost(fixture.app.Options(), fixture.app.BridgeOptions(), null, [new EchoBridge()], Register, fixture.Session);
                    await fixture.Host.StartAsync();
                    server = fixture.Host.Server;
                }
                else
                {
                    server = fixture.app.CreateServer(fixture.app.BridgeOptions(), [new EchoBridge()], Register);
                    await server.StartAsync();
                }

                fixture.Recorder = server.Http.Services!.GetRequiredService<TrafficRecorder>();
                fixture.timeout.CancelAfter(TimeSpan.FromSeconds(10));
                fixture.Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
                {
                    BaseAddress = server.Origin
                };
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.Client?.Dispose();
            this.timeout.Dispose();

            if (this.Host is not null)
                await this.Host.DisposeAsync();

            await this.app.DisposeAsync();
        }
    }
}
