using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// The device's apps and feedback, over .NET MAUI Essentials: the browser, maps, the share sheet, haptics, vibration,
/// keeping the screen awake and the clipboard. Each feature fails with 501 on its own where the platform lacks it.
/// </summary>
[BridgeClient("device", typeof(DeviceJsonContext))]
public interface IDeviceBridge
{
    /// <summary>
    /// Opens an http, https, mailto or tel URI — in an in-app browser sheet, or the default browser with
    /// <see cref="BrowserLaunchMode.External"/>.
    /// </summary>
    [BridgePost("browser")]
    Task<DeviceActionResult> OpenBrowserAsync(OpenBrowserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens the maps app at a location, optionally with directions.</summary>
    [BridgePost("map")]
    Task<DeviceActionResult> OpenMapAsync(OpenMapRequest request, CancellationToken cancellationToken = default);

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

    /// <summary>Whether the screen is being kept on.</summary>
    [BridgeGet("keep-awake")]
    Task<KeepAwakeStatus> GetKeepAwakeAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps the screen on.</summary>
    [BridgePut("keep-awake")]
    Task KeepScreenAwakeAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets the screen sleep again.</summary>
    [BridgeDelete("keep-awake")]
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
}

public enum BrowserLaunchMode
{
    /// <summary>An in-app browser sheet where the platform has one.</summary>
    SystemPreferred,

    /// <summary>The default browser app.</summary>
    External
}

public enum NavigationMode { None, Default, Bicycling, Driving, Transit, Walking }

public enum HapticFeedbackType { Click, LongPress }

/// <param name="Success">False when the platform could not do it.</param>
public sealed record DeviceActionResult(bool Success);

/// <param name="Uri">An http, https, mailto or tel URI.</param>
public sealed record OpenBrowserRequest(string Uri, bool ShowTitle = true, BrowserLaunchMode LaunchMode = BrowserLaunchMode.SystemPreferred);

public sealed record OpenMapRequest(double Latitude, double Longitude, NavigationMode NavigationMode = NavigationMode.None);

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

public sealed record KeepAwakeStatus(bool KeepAwake);

public sealed record ClipboardText(string? Text);

/// <summary>Serialization for every device contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DeviceActionResult))]
[JsonSerializable(typeof(OpenBrowserRequest))]
[JsonSerializable(typeof(OpenMapRequest))]
[JsonSerializable(typeof(ShareRequest))]
[JsonSerializable(typeof(HapticsRequest))]
[JsonSerializable(typeof(VibrateRequest))]
[JsonSerializable(typeof(KeepAwakeStatus))]
[JsonSerializable(typeof(ClipboardText))]
public partial class DeviceJsonContext : JsonSerializerContext;
