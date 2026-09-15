using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Shiny.WebAppHost.Blazor;

namespace Sample.Blazor.Components;

/// <summary>What every sample page does: call the bridge, show the result or the error, listen for events.</summary>
public abstract class BridgePage : ComponentBase, IAsyncDisposable
{
    readonly List<IAsyncDisposable> subscriptions = [];

    [Inject] protected WebAppBridge Bridge { get; set; } = null!;
    [Inject] protected WebAppEvents Events { get; set; } = null!;

    protected string? Output { get; set; }
    protected string? Error { get; set; }
    protected bool Busy { get; set; }

    protected async Task Run(Func<Task<JsonElement?>> call)
    {
        this.Busy = true;
        this.Error = null;

        try
        {
            this.Output = (await call()).ToIndentedJson();
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
        }
        finally
        {
            this.Busy = false;
        }
    }

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

    /// <summary>A GET that treats 404 as nothing, for values that may simply not exist yet.</summary>
    protected async Task<JsonElement?> TryGet(string path)
    {
        try
        {
            return await this.Bridge.GetAsync(path);
        }
        catch (WebAppBridgeException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    protected static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    protected static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
