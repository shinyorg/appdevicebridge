using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// The host itself: what is serving the page, which bridges it has, a pending web app update, a native app update in the
/// store, and the page telling the host it has finished loading.
/// </summary>
[BridgeClient("host", typeof(AppDeviceBridgeJsonContext))]
public interface IHostBridge
{
    /// <summary>The app, the web app build being served, and every registered bridge with whether it works here.</summary>
    [BridgeGet]
    Task<HostInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches to a downloaded update, if one is waiting. The page reloads itself afterwards; the host never reloads it
    /// out from under the user.
    /// </summary>
    [BridgePost("apply-update")]
    Task<ApplyUpdateResult> ApplyUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the host the page has rendered, so it takes its splash screen down. The Blazor client sends this by itself once
    /// Blazor has started; any other page calls it when its first screen is up. Until it arrives — or the host's timeout
    /// passes — the user sees the splash screen, not a half-drawn page. Calling it again does nothing.
    /// </summary>
    [BridgePost("ready")]
    Task ReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the native app's store page, for <see cref="HostInfo.StoreUpdate"/>: through Shiny's <c>IAppStore</c> when the
    /// app registers it, otherwise the store URL the update server sent.
    /// </summary>
    [BridgePost("open-store")]
    Task<OpenStoreResult> OpenStoreAsync(CancellationToken cancellationToken = default);
}

/// <summary>A newer native app in the store, from the update server.</summary>
/// <param name="IsRequired">
/// True when this native app is below the server's minimum. A page never sees that in practice — the host stops at its splash
/// screen instead — but a page already open when a background check finds it does.
/// </param>
/// <param name="MinimumVersion">The oldest native app version the server accepts.</param>
/// <param name="LatestVersion">The version in the store.</param>
/// <param name="ReleaseNotes">What the store build changes, in the user's language when the server has it.</param>
public sealed record StoreUpdate(bool IsRequired, string? MinimumVersion, string? LatestVersion, string? ReleaseNotes);

/// <summary>The outcome of opening the store.</summary>
/// <param name="Opened">False when the host has neither a store lookup nor a store URL, or the store would not open.</param>
public sealed record OpenStoreResult(bool Opened);

/// <summary>Where the web app being served came from.</summary>
public enum WebAppOrigin
{
    /// <summary>The zip compiled into the app.</summary>
    Baseline,

    /// <summary>A verified download.</summary>
    Installed
}

/// <summary>A registered bridge.</summary>
/// <param name="Name">Its path under the bridge prefix — <c>calendar</c> for <c>/_bridge/calendar</c>.</param>
/// <param name="IsSupported">False where the platform has no implementation; its endpoints then answer 501.</param>
public sealed record BridgeCapability(string Name, bool IsSupported);

/// <summary>What is serving the page.</summary>
/// <param name="AppId">The app's id, as the update server knows it.</param>
/// <param name="HostVersion">The native app's version.</param>
/// <param name="Platform">android, ios, maccatalyst, macos, windows or linux.</param>
/// <param name="WebAppVersion">The web app build being served; null while pages come from a dev server.</param>
/// <param name="WebAppOrigin">Whether that build is the baseline or a download.</param>
/// <param name="PendingVersion">A downloaded update waiting for <see cref="IHostBridge.ApplyUpdateAsync"/>.</param>
/// <param name="Bridges">Every registered bridge.</param>
/// <param name="DevServer">The dev server pages are proxied from, in a debug build that uses one.</param>
/// <param name="PendingReleaseNotes">What <paramref name="PendingVersion"/> changes, in the user's language when the server has it.</param>
/// <param name="StoreUpdate">A newer native app in the store, from the last update check.</param>
public sealed record HostInfo(
    string AppId,
    string HostVersion,
    string Platform,
    string? WebAppVersion,
    WebAppOrigin? WebAppOrigin,
    string? PendingVersion,
    IReadOnlyList<BridgeCapability> Bridges,
    string? DevServer,
    string? PendingReleaseNotes,
    StoreUpdate? StoreUpdate
);

/// <summary>The outcome of applying an update.</summary>
/// <param name="Applied">False when nothing was waiting.</param>
/// <param name="Version">The version now being served, when one was applied.</param>
public sealed record ApplyUpdateResult(bool Applied, string? Version);

/// <summary>Serialization for the built-in bridges' contracts, shared by the page's clients and the host.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(ApplyUpdateResult))]
[JsonSerializable(typeof(OpenStoreResult))]
[JsonSerializable(typeof(AppLink))]
[JsonSerializable(typeof(SettingsList))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<FileEntry>))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileTransfer))]
[JsonSerializable(typeof(FileRootsChanged))]
[JsonSerializable(typeof(BridgeFile))]
[JsonSerializable(typeof(EventStreamOpened))]
[JsonSerializable(typeof(EventStreamTopics))]
[JsonSerializable(typeof(EventStreamError))]
public partial class AppDeviceBridgeJsonContext : JsonSerializerContext;
