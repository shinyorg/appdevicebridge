using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost.Bridge.AppSupport;

/// <summary>
/// <c>/_bridge/app</c> — device and app information, orientation, the browser, maps, settings and the
/// app store, over Shiny.Extensions.MauiHosting.
/// <code>
/// GET    /_bridge/app/info
/// POST   /_bridge/app/orientation     { "orientation": "Portrait" }
/// DELETE /_bridge/app/orientation
/// POST   /_bridge/app/browser         { "uri": "https://…" }
/// POST   /_bridge/app/map             { "latitude": 43.6, "longitude": -79.4 }
/// POST   /_bridge/app/settings
/// GET    /_bridge/app/store           (needs AddAppStore)
/// POST   /_bridge/app/store/open
/// POST   /_bridge/app/store/review
///
/// events: app.orientation, app.culture, app.timezone
/// </code>
/// </summary>
public sealed class AppSupportBridge : IWebAppBridge, IDisposable
{
    readonly IAppSupport? app;
    readonly IAppStore? store;
    readonly WebAppEventHub events;

    public AppSupportBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.app = services.GetOptionalService<IAppSupport>();
        this.store = services.GetOptionalService<IAppStore>();
        this.events = events;

        if (this.app is not null)
        {
            this.app.OrientationChanged += this.OnOrientationChanged;
            this.app.CultureChanged += this.OnCultureChanged;
            this.app.TimeZoneChanged += this.OnTimeZoneChanged;
        }
    }

    public string Name => "app";

    public bool IsSupported => this.app is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/info", this.InfoAsync)
        .MapPost("/orientation", this.SetOrientationAsync)
        .MapDelete("/orientation", this.ResetOrientationAsync)
        .MapPost("/browser", this.OpenBrowserAsync)
        .MapPost("/map", this.OpenMapAsync)
        .MapPost("/settings", this.OpenSettingsAsync)
        .MapGet("/store", this.StoreAsync)
        .MapPost("/store/open", this.OpenStoreAsync)
        .MapPost("/store/review", this.RequestReviewAsync);

    ValueTask InfoAsync(HttpContext context)
    {
        if (this.app is not { } a)
            return WebAppBridgeResults.NotSupported(context, "App support");

        var info = new AppInfoResponse(
            a.AppVersion.ToString(),
            a.DeviceManufacturer,
            a.DeviceModel,
            a.PlatformVersion?.ToString(),
            a.Platform,
            a.DeviceIdiom.ToString(),
            a.CurrentOrientation,
            a.CurrentCulture.Name,
            a.CurrentTimeZone.Id
        );

        return WebAppBridgeResults.Json(context, info, AppBridgeJsonContext.Default.AppInfoResponse);
    }

    async ValueTask SetOrientationAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Orientation");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, AppBridgeJsonContext.Default.OrientationRequest) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"orientation\": \"Portrait\" | \"Landscape\" }.");
            return;
        }

        var success = await OnMainThread(() => a.SetOrientation(body.Orientation));
        await Success(context, success);
    }

    async ValueTask ResetOrientationAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Orientation");
            return;
        }

        await Success(context, await OnMainThread(a.ResetOrientation));
    }

    async ValueTask OpenBrowserAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Browser");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AppBridgeJsonContext.Default.OpenBrowserRequest);

        // The page can ask for web and contact links, not arbitrary schemes that open other apps.
        if (body is null
            || !Uri.TryCreate(body.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "mailto" or "tel"))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"uri\": \"https://…\" } with an http, https, mailto or tel URI.");
            return;
        }

        var success = await OnMainThread(() => a.OpenBrowser(uri.AbsoluteUri, body.ShowTitle, body.LaunchMode));
        await Success(context, success);
    }

    async ValueTask OpenMapAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Maps");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, AppBridgeJsonContext.Default.OpenMapRequest);
        if (body is null || body.Latitude is < -90 or > 90 || body.Longitude is < -180 or > 180)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"latitude\": …, \"longitude\": … } within range.");
            return;
        }

        var success = await OnMainThread(() => a.OpenMap(body.Latitude, body.Longitude, body.NavigationMode));
        await Success(context, success);
    }

    async ValueTask OpenSettingsAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "App settings");
            return;
        }

        await OnMainThread(() =>
        {
            a.OpenAppSettings();
            return Task.FromResult(true);
        });

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask StoreAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "App store lookup");
            return;
        }

        if (await s.GetCurrent(context.RequestAborted) is not { } result)
        {
            await WebAppBridgeResults.NotFound(context, "The app was not found in the store.");
            return;
        }

        await WebAppBridgeResults.Json(
            context,
            new AppStoreResponse(
                result.StoreVersion.ToString(),
                result.CurrentVersion.ToString(),
                result.NeedsUpdate,
                result.StoreUrl,
                result.ReleaseNotes,
                result.ReleasedAt,
                result.AverageRating,
                result.RatingCount,
                result.MinimumOsVersion
            ),
            AppBridgeJsonContext.Default.AppStoreResponse
        );
    }

    async ValueTask OpenStoreAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "App store");
            return;
        }

        await Success(context, await OnMainThread(s.OpenStore));
    }

    async ValueTask RequestReviewAsync(HttpContext context)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "App review");
            return;
        }

        await Success(context, await OnMainThread(s.RequestReview));
    }

    static ValueTask Success(HttpContext context, bool success)
        => WebAppBridgeResults.Json(context, new SuccessResponse(success), AppBridgeJsonContext.Default.SuccessResponse);

    /// <summary>Requests arrive on server threads; anything that presents UI has to run on the main one.</summary>
    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    void OnOrientationChanged(object? sender, DisplayOrientation orientation)
        => this.events.Publish("app.orientation", new OrientationEvent(orientation), AppBridgeJsonContext.Default.OrientationEvent);

    void OnCultureChanged(object? sender, CultureInfo culture)
        => this.events.Publish("app.culture", new CultureEvent(culture.Name), AppBridgeJsonContext.Default.CultureEvent);

    void OnTimeZoneChanged(object? sender, TimeZoneInfo timeZone)
        => this.events.Publish("app.timezone", new TimeZoneEvent(timeZone.Id), AppBridgeJsonContext.Default.TimeZoneEvent);

    public void Dispose()
    {
        if (this.app is null)
            return;

        this.app.OrientationChanged -= this.OnOrientationChanged;
        this.app.CultureChanged -= this.OnCultureChanged;
        this.app.TimeZoneChanged -= this.OnTimeZoneChanged;
    }
}

