using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.CallDirectory.Client;

/// <summary>What happens to calls from a number.</summary>
public enum CallDirectoryEntryType
{
    /// <summary>Calls are blocked.</summary>
    Block,

    /// <summary>Calls ring with the entry's label as the caller ID.</summary>
    Identify,

    /// <summary>
    /// Android: always rings, even when unlisted callers are blocked. iOS has no allow list, so it is stored and loaded
    /// as <see cref="Identify"/>.
    /// </summary>
    Allow
}

/// <summary>How a reload went.</summary>
public enum CallDirectoryReloadResult
{
    /// <summary>The system loaded the list.</summary>
    Success,

    /// <summary>The platform has no call directory.</summary>
    NotSupported,

    /// <summary>iOS: the user has not turned the extension on in Settings. Android: the app does not hold the call screening role.</summary>
    ExtensionDisabled,

    /// <summary>The app was built without the Call Directory extension.</summary>
    ExtensionNotFound,

    /// <summary>A load is already running. Try again shortly.</summary>
    CurrentlyLoading,

    /// <summary>The list is larger than the system accepts.</summary>
    MaximumEntriesExceeded,

    /// <summary>The extension ran out of memory or time.</summary>
    LoadingInterrupted,

    /// <summary>Anything else; the app logs the error.</summary>
    Failed
}

/// <summary>A number in the call directory.</summary>
/// <param name="PhoneNumber">
/// Country code and digits, no <c>+</c> or formatting: <c>18005550100</c>. <see cref="ICallDirectoryBridge.NormalizeAsync"/>
/// converts a number as people write it.
/// </param>
/// <param name="Label">The caller ID shown for <see cref="CallDirectoryEntryType.Identify"/> and <see cref="CallDirectoryEntryType.Allow"/>.</param>
public sealed record CallDirectoryEntry(long PhoneNumber, string? Label = null, CallDirectoryEntryType Type = CallDirectoryEntryType.Identify);

/// <param name="Access">
/// iOS: whether the user turned the extension on in Settings › Apps › Phone › Call Blocking &amp; Identification. Android:
/// whether the app holds the call screening role.
/// </param>
/// <param name="Count">How many entries are stored.</param>
public sealed record CallDirectoryStatus(AccessState Access, int Count);

public sealed record CallDirectoryAccessResult(AccessState Access);

public sealed record CallDirectoryEntriesRequest(IReadOnlyList<CallDirectoryEntry> Entries);

public sealed record CallDirectoryRemoveRequest(IReadOnlyList<long> PhoneNumbers);

public sealed record CallDirectoryReload(CallDirectoryReloadResult Result);

/// <param name="Number">A number as people write it: <c>+1 (800) 555-0100</c>, <c>800-555-0100</c>, <c>0044 20 7946 0000</c>.</param>
/// <param name="DefaultCountryCode">The country code for a number written without one; the app's configured default when null.</param>
public sealed record NormalizeNumberRequest(string Number, int? DefaultCountryCode = null);

public sealed record NormalizedNumber(long PhoneNumber);

/// <summary>Serialization for every call directory contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CallDirectoryStatus))]
[JsonSerializable(typeof(CallDirectoryAccessResult))]
[JsonSerializable(typeof(CallDirectoryEntry))]
[JsonSerializable(typeof(CallDirectoryEntriesRequest))]
[JsonSerializable(typeof(CallDirectoryRemoveRequest))]
[JsonSerializable(typeof(CallDirectoryReload))]
[JsonSerializable(typeof(NormalizeNumberRequest))]
[JsonSerializable(typeof(NormalizedNumber))]
public partial class CallDirectoryJsonContext : JsonSerializerContext;
