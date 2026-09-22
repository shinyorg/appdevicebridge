using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Shiny.AppDeviceBridge.Simulator.Control;
using Shiny.AppDeviceBridge.Simulator.Hosting;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The MCP control endpoint: that an agent can reach it and drive the simulator through it, and — as much of the point —
/// that nothing else can, the page it is serving least of all.
/// </summary>
public class SimulatorMcpTests : IAsyncLifetime
{
    const string Token = "test-control-token";

    SimulatorHost host = null!;
    HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        this.host = SimulatorHost.Create(new SimulatorOptions { Port = 0, Mcp = true, McpToken = Token, Platform = "ios" });
        var origin = await this.host.StartAsync(TestContext.Current.CancellationToken);
        this.http = new HttpClient { BaseAddress = origin };
    }

    public async ValueTask DisposeAsync()
    {
        this.http.Dispose();
        await this.host.DisposeAsync();
    }

    async Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = this.host.McpEndpoint!,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" }
        });

        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_agent_sees_the_tools_and_is_told_what_this_is()
    {
        await using var client = await this.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var names = tools.Select(x => x.Name).ToList();

        Assert.Contains("get_status", names);
        Assert.Contains("set_route", names);
        Assert.Contains("fire_event", names);
        Assert.Contains("wait_for_request", names);

        // Every tool has to carry its own description: it is the only documentation an agent ever gets.
        Assert.All(tools, x => Assert.False(String.IsNullOrWhiteSpace(x.Description)));

        Assert.Contains("simulator", client.ServerInstructions ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_route_set_over_mcp_is_what_the_page_gets()
    {
        await using var client = await this.ConnectAsync();

        await client.CallToolAsync(
            "set_route",
            new Dictionary<string, object?>
            {
                ["bridge"] = "wifi",
                ["key"] = "GET current",
                ["value"] = JsonSerializer.Deserialize<JsonElement>("""{"interfaceName":"en0","ssid":"Over MCP"}""")
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var answered = await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        Assert.Contains("Over MCP", answered);
    }

    [Fact]
    public async Task A_bad_value_comes_back_as_an_error_the_agent_can_read()
    {
        await using var client = await this.ConnectAsync();

        var result = await client.CallToolAsync(
            "set_route",
            new Dictionary<string, object?>
            {
                ["bridge"] = "wifi",
                ["key"] = "GET current",
                ["value"] = JsonSerializer.Deserialize<JsonElement>("""{"ssid":42}""")
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("WifiNetworkInfo", String.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(x => x.Text)));
    }

    [Fact]
    public async Task Reading_traffic_over_mcp_shows_what_the_page_did()
    {
        await this.http.GetStringAsync("/_bridge/wifi/current", TestContext.Current.CancellationToken);
        await this.host.Traffic.WaitForAsync(x => x.Path.Contains("wifi") && x.StatusCode != 0, TestContext.Current.CancellationToken);

        await using var client = await this.ConnectAsync();
        var result = await client.CallToolAsync(
            "get_traffic",
            new Dictionary<string, object?> { ["match"] = "wifi" },
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("wifi", String.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(x => x.Text)));
    }

    [Fact]
    public async Task Without_the_token_nothing_gets_in()
    {
        var response = await this.Post(null, null);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"expected a refusal, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_wrong_token_gets_in_no_further()
    {
        var response = await this.Post("Bearer not-the-token", null);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"expected a refusal, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_page_it_is_serving_cannot_drive_it()
    {
        // The endpoint sits on the origin the page is served from. A page that fetched it could rewrite the very device
        // answers it is being tested against — so a request carrying any Origin at all is refused, token or not.
        var response = await this.Post($"Bearer {Token}", this.http.BaseAddress!.ToString().TrimEnd('/'));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task It_is_not_served_at_all_unless_it_was_asked_for()
    {
        await using var quiet = SimulatorHost.Create(new SimulatorOptions { Port = 0 });
        using var client = new HttpClient { BaseAddress = await quiet.StartAsync(TestContext.Current.CancellationToken) };

        Assert.Null(quiet.McpToken);
        Assert.Null(quiet.McpEndpoint);

        var response = await client.PostAsync(McpSetup.Path, new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_a_dev_server_the_endpoint_is_answered_here_not_forwarded()
    {
        // Nothing listens on the dev server's port: a request forwarded there comes back 502.
        await using var proxied = SimulatorHost.Create(new SimulatorOptions { Port = 0, Mcp = true, McpToken = Token, DevServer = new Uri("http://127.0.0.1:1/") });
        await proxied.StartAsync(TestContext.Current.CancellationToken);

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = proxied.McpEndpoint!,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" }
            }),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, x => x.Name == "get_status");
    }

    [Fact]
    public void Every_control_operation_is_something_an_agent_can_call()
    {
        // The surface is only useful if it is whole: a new operation on the control that no tool exposes is invisible to
        // an agent, and this is what says so.
        var tools = McpTools.Create(() => this.host.Control).Select(x => x.ProtocolTool.Name).ToList();

        var operations = typeof(SimulatorControl)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => !x.IsSpecialName)
            .Select(x => x.Name.EndsWith("Async", StringComparison.Ordinal) ? x.Name[..^"Async".Length] : x.Name)
            .Distinct()
            .ToList();

        var missing = operations.Where(op => !tools.Contains(ToSnake(op), StringComparer.Ordinal)).ToList();
        Assert.Empty(missing);
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

    async Task<HttpResponseMessage> Post(string? authorization, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, this.host.McpEndpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Encoding.UTF8,
                "application/json"
            )
        };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);

        return await this.http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
