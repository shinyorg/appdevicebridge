using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AppSupport.Client;

public enum DisplayOrientation { Unknown, Portrait, Landscape }

public enum DisplayRotation { Unknown, Rotation0, Rotation90, Rotation180, Rotation270 }

public enum BrowserLaunchMode
{
    /// <summary>An in-app browser sheet where the platform has one.</summary>
    SystemPreferred,

    /// <summary>The default browser app.</summary>
    External
}

public enum NavigationMode { None, Default, Bicycling, Driving, Transit, Walking }

public enum StartupState { NotSupported, NotRegistered, Enabled, DisabledByUser, DisabledByPolicy, RequiresApproval }

public enum HapticFeedbackType { Click, LongPress }

public enum NetworkAccess { Unknown, None, Local, ConstrainedInternet, Internet }

public enum ConnectionProfile { Unknown, Bluetooth, Cellular, Ethernet, WiFi }

public enum BatteryState { Unknown, Charging, Discharging, Full, NotCharging, NotPresent }

public enum BatteryPowerSource { Unknown, Battery, AC, Usb, Wireless }

public enum EnergySaverStatus { Unknown, On, Off }

/// <param name="DeviceIdiom">Phone, Tablet, Desktop, TV, Watch and so on.</param>
/// <param name="Culture">A culture name such as <c>en-US</c>.</param>
/// <param name="TimeZone">A time zone id.</param>
public sealed record AppInfo(
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

/// <param name="Success">False when the platform could not do it.</param>
public sealed record AppActionResult(bool Success);

public sealed record OrientationRequest(DisplayOrientation Orientation);

/// <param name="Uri">An http, https, mailto or tel URI.</param>
public sealed record OpenBrowserRequest(string Uri, bool ShowTitle = true, BrowserLaunchMode LaunchMode = BrowserLaunchMode.SystemPreferred);

public sealed record OpenMapRequest(double Latitude, double Longitude, NavigationMode NavigationMode = NavigationMode.None);

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

/// <summary>
/// What to share: <see cref="Text"/> and <see cref="Uri"/>, or <see cref="Files"/> from the page's file roots — at most 20.
/// </summary>
/// <param name="Uri">An absolute http or https URI.</param>
public sealed record ShareRequest(
    string? Text = null,
    string? Uri = null,
    string? Title = null,
    string? Subject = null,
    IReadOnlyList<BridgeFile>? Files = null
);

public sealed record HapticsRequest(HapticFeedbackType Type);

/// <param name="DurationMs">Half a second when null; at most 5,000.</param>
public sealed record VibrateRequest(int? DurationMs = null);

public sealed record ClipboardText(string? Text);

public sealed record ConnectivityInfo(NetworkAccess NetworkAccess, IReadOnlyList<ConnectionProfile> ConnectionProfiles);

/// <param name="ChargeLevel">0–1.</param>
public sealed record BatteryInfo(double ChargeLevel, BatteryState State, BatteryPowerSource PowerSource, EnergySaverStatus EnergySaverStatus);

/// <param name="ChargeLevel">0–1.</param>
public sealed record BatteryChanged(double ChargeLevel, BatteryState State, BatteryPowerSource PowerSource);

public sealed record EnergySaverChanged(EnergySaverStatus EnergySaverStatus);

/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
public sealed record ScreenInfo(
    double Width,
    double Height,
    double Density,
    DisplayOrientation Orientation,
    DisplayRotation Rotation,
    float RefreshRate,
    bool KeepScreenOn
);

public sealed record OrientationChanged(DisplayOrientation Orientation);

public sealed record CultureChanged(string Culture);

public sealed record TimeZoneChanged(string TimeZone);

/// <summary>Serialization for every app contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppInfo))]
[JsonSerializable(typeof(AppActionResult))]
[JsonSerializable(typeof(OrientationRequest))]
[JsonSerializable(typeof(OpenBrowserRequest))]
[JsonSerializable(typeof(OpenMapRequest))]
[JsonSerializable(typeof(AppStoreListing))]
[JsonSerializable(typeof(StartupStatus))]
[JsonSerializable(typeof(StartupSettingsResult))]
[JsonSerializable(typeof(ShareRequest))]
[JsonSerializable(typeof(HapticsRequest))]
[JsonSerializable(typeof(VibrateRequest))]
[JsonSerializable(typeof(ClipboardText))]
[JsonSerializable(typeof(ConnectivityInfo))]
[JsonSerializable(typeof(BatteryInfo))]
[JsonSerializable(typeof(BatteryChanged))]
[JsonSerializable(typeof(EnergySaverChanged))]
[JsonSerializable(typeof(ScreenInfo))]
[JsonSerializable(typeof(OrientationChanged))]
[JsonSerializable(typeof(CultureChanged))]
[JsonSerializable(typeof(TimeZoneChanged))]
public partial class AppJsonContext : JsonSerializerContext;
