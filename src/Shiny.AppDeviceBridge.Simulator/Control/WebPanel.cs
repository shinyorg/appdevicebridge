using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>
/// The simulator's control surface in a browser: <c>--web</c> serves a panel at <see cref="Path"/> in place of the TUI,
/// and every <see cref="SimulatorControl"/> operation at <c>POST /_sim/api/{operation}</c>, named as the MCP tools are
/// and taking the same arguments as one JSON object — so the panel, a script with curl and an agent all drive the same
/// thing.
/// <para>
/// The panel sits on the origin the page under test is served from, so it is guarded as the MCP endpoint is: the caller
/// must be on this machine (<see cref="BridgeCallers.IsOnDevice"/>) and the API needs the token printed at startup. The
/// browser has to send an <c>Origin</c> on the panel's own POSTs, so unlike MCP a request carrying one is admitted — but
/// only this origin's, and never without the token. The panel is handed the token in its URL fragment, which no request
/// carries, and keeps it in its own tab's session storage, which a page in another tab cannot read.
/// </para>
/// </summary>
public static class WebPanel
{
    /// <summary>The simulator's own prefix: the panel, its API and the MCP endpoint are all under it.</summary>
    public const string ControlPrefix = "/_sim";

    /// <summary>Where the panel is served.</summary>
    public const string Path = "/_sim/";

    /// <summary>Where its operations are: <c>POST /_sim/api/set_route</c>.</summary>
    public const string ApiPath = "/_sim/api";

    /// <summary>The panel's address with its token, as printed at startup.</summary>
    public static Uri Url(Uri origin, string token) => new(origin, $"{Path.TrimStart('/')}#token={Uri.EscapeDataString(token)}");

    delegate Task<JsonNode?> Operation(SimulatorControl control, JsonObject args, CancellationToken cancellationToken);

    static readonly ControlJsonContext Json = ControlJsonContext.Default;

