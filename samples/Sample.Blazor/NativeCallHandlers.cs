using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using Shiny.WebAppHost.Blazor;

namespace Sample.Blazor;

/// <summary>
/// The page's side of background work. While the app is open these handle the native calls; when it is not,
/// wwwroot/background.js handles the same names.
/// </summary>
public sealed class NativeCallHandlers(WebAppNativeCalls nativeCalls)
{
    readonly List<string> log = [];
    bool started;

    public IReadOnlyList<string> Calls => this.log;

    public event Action? Changed;

    public async Task StartAsync()
    {
        if (this.started)
            return;

        this.started = true;

        try
        {
            await nativeCalls.HandleAsync("job:sync", async payload =>
            {
                this.Record("job:sync", payload);
                await Task.Delay(250);    // stands in for real work
                return new JsonObject { ["ranIn"] = "page" };
            });

            await nativeCalls.HandleAsync("gps", payload => this.Recorded("gps", payload));
            await nativeCalls.HandleAsync("geofence", payload => this.Recorded("geofence", payload));
            await nativeCalls.HandleAsync("push.received", payload => this.Recorded("push.received", payload));
            await nativeCalls.HandleAsync("push.entry", payload => this.Recorded("push.entry", payload));
        }
        catch (JSException ex)
        {
            // Opened straight from the dev server in a desktop browser: there is no host, so no /_bridge.
            this.log.Add($"Native calls are unavailable outside the app: {ex.Message}");
        }
    }

    Task Recorded(string name, JsonElement payload)
    {
        this.Record(name, payload);
        return Task.CompletedTask;
    }

    void Record(string name, JsonElement payload)
    {
        this.log.Insert(0, $"{DateTime.Now:T}  {name}  {payload.GetRawText()}");

        if (this.log.Count > 50)
            this.log.RemoveAt(this.log.Count - 1);

        this.Changed?.Invoke();
    }
}
