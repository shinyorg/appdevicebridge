using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppStore;
using Shiny.Infrastructure;
using Shiny.Net;
using Shiny.Net.HttpServer;
using Shiny.Power;
using Contracts = Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// <c>/_bridge/app</c> — the app and the device it runs on, over Shiny.Core: <see cref="IPlatform"/> for the device, culture,
/// time zone and settings, <see cref="IScreenInfo"/> for the screen and orientation, <see cref="IAppStartupInstaller"/> for
/// launch at login, <see cref="IAppStore"/> (Shiny.AppStore) for the store, and <see cref="IBattery"/> and
/// <see cref="IConnectivity"/> where the host registered them.
/// <code>
/// GET    /_bridge/app/info
/// POST   /_bridge/app/orientation          { "orientation": "Portrait" }
/// DELETE /_bridge/app/orientation
/// GET    /_bridge/app/screen
/// POST   /_bridge/app/settings
/// GET    /_bridge/app/store                (needs a store)
/// POST   /_bridge/app/store/open
/// POST   /_bridge/app/store/review
/// POST   /_bridge/app/store/review-page
/// GET    /_bridge/app/startup
/// POST   /_bridge/app/startup/registration
/// DELETE /_bridge/app/startup/registration
/// POST   /_bridge/app/startup/settings
/// GET    /_bridge/app/battery
/// GET    /_bridge/app/connectivity
///
/// events: app.orientation, app.screen, app.culture, app.timezone, app.battery, app.connectivity
/// </code>
/// <para>
/// Launch at login is desktop only: Windows writes the app under <c>HKCU\…\CurrentVersion\Run</c> (unpackaged apps only —
/// the OS virtualizes that key for MSIX), macOS 13+ submits the running bundle to <c>SMAppService</c>, and Linux writes
/// <c>~/.config/autostart/{Identifier}.desktop</c> or a systemd unit. Mobile has no such list and answers
/// <c>{ "supported": false, "state": "NotSupported" }</c>. The state is read back from the OS on every call rather than
/// remembered, because the user can turn a registered app off in Task Manager, System Settings or Login Items.
/// </para>
/// </summary>
public sealed class AppBridge : IWebAppBridge
{
    readonly IPlatform? platform;
    readonly IScreenInfo? screen;
    readonly IAppStore? store;
    readonly IAppStartupInstaller? startup;
    readonly IBattery? battery;
    readonly IConnectivity? connectivity;
    readonly IWebAppMainThread mainThread;

    public AppBridge(IServiceProvider services)
    {
        this.platform = services.GetOptionalService<IPlatform>();
        this.screen = services.GetOptionalService<IScreenInfo>();
        this.store = services.GetOptionalService<IAppStore>();
        this.startup = services.GetOptionalService<IAppStartupInstaller>();
        this.battery = services.GetOptionalService<IBattery>();
        this.connectivity = services.GetOptionalService<IConnectivity>();
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
    }

    public string Name => "app";

    public bool IsSupported => this.platform is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/info", this.InfoAsync)
        .MapPost("/orientation", this.SetOrientationAsync)
        .MapDelete("/orientation", this.ResetOrientationAsync)
        .MapGet("/screen", this.ScreenAsync)
        .MapPost("/settings", this.OpenSettingsAsync)
        .MapGet("/store", this.StoreAsync)
        .MapPost("/store/open", ctx => this.WithStore(ctx, "App store", s => s.OpenStore()))
        .MapPost("/store/review", ctx => this.WithStore(ctx, "App review", s => s.RequestReview()))
        .MapPost("/store/review-page", ctx => this.WithStore(ctx, "App review", s => s.OpenReviewPage()))
        .MapGet("/startup", this.StartupStateAsync)
        .MapPost("/startup/registration", ctx => this.WithStartup(ctx, s => s.Register(ctx.RequestAborted)))
        .MapDelete("/startup/registration", ctx => this.WithStartup(ctx, s => s.Unregister(ctx.RequestAborted)))
        .MapPost("/startup/settings", this.OpenStartupSettingsAsync)
        .MapGet("/battery", this.BatteryAsync)
        .MapGet("/connectivity", this.ConnectivityAsync)
        .MapEvent("app.orientation", this.OrientationChanges, AppJsonContext.Default.OrientationChanged)
        .MapEvent("app.screen", this.ScreenChanges, AppJsonContext.Default.ScreenInfo)
        .MapEvent("app.culture", this.CultureChanges, AppJsonContext.Default.CultureChanged)
        .MapEvent("app.timezone", this.TimeZoneChanges, AppJsonContext.Default.TimeZoneChanged)
        .MapEvent("app.battery", this.BatteryChanges, AppJsonContext.Default.BatteryInfo)
        .MapEvent("app.connectivity", this.ConnectivityChanges, AppJsonContext.Default.ConnectivityInfo);

