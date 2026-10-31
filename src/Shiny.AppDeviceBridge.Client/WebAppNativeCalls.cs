namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// Native calls the page answers — background jobs, GPS readings, geofences, pushes, hub calls — while it is open; when
/// it is not, the host runs the same names in background.js. Shiny.AppDeviceBridge.Blazor's <c>WebAppNativeCalls</c>
/// implements it, so a client package can take calls without depending on Blazor.
/// </summary>
public interface IWebAppNativeCalls
{
    /// <summary>
    /// Takes the native call <paramref name="name"/> until the returned handle is disposed. The handler receives the
    /// payload as JSON and returns the result as JSON, or null for none; a thrown exception is reported to the host as a
    /// failure.
    /// </summary>
    Task<IAsyncDisposable> HandleAsync(string name, Func<string, Task<string?>> handler);
}
