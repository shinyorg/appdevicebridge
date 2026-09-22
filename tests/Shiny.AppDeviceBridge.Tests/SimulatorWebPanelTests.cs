using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Shiny.AppDeviceBridge.Simulator.Control;
using Shiny.AppDeviceBridge.Simulator.Hosting;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// <c>--web</c>: the control panel in a browser. That it drives the simulator as the TUI and an agent do, and — like the
/// MCP endpoint, which it sits beside — that nothing without its token can, the page it is serving least of all.
/// </summary>
public class SimulatorWebPanelTests : IAsyncLifetime
{
    const string Token = "test-panel-token";

    SimulatorHost host = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Web = true, WebToken = Token, Platform = "ios" });
        var origin = await this.host.StartAsync(TestContext.Current.CancellationToken);
        this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin };
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    [Fact]
    public async Task The_panel_is_served_with_its_token_in_the_fragment()
    {
        var url = this.host.PanelUrl!;
        Assert.Equal(WebPanel.Path, url.AbsolutePath);
        Assert.Equal($"#token={Token}", url.Fragment);

        var response = await this.http.GetAsync(WebPanel.Path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("shiny-bridge-sim", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The page under test shares this origin; it must not be able to frame the panel and read it.
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Without_the_trailing_slash_it_redirects_so_the_api_resolves()
    {
        var response = await this.http.GetAsync("/_sim", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(WebPanel.Path, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_route_set_from_the_panel_is_what_the_page_gets()
    {
        var response = await this.Call("set_route", """{"bridge":"wifi","key":"GET current","value":{"interfaceName":"en0","ssid":"From the panel"}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answered = await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Contains("From the panel", answered);

        // The log says who did it, so a change from the panel is not mistaken for an agent's.
        Assert.Contains(this.host.Control.GetActivity(), x => x.Text.StartsWith("panel set wifi/current", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_come_back_as_the_control_contracts()
    {
        var status = await this.Json("get_status");
        Assert.Equal("ios", status["platform"]!.GetValue<string>());

        var bridge = await this.Json("describe_bridge", """{"bridge":"wifi"}""");
        Assert.Contains(bridge["routes"]!.AsArray(), x => x!["key"]!.GetValue<string>() == "GET current");
    }

    [Fact]
    public async Task A_bad_value_is_refused_with_a_reason_the_panel_can_show()
    {
        var response = await this.Call("set_route", """{"bridge":"wifi","key":"GET current","value":{"ssid":42}}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WifiNetworkInfo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_operation_that_returns_nothing_answers_204()
    {
        var response = await this.Call("clear_traffic");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_operation_is_404_and_a_get_is_405()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await this.Call("drop_tables")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{WebPanel.ApiPath}/get_status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        var get = await this.http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task Without_the_token_nothing_gets_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await this.Call("get_status", authorization: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await this.Call("get_status", authorization: "Bearer not-the-token")).StatusCode);
    }

    [Fact]
    public async Task A_page_on_another_site_is_refused_even_holding_the_token()
    {
        var response = await this.Call("get_status", origin: "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_panel_on_this_origin_is_admitted()
    {
        // The browser sends Origin on the panel's POSTs; this origin's, with the token, is the panel itself.
        var response = await this.Call("get_status", origin: this.http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_panel_polling_does_not_crowd_the_page_out_of_the_traffic()
    {
        await this.Call("clear_traffic");
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        await this.host.Traffic.WaitForAsync(x => x.Path.Contains("wifi") && x.StatusCode != 0, TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
            await this.Call("get_status");

        await this.http.GetAsync(WebPanel.Path, TestContext.Current.CancellationToken);

        var recorded = this.host.Traffic.Snapshot();
        Assert.Contains(recorded, x => x.Path == "/_bridge/wifi/current");
        Assert.DoesNotContain(recorded, x => x.Path.StartsWith("/_sim", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_control_operation_is_something_the_panel_can_call()
    {
        var operations = typeof(SimulatorControl)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => !x.IsSpecialName)
            .Select(x => ToSnake(x.Name.EndsWith("Async", StringComparison.Ordinal) ? x.Name[..^"Async".Length] : x.Name))
            .Distinct()
            .ToList();

        Assert.Empty(operations.Where(x => !WebPanel.OperationNames.Contains(x)));

        // And the other way: the panel offers nothing an agent cannot also do.
        var tools = McpTools.Create(() => this.host.Control).Select(x => x.ProtocolTool.Name).ToHashSet();
        Assert.Empty(WebPanel.OperationNames.Where(x => !tools.Contains(x)));
    }

    [Fact]
    public async Task It_is_not_served_at_all_unless_it_was_asked_for()
    {
        await using var quiet = SimulatorHost.Create(new SimulatorOptions { Port = 0 });
        using var client = new HttpClient { BaseAddress = await quiet.StartAsync(TestContext.Current.CancellationToken) };

        Assert.Null(quiet.WebToken);
        Assert.Null(quiet.PanelUrl);

        var response = await client.PostAsync($"{WebPanel.ApiPath}/get_status", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_blazor_app_fallback_does_not_swallow_the_panel()
    {
        var app = Path.Combine(Path.GetTempPath(), "shiny-sim-panel-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(app, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(app, "index.html"), "<html>the app</html>", TestContext.Current.CancellationToken);

        try
        {
            await using var served = SimulatorHost.Create(new SimulatorOptions { Port = 0, Web = true, AppDirectory = app });
            using var client = new HttpClient { BaseAddress = await served.StartAsync(TestContext.Current.CancellationToken) };

            Assert.Contains("the app", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
            Assert.Contains("shiny-bridge-sim", await client.GetStringAsync(WebPanel.Path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(app, recursive: true);
        }
    }

    [Fact]
    public async Task With_a_dev_server_the_panel_is_answered_here_not_forwarded()
    {
        // Nothing listens on the dev server's port: a request forwarded there comes back 502.
        await using var proxied = SimulatorHost.Create(new SimulatorOptions { Port = 0, Web = true, WebToken = Token, DevServer = new Uri("http://127.0.0.1:1/") });
        using var client = new HttpClient { BaseAddress = await proxied.StartAsync(TestContext.Current.CancellationToken) };

        Assert.Contains("shiny-bridge-sim", await client.GetStringAsync(WebPanel.Path, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public void Parses_the_web_options()
    {
        var options = SimulatorOptions.Parse(["--web", "--web-token", "abc", "--mcp"]);
        Assert.True(options.Web);
        Assert.Equal("abc", options.WebToken);
        Assert.True(options.Mcp);
    }

    [Theory]
    [InlineData("--web-token", "abc")]
    [InlineData("--web", "--mcp-stdio")]
    public void Refuses_web_options_that_do_not_go_together(string first, string second)
        => Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse([first, second]));

    async Task<JsonNode> Json(string operation, string? body = null)
    {
        var response = await this.Call(operation, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    async Task<HttpResponseMessage> Call(string operation, string? body = null, string? authorization = $"Bearer {Token}", string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WebPanel.ApiPath}/{operation}")
        {
            Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json")
        };

        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);

        return await this.http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    static string ToSnake(string name)
    {
        var text = new StringBuilder();
        foreach (var letter in name)
        {
            if (Char.IsUpper(letter) && text.Length > 0)
                text.Append('_');

            text.Append(Char.ToLowerInvariant(letter));
        }

        return text.ToString();
    }
}
