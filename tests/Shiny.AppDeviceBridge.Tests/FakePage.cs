using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>Plays the part of client.js: a session, an event stream carrying host.invoke, and declared handlers.</summary>
sealed class FakePage : IAsyncDisposable
{
    /// <summary>A host with a page to play, and the invoker native code calls into it with.</summary>
    public static async Task<(WebAppHost Host, WebAppInvoker Invoker)> CreateHostAsync(TestApp app, AppDeviceBridgeOptions? bridgeOptions = null)
    {
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0"));
        await app.StartReleaseServerAsync();

        bridgeOptions ??= app.BridgeOptions();
        var invoker = new WebAppInvoker(bridgeOptions);
        var host = app.CreateHost(app.Options(), bridgeOptions, new WebAppEventHub(), [invoker]);
        return (host, invoker);
    }

    readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
    TestEventStream? stream;

    public HttpClient Client { get; private set; } = null!;

    public static async Task<FakePage> OpenAsync(WebAppHost host, params string[] handlers)
    {
        var page = new FakePage();
        var start = await host.StartAsync();

        page.Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
        await page.Client.GetStringAsync(start);

        page.stream = await TestEventStream.OpenAsync(page.Client, WebAppInvoker.InvokeEventName, page.lifetime.Token);

        if (handlers.Length > 0)
            (await page.Client.PutAsJsonAsync("/_bridge/invoke/handlers", new { handlers })).EnsureSuccessStatusCode();

        return page;
    }

    public async Task<(string Id, string Handler, JsonElement Payload)> NextCallAsync()
    {
        using var json = JsonDocument.Parse(await this.stream!.NextAsync(WebAppInvoker.InvokeEventName, this.lifetime.Token));
        var root = json.RootElement;

        return (root.GetProperty("id").GetString()!, root.GetProperty("handler").GetString()!, root.GetProperty("payload").Clone());
    }

    /// <summary>Takes the next call the way client.js does — accept, then reply — and returns what it carried.</summary>
    public async Task<(string Handler, JsonElement Payload)> AnswerAsync(Func<JsonElement, object?> reply)
    {
        var call = await this.NextCallAsync();
        (await this.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null)).EnsureSuccessStatusCode();
        (await this.Client.PostAsJsonAsync($"/_bridge/invoke/{call.Id}", new { ok = true, result = reply(call.Payload) })).EnsureSuccessStatusCode();
        return (call.Handler, call.Payload);
    }

    /// <summary>The page goes away without a word, as a WebView that navigated or closed does.</summary>
    public async Task LeaveAsync()
    {
        if (this.stream is { } open)
            await open.DisposeAsync();

        this.stream = null;
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        await this.LeaveAsync();
        this.Client.Dispose();
        this.lifetime.Dispose();
    }
}
