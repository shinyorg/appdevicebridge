using System.Text.RegularExpressions;
using Shiny.AppDeviceBridge.Maps.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>
/// <c>/_bridge/maps</c>: the tiles, glyphs and sprites a vector map draws with, and the regions the user can download.
/// <code>
/// GET    /_bridge/maps                            { tilesUrl, glyphsUrl, spritesUrl, maxZoom, online, catalog, attribution, traffic }
/// GET    /_bridge/maps/regions?refresh=true       { regions: [ … ], catalogReachable, installedBytes }
/// POST   /_bridge/maps/regions/{id}               { "directions": true }        202, progress as maps.download
/// DELETE /_bridge/maps/regions/{id}
/// DELETE /_bridge/maps/regions/{id}/directions
/// DELETE /_bridge/maps/regions/{id}/download
/// GET    /_bridge/maps/tiles/{z}/{x}/{y}          a vector tile, or 204
/// GET    /_bridge/maps/traffic/{z}/{x}/{y}        a live traffic tile, or 204; 501 with no traffic provider
/// GET    /_bridge/maps/incidents/{z}/{x}/{y}      a live incident tile, or 204; 501 with no incident provider
/// GET    /_bridge/maps/glyphs/{fontstack}/{range}.pbf
/// GET    /_bridge/maps/sprites/{flavor}[@2x].json|png
///
/// events: maps.download
/// </code>
/// </summary>
sealed partial class MapsBridge(MapsService maps) : IWebAppBridge
{
    public string Name => "maps";

    public bool IsSupported => true;

    string prefix = "/_bridge/maps";

    public void Map(WebAppBridgeRoutes routes)
    {
        this.prefix = routes.Prefix;

        routes
            .MapGet("", this.InfoAsync)
            .MapGet("/regions", this.RegionsAsync)
            .MapPost("/regions/{id}", ctx => this.WithRegion(ctx, this.InstallAsync))
            .MapDelete("/regions/{id}", ctx => this.WithRegion(ctx, this.RemoveAsync))
            .MapDelete("/regions/{id}/directions", ctx => this.WithRegion(ctx, this.RemoveDirectionsAsync))
            .MapDelete("/regions/{id}/download", ctx => this.WithRegion(ctx, this.CancelAsync))
            .MapGet("/tiles/{z}/{x}/{y}", this.TileAsync)
            .MapGet("/traffic/{z}/{x}/{y}", this.TrafficAsync)
            .MapGet("/incidents/{z}/{x}/{y}", this.IncidentsAsync)
            .MapGet("/glyphs/{fontstack}/{range}", this.GlyphsAsync)
            .MapGet("/sprites/{name}", this.SpriteAsync);
    }

    ValueTask InfoAsync(HttpContext context)
    {
        var options = maps.Options;
        return WebAppBridgeResults.Json(
            context,
            new MapsInfo(
                $"{this.prefix}/tiles/{{z}}/{{x}}/{{y}}",
                $"{this.prefix}/glyphs/{{fontstack}}/{{range}}.pbf",
                $"{this.prefix}/sprites",
                options.OnlineMaxZoom,
                options.OnlineTiles is not null,
                options.Catalog is not null,
                options.Attribution,
                options.Traffic?.Layer is { } traffic
                    ? new TrafficInfo(
                        $"{this.prefix}/traffic/{{z}}/{{x}}/{{y}}",
                        traffic.Format,
                        traffic.MinZoom,
                        traffic.MaxZoom,
                        (int)Math.Max(1, traffic.Refresh.TotalSeconds),
                        traffic.SourceLayer,
                        traffic.SpeedRatioProperty,
                        traffic.ClosedProperty,
                        traffic.TileSize,
                        traffic.Attribution
                    )
                    : null,
                options.TrafficIncidents?.Layer is { } incidents
                    ? new TrafficIncidentInfo(
                        $"{this.prefix}/incidents/{{z}}/{{x}}/{{y}}",
                        incidents.MinZoom,
                        incidents.MaxZoom,
                        (int)Math.Max(1, incidents.Refresh.TotalSeconds),
                        incidents.LineSourceLayer,
                        incidents.PointSourceLayer,
                        incidents.KindProperty,
                        incidents.Kinds,
                        incidents.DescriptionProperty,
                        incidents.DelayProperty,
                        incidents.ClusterSizeProperty,
                        incidents.Attribution
                    )
                    : null
            ),
            MapsJsonContext.Default.MapsInfo
        );
    }

