using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;

namespace Shiny.AppDeviceBridge.AppSupport;

/// <summary>
/// <c>/_bridge/app</c> — device and app information, orientation, the browser, maps, settings, the app store
/// and launch-at-login, over Shiny.Extensions.MauiHosting (see AppSupportBridge.Startup.cs) — plus sharing,
/// haptics, connectivity, battery, the screen and the clipboard over .NET MAUI Essentials (see
/// AppSupportBridge.Device.cs).
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
/// GET    /_bridge/app/startup
/// POST   /_bridge/app/startup/registration
/// DELETE /_bridge/app/startup/registration
/// POST   /_bridge/app/startup/settings
///
/// events: app.orientation, app.culture, app.timezone
/// </code>
/// </summary>
public sealed partial class AppSupportBridge : IWebAppBridge, IDisposable
{
    readonly IAppSupport? app;
    readonly IAppStore? store;
    readonly WebAppFileRoots? fileRoots;
    readonly WebAppEventHub events;
    bool disposed;

    public AppSupportBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.app = services.GetOptionalService<IAppSupport>();
        this.store = services.GetOptionalService<IAppStore>();
        this.startup = services.GetOptionalService<IStartupService>();
        this.fileRoots = services.GetOptionalService<WebAppFileRoots>();
        this.events = events;

        if (this.app is not null)
        {
            this.app.OrientationChanged += this.OnOrientationChanged;
            this.app.CultureChanged += this.OnCultureChanged;
            this.app.TimeZoneChanged += this.OnTimeZoneChanged;
        }

        events.SubscribersChanged += this.UpdateWatchers;
    }

    public string Name => "app";

    public bool IsSupported => this.app is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        routes
            .MapGet("/info", this.InfoAsync)
            .MapPost("/orientation", this.SetOrientationAsync)
            .MapDelete("/orientation", this.ResetOrientationAsync)
            .MapPost("/browser", this.OpenBrowserAsync)
            .MapPost("/map", this.OpenMapAsync)
            .MapPost("/settings", this.OpenSettingsAsync)
            .MapGet("/store", this.StoreAsync)
            .MapPost("/store/open", this.OpenStoreAsync)
            .MapPost("/store/review", this.RequestReviewAsync);

        this.MapDevice(routes);
        this.MapStartup(routes);
    }

    ValueTask InfoAsync(HttpContext context)
    {
        if (this.app is not { } a)
            return WebAppBridgeResults.NotSupported(context, "App support");

        var info = new Contracts.AppInfo(
            a.AppVersion.ToString(),
            a.DeviceManufacturer,
            a.DeviceModel,
            a.PlatformVersion?.ToString(),
            a.Platform,
            a.DeviceIdiom.ToString(),
            Convert<DisplayOrientation, Contracts.DisplayOrientation>(a.CurrentOrientation),
            a.CurrentCulture.Name,
            a.CurrentTimeZone.Id
        );

        return WebAppBridgeResults.Json(context, info, Contracts.AppJsonContext.Default.AppInfo);
    }

    async ValueTask SetOrientationAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Orientation");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.OrientationRequest) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"orientation\": \"Portrait\" | \"Landscape\" }.");
            return;
        }

        var success = await OnMainThread(() => a.SetOrientation(Convert<Contracts.DisplayOrientation, DisplayOrientation>(body.Orientation)));
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

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.OpenBrowserRequest);

        // The page can ask for web and contact links, not arbitrary schemes that open other apps.
        if (body is null
            || !Uri.TryCreate(body.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "mailto" or "tel"))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"uri\": \"https://…\" } with an http, https, mailto or tel URI.");
            return;
        }

        var success = await OnMainThread(() => a.OpenBrowser(uri.AbsoluteUri, body.ShowTitle, Convert<Contracts.BrowserLaunchMode, BrowserLaunchMode>(body.LaunchMode)));
        await Success(context, success);
    }

    async ValueTask OpenMapAsync(HttpContext context)
    {
        if (this.app is not { } a)
        {
            await WebAppBridgeResults.NotSupported(context, "Maps");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.AppJsonContext.Default.OpenMapRequest);
        if (body is null || body.Latitude is < -90 or > 90 || body.Longitude is < -180 or > 180)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"latitude\": …, \"longitude\": … } within range.");
            return;
        }

        var success = await OnMainThread(() => a.OpenMap(body.Latitude, body.Longitude, Convert<Contracts.NavigationMode, NavigationMode>(body.NavigationMode)));
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
            new Contracts.AppStoreListing(
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
            Contracts.AppJsonContext.Default.AppStoreListing
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
        => WebAppBridgeResults.Json(context, new Contracts.AppActionResult(success), Contracts.AppJsonContext.Default.AppActionResult);

    /// <summary>Requests arrive on server threads; anything that presents UI has to run on the main one.</summary>
    static Task<T> OnMainThread<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(action) : action();

    /// <summary>By name: the contracts mirror the platform enums without referencing them.</summary>
    static TTo Convert<TFrom, TTo>(TFrom value) where TFrom : struct, Enum where TTo : struct, Enum
        => BridgeEnum.Convert<TFrom, TTo>(value);

    void OnOrientationChanged(object? sender, DisplayOrientation orientation)
        => this.events.Publish(
            "app.orientation",
            new Contracts.OrientationChanged(Convert<DisplayOrientation, Contracts.DisplayOrientation>(orientation)),
            Contracts.AppJsonContext.Default.OrientationChanged
        );

    void OnCultureChanged(object? sender, CultureInfo culture)
        => this.events.Publish("app.culture", new Contracts.CultureChanged(culture.Name), Contracts.AppJsonContext.Default.CultureChanged);

    void OnTimeZoneChanged(object? sender, TimeZoneInfo timeZone)
        => this.events.Publish("app.timezone", new Contracts.TimeZoneChanged(timeZone.Id), Contracts.AppJsonContext.Default.TimeZoneChanged);

    public void Dispose()
    {
        lock (this.watchGate)
            this.disposed = true;

        this.events.SubscribersChanged -= this.UpdateWatchers;
        this.UpdateWatchers();

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
    /// Adds <c>/_bridge/app</c> and <c>/_bridge/sensors</c> (accelerometer, gyroscope, magnetometer, compass, barometer
    /// and orientation), and registers <c>IAppSupport</c> and <c>IStartupService</c> — there is nothing else to call. Pass <paramref name="appStore"/> to register <c>IAppStore</c> too and light up the store
    /// endpoints, and <paramref name="startup"/> to configure the launch-at-login entry.
    /// <code>
    /// builder.AddAppSupportBridge(
    ///     store => store.AppleAppId = "123456789",
    ///     startup => startup.Arguments.Add("--autostart")
    /// );
    /// </code>
    /// <para>
    /// The startup endpoints are always mapped, because <c>IStartupService</c> needs no configuration to work and
    /// does nothing until the page asks it to. On a platform with no startup list they report themselves
    /// unsupported rather than disappearing, so a shared web app can keep the call in and hide the UI.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddAppSupportBridge(
        this MauiAppBuilder builder,
        Action<AppStoreOptions>? appStore = null,
        Action<StartupServiceOptions>? startup = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddAppSupport();
        builder.AddStartupService(startup);

        if (appStore is not null)
            builder.AddAppStore(appStore);

        builder.Services.AddWebAppBridge<AppSupportBridge>();
        builder.Services.AddWebAppBridge<SensorsBridge>();
        return builder;
    }
}
