using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Simulator.Simulation;

/// <summary>
/// A bridge answering from <see cref="SimulatorState"/> instead of the device. Mounted on the real bridge server, so it sits
/// behind the same guard, policy and event stream a device's bridges do.
/// </summary>
public sealed class SimulatedBridge(SimulatorState state, BridgeState bridge) : IWebAppBridge
{
    const int MaxStoredBody = 8 * 1024 * 1024;

    public string Name => bridge.Name;

    public bool IsSupported => bridge.IsSupported;

    public void Map(WebAppBridgeRoutes routes)
    {
        foreach (var route in bridge.Routes)
        {
            var pattern = route.Route.Pattern;
            RequestDelegate handler = ctx => this.HandleAsync(ctx, route);

            _ = route.Route.Method switch
            {
                "GET" => routes.MapGet(pattern, handler),
                "POST" => routes.MapPost(pattern, handler),
                "PUT" => routes.MapPut(pattern, handler),
                "DELETE" => routes.MapDelete(pattern, handler),
                var other => throw new NotSupportedException($"{route.Route.Path} uses {other}, which bridges do not.")
            };
        }
    }

    async ValueTask HandleAsync(HttpContext context, RouteState route)
    {
        route.Hit();
        state.OnChanged();

        if (!bridge.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, bridge.Name);
            return;
        }

        var behavior = route.Behavior;
        if (behavior.DelayMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(behavior.DelayMs), state.Time, context.RequestAborted);

        switch (behavior.Mode)
        {
            case ResponseMode.Error:
                await WebAppBridgeResults.Error(context, behavior.StatusCode, behavior.ErrorCode, behavior.ErrorMessage);
                return;

            case ResponseMode.Null:
                await WebAppBridgeResults.NoContent(context);
                return;
        }

        if (state.StickyWrites && route.Route is { Method: "PUT" or "POST", BodyType: { } bodyType } && !IsRaw(bodyType) && context.Request.HasBody)
            this.StoreWrite(route, bodyType, await context.Request.ReadBodyAsStringAsync(MaxStoredBody, context.RequestAborted));

        switch (route.Route.Kind)
        {
            case ResponseKind.Empty:
                await WebAppBridgeResults.NoContent(context);
                return;

            case ResponseKind.Binary:
                await SendFileAsync(context, behavior.FilePath);
                return;

            default:
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteTextAsync(PayloadTokens.Expand(behavior.Json, state.Time), "application/json; charset=utf-8", context.RequestAborted);
                return;
        }
    }

    /// <summary>A write whose body is what the same path's GET returns becomes that GET's value.</summary>
    void StoreWrite(RouteState route, Type bodyType, string body)
    {
        var read = bridge.Routes.FirstOrDefault(x =>
            x.Route.Method == "GET"
            && x.Route.Pattern == route.Route.Pattern
            && x.Route.ResultType is { } result
            && (Nullable.GetUnderlyingType(result) ?? result) == (Nullable.GetUnderlyingType(bodyType) ?? bodyType));

        if (read is null || !SampleJson.TryValidate(body, bodyType, route.Route.Json, out _))
            return;

        read.Behavior = read.Behavior with { Mode = ResponseMode.Value, Json = body };
        state.Log($"{route.Route.Method} {route.Route.Path} stored as GET {read.Route.Path}");
    }

    static async ValueTask SendFileAsync(HttpContext context, string? path)
    {
        if (path is null)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteBytesAsync(Placeholder.Png, "image/png", context.RequestAborted);
            return;
        }

        if (!File.Exists(path))
        {
            await WebAppBridgeResults.NotFound(context, $"The simulator's file {path} does not exist.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await using var file = File.OpenRead(path);
        await context.Response.WriteStreamAsync(file, ContentTypes.For(path), context.RequestAborted);
    }

    static bool IsRaw(Type type) => type == typeof(Stream) || type == typeof(byte[]);
}

static class ContentTypes
{
    public static string For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".mp4" => "video/mp4",
        ".mjpeg" or ".mjpg" => "multipart/x-mixed-replace",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}

/// <summary>What a binary route sends until a file is chosen: a 16×16 grey PNG.</summary>
static class Placeholder
{
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFElEQVR42mOYRSJgGNUwqmH4agAAnH3OEOF9VzYAAAAASUVORK5CYII=");
}
