using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Client;

namespace Sample.Blazor.Components;

/// <summary>What every sample page does: call the bridge, show the result or the error, listen for events.</summary>
public abstract class BridgePage : ComponentBase, IAsyncDisposable
{
    readonly List<IAsyncDisposable> subscriptions = [];

    [Inject] protected WebAppEvents Events { get; set; } = null!;

    protected string? Output { get; set; }
    protected string? Error { get; set; }
    protected bool Busy { get; set; }

    /// <summary>A typed call, its result shown as JSON through the bridge's own serialization metadata.</summary>
    protected async Task Run<T>(Func<Task<T>> call, JsonTypeInfo<T> display)
    {
        this.Busy = true;
        this.Error = null;

        try
        {
            var result = await call();
            this.Output = ((JsonElement?)JsonSerializer.SerializeToElement(result, display)).ToIndentedJson();
        }
        catch (Exception ex)
        {
            this.Error = Describe(ex);
        }
        finally
        {
            this.Busy = false;
        }
    }

    /// <summary>A typed call with nothing to show but that it worked.</summary>
    protected async Task Run(Func<Task> call, string done)
    {
        this.Busy = true;
        this.Error = null;

        try
        {
            await call();
            this.Output = done;
        }
        catch (Exception ex)
        {
            this.Error = Describe(ex);
        }
        finally
        {
            this.Busy = false;
        }
    }

    /// <summary>A bridge refusal with its status and code, so the page shows what a caller would switch on.</summary>
    static string Describe(Exception ex) => ex is BridgeException bridge
        ? $"{(int)bridge.StatusCode} {bridge.Code ?? bridge.StatusCode.ToString()}: {bridge.Message}"
        : ex.Message;

    /// <summary>A raw event by name, for a page that only shows what arrives. A bridge's typed client has the same events, typed.</summary>
    protected async Task Listen(string eventName, Action<JsonElement> handler)
    {
        try
        {
            this.subscriptions.Add(await this.Events.OnAsync(eventName, e => this.InvokeAsync(() =>
            {
                handler(e);
                this.StateHasChanged();
            })));
        }
        catch (Exception ex)
        {
            this.Error = $"Events are unavailable: {ex.Message}";
        }
    }

    /// <summary>A typed bridge event, handled on the renderer's thread and re-rendered after.</summary>
    protected async Task Listen<T>(Func<Func<T, Task>, Task<IAsyncDisposable>> subscribe, Action<T> handler)
    {
        try
        {
            this.subscriptions.Add(await subscribe(e => this.InvokeAsync(() =>
            {
                handler(e);
                this.StateHasChanged();
            })));
        }
        catch (Exception ex)
        {
            this.Error = $"Events are unavailable: {ex.Message}";
        }
    }

    /// <summary>A contract as compact JSON, for a log line.</summary>
    protected static string Compact<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    protected static string Prepend(string log, string line)
    {
        var combined = line + "\n" + log;
        return combined.Length > 4000 ? combined[..4000] : combined;
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var subscription in this.subscriptions)
            await subscription.DisposeAsync();
    }
}
