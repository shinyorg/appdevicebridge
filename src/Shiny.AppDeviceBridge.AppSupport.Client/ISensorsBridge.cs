using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.AppSupport.Client;

/// <summary>
/// Motion and environment sensors — accelerometer, gyroscope, magnetometer, compass, barometer and orientation — as live
/// events. Start a sensor, listen for its event, stop it when done. A sensor stops on its own once nothing listens to its
/// event any more — the accelerometer while shakes are listened to counts — so a closed page never keeps one running. A
/// sensor the device lacks fails with 501.
/// </summary>
[BridgeClient("sensors", typeof(SensorsJsonContext))]
public interface ISensorsBridge
{
    /// <summary>Every sensor: whether the device has it, and whether it is running.</summary>
    [BridgeGet]
    Task<SensorStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a sensor, or changes the speed of a running one.</summary>
    [BridgePost("{sensor}")]
    Task<SensorInfo> StartAsync(Sensor sensor, SensorStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops a sensor. Stopping one that is not running is not an error.</summary>
    [BridgeDelete("{sensor}")]
    Task StopAsync(Sensor sensor, CancellationToken cancellationToken = default);

    /// <summary>Stops every sensor.</summary>
    [BridgeDelete]
    Task StopAllAsync(CancellationToken cancellationToken = default);

    [BridgeEvent("sensors.accelerometer")]
    Task<IAsyncDisposable> OnAccelerometerAsync(Func<VectorReading, Task> handler);

    [BridgeEvent("sensors.gyroscope")]
    Task<IAsyncDisposable> OnGyroscopeAsync(Func<VectorReading, Task> handler);

    [BridgeEvent("sensors.magnetometer")]
    Task<IAsyncDisposable> OnMagnetometerAsync(Func<VectorReading, Task> handler);

    [BridgeEvent("sensors.compass")]
    Task<IAsyncDisposable> OnCompassAsync(Func<CompassReading, Task> handler);

    [BridgeEvent("sensors.barometer")]
    Task<IAsyncDisposable> OnBarometerAsync(Func<BarometerReading, Task> handler);

    [BridgeEvent("sensors.orientation")]
    Task<IAsyncDisposable> OnOrientationAsync(Func<OrientationReading, Task> handler);

    /// <summary>The device was shaken. Raised only while the accelerometer is running.</summary>
    [BridgeEvent("sensors.shake")]
    Task<IAsyncDisposable> OnShakeAsync(Func<ShakeDetected, Task> handler);
}
