using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shiny.Extensions.Stores;

namespace Shiny.AppDeviceBridge.Tests;

public class InvocationTests
{
    const string BackgroundScript = """
        appdevicebridge.on("job:sync", async ({ name }) => {
            // Through the bridge, exactly as the page would.
            const put = await fetch("/_bridge/settings/local/last-job", { method: "PUT", body: JSON.stringify(name) });
            if (!put.ok)
                throw new Error("settings refused: " + put.status);

            const stored = await (await fetch("/_bridge/settings/local/last-job")).json();
            console.log("synced", name);
            return { name, stored };
        });

        appdevicebridge.on("fail", () => { throw new Error("nope"); });
        appdevicebridge.on("forever", () => { while (true) { } });
        appdevicebridge.on("page-or-script", () => "script");
        """;

    /// <summary>A host whose invoker is wired the way AddWebAppHost wires it, with in-memory settings.</summary>
    static async Task<(WebAppHost Host, WebAppInvoker Invoker, WebAppEventHub Events)> CreateAsync(TestApp app, WebAppHostOptions options)
    {
        app.Store.Add("1.0.0", TestApp.Zip("1.0.0", backgroundScript: BackgroundScript));
        await app.StartReleaseServerAsync();

        // Install once, the way a foreground launch would, then build the host a background wake-up gets.
        await using (var first = app.CreateHost())
            await first.StartAsync();

        var events = new WebAppEventHub();
        WebAppHost host = null!;
        var invoker = new WebAppInvoker(options, events, () => host);

        host = new WebAppHost(
            options,
            new WebAppSession(),
            events,
            [invoker, new WebAppSettingsBridge(new MemoryKeyValueStore(), new MemoryKeyValueStore(), TestApp.AppId)]
        );

        return (host, invoker, events);
    }

    [Fact]
    public async Task RunsBackgroundScriptWithoutAPage()
    {
        await using var app = new TestApp();
        var (host, invoker, _) = await CreateAsync(app, app.Options());
        await using var _ = host;

        // No StartAsync: a background wake-up has no WebView and runs no update check.
        var result = await invoker.InvokeAsync("job:sync", """{ "name": "sync" }""");

        Assert.Equal(WebAppInvocationTarget.BackgroundScript, result.Target);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("""{"name":"sync","stored":"sync"}""", result.ResultJson);
    }

    [Fact]
    public async Task ReportsFailuresTimeoutsAndMissingHandlers()
    {
        await using var app = new TestApp();
        var options = app.Options();
        options.BackgroundScriptTimeout = TimeSpan.FromMilliseconds(500);

        var (host, invoker, _) = await CreateAsync(app, options);
        await using var _ = host;

        var failed = await invoker.InvokeAsync("fail", "{}");
        Assert.Equal(WebAppInvocationTarget.BackgroundScript, failed.Target);
        Assert.False(failed.Succeeded);
        Assert.Contains("nope", failed.Error);

        var forever = await invoker.InvokeAsync("forever", "{}");
        Assert.False(forever.Succeeded);
        Assert.Contains("did not finish", forever.Error);

        var missing = await invoker.InvokeAsync("job:unknown", "{}");
        Assert.Equal(WebAppInvocationTarget.None, missing.Target);

        // The engine survives a run it had to stop.
        Assert.True((await invoker.InvokeAsync("page-or-script", "{}")).Succeeded);
    }

    [Fact]
    public async Task PageTakesCallsItAccepts()
    {
        await using var app = new TestApp();
        var (host, invoker, events) = await CreateAsync(app, app.Options());
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, events, "page-or-script");

        var invocation = invoker.InvokeAsync("page-or-script", """{ "hello": "page" }""");
        var call = await page.NextCallAsync();

        Assert.Equal("page-or-script", call.Handler);
        Assert.Equal("page", call.Payload.GetProperty("hello").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await page.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await page.Client.PostAsJsonAsync($"/_bridge/invoke/{call.Id}", new { ok = true, result = new { from = "page" } })).StatusCode);

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebAppInvocationTarget.Page, result.Target);
        Assert.True(result.Succeeded);
        Assert.Equal("""{"from":"page"}""", result.ResultJson);
    }

    [Fact]
    public async Task UnresponsivePageFallsBackToScriptAndCannotRunItLate()
    {
        await using var app = new TestApp();
        var options = app.Options();
        options.PageAcceptTimeout = TimeSpan.FromMilliseconds(300);

        var (host, invoker, events) = await CreateAsync(app, options);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, events, "page-or-script");

        // The page receives the call and sits on it, as a suspended WebView would.
        var invocation = invoker.InvokeAsync("page-or-script", "{}");
        var call = await page.NextCallAsync();

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebAppInvocationTarget.BackgroundScript, result.Target);
        Assert.Equal("\"script\"", result.ResultJson);

        Assert.Equal(HttpStatusCode.Gone, (await page.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task ServesThePageClient()
    {
        await using var app = new TestApp();
        var (host, _, events) = await CreateAsync(app, app.Options());
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, events);
        var response = await page.Client.GetAsync("/_bridge/invoke/client.js");

        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("export function on", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Plays the part of client.js: a session, an event stream, and declared handlers.</summary>
    sealed class FakePage : IAsyncDisposable
    {
        readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        HttpResponseMessage? stream;
        StreamReader? reader;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<FakePage> OpenAsync(WebAppHost host, WebAppEventHub events, params string[] handlers)
        {
            var page = new FakePage();
            var start = await host.StartAsync();

            page.Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = host.Origin };
            await page.Client.GetStringAsync(start);

            page.stream = await page.Client.GetAsync("/_bridge/events", HttpCompletionOption.ResponseHeadersRead, page.lifetime.Token);
            page.reader = new StreamReader(await page.stream.Content.ReadAsStreamAsync(page.lifetime.Token));

            // The stream is registered once the handler runs; wait for it rather than guess.
            while (events.SubscriberCount == 0)
                await Task.Delay(10, page.lifetime.Token);

            if (handlers.Length > 0)
                (await page.Client.PutAsJsonAsync("/_bridge/invoke/handlers", new { handlers })).EnsureSuccessStatusCode();

            return page;
        }

        public async Task<(string Id, string Handler, JsonElement Payload)> NextCallAsync()
        {
            string? line;
            while ((line = await this.reader!.ReadLineAsync(this.lifetime.Token)) is not null)
            {
                if (line != "event: host.invoke" && line != "event:host.invoke")
                    continue;

                var data = (await this.reader.ReadLineAsync(this.lifetime.Token))!;
                using var json = JsonDocument.Parse(data[(data.IndexOf(':') + 1)..]);
                var root = json.RootElement;

                return (root.GetProperty("id").GetString()!, root.GetProperty("handler").GetString()!, root.GetProperty("payload").Clone());
            }

            throw new InvalidOperationException("The event stream ended.");
        }

        public async ValueTask DisposeAsync()
        {
            await this.lifetime.CancelAsync();
            this.reader?.Dispose();
            this.stream?.Dispose();
            this.Client.Dispose();
            this.lifetime.Dispose();
        }
    }
}
