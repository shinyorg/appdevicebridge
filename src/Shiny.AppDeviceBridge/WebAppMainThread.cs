namespace Shiny.AppDeviceBridge;

/// <summary>
/// Runs work on the host's UI thread, for a bridge whose native call shows UI — a permission prompt, a picker. A MAUI app's
/// host registers one over the app's dispatcher; with no UI thread (a headless device, a test) the work runs where it is.
/// Bridges resolve it from the container rather than reaching for a UI framework themselves, so they need no MAUI.
/// </summary>
public interface IWebAppMainThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public static class WebAppMainThreadExtensions
{
    public static async Task InvokeAsync(this IWebAppMainThread mainThread, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        ArgumentNullException.ThrowIfNull(action);

        await mainThread.InvokeAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }
}

/// <summary>The default: no UI thread to move to.</summary>
sealed class InlineWebAppMainThread : IWebAppMainThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}