    async ValueTask RegionsAsync(HttpContext context)
    {
        var refresh = context.Request.Query["refresh"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        var (catalog, reachable) = await maps.GetCatalogAsync(refresh, context.RequestAborted);
        var installed = maps.Installed.ToDictionary(x => x.Id);
        var regions = new List<MapRegion>();

        foreach (var region in catalog?.Regions ?? [])
        {
            installed.Remove(region.Id, out var local);
            regions.Add(new MapRegion(
                region.Id,
                region.Name,
                region.Bounds,
                region.Map.Size,
                region.Directions?.Size,
                local?.MapVersion is not null,
                local?.DirectionsVersion is not null,
                (local?.MapVersion is { } m && m != region.Map.Version)
                    || (local?.DirectionsVersion is { } d && region.Directions is { } rd && d != rd.Version),
                maps.GetDownload(region.Id)
            ));
        }

        // Installed regions the catalog no longer lists — or cannot be fetched to list — can still be seen and removed.
        foreach (var local in installed.Values)
        {
            regions.Add(new MapRegion(
                local.Id,
                local.Name,
                local.Bounds,
                local.MapSize,
                local.DirectionsVersion is null ? null : local.DirectionsSize,
                local.MapVersion is not null,
                local.DirectionsVersion is not null,
                false,
                maps.GetDownload(local.Id)
            ));
        }

        await WebAppBridgeResults.Json(
            context,
            new MapCatalog([.. regions.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)], reachable, maps.InstalledBytes),
            MapsJsonContext.Default.MapCatalog
        );
    }

