using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Gps.Client;

/// <summary>
/// GPS: permission, the last and current position, and a listener whose readings arrive as events. Android, iOS, Mac
/// Catalyst and Windows; elsewhere every call fails with 501.
/// </summary>
[BridgeClient("gps", typeof(GpsJsonContext))]
public interface IGpsBridge
{
    /// <summary>The permission state for a kind of GPS use, without prompting.</summary>
    [BridgeGet("status")]
    Task<GpsAccessResult> GetStatusAsync(GpsAccessMode mode = GpsAccessMode.Foreground, CancellationToken cancellationToken = default);

    /// <summary>Requests the permission the listener settings need — background access for a background mode.</summary>
    [BridgePost("access")]
    Task<GpsAccessResult> RequestAccessAsync(GpsListenerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The last known position, or null when there is none.</summary>
    [BridgeGet("last")]
    Task<GpsReading?> GetLastReadingAsync(CancellationToken cancellationToken = default);

    /// <summary>A fresh position, or null when none could be had.</summary>
    [BridgeGet("current")]
    Task<GpsReading?> GetCurrentPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>The running listener's settings, or null when not listening.</summary>
    [BridgeGet("listener")]
    Task<GpsListenerSettings?> GetListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts listening; readings arrive through <see cref="OnReadingAsync"/>, and in the background to the web app's
    /// <c>gps</c> handler. Fails with 409 without permission or while another listener runs.
    /// </summary>
    [BridgePost("listener")]
    Task StartListenerAsync(GpsListenerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Stops listening.</summary>
    [BridgeDelete("listener")]
    Task StopListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>A reading from the listener, while the page is open.</summary>
    [BridgeEvent("gps.reading")]
    Task<IAsyncDisposable> OnReadingAsync(Func<GpsReading, Task> handler);
}