    /// <summary>
    /// Every operation, written out rather than found by reflection — the trim analyzers stay quiet, and the list of what
    /// the panel can do is in one place. A test fails when <see cref="SimulatorControl"/> gains one this does not have.
    /// </summary>
    static readonly Dictionary<string, Operation> Operations = new(StringComparer.Ordinal)
    {
        ["get_status"] = Sync(c => To(c.GetStatus(), Json.SimulatorStatus)),
        ["list_bridges"] = Sync(c => To(c.ListBridges(), Json.IReadOnlyListBridgeSummary)),
        ["describe_bridge"] = Sync((c, a) => To(c.DescribeBridge(Required(a, "bridge")), Json.BridgeDetail)),
        ["get_route"] = Sync((c, a) => To(c.GetRoute(Required(a, "bridge"), Required(a, "key")), Json.RouteDetail)),
        ["set_route"] = Sync((c, a) => To(
            c.SetRoute(
                Required(a, "bridge"),
                Required(a, "key"),
                Optional(a, "mode"),
                a["value"]?.DeepClone(),
                Int(a, "status"),
                Optional(a, "code"),
                Optional(a, "message"),
                Int(a, "delayMs"),
                Optional(a, "file")
            ),
            Json.RouteDetail
        )),
        ["set_route_sequence"] = Sync((c, a) => To(
            c.SetRouteSequence(
                Required(a, "bridge"),
                Required(a, "key"),
                a["values"] is JsonArray values ? [.. values.Select(x => x?.DeepClone())] : throw new ArgumentException("'values' must be an array."),
                Int(a, "delayMs")
            ),
            Json.RouteDetail
        )),
        ["reset_route"] = Sync((c, a) => To(c.ResetRoute(Required(a, "bridge"), Required(a, "key")), Json.RouteDetail)),
        ["set_bridge_supported"] = Sync((c, a) => To(c.SetBridgeSupported(Required(a, "bridge"), Bool(a, "supported") ?? throw Missing("supported")), Json.BridgeSummary)),
        ["set_platform"] = Sync((c, a) => To(c.SetPlatform(Required(a, "platform")), Json.SimulatorStatus)),
        ["set_sticky_writes"] = Sync((c, a) => To(c.SetStickyWrites(Bool(a, "on") ?? throw Missing("on")), Json.SimulatorStatus)),
        ["fire_event"] = Sync((c, a) => To(c.FireEvent(Required(a, "name"), a["payload"]?.DeepClone()), Json.FireResult)),
        ["set_event_payload"] = Sync((c, a) => To(c.SetEventPayload(Required(a, "name"), a["payload"]?.DeepClone() ?? throw Missing("payload")), Json.EventDetail)),
        ["list_trails"] = Sync(c => To(c.ListTrails(), Json.IReadOnlyListTrailStatus)),
        ["load_trail"] = Sync((c, a) => To(c.LoadTrail(a["trail"]?.DeepClone() ?? throw Missing("trail")), Json.TrailStatus)),
        ["load_trail_file"] = Sync((c, a) => To(c.LoadTrailFile(Required(a, "path")), Json.TrailStatus)),
        ["play_trail"] = Sync((c, a) => To(c.PlayTrail(Required(a, "name"), Double(a, "speed"), Bool(a, "loop")), Json.TrailStatus)),
        ["pause_trail"] = Sync((c, a) => To(c.PauseTrail(Required(a, "name")), Json.TrailStatus)),
        ["stop_trail"] = Sync((c, a) => To(c.StopTrail(Required(a, "name")), Json.TrailStatus)),
        ["remove_trail"] = Sync((c, a) =>
        {
            c.RemoveTrail(Required(a, "name"));
            return null;
        }),
        ["apply_scenario"] = Sync((c, a) => To(c.ApplyScenario(a["scenario"]?.DeepClone() ?? throw Missing("scenario")), Json.IReadOnlyListString)),
        ["apply_scenario_file"] = Sync((c, a) => To(c.ApplyScenarioFile(Required(a, "path")), Json.IReadOnlyListString)),
        ["capture_scenario"] = Sync(c => c.CaptureScenario()),
        ["get_traffic"] = Sync((c, a) => To(c.GetTraffic(Optional(a, "match"), Int(a, "limit") ?? 50, Bool(a, "bodies") ?? false), Json.IReadOnlyListTrafficEntry)),
        ["clear_traffic"] = Sync(c =>
        {
            c.ClearTraffic();
            return null;
        }),
        ["set_recording"] = Sync((c, a) => JsonValue.Create(c.SetRecording(Bool(a, "on") ?? throw Missing("on")))),
        ["get_activity"] = Sync((c, a) => To(c.GetActivity(Int(a, "limit") ?? 50), Json.IReadOnlyListActivityEntry)),
        ["wait_for_request"] = async (c, a, ct) => To(
            await c.WaitForRequestAsync(Required(a, "match"), Int(a, "timeoutMs") ?? 10_000, Bool(a, "bodies") ?? true, ct),
            Json.TrafficEntry
        ),
        ["wait_for_quiet"] = async (c, a, ct) => To(
            await c.WaitForQuietAsync(Int(a, "quietMs") ?? 750, Int(a, "timeoutMs") ?? 10_000, ct),
            Json.SimulatorStatus
        )
    };

    /// <summary>The operations the API answers, by name.</summary>
    public static IReadOnlyCollection<string> OperationNames => Operations.Keys;

    /// <summary>
    /// Whether a request is the simulator's own control traffic — the panel, its API, MCP — rather than the page's. The
    /// traffic recorder leaves it out: the panel polls, and would otherwise push what the page did out of the window.
    /// </summary>
    public static bool IsControl(HttpContext context) => AppDeviceBridgeServer.IsUnder(context.Request.Path, ControlPrefix);

    /// <summary>Serves the panel and its API, ahead of whatever serves the page, so a page's fallback route cannot swallow them.</summary>
    /// <param name="control">Resolved on the first call, once the container is built.</param>
    public static void MapWebPanel(ShinyHttpServerBuilder http, string token, Func<SimulatorControl> control)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(control);