public static class AppSupportBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/app</c> and registers <c>IAppSupport</c> — there is nothing else to call. Pass
    /// <paramref name="appStore"/> to register <c>IAppStore</c> too and light up the store endpoints.
    /// <code>
    /// builder.AddAppSupportBridge(store => store.AppleAppId = "123456789");
    /// </code>
    /// </summary>
    public static MauiAppBuilder AddAppSupportBridge(this MauiAppBuilder builder, Action<AppStoreOptions>? appStore = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddAppSupport();

        if (appStore is not null)
            builder.AddAppStore(appStore);

        builder.Services.AddWebAppBridge<AppSupportBridge>();
        return builder;
    }
}

public sealed record AppInfoResponse(
    string AppVersion,
    string DeviceManufacturer,
    string DeviceModel,
    string? PlatformVersion,
    string Platform,
    string DeviceIdiom,
    DisplayOrientation Orientation,
    string Culture,
    string TimeZone
);

public sealed record OrientationRequest(DisplayOrientation Orientation);
public sealed record OpenBrowserRequest(string Uri, bool ShowTitle = true, BrowserLaunchMode LaunchMode = BrowserLaunchMode.SystemPreferred);
public sealed record OpenMapRequest(double Latitude, double Longitude, NavigationMode NavigationMode = NavigationMode.None);
public sealed record SuccessResponse(bool Success);

public sealed record AppStoreResponse(
    string StoreVersion,
    string CurrentVersion,
    bool NeedsUpdate,
    string StoreUrl,
    string? ReleaseNotes,
    DateTimeOffset? ReleasedAt,
    double? AverageRating,
    long? RatingCount,
    string? MinimumOsVersion
);

public sealed record OrientationEvent(DisplayOrientation Orientation);
public sealed record CultureEvent(string Culture);
public sealed record TimeZoneEvent(string TimeZone);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppInfoResponse))]
[JsonSerializable(typeof(OrientationRequest))]
[JsonSerializable(typeof(OpenBrowserRequest))]
[JsonSerializable(typeof(OpenMapRequest))]
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(AppStoreResponse))]
[JsonSerializable(typeof(OrientationEvent))]
[JsonSerializable(typeof(CultureEvent))]
[JsonSerializable(typeof(TimeZoneEvent))]
partial class AppBridgeJsonContext : JsonSerializerContext;
