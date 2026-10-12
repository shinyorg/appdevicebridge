using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maui;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The device bridge's checks, and how it answers where Essentials has no implementation — which is what the net10.0
/// build the tests run on is: every feature reaching the platform answers 501, never a 500.
/// </summary>
public class DeviceBridgeTests
{
    [Theory]
    [InlineData("""{ "uri": "javascript:alert(1)" }""")]
    [InlineData("""{ "uri": "file:///etc/passwd" }""")]
    [InlineData("""{ "uri": "/relative" }""")]
    [InlineData("""{ "uri": "https://shinylib.net", "launchMode": 9 }""")]
    public async Task Refuses_a_browser_uri_that_is_not_a_web_or_contact_link(string body)
    {
        await using var fixture = await StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("browser", body)).StatusCode);
    }

    [Theory]
    [InlineData("""{ "latitude": 91, "longitude": 0 }""")]
    [InlineData("""{ "latitude": 0, "longitude": -181 }""")]
    [InlineData("""{ "latitude": 0, "longitude": 0, "navigationMode": 42 }""")]
    public async Task Refuses_a_map_location_out_of_range(string body)
    {
        await using var fixture = await StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("map", body)).StatusCode);
    }

    [Fact]
    public async Task Refuses_shares_it_cannot_make()
    {
        await using var fixture = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("share", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("share", """{ "uri": "ftp://x" }""")).StatusCode);

        var tooMany = String.Join(",", Enumerable.Repeat("""{ "root": "data", "path": "a.txt" }""", 21));
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("share", $$"""{ "files": [{{tooMany}}] }""")).StatusCode);

        // No file roots registered: there is nothing a file could be shared from.
        Assert.Equal(HttpStatusCode.NotImplemented, (await fixture.Post("share", """{ "files": [{ "root": "data", "path": "a.txt" }] }""")).StatusCode);
    }

    [Fact]
    public async Task Refuses_bad_feedback_and_clipboard_requests()
    {
        await using var fixture = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("haptics", """{ "type": 5 }""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Post("vibrate", """{ "durationMs": 0 }""")).StatusCode);

        var huge = new ClipboardText(new string('x', 1024 * 1024 + 1));
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetClipboardAsync(huge));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Answers_501_where_essentials_has_no_implementation()
    {
        await using var fixture = await StartAsync();

        foreach (var call in new Func<Task>[]
        {
            () => fixture.Client.OpenBrowserAsync(new OpenBrowserRequest("https://shinylib.net")),
            () => fixture.Client.OpenBrowserAsync(new OpenBrowserRequest("mailto:hello@shinylib.net")),
            () => fixture.Client.OpenMapAsync(new OpenMapRequest(43.6, -79.4, Client.NavigationMode.Driving)),
            () => fixture.Client.ShareAsync(new ShareRequest("hello")),
            () => fixture.Client.PerformHapticsAsync(new HapticsRequest(Client.HapticFeedbackType.Click)),
            () => fixture.Client.VibrateAsync(new VibrateRequest(100)),
            () => fixture.Client.CancelVibrationAsync(),
            () => fixture.Client.GetClipboardAsync(),
            () => fixture.Client.SetClipboardAsync(new ClipboardText("hi")),
            () => fixture.Client.ClearClipboardAsync()
        })
        {
            Assert.True((await Assert.ThrowsAsync<BridgeException>(call)).IsNotSupported);
        }
    }

    [Fact]
    public async Task Keeping_the_screen_on_where_it_does_not_hold_is_501_not_a_silent_success()
    {
        await using var fixture = await StartAsync();

        // net10.0 Essentials takes the value and drops it.
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.KeepScreenAwakeAsync())).IsNotSupported);
        Assert.False((await fixture.Client.GetKeepAwakeAsync()).KeepAwake);

        // Letting it sleep is already true there.
        await fixture.Client.AllowScreenSleepAsync();
    }

    static async Task<DeviceFixture> StartAsync()
    {
        HttpClient? webView = null;
        var host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new DeviceBridge(new ServiceCollection().BuildServiceProvider())], null, client => webView = client);
        return new DeviceFixture(host, webView!);
    }

    sealed class DeviceFixture(BuiltInClientTests.HostFixture host, HttpClient webView) : IAsyncDisposable
    {
        public DeviceBridgeClient Client { get; } = new(host.Transport);

        public Task<HttpResponseMessage> Post(string path, string json)
            => webView.PostAsync($"/_bridge/device/{path}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
