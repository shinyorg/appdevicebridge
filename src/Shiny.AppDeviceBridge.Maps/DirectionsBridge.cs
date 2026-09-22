using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Maps.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// <c>/_bridge/directions</c>: routes computed on the device when a downloaded road network covers every stop, and by the
/// app's online router otherwise.
/// <code>
/// GET  /_bridge/directions          { online, onDevice, offlineRegions }
/// POST /_bridge/directions/route    { stops: [{ latitude, longitude }, …], mode, units, language, source, avoid }
///                                   → { source, distance, duration, shape: [[lon, lat], …], bounds, legs: [{ maneuvers }] }
/// </code>
/// </summary>
sealed class DirectionsBridge(MapsService maps, ILoggerFactory? loggerFactory = null) : IWebAppBridge
{
    const int MaxStops = 20;

    readonly ILogger logger = (ILogger?)loggerFactory?.CreateLogger<DirectionsBridge>() ?? NullLogger.Instance;

    public string Name => "directions";

    public bool IsSupported => maps.OnlineRouter is not null || maps.OnDeviceDirections;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.InfoAsync)
        .MapPost("/route", this.RouteAsync);

    ValueTask InfoAsync(HttpContext context) => WebAppBridgeResults.Json(
        context,
        new DirectionsInfo(
            maps.OnlineRouter is not null,
            maps.OnDeviceDirections,
            [.. maps.Installed.Where(x => x.DirectionsVersion is not null).Select(x => x.Id)]
        ),
        DirectionsJsonContext.Default.DirectionsInfo
    );

    async ValueTask RouteAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Directions");
            return;
        }

        var request = await WebAppBridgeResults.ReadBodyAsync(context, DirectionsJsonContext.Default.DirectionsRequest);
        if (request?.Stops is not { Count: >= 2 and <= MaxStops } stops
            || stops.Any(s => s is null || !(s.Latitude is >= -90 and <= 90) || !(s.Longitude is >= -180 and <= 180))
            || !Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Source) || !Enum.IsDefined(request.Units))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"stops\": [ {{ \"latitude\", \"longitude\" }}, … ] }} with 2 to {MaxStops} stops on the map.");
            return;
        }

        if (request.Language is { } language && (language.Length > 16 || !language.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            await WebAppBridgeResults.BadRequest(context, "\"language\" must be a language tag such as en-US.");
            return;
        }

        var json = ValhallaTranslation.ToRequest(request);
        IValhallaRouter? device = null;
        string? deviceProblem = null;

        if (request.Source != DirectionsSource.Online)
        {
            try
            {
                device = maps.FindOnDeviceRouter(stops);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or DllNotFoundException or EntryPointNotFoundException)
            {
                // A road network the engine cannot open — damaged, or from an incompatible Valhalla — is the same as none.
                this.logger.LogWarning(ex, "The on-device router could not start");
                deviceProblem = ex.Message;
            }
        }

        if (device is not null)
        {
            try
            {
                var answer = await device.RouteAsync(json, context.RequestAborted);
                await this.RespondAsync(context, answer, DirectionsSource.Device);
                return;
            }
            catch (ValhallaException ex) when (request.Source == DirectionsSource.Auto && maps.OnlineRouter is not null)
            {
                // Stops near a region's edge can need roads outside it; the online router has them all.
                this.logger.LogInformation("On-device route failed ({Code}: {Message}); trying online", ex.ErrorCode, ex.Message);
            }
            catch (ValhallaException ex)
            {
                await Fail(context, ex);
                return;
            }
        }

        if (request.Source == DirectionsSource.Device)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "offline_unavailable",
                deviceProblem ?? (maps.OnDeviceDirections
                    ? "No downloaded road network covers every stop."
                    : "This platform cannot compute directions on the device.")
            );
            return;
        }

        if (maps.OnlineRouter is not { } online)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "offline_unavailable",
                "No downloaded road network covers every stop, and the app has no online router."
            );
            return;
        }

        try
        {
            var answer = await online.RouteAsync(json, context.RequestAborted);
            await this.RespondAsync(context, answer, DirectionsSource.Online);
        }
        catch (ValhallaException ex)
        {
            await Fail(context, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            this.logger.LogInformation(ex, "Online router unavailable");
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "offline_unavailable",
                "The online router could not be reached, and no downloaded road network covers every stop."
            );
        }
    }

    async ValueTask RespondAsync(HttpContext context, string answer, DirectionsSource source)
    {
        DirectionsRoute route;
        try
        {
            route = ValhallaTranslation.FromResponse(answer, source);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            this.logger.LogWarning(ex, "The {Source} router answered with something that is not a route", source);
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "router_error", "The router answered with something that is not a route.");
            return;
        }
        catch (ValhallaException ex)
        {
            await Fail(context, ex);
            return;
        }

        await WebAppBridgeResults.Json(context, route, DirectionsJsonContext.Default.DirectionsRoute);
    }

    static ValueTask Fail(HttpContext context, ValhallaException ex) => ex.IsNoRoute
        ? WebAppBridgeResults.Error(context, StatusCodes.Status404NotFound, "no_route", ex.Message)
        : ex.HttpStatus >= 500 || ex.ErrorCode == 0
            ? WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "router_error", ex.Message)
            : WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "bad_request", ex.Message);
}
