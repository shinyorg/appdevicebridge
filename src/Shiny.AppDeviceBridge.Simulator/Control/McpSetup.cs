using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Mcp;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>
/// Puts the control surface on the simulator's own server as an MCP endpoint, so an agent drives the same simulator a
/// human is watching in the terminal.
/// <para>
/// Three things stand between the endpoint and anything that should not reach it, and the page under test is one of
/// them. The endpoint is on the origin the page is served from, so a page could otherwise fetch it and rewrite its own
/// device answers — a test that quietly changes its own fixtures. So: the connection must be local and untunneled and
/// name loopback (<see cref="BridgeCallers.IsOnDevice"/>); the request must carry the token printed at startup; and the
/// transport refuses any request carrying an <c>Origin</c> header at all, which every browser sends on the POST that MCP
/// needs. Native MCP clients send none and are unaffected.
/// </para>
/// </summary>
public static class McpSetup
{
    /// <summary>Where the MCP endpoint is mounted, under the simulator's own prefix rather than the bridge prefix.</summary>
    public const string Path = "/_sim/mcp";

    /// <summary>The header the control token is sent in.</summary>
    public const string TokenHeader = "Authorization";

    /// <summary>What an agent is told about this server the moment it connects, before it has called anything.</summary>
    public const string Instructions = """
        This is a Shiny.AppDeviceBridge simulator: it stands in for a phone or desktop app hosting a web page, and serves
        every device bridge (wifi, gps, ble, camera, notifications, …) to that page over HTTP. You decide what each
        bridge answers, so you can put the page in states a real device would take hours to reach.

        Start with get_status and list_bridges. Before setting what a route answers, call get_route and read its
        'sample': that is the generated shape of the bridge's contract, and a value that is not that shape is rejected —
        the simulator will not send the page something a real device could not.

        To test a reaction rather than guess at it: set_route, then wait_for_request for the path the page will call.
        If an event seems not to arrive, check the listener count fire_event returns — zero means the page never
        subscribed. get_traffic shows what the page actually asked for and got.

        Everything you change is logged where the person running the simulator can see it.
        """;

    /// <summary>Adds the MCP server, its tools and the streamable HTTP transport.</summary>
    /// <param name="control">Resolved on the first tool call, once the container is built.</param>
    public static void AddMcp(this IServiceCollection services, Func<SimulatorControl> control)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = "shiny-bridge-sim",
                    Version = typeof(McpSetup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
                };
                o.ServerInstructions = Instructions;
            })
            .WithTools(McpTools.Create(control))

            // Without this the SDK reports every failure as "An error occurred invoking 'set_route'." An agent that
            // cannot see *why* a value was refused has no way to correct it, and the refusals here are the useful part:
            // they say which contract the value should have matched.
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
            {
                try
                {
                    return await next(request, cancellationToken);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or FileNotFoundException or IOException)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));
    }

    /// <summary>Adds the transport, mounts the endpoint, and puts the guard in front of it.</summary>
    /// <remarks>
    /// The guard is middleware on the path rather than an authorization policy on the endpoint. The MCP transport does
    /// support <c>RequireAuthorization</c>, but only a server whose pipeline runs the authorization middleware enforces
    /// it — and this one does not: the bridge server evaluates its own policy inside its guard instead. A policy nothing
    /// evaluates is an endpoint that looks guarded and is not, so the check is written where it plainly runs.
    /// </remarks>
    public static void MapMcp(ShinyHttpServerBuilder http, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Empty AllowedOrigins is the safe default and the one we want: a request carrying Origin came from a page.
        http.AddMcpHttpTransport(o => o.AllowServerToClientStream = true);

        http.Configure(server =>
        {
            server.Use((context, next) => Guard(context, next, token));
            server.MapMcp(Path);
        });
    }

    /// <summary>
    /// Refuses anything reaching the control endpoint that is not an MCP client on this machine holding the token.
    /// Everything else is passed straight through, so this costs one path comparison per request.
    /// </summary>
    static async ValueTask Guard(HttpContext context, RequestDelegate next, string token)
    {
        if (!AppDeviceBridgeServer.IsUnder(context.Request.Path, Path))
        {
            await next(context);
            return;
        }

        // The preflight carries no credentials by definition; the request it precedes is still guarded.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!BridgeCallers.IsOnDevice(context))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "The simulator's control endpoint answers callers on this device only.");
            return;
        }

        if (!HasToken(context, token))
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await Refuse(context, StatusCodes.Status401Unauthorized, "The simulator's control endpoint needs its token, which it printed at startup.");
            return;
        }

        await next(context);
    }

    static ValueTask Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;

        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "control_denied",
                ["message"] = message
            }
        };

        return context.Response.WriteTextAsync(body.ToJsonString(), "application/json; charset=utf-8", context.RequestAborted);
    }

    /// <summary>A token to guard the endpoint with: 24 bytes, url-safe, generated fresh unless one was asked for.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>What to paste into an MCP client, so the person does not have to work out the shape of it.</summary>
    public static string ClientConfig(Uri origin, string token) => $$"""
        {
          "mcpServers": {
            "shiny-bridge-sim": {
              "type": "http",
              "url": "{{new Uri(origin, Path.TrimStart('/'))}}",
              "headers": { "Authorization": "Bearer {{token}}" }
            }
          }
        }
        """;

    static bool HasToken(HttpContext context, string token)
    {
        var sent = context.Request.Headers[TokenHeader].ToString();
        if (sent.Length == 0)
            return false;

        const string bearer = "Bearer ";
        var value = sent.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) ? sent[bearer.Length..] : sent;

        // Fixed-time: the token guards a control plane, and a loopback attacker can make a great many guesses.
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(value.Trim()),
            System.Text.Encoding.UTF8.GetBytes(token)
        );
    }
}