    async ValueTask InstallAsync(HttpContext context, string id)
    {
        if (maps.Options.Catalog is null)
        {
            await WebAppBridgeResults.NotSupported(context, "Downloading map regions");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, MapsJsonContext.Default.MapPackInstallRequest) ?? new MapPackInstallRequest();
        var (download, running) = await maps.InstallAsync(id, body.Directions, context.RequestAborted);

        if (download is null)
        {
            await WebAppBridgeResults.NotFound(context, $"The catalog has no region '{id}'.");
            return;
        }

        if (running)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "downloading", $"Region '{id}' is already downloading.");
            return;
        }

        await WebAppBridgeResults.Json(context, download, MapsJsonContext.Default.MapPackDownload, StatusCodes.Status202Accepted);
    }

    async ValueTask RemoveAsync(HttpContext context, string id)
    {
        maps.Remove(id);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask RemoveDirectionsAsync(HttpContext context, string id)
    {
        if (!maps.RemoveDirections(id))
        {
            await WebAppBridgeResults.NotFound(context, $"Region '{id}' has no road network on the device.");
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask CancelAsync(HttpContext context, string id)
    {
        if (!maps.CancelDownload(id))
        {
            await WebAppBridgeResults.NotFound(context, $"Region '{id}' is not downloading.");
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask WithRegion(HttpContext context, Func<HttpContext, string, ValueTask> action)
    {
        var id = context.Request.RouteValues["id"] ?? String.Empty;
        if (!MapPackSignature.IsValidRegionId(id))
        {
            await WebAppBridgeResults.NotFound(context, $"No region '{id}'.");
            return;
        }

        await action(context, id);
    }

    async ValueTask TileAsync(HttpContext context)
    {
        var values = context.Request.RouteValues;
        if (!int.TryParse(values["z"], out var z) || !int.TryParse(values["x"], out var x) || !int.TryParse(Strip(values["y"], ".mvt", ".pbf"), out var y)
            || !PmTilesArchive.IsValidTile(z, x, y))
        {
            await WebAppBridgeResults.NotFound(context, "No such tile.");
            return;
        }

        var tile = await maps.GetTileAsync(z, x, y, context.RequestAborted);
        if (tile is null)
        {
            // MapLibre draws an empty tile for 204, where a 404 would log an error for every tile off the map.
            await WebAppBridgeResults.NoContent(context);
            return;
        }

        context.Response.ContentType = "application/vnd.mapbox-vector-tile";
        switch (tile.Compression)
        {
            case PmTilesCompression.Gzip:
                context.Response.Headers["Content-Encoding"] = "gzip";
                break;
            case PmTilesCompression.Brotli:
                context.Response.Headers["Content-Encoding"] = "br";
                break;
        }

        await WriteAsync(context, tile.Data);
    }

    ValueTask TrafficAsync(HttpContext context) => maps.Options.Traffic is { } provider
        ? this.LiveTileAsync(context, provider.Layer.Refresh, maps.GetTrafficTileAsync)
        : WebAppBridgeResults.NotSupported(context, "Traffic");

    ValueTask IncidentsAsync(HttpContext context) => maps.Options.TrafficIncidents is { } provider
        ? this.LiveTileAsync(context, provider.Layer.Refresh, maps.GetIncidentTileAsync)
        : WebAppBridgeResults.NotSupported(context, "Traffic incidents");

    async ValueTask LiveTileAsync(HttpContext context, TimeSpan refresh, Func<int, int, int, CancellationToken, Task<TrafficTile?>> get)
    {
        var values = context.Request.RouteValues;
        if (!int.TryParse(values["z"], out var z) || !int.TryParse(values["x"], out var x)
            || !int.TryParse(Strip(values["y"], ".mvt", ".pbf", ".png", ".jpg"), out var y) || !PmTilesArchive.IsValidTile(z, x, y))
        {
            await WebAppBridgeResults.NotFound(context, "No such tile.");
            return;
        }

        var tile = await get(z, x, y, context.RequestAborted);
        if (tile is null)
        {
            await WebAppBridgeResults.NoContent(context);
            return;
        }

        context.Response.ContentType = tile.ContentType;
        if (tile.ContentEncoding is { } encoding)
            context.Response.Headers["Content-Encoding"] = encoding;

        // The page asks again after the refresh interval with a new query string; until then the WebView may keep it.
        context.Response.Headers["Cache-Control"] = $"max-age={(int)Math.Max(1, refresh.TotalSeconds)}";
        await WriteAsync(context, tile.Data);
    }

    async ValueTask GlyphsAsync(HttpContext context)
    {
        var fontstack = context.Request.RouteValues["fontstack"] ?? String.Empty;
        var range = Strip(context.Request.RouteValues["range"], ".pbf");

        if (!GlyphRange().IsMatch(range))
        {
            await WebAppBridgeResults.NotFound(context, "No such glyph range.");
            return;
        }

        // A stack names fallbacks — "Noto Sans Regular,Arial Unicode MS Regular" — and the first font there is serves.
        foreach (var font in fontstack.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!FontName().IsMatch(font))
                continue;

            if (await maps.GetAssetAsync($"fonts/{font}/{range}.pbf", context.RequestAborted) is { } data)
            {
                context.Response.ContentType = "application/x-protobuf";
                await WriteAsync(context, data);
                return;
            }
        }

        await WebAppBridgeResults.NotFound(context, $"No glyphs for '{fontstack}'.");
    }

    async ValueTask SpriteAsync(HttpContext context)
    {
        var name = context.Request.RouteValues["name"] ?? String.Empty;
        var match = SpriteName().Match(name);

        if (!match.Success || await maps.GetAssetAsync($"sprites/v4/{name}", context.RequestAborted) is not { } data)
        {
            await WebAppBridgeResults.NotFound(context, $"No sprite '{name}'.");
            return;
        }

        context.Response.ContentType = match.Groups["ext"].Value == "json" ? "application/json" : "image/png";
        await WriteAsync(context, data);
    }

    static async ValueTask WriteAsync(HttpContext context, byte[] data)
    {
        context.Response.ContentLength = data.Length;
        await context.Response.Body.WriteAsync(data, context.RequestAborted);
    }

    static string Strip(string? value, params string[] suffixes)
    {
        value ??= String.Empty;
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length];
        }

        return value;
    }

    [GeneratedRegex(@"^\d{1,5}-\d{1,5}$")]
    private static partial Regex GlyphRange();

    [GeneratedRegex(@"^[A-Za-z0-9 _\-]{1,64}$")]
    private static partial Regex FontName();

    [GeneratedRegex(@"^[a-z0-9_\-]{1,32}(@2x)?\.(?<ext>json|png)$")]
    private static partial Regex SpriteName();
}
