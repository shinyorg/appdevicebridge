using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// The app and the device it runs on, over Shiny.Core: information, orientation, the screen, settings, the app store,
/// launch at login, battery and connectivity. Each feature fails with 501 on its own where the platform lacks it.
/// </summary>
[BridgeClient("app", typeof(AppJsonContext))]
public interface IAppBridge
{
    /// <summary>The app's version and the device, platform, orientation, culture and time zone.</summary>
    [BridgeGet("info")]
    Task<AppInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the app to an orientation and holds it there. False where the platform cannot.</summary>
    [BridgePost("orientation")]
    Task<AppActionResult> SetOrientationAsync(OrientationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Unlocks the orientation.</summary>
    [BridgeDelete("orientation")]
    Task<AppActionResult> ResetOrientationAsync(CancellationToken cancellationToken = default);

    /// <summary>The main display.</summary>
    [BridgeGet("screen")]
    Task<ScreenInfo> GetScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the app's page in the OS settings, or the system settings where there is no such page.</summary>
    [BridgePost("settings")]
    Task OpenSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The app's store listing. Fails with 501 unless the app registered the store, 404 when it is not listed.</summary>
    [BridgeGet("store")]
    Task<AppStoreListing> GetStoreListingAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the app's store listing.</summary>
    [BridgePost("store/open")]
    Task<AppActionResult> OpenStoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the OS to show its review prompt, which it may decline to do.</summary>
    [BridgePost("store/review")]
    Task<AppActionResult> RequestReviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the store listing on its reviews, where the store has such a page.</summary>
    [BridgePost("store/review-page")]
    Task<AppActionResult> OpenReviewPageAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the app launches at login. Always answers, so a page can decide whether to show the option.</summary>
    [BridgeGet("startup")]
    Task<StartupStatus> GetStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches the app at login. A state of <see cref="StartupState.RequiresApproval"/> or disabled did not fail: the
    /// user finishes it in the OS, which <see cref="OpenStartupSettingsAsync"/> opens. Fails with 409 when the OS refuses.
    /// </summary>
    [BridgePost("startup/registration")]
    Task<StartupStatus> RegisterStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops launching the app at login.</summary>
    [BridgeDelete("startup/registration")]
    Task<StartupStatus> UnregisterStartupAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the OS's login items screen. <see cref="StartupSettingsResult.Opened"/> is false where there is none.</summary>
    [BridgePost("startup/settings")]
    Task<StartupSettingsResult> OpenStartupSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Charge, charging state, power source and energy saver.</summary>
    [BridgeGet("battery")]
    Task<BatteryInfo> GetBatteryAsync(CancellationToken cancellationToken = default);

    /// <summary>Network access and the kinds of connection in use.</summary>
    [BridgeGet("connectivity")]
    Task<ConnectivityInfo> GetConnectivityAsync(CancellationToken cancellationToken = default);

    /// <summary>The app turned between portrait and landscape.</summary>
    [BridgeEvent("app.orientation")]
    Task<IAsyncDisposable> OnOrientationChangedAsync(Func<OrientationChanged, Task> handler);

    /// <summary>The main display changed: a rotation, a resolution or scale change, or another display.</summary>
    [BridgeEvent("app.screen")]
    Task<IAsyncDisposable> OnScreenChangedAsync(Func<ScreenInfo, Task> handler);

    /// <summary>The device's culture changed.</summary>
    [BridgeEvent("app.culture")]
    Task<IAsyncDisposable> OnCultureChangedAsync(Func<CultureChanged, Task> handler);

    /// <summary>The device's time zone changed.</summary>
    [BridgeEvent("app.timezone")]
    Task<IAsyncDisposable> OnTimeZoneChangedAsync(Func<TimeZoneChanged, Task> handler);

    /// <summary>The charge, charging state, power source or energy saver changed.</summary>
    [BridgeEvent("app.battery")]
    Task<IAsyncDisposable> OnBatteryChangedAsync(Func<BatteryInfo, Task> handler);

    /// <summary>Network access or the connections in use changed.</summary>
    [BridgeEvent("app.connectivity")]
    Task<IAsyncDisposable> OnConnectivityChangedAsync(Func<ConnectivityInfo, Task> handler);
}

public enum DisplayOrientation { Unknown, Portrait, Landscape }

public enum StartupState { NotSupported, NotRegistered, Enabled, DisabledByUser, DisabledByPolicy, RequiresApproval }

public enum NetworkAccess { Unknown, None, Local, ConstrainedInternet, Internet }

public enum ConnectionProfile { Unknown, Bluetooth, Cellular, Ethernet, WiFi }

public enum BatteryState { Unknown, Charging, Discharging, Full, NotCharging, NotPresent }

public enum BatteryPowerSource { Unknown, Battery, AC, Usb, Wireless }

public enum EnergySaverStatus { Unknown, On, Off }

/// <param name="DeviceIdiom">Unknown, Phone, Tablet, Desktop, TV, Watch or Car.</param>
/// <param name="Platform">Android, iOS, MacCatalyst, macOS, Windows or Linux.</param>
/// <param name="Culture">A culture name such as <c>en-US</c>.</param>
/// <param name="TimeZone">A time zone id.</param>
/// <param name="ApplicationIdentifier">
/// The bundle identifier on Apple, the package name on Android, the package identity name on a packaged Windows app,
/// the Flatpak app id or Snap name on Linux — null when the app has no such identity.
/// </param>
public sealed record AppInfo(
    string AppVersion,
    string DeviceManufacturer,
    string DeviceModel,
    string? PlatformVersion,
    string Platform,
    string DeviceIdiom,
    DisplayOrientation Orientation,
    string Culture,
    string TimeZone,
    string? ApplicationIdentifier = null
);

/// <param name="Success">False when the platform could not do it.</param>
public sealed record AppActionResult(bool Success);

public sealed record OrientationRequest(DisplayOrientation Orientation);

/// <param name="Width">Physical pixels, as currently oriented.</param>
/// <param name="Height">Physical pixels, as currently oriented.</param>
/// <param name="Density">Physical pixels per device-independent unit.</param>
/// <param name="RefreshRate">Hertz, or 0 where the platform does not report it.</param>
public sealed record ScreenInfo(double Width, double Height, double Density, DisplayOrientation Orientation, double RefreshRate);

public sealed record AppStoreListing(
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

/// <param name="Supported">Whether this platform has a launch-at-login list.</param>
/// <param name="State">Read back from the OS every time: the user can turn it off there.</param>
public sealed record StartupStatus(bool Supported, StartupState State);

public sealed record StartupSettingsResult(bool Opened);

/// <param name="ChargeLevel">0–1.</param>
public sealed record BatteryInfo(double ChargeLevel, BatteryState State, BatteryPowerSource PowerSource, EnergySaverStatus EnergySaverStatus);

public sealed record ConnectivityInfo(NetworkAccess NetworkAccess, IReadOnlyList<ConnectionProfile> ConnectionProfiles);

public sealed record OrientationChanged(DisplayOrientation Orientation);

public sealed record CultureChanged(string Culture);

public sealed record TimeZoneChanged(string TimeZone);

/// <summary>Serialization for every app contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppInfo))]
[JsonSerializable(typeof(AppActionResult))]
[JsonSerializable(typeof(OrientationRequest))]
[JsonSerializable(typeof(ScreenInfo))]
[JsonSerializable(typeof(AppStoreListing))]
[JsonSerializable(typeof(StartupStatus))]
[JsonSerializable(typeof(StartupSettingsResult))]
[JsonSerializable(typeof(BatteryInfo))]
[JsonSerializable(typeof(ConnectivityInfo))]
[JsonSerializable(typeof(OrientationChanged))]
[JsonSerializable(typeof(CultureChanged))]
[JsonSerializable(typeof(TimeZoneChanged))]
public partial class AppJsonContext : JsonSerializerContext;
