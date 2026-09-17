using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>The host itself: what is serving the page, which bridges it has, and a pending web app update.</summary>
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
}

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
public sealed record HostInfo(
    string AppId,
    string HostVersion,
    string Platform,
    string? WebAppVersion,
    WebAppOrigin? WebAppOrigin,
    string? PendingVersion,
    IReadOnlyList<BridgeCapability> Bridges,
    string? DevServer
);

/// <summary>The outcome of applying an update.</summary>
/// <param name="Applied">False when nothing was waiting.</param>
/// <param name="Version">The version now being served, when one was applied.</param>
public sealed record ApplyUpdateResult(bool Applied, string? Version);

/// <summary>What a <c>job:{name}</c> handler receives when a background job the app scheduled runs.</summary>
/// <param name="Name">The job's name, without the <c>job:</c> prefix.</param>
public sealed record JobRun(string Name);

/// <summary>Serialization for the built-in bridges' contracts, shared by the page's clients and the host.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(ApplyUpdateResult))]
[JsonSerializable(typeof(AppLink))]
[JsonSerializable(typeof(SettingsList))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<FileEntry>))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileTransfer))]
[JsonSerializable(typeof(FileRootsChanged))]
[JsonSerializable(typeof(BridgeFile))]
[JsonSerializable(typeof(JobRun))]
public partial class AppDeviceBridgeJsonContext : JsonSerializerContext;
