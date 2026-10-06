using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Maps.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// <c>/_bridge/directions</c>: routes computed on the device when a downloaded road network covers every stop, and by the
/// app's online router otherwise — whichever <see cref="IRouteProvider"/> it is; addresses turned into stops by the app's
/// geocoder.
/// <code>
/// GET  /_bridge/directions          { online, onlineModes, onDevice, offlineRegions, geocoding, router }
/// POST /_bridge/directions/route    { stops: [{ latitude, longitude }, …], mode, units, language, source, avoid }
///                                   → { source, distance, duration, shape: [[lon, lat], …], bounds, legs: [{ maneuvers }] }
/// GET  /_bridge/directions/geocode?query=&amp;limit=5&amp;language=
///                                   → { places: [{ name, address, latitude, longitude, bounds }], attribution }
/// </code>
/// </summary>
sealed class DirectionsBridge(MapsService maps, ILoggerFactory? loggerFactory = null) : IWebAppBridge
{
    const int MaxStops = 20;
    const int MaxQueryLength = 200;
    const int MaxPlaces = 20;

    // What on-device routes are computed from: Valhalla over OpenStreetMap's roads.
    const string DeviceAttribution = "<a href=\"https://www.openstreetmap.org/copyright\">© OpenStreetMap</a>";

    readonly ILogger logger = (ILogger?)loggerFactory?.CreateLogger<DirectionsBridge>() ?? NullLogger.Instance;

    public string Name => "directions";

    public bool IsSupported => this.CanRoute || maps.Options.Directions.Geocoder is not null;

    bool CanRoute => maps.Options.Directions.Router is not null || maps.OnDeviceDirections;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.InfoAsync)
        .MapPost("/route", this.RouteAsync)
        .MapGet("/geocode", this.GeocodeAsync);

    ValueTask InfoAsync(HttpContext context)
    {
        var router = maps.Options.Directions.Router;
        return WebAppBridgeResults.Json(
            context,
            new DirectionsInfo(
                router is not null,
                router is null ? [] : [.. router.Modes.Order()],
                maps.OnDeviceDirections,
                [.. maps.Installed.Where(x => x.DirectionsVersion is not null).Select(x => x.Id)],
                maps.Options.Directions.Geocoder is not null,
                router?.Name
            ),
            DirectionsJsonContext.Default.DirectionsInfo
        );
    }

    async ValueTask RouteAsync(HttpContext context)
    {
        if (!this.CanRoute)
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

        if (request.Language is { } language && !IsLanguageTag(language))
        {
            await WebAppBridgeResults.BadRequest(context, "\"language\" must be a language tag such as en-US.");
            return;
        }

        IValhallaRouter? device = null;
        string? deviceProblem = null;

        // Read per request, so a router the app swaps while it runs answers the next route.
        var online = maps.Options.Directions.Router;

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
                var answer = await device.RouteAsync(ValhallaTranslation.ToRequest(request), context.RequestAborted);
                await this.RespondAsync(context, () => ValhallaTranslation.FromResponse(answer, DirectionsSource.Device), DirectionsSource.Device, DeviceAttribution);
                return;
            }
            catch (DirectionsException ex) when (request.Source == DirectionsSource.Auto && online is not null && online.Modes.Contains(request.Mode))
            {
                // Stops near a region's edge can need roads outside it; the online router has them all.
                this.logger.LogInformation("On-device route failed ({Error}: {Message}); trying online", ex.Error, ex.Message);
            }
            catch (DirectionsException ex)
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

        if (online is null)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "offline_unavailable",
                "No downloaded road network covers every stop, and the app has no online router."
            );
            return;
        }

        if (!online.Modes.Contains(request.Mode))
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status400BadRequest,
                "mode_unsupported",
                $"{online.Name} has no {request.Mode.ToString().ToLowerInvariant()} routes."
            );
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(maps.Options.Directions.Timeout);

            var route = await online.RouteAsync(request, maps.Http, timeout.Token);
            await this.RespondAsync(context, () => route, DirectionsSource.Online, online.Attribution);
        }
        catch (DirectionsException ex)
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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            this.logger.LogWarning(ex, "The {Router} router answered with something that is not a route", online.Name);
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "router_error", "The router answered with something that is not a route.");
        }
    }

    async ValueTask GeocodeAsync(HttpContext context)
    {
        // Read per request, so an app that swaps the geocoder while it runs is answered by the new one.
        if (maps.Options.Directions.Geocoder is not { } geocoder)
        {
            await WebAppBridgeResults.NotSupported(context, "Geocoding");
            return;
        }

        var query = context.Request.Query;
        var text = query["query"].ToString().Trim();
        if (text.Length is 0 or > MaxQueryLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"\"query\" must be 1 to {MaxQueryLength} characters.");
            return;
        }

        var limit = 5;
        if (query["limit"].ToString() is { Length: > 0 } requested
            && (!Int32.TryParse(requested, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxPlaces))
        {
            await WebAppBridgeResults.BadRequest(context, $"\"limit\" must be 1 to {MaxPlaces}.");
            return;
        }

        var language = query["language"].ToString() is { Length: > 0 } tag ? tag : null;
        if (language is not null && !IsLanguageTag(language))
        {
            await WebAppBridgeResults.BadRequest(context, "\"language\" must be a language tag such as en-US.");
            return;
        }

        IReadOnlyList<GeocodedPlace> places;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(maps.Options.Directions.Timeout);
            places = await geocoder.SearchAsync(new GeocodeQuery(text, limit, language), maps.Http, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            this.logger.LogInformation(ex, "Geocoder unavailable");
            await WebAppBridgeResults.Error(context, StatusCodes.Status503ServiceUnavailable, "geocoder_unavailable", "The geocoder could not be reached.");
            return;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            this.logger.LogWarning(ex, "The geocoder answered with something that is not a list of places");
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "geocoder_error", "The geocoder answered with something that is not a list of places.");
            return;
        }

        await WebAppBridgeResults.Json(
            context,
            new GeocodeResult([.. places.Take(limit)], geocoder.Attribution),
            DirectionsJsonContext.Default.GeocodeResult
        );
    }

    static bool IsLanguageTag(string language)
        => language.Length <= 16 && language.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    async ValueTask RespondAsync(HttpContext context, Func<DirectionsRoute> read, DirectionsSource source, string attribution)
    {
        DirectionsRoute route;
        try
        {
            route = read() with { Source = source, Attribution = attribution };
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            this.logger.LogWarning(ex, "The {Source} router answered with something that is not a route", source);
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "router_error", "The router answered with something that is not a route.");
            return;
        }
        catch (DirectionsException ex)
        {
            await Fail(context, ex);
            return;
        }

        await WebAppBridgeResults.Json(context, route, DirectionsJsonContext.Default.DirectionsRoute);
    }

    static ValueTask Fail(HttpContext context, DirectionsException ex) => ex.Error switch
    {
        DirectionsError.NoRoute => WebAppBridgeResults.Error(context, StatusCodes.Status404NotFound, "no_route", ex.Message),
        DirectionsError.InvalidRequest => WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "bad_request", ex.Message),
        _ => WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "router_error", ex.Message)
    };
}
