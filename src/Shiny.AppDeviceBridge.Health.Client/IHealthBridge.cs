using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Health.Client;

/// <summary>
/// HealthKit on iOS and Health Connect on Android: request access per type, read samples, write them, and watch a type
/// for new readings. Elsewhere every call fails with 501; where the store is missing (Health Connect not installed),
/// with 503; without access to a type, with 403.
/// </summary>
[BridgeClient("health", typeof(HealthJsonContext))]
public interface IHealthBridge
{
    /// <summary>Whether the store is available, every type with its unit, and the types being watched.</summary>
    [BridgeGet]
    Task<HealthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests access to up to 64 types. The platform decides which prompts to show.</summary>
    [BridgePost("access")]
    Task<HealthAccessResults> RequestAccessAsync(HealthAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples in a range of at most 366 days. Numeric types and blood pressure are bucketed by
    /// <paramref name="interval"/>, at most 2,000 buckets; record types — cycle tracking, workouts, nutrition — are
    /// individual records and ignore it.
    /// </summary>
    [BridgeGet("samples/{type}")]
    Task<HealthSamples> GetSamplesAsync(HealthDataType type, DateTimeOffset start, DateTimeOffset end, HealthInterval interval = HealthInterval.Days, CancellationToken cancellationToken = default);

    /// <summary>Writes one sample. Which fields it needs depends on the type; a missing one fails with 400.</summary>
    [BridgePost("samples/{type}")]
    Task WriteSampleAsync(HealthDataType type, HealthSampleInput sample, CancellationToken cancellationToken = default);

    /// <summary>
    /// Watches a type; readings arrive through <see cref="OnReadingAsync"/>, so listen first: without a listener it fails
    /// with 409. At most 8 types at once. Every watch stops once <see cref="OnReadingAsync"/> has no listener left.
    /// </summary>
    [BridgePost("listeners/{type}")]
    Task<HealthListener> StartListeningAsync(HealthDataType type, HealthListenerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops watching a type. Fails with 404 when it is not being watched.</summary>
    [BridgeDelete("listeners/{type}")]
    Task StopListeningAsync(HealthDataType type, CancellationToken cancellationToken = default);

    /// <summary>A new reading for a watched type.</summary>
    [BridgeEvent("health.reading")]
    Task<IAsyncDisposable> OnReadingAsync(Func<HealthReading, Task> handler);

    /// <summary>Watching a type ended, because it was stopped or it failed.</summary>
    [BridgeEvent("health.stopped")]
    Task<IAsyncDisposable> OnStoppedAsync(Func<HealthListenerStopped, Task> handler);
}
