using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Shiny.AppDeviceBridge.Tests;

public class InvocationTests
{
    static Task<(WebAppHost Host, WebAppInvoker Invoker)> CreateAsync(TestApp app, AppDeviceBridgeOptions? bridgeOptions = null)
        => FakePage.CreateHostAsync(app, bridgeOptions);

    [Fact]
    public async Task NothingRunsACallWithNoPageOpen()
    {
        await using var app = new TestApp();
        var (host, invoker) = await CreateAsync(app);
        await using var _ = host;

        // No StartAsync: a background wake-up has no WebView, and nothing stands in for it.
        var result = await invoker.InvokeAsync("gps", """{ "latitude": 1 }""").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(result.Handled);
        Assert.False(result.Succeeded);
        Assert.Same(WebAppInvocationResult.NotHandled, result);
        Assert.False(invoker.IsPageListening("gps"));
    }

    [Fact]
    public async Task PageTakesCallsItAccepts()
    {
        await using var app = new TestApp();
        var (host, invoker) = await CreateAsync(app);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, "gps");

        Assert.True(invoker.IsPageListening("gps"));
        Assert.False(invoker.IsPageListening("geofence"));

        var invocation = invoker.InvokeAsync("gps", """{ "hello": "page" }""");
        var call = await page.NextCallAsync();

        Assert.Equal("gps", call.Handler);
        Assert.Equal("page", call.Payload.GetProperty("hello").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await page.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await page.Client.PostAsJsonAsync($"/_bridge/invoke/{call.Id}", new { ok = true, result = new { from = "page" } })).StatusCode);

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Handled);
        Assert.True(result.Succeeded);
        Assert.Equal("""{"from":"page"}""", result.ResultJson);

        // A name the page never declared is not offered to it at all.
        Assert.False((await invoker.InvokeAsync("geofence", "{}")).Handled);
    }

    [Fact]
    public async Task APageThatAcceptedAndFailedIsReportedAsAFailure()
    {
        await using var app = new TestApp();
        var (host, invoker) = await CreateAsync(app);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, "gps");

        var invocation = invoker.InvokeAsync("gps", "{}");
        var call = await page.NextCallAsync();

        await page.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null);
        await page.Client.PostAsJsonAsync($"/_bridge/invoke/{call.Id}", new { ok = false, error = "nope" });

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Handled);
        Assert.False(result.Succeeded);
        Assert.Equal("nope", result.Error);
    }

    [Fact]
    public async Task APageThatLeftNoLongerTakesCalls()
    {
        await using var app = new TestApp();
        var (host, invoker) = await CreateAsync(app);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, "gps");
        await page.LeaveAsync();

        // Its handlers went with its stream, so the call is not handled straight away rather than waiting on a page.
        while (host.Server.Events.StreamCount > 0)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.False(invoker.IsPageListening("gps"));
        var result = await invoker.InvokeAsync("gps", "{}").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(result.Handled);
    }

    [Fact]
    public async Task AnUnresponsivePageIsNotHandledAndCannotRunTheCallLate()
    {
        await using var app = new TestApp();
        var bridgeOptions = app.BridgeOptions(o => o.PageAcceptTimeout = TimeSpan.FromMilliseconds(300));

        var (host, invoker) = await CreateAsync(app, bridgeOptions);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host, "gps");

        // The page receives the call and sits on it, as a suspended WebView would.
        var invocation = invoker.InvokeAsync("gps", "{}");
        var call = await page.NextCallAsync();

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Handled);

        Assert.Equal(HttpStatusCode.Gone, (await page.Client.PostAsync($"/_bridge/invoke/{call.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task ServesThePageClient()
    {
        await using var app = new TestApp();
        var (host, _) = await CreateAsync(app);
        await using var _ = host;

        await using var page = await FakePage.OpenAsync(host);
        var response = await page.Client.GetAsync("/_bridge/invoke/client.js");

        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("export function on", await response.Content.ReadAsStringAsync());
    }
}