    ValueTask InfoAsync(HttpContext context)
    {
        if (this.platform is not { } p)
            return WebAppBridgeResults.NotSupported(context, "App info");

        var info = new AppInfo(
            p.AppVersion.ToString(),
            p.DeviceManufacturer,
            p.DeviceModel,
            p.PlatformVersion?.ToString(),
            p.Platform,
            p.DeviceFormFactor.ToString(),
            ToContract(this.screen?.Orientation ?? DeviceOrientation.Unknown),
            p.CurrentCulture.Name,
            p.CurrentTimeZone.Id,
            p.ApplicationIdentifier
        );

        return WebAppBridgeResults.Json(context, info, AppJsonContext.Default.AppInfo);
    }

    async ValueTask SetOrientationAsync(HttpContext context)
    {
        if (this.screen is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Orientation");
            return;
        }

        if (await WebAppBridgeResults.ReadBodyAsync(context, AppJsonContext.Default.OrientationRequest) is not { } body || !Enum.IsDefined(body.Orientation))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"orientation\": \"Portrait\" | \"Landscape\" }.");
            return;
        }

        var orientation = BridgeEnum.Convert<Contracts.DisplayOrientation, DeviceOrientation>(body.Orientation);
        await Success(context, await this.mainThread.InvokeAsync(() => s.SetOrientation(orientation)));
    }

    async ValueTask ResetOrientationAsync(HttpContext context)
    {
        if (this.screen is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Orientation");
            return;
        }

        await Success(context, await this.mainThread.InvokeAsync(s.ResetOrientation));
    }

    ValueTask ScreenAsync(HttpContext context)
        => this.screen is { } s
            ? WebAppBridgeResults.Json(context, ToContract(s.Metrics), AppJsonContext.Default.ScreenInfo)
            : WebAppBridgeResults.NotSupported(context, "Screen info");

    async ValueTask OpenSettingsAsync(HttpContext context)
    {
        if (this.platform is not { } p)
        {
            await WebAppBridgeResults.NotSupported(context, "App settings");
            return;
        }

        await this.mainThread.InvokeAsync(() =>
        {
            p.OpenAppSettings();
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
            new AppStoreListing(
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
            AppJsonContext.Default.AppStoreListing
        );
    }

    async ValueTask WithStore(HttpContext context, string feature, Func<IAppStore, Task<bool>> action)
    {
        if (this.store is not { } s)
        {
            await WebAppBridgeResults.NotSupported(context, feature);
            return;
        }

        // Each presents UI, or hands off to another app.
        await Success(context, await this.mainThread.InvokeAsync(() => action(s)));
    }

    /// <summary>Always answers, so a page can decide whether to show a "run at startup" toggle at all.</summary>
    ValueTask StartupStateAsync(HttpContext context)
        => this.startup is { IsSupported: true } s
            ? StartupResult(context, () => s.GetState(context.RequestAborted))
            : WebAppBridgeResults.Json(context, new StartupStatus(false, StartupState.NotSupported), AppJsonContext.Default.StartupStatus);

    async ValueTask OpenStartupSettingsAsync(HttpContext context)
    {
        if (this.startup is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Startup registration");
            return;
        }

        // Presents an OS window; Linux has nothing to open and says so.
        var opened = await this.mainThread.InvokeAsync(s.OpenSettings);
        await WebAppBridgeResults.Json(context, new StartupSettingsResult(opened), AppJsonContext.Default.StartupSettingsResult);
    }

    async ValueTask WithStartup(HttpContext context, Func<IAppStartupInstaller, Task<AppStartupState>> action)
    {
        if (this.startup is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Startup registration");
            return;
        }

        await StartupResult(context, () => action(s));
    }

    /// <summary>
    /// The OS rejecting a registration, and a misconfigured identifier or executable path, both surface as
    /// <see cref="InvalidOperationException"/>; the page gets one code it can switch on.
    /// </summary>
    static async ValueTask StartupResult(HttpContext context, Func<Task<AppStartupState>> action)
    {
        try
        {
            var state = BridgeEnum.Convert<AppStartupState, StartupState>(await action());
            await WebAppBridgeResults.Json(context, new StartupStatus(true, state), AppJsonContext.Default.StartupStatus);
        }
        catch (InvalidOperationException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "startup_failed", ex.Message);
        }
    }

    // UIDevice battery monitoring is main-thread only, so the battery is read and watched there.

    async ValueTask BatteryAsync(HttpContext context)
    {
        if (this.battery is not { } b)
        {
            await WebAppBridgeResults.NotSupported(context, "Battery");
            return;
        }

        var info = await this.mainThread.InvokeAsync(() => Task.FromResult(ToContract(b)));
        await WebAppBridgeResults.Json(context, info, AppJsonContext.Default.BatteryInfo);
    }

    ValueTask ConnectivityAsync(HttpContext context)
        => this.connectivity is { } c
            ? WebAppBridgeResults.Json(context, ToContract(c), AppJsonContext.Default.ConnectivityInfo)
            : WebAppBridgeResults.NotSupported(context, "Connectivity");

    static ValueTask Success(HttpContext context, bool success)
        => WebAppBridgeResults.Json(context, new AppActionResult(success), AppJsonContext.Default.AppActionResult);

    // Shiny starts each native listener with the first subscriber and stops it with the last, so a stream hooks its
    // event only while it runs. A service the host did not register fails that stream alone.

    IAsyncEnumerable<OrientationChanged> OrientationChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<OrientationChanged>(emit =>
        {
            var s = Require(this.screen, "Screen info");
            EventHandler<DeviceOrientation> handler = (_, o) => emit(new(ToContract(o)));
            s.OrientationChanged += handler;
            return () => s.OrientationChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<ScreenInfo> ScreenChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<ScreenInfo>(emit =>
        {
            var s = Require(this.screen, "Screen info");
            EventHandler<ScreenMetrics> handler = (_, m) => emit(ToContract(m));
            s.MetricsChanged += handler;
            return () => s.MetricsChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<CultureChanged> CultureChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<CultureChanged>(emit =>
        {
            var p = Require(this.platform, "App info");
            EventHandler<CultureInfo> handler = (_, culture) => emit(new(culture.Name));
            p.CultureChanged += handler;
            return () => p.CultureChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<TimeZoneChanged> TimeZoneChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<TimeZoneChanged>(emit =>
        {
            var p = Require(this.platform, "App info");
            EventHandler<TimeZoneInfo> handler = (_, timeZone) => emit(new(timeZone.Id));
            p.TimeZoneChanged += handler;
            return () => p.TimeZoneChanged -= handler;
        }, cancellationToken);

    IAsyncEnumerable<BatteryInfo> BatteryChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<BatteryInfo>(async emit =>
        {
            var b = Require(this.battery, "Battery");
            EventHandler handler = (_, _) => emit(ToContract(b));

            await this.mainThread.InvokeAsync(() =>
            {
                b.Changed += handler;
                return Task.FromResult(true);
            }).ConfigureAwait(false);

            return () => this.mainThread.InvokeAsync(() =>
            {
                b.Changed -= handler;
                return Task.FromResult(true);
            });
        }, cancellationToken);

    IAsyncEnumerable<ConnectivityInfo> ConnectivityChanges(CancellationToken cancellationToken)
        => WebAppEventStream.FromEvent<ConnectivityInfo>(emit =>
        {
            var c = Require(this.connectivity, "Connectivity");
            EventHandler handler = (_, _) => emit(ToContract(c));
            c.Changed += handler;
            return () => c.Changed -= handler;
        }, cancellationToken);

    static T Require<T>(T? service, string feature) where T : class
        => service ?? throw new NotSupportedException($"{feature} is not registered.");

    static Contracts.DisplayOrientation ToContract(DeviceOrientation orientation)
        => BridgeEnum.Convert<DeviceOrientation, Contracts.DisplayOrientation>(orientation);

    internal static ScreenInfo ToContract(ScreenMetrics m)
        => new(m.Width, m.Height, m.Density, ToContract(m.Orientation), m.RefreshRate);

    internal static BatteryInfo ToContract(IBattery battery) => new(
        battery.Level,
        battery.Status switch
        {
            // Shiny's "no battery detected"; the contract has always called it NotPresent.
            Shiny.Power.BatteryState.None => Contracts.BatteryState.NotPresent,
            var state => BridgeEnum.Convert<Shiny.Power.BatteryState, Contracts.BatteryState>(state)
        },
        BridgeEnum.Convert<Shiny.Power.BatteryPowerSource, Contracts.BatteryPowerSource>(battery.PowerSource),
        BridgeEnum.Convert<Shiny.Power.EnergySaverStatus, Contracts.EnergySaverStatus>(battery.EnergySaverStatus)
    );

    internal static ConnectivityInfo ToContract(IConnectivity connectivity)
    {
        var types = connectivity.ConnectionTypes;
        var profiles = new List<ConnectionProfile>(4);

        if (types.HasFlag(ConnectionTypes.Wifi))
            profiles.Add(ConnectionProfile.WiFi);
        if (types.HasFlag(ConnectionTypes.Wired))
            profiles.Add(ConnectionProfile.Ethernet);
        if (types.HasFlag(ConnectionTypes.Cellular))
            profiles.Add(ConnectionProfile.Cellular);
        if (types.HasFlag(ConnectionTypes.Bluetooth))
            profiles.Add(ConnectionProfile.Bluetooth);
        if (types.HasFlag(ConnectionTypes.Unknown))
            profiles.Add(ConnectionProfile.Unknown);

        return new(BridgeEnum.Convert<Shiny.Net.NetworkAccess, Contracts.NetworkAccess>(connectivity.Access), profiles);
    }
}

public static class AppBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/app</c> over Shiny.Core, registering launch at login (<c>IAppStartupInstaller</c>) and Shiny's core
    /// services where no host has. Pass <paramref name="appStore"/> to register Shiny.AppStore's <c>IAppStore</c> and light up
    /// the store endpoints, and <paramref name="startup"/> to configure the launch-at-login entry.
    /// <code>
    /// bridge.AddAppBridge(
    ///     store => store.AppleAppId = "123456789",
    ///     startup => startup.Arguments.Add("--autostart")
    /// );
    /// </code>
    /// <para>
    /// Battery and connectivity come from Shiny.Core's <c>IBattery</c> and <c>IConnectivity</c>. A MAUI app's
    /// <c>UseAppDeviceBridge</c> registers both; any other host calls <c>services.AddBattery()</c> and
    /// <c>services.AddConnectivity()</c> itself, or those endpoints answer 501.
    /// </para>
    /// </summary>
    public static TBuilder AddAppBridge<TBuilder>(
        this TBuilder bridge,
        Action<AppStoreOptions>? appStore = null,
        Action<AppStartupOptions>? startup = null
    ) where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        // A MAUI host has already registered these, and this is then a no-op; a host that is just .NET has not.
        bridge.Services.AddShinyCoreServices();
        bridge.Services.AddAppStartupInstaller(startup);

        if (appStore is not null)
            bridge.Services.AddAppStore(appStore);

        bridge.AddBridge<AppBridge>();
        return bridge;
    }
}
