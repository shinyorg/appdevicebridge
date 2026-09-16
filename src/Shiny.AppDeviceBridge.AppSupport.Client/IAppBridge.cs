using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AppSupport.Client;

/// <summary>
/// The app and the device it runs on: information, orientation, the system browser, maps, settings, the app store and
/// launch at login, plus sharing, haptics, connectivity, battery, the screen and the clipboard. Each feature fails with
/// 501 on its own where the platform lacks it.
/// </summary>
[BridgeClient("app", typeof(AppJsonContext))]
public interface IAppBridge
{
    /// <summary>The app's version and the device, platform, orientation, culture and time zone.</summary>
    [BridgeGet("info")]
    Task<AppInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Locks the orientation.</summary>
    [BridgePost("orientation")]
    Task<AppActionResult> SetOrientationAsync(OrientationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Unlocks the orientation.</summary>
    [BridgeDelete("orientation")]
    Task<AppActionResult> ResetOrientationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an http, https, mailto or tel URI — in an in-app browser sheet, or the default browser with
    /// <see cref="BrowserLaunchMode.External"/>.
    /// </summary>
    [BridgePost("browser")]
    Task<AppActionResult> OpenBrowserAsync(OpenBrowserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens the maps app at a location, optionally with directions.</summary>
    [BridgePost("map")]
    Task<AppActionResult> OpenMapAsync(OpenMapRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens the app's page in the OS settings.</summary>
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

    /// <summary>Opens the share sheet for text and a link, or for files in the page's file roots.</summary>
    [BridgePost("share")]
    Task ShareAsync(ShareRequest request, CancellationToken cancellationToken = default);

    /// <summary>Performs haptic feedback.</summary>
    [BridgePost("haptics")]
    Task PerformHapticsAsync(HapticsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Vibrates, for at most 5 seconds.</summary>
    [BridgePost("vibrate")]
    Task VibrateAsync(VibrateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops vibrating.</summary>
    [BridgeDelete("vibrate")]
    Task CancelVibrationAsync(CancellationToken cancellationToken = default);

    /// <summary>Network access and the kinds of connection in use.</summary>
    [BridgeGet("connectivity")]
    Task<ConnectivityInfo> GetConnectivityAsync(CancellationToken cancellationToken = default);

    /// <summary>Charge, charging state, power source and energy saver.</summary>
    [BridgeGet("battery")]
    Task<BatteryInfo> GetBatteryAsync(CancellationToken cancellationToken = default);

    /// <summary>The main display.</summary>
    [BridgeGet("screen")]
    Task<ScreenInfo> GetScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps the screen on.</summary>
    [BridgePut("screen/keep-awake")]
    Task KeepScreenAwakeAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets the screen sleep again.</summary>
    [BridgeDelete("screen/keep-awake")]
    Task AllowScreenSleepAsync(CancellationToken cancellationToken = default);

    /// <summary>The clipboard's text, or null text when it holds none.</summary>
    [BridgeGet("clipboard")]
    Task<ClipboardText> GetClipboardAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts text on the clipboard, at most 1 MB.</summary>
    [BridgePut("clipboard")]
    Task SetClipboardAsync(ClipboardText text, CancellationToken cancellationToken = default);

    /// <summary>Clears the clipboard.</summary>
    [BridgeDelete("clipboard")]
    Task ClearClipboardAsync(CancellationToken cancellationToken = default);

    /// <summary>The orientation changed.</summary>
    [BridgeEvent("app.orientation")]
    Task<IAsyncDisposable> OnOrientationChangedAsync(Func<OrientationChanged, Task> handler);

    /// <summary>The device's culture changed.</summary>
    [BridgeEvent("app.culture")]
    Task<IAsyncDisposable> OnCultureChangedAsync(Func<CultureChanged, Task> handler);

    /// <summary>The device's time zone changed.</summary>
    [BridgeEvent("app.timezone")]
    Task<IAsyncDisposable> OnTimeZoneChangedAsync(Func<TimeZoneChanged, Task> handler);

    /// <summary>Network access or the connections in use changed.</summary>
    [BridgeEvent("app.connectivity")]
    Task<IAsyncDisposable> OnConnectivityChangedAsync(Func<ConnectivityInfo, Task> handler);

    /// <summary>The charge, charging state or power source changed.</summary>
    [BridgeEvent("app.battery")]
    Task<IAsyncDisposable> OnBatteryChangedAsync(Func<BatteryChanged, Task> handler);

    /// <summary>Energy saver was turned on or off.</summary>
    [BridgeEvent("app.energysaver")]
    Task<IAsyncDisposable> OnEnergySaverChangedAsync(Func<EnergySaverChanged, Task> handler);
}
