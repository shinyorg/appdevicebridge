using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Gps.Client;

/// <summary>
/// Motion activity: walking, running, cycling, driving or stationary, as the OS's activity recognition reports it.
/// Android, iOS and Mac Catalyst; elsewhere every call fails with 501. There is no history, only the latest reading and
/// live ones.
/// </summary>
[BridgeClient("motion", typeof(GpsJsonContext))]
public interface IMotionBridge
{
    /// <summary>The permission state, without prompting.</summary>
    [BridgeGet("status")]
    Task<MotionAccessResult> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests motion activity permission.</summary>
    [BridgePost("access")]
    Task<MotionAccessResult> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>The latest reading, or null when there is none.</summary>
    [BridgeGet("current")]
    Task<MotionActivity?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the listener is running.</summary>
    [BridgeGet("listener")]
    Task<MotionListener> GetListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts listening; readings arrive through <see cref="OnActivityAsync"/>, and in the background to the web app's
    /// <c>motion</c> handler. Fails with 409 without permission.
    /// </summary>
    [BridgePost("listener")]
    Task StartListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening.</summary>
    [BridgeDelete("listener")]
    Task StopListenerAsync(CancellationToken cancellationToken = default);

    /// <summary>A reading from the listener, while the page is open.</summary>
    [BridgeEvent("motion.activity")]
    Task<IAsyncDisposable> OnActivityAsync(Func<MotionActivity, Task> handler);
}
