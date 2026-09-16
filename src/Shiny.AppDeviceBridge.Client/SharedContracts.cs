namespace Shiny.AppDeviceBridge.Client;

/// <summary>Where a permission stands. Reported by every bridge that needs one; the names match Shiny's own.</summary>
public enum AccessState
{
    /// <summary>Not yet asked, or the platform will not say until asked.</summary>
    Unknown,

    NotSupported,

    /// <summary>The app is missing the manifest entry or usage description the permission needs.</summary>
    NotSetup,

    /// <summary>The feature is switched off on the device.</summary>
    Disabled,

    /// <summary>Granted in part — foreground but not background location, a limited photo selection.</summary>
    Restricted,

    Denied,

    Available
}

/// <summary>
/// A file the page can reach through the files bridge: a named root and a path inside it. Bridges that take or return
/// a file use this, so nothing reaches the page that <c>/_bridge/files</c> would not also serve.
/// </summary>
public sealed record BridgeFile(string Root, string Path);

/// <summary>
/// For native bridges mapping a platform enum onto its contract twin, which carries the same member names. By name, not
/// by value: the two are declared independently, and matching names is what the wire already relies on.
/// </summary>
public static class BridgeEnum
{
    /// <summary>
    /// The member of <typeparamref name="TTo"/> with the same name, or <typeparamref name="TTo"/>'s default when the
    /// platform has grown a member the contract does not know — a new OS state should not turn a read into a 500.
    /// </summary>
    public static TTo Convert<TFrom, TTo>(TFrom value)
        where TFrom : struct, Enum
        where TTo : struct, Enum
        => Enum.TryParse<TTo>(value.ToString(), out var converted) ? converted : default;
}