        http.Configure(server => server.Use((context, next) => HandleAsync(context, next, token, control)));
    }

    static async ValueTask HandleAsync(HttpContext context, RequestDelegate next, string token, Func<SimulatorControl> control)
    {
        var path = context.Request.Path;
        var isApi = AppDeviceBridgeServer.IsUnder(path, ApiPath);
        var isPage = !isApi && (path == Path || path == Path.TrimEnd('/'));

        if (!isApi && !isPage)
        {
            await next(context);
            return;
        }

        if (!BridgeCallers.IsOnDevice(context))
        {
            await Error(context, StatusCodes.Status403Forbidden, "control_denied", "The simulator's panel answers callers on this device only.");
            return;
        }

        if (isPage)
        {
            // The panel calls its API relative to itself, which only works from the directory.
            if (path != Path)
            {
                context.Response.Redirect(Path, permanent: false, preserveMethod: true);
                return;
            }

            await ServePageAsync(context);
            return;
        }

        if (!IsSameOrigin(context))
        {
            await Error(context, StatusCodes.Status403Forbidden, "control_denied", "The simulator's panel API answers the panel, not other sites.");
            return;
        }

        if (!HasToken(context, token))
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await Error(context, StatusCodes.Status401Unauthorized, "control_denied", "The simulator's panel API needs its token, which it printed at startup.");
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers["Allow"] = "POST";
            await Error(context, StatusCodes.Status405MethodNotAllowed, "bad_request", "Operations are POSTed, with their arguments as a JSON object.");
            return;
        }

        var name = path[(ApiPath.Length + 1)..].Trim('/');
        if (!Operations.TryGetValue(name, out var operation))
        {
            await Error(context, StatusCodes.Status404NotFound, "not_found", $"There is no operation '{name}'. There is {String.Join(", ", Operations.Keys)}.");
            return;
        }

        JsonObject args;
        try
        {
            args = await ReadArgsAsync(context);
        }
        catch (JsonException ex)
        {
            await Error(context, StatusCodes.Status400BadRequest, "bad_request", $"The body is not a JSON object: {ex.Message}");
            return;
        }

        JsonNode? result;
        try
        {
            result = await operation(control(), args, context.RequestAborted);
        }
        catch (FileNotFoundException ex)
        {
            await Error(context, StatusCodes.Status404NotFound, "not_found", ex.Message);
            return;
        }
        catch (TimeoutException ex)
        {
            await Error(context, StatusCodes.Status408RequestTimeout, "timeout", ex.Message);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
        {
            // The refusals are the useful part: they say which contract a value should have matched.
            await Error(context, StatusCodes.Status400BadRequest, "bad_request", ex.Message);
            return;
        }

        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteTextAsync(result.ToJsonString(), "application/json; charset=utf-8", context.RequestAborted);
    }

    static async Task<JsonObject> ReadArgsAsync(HttpContext context)
    {
        if (!context.Request.HasBody)
            return [];

        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync(context.RequestAborted);
        if (String.IsNullOrWhiteSpace(text))
            return [];

        return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("expected an object.");
    }

    static ValueTask ServePageAsync(HttpContext context)
    {
        // The panel holds no secret — the token arrives in its fragment — but nothing else should frame or cache it.
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'none'";

        return context.Response.WriteTextAsync(PanelHtml.Value, "text/html; charset=utf-8", context.RequestAborted);
    }

    static readonly Lazy<string> PanelHtml = new(() =>
    {
        using var stream = typeof(WebPanel).Assembly.GetManifestResourceStream("Shiny.AppDeviceBridge.Simulator.panel.html")
            ?? throw new InvalidOperationException("The panel page is missing from the simulator's resources.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// A browser sends <c>Origin</c> on the panel's POSTs; one that names another site is a page elsewhere trying its luck.
    /// A client that is not a browser sends none.
    /// </summary>
    static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers["Origin"].ToString();
        if (origin.Length == 0)
            return true;

        var host = context.Request.Host;
        return Uri.TryCreate(origin, UriKind.Absolute, out var sent)
               && sent.Scheme is "http" or "https"
               && String.Equals(sent.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    static bool HasToken(HttpContext context, string token)
    {
        var sent = context.Request.Headers["Authorization"].ToString();
        const string bearer = "Bearer ";
        if (!sent.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(sent[bearer.Length..].Trim()),
            System.Text.Encoding.UTF8.GetBytes(token)
        );
    }

    static ValueTask Error(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        return context.Response.WriteTextAsync(body.ToJsonString(), "application/json; charset=utf-8", context.RequestAborted);
    }

    static Operation Sync(Func<SimulatorControl, JsonNode?> run) => (c, _, _) => Task.FromResult(run(c));

    static Operation Sync(Func<SimulatorControl, JsonObject, JsonNode?> run) => (c, a, _) => Task.FromResult(run(c, a));

    static JsonNode? To<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToNode(value, type);

    static ArgumentException Missing(string name) => new($"'{name}' is required.", name);

    static string Required(JsonObject args, string name) => Optional(args, name) ?? throw Missing(name);

    static string? Optional(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ArgumentException($"'{name}' must be a string.", name)
    };

    static int? Int(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        _ => throw new ArgumentException($"'{name}' must be a whole number.", name)
    };

    static double? Double(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        _ => throw new ArgumentException($"'{name}' must be a number.", name)
    };

    static bool? Bool(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new ArgumentException($"'{name}' must be true or false.", name)
    };
}
