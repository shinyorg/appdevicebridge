using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.CallDirectory.Client;

/// <summary>
/// Call blocking and caller identification from a list of numbers the app owns: iOS loads it through a Call Directory
/// extension, and Android screens calls against it. Edits are stored straight away but reach iOS only on
/// <see cref="ReloadAsync"/>, so a batch of edits costs one reload. iOS and Android; everywhere else answers 501.
/// </summary>
[BridgeClient("calldirectory", typeof(CallDirectoryJsonContext))]
public interface ICallDirectoryBridge
{
    /// <summary>Whether the call directory is turned on, and how many numbers it holds.</summary>
    [BridgeGet]
    Task<CallDirectoryStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// iOS has no prompt: this opens Settings (best effort) and answers the current state, so check again when the app
    /// is back in the foreground. Android asks for the call screening role.
    /// </summary>
    [BridgePost("access")]
    Task<CallDirectoryAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>The stored entry for a number. Fails with 404 when there is none.</summary>
    [BridgeGet("entries/{phoneNumber}")]
    Task<CallDirectoryEntry> GetEntryAsync(long phoneNumber, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces entries.</summary>
    [BridgePut("entries")]
    Task SetAsync(CallDirectoryEntriesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes entries. Numbers that are not stored are ignored.</summary>
    [BridgePost("entries/remove")]
    Task RemoveAsync(CallDirectoryRemoveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the whole list in one transaction — for a sync from a server.</summary>
    [BridgePost("entries/replace")]
    Task ReplaceAllAsync(CallDirectoryEntriesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry.</summary>
    [BridgeDelete("entries")]
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>iOS: has the system load the stored list, and answers how it went. Android reads the list live, so it only reports whether screening is on.</summary>
    [BridgePost("reload")]
    Task<CallDirectoryReload> ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a number as people write it into the form the directory stores: formatting and a leading <c>+</c> or
    /// <c>00</c> dropped, and a country code added when it has none. Fails with 400 when it is not a phone number.
    /// </summary>
    [BridgePost("normalize")]
    Task<NormalizedNumber> NormalizeAsync(NormalizeNumberRequest request, CancellationToken cancellationToken = default);
}
