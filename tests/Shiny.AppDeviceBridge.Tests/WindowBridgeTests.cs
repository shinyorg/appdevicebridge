using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Desktop;
using Shiny.AppDeviceBridge.Desktop.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The window bridge over a fake window: 501 where the window can't be hidden, hiding only while a tray icon is left to
/// bring it back, show/hide/quit reaching the window, and visibility changes reaching the page. The platform halves —
/// NSWindow, AppWindow, GTK — need a desktop head and are seen in the app, not here.
/// </summary>
public class WindowBridgeTests
{
    [Fact]
    public async Task Says_it_is_unsupported_and_answers_501_without_a_window()
    {
        // The test host is the plain net10.0 build off Linux: the real window answers unsupported.
        var services = new ServiceCollection()
            .AddSingleton(new WindowBridgeOptions())
            .AddSingleton<TrayPresence>()
            .AddSingleton<IDesktopWindow>(sp => new DesktopWindow(sp.GetRequiredService<WindowBridgeOptions>(), sp.GetRequiredService<TrayPresence>()))
            .BuildServiceProvider();
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)]);
        var window = new WindowBridgeClient(fixture.Transport);

        Assert.Equal(new WindowStatus(false, false, false), await window.GetStatusAsync());

        foreach (var call in new Func<Task>[] { () => window.ShowAsync(), () => window.HideAsync(), () => window.QuitAsync() })
            Assert.Equal(HttpStatusCode.NotImplemented, (await Assert.ThrowsAsync<BridgeException>(call)).StatusCode);
    }

    [Fact]
    public async Task Hides_only_while_a_tray_icon_is_showing()
    {
        var (services, fake, trays) = Create(new WindowBridgeOptions { HideOnClose = true });
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)]);
        var window = new WindowBridgeClient(fixture.Transport);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => window.HideAsync());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("no_tray_icon", refused.Code);
        Assert.True(fake.IsVisible);

        trays.Set(1);
        await window.HideAsync();
        Assert.False(fake.IsVisible);
        Assert.Equal(new WindowStatus(true, false, true), await window.GetStatusAsync());

        await window.ShowAsync();
        Assert.True(fake.IsVisible);
    }

    [Fact]
    public async Task Hides_without_a_tray_icon_when_the_app_turns_the_check_off()
    {
        var (services, fake, _) = Create(new WindowBridgeOptions { RequireTrayIcon = false });
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)]);

        await new WindowBridgeClient(fixture.Transport).HideAsync();
        Assert.False(fake.IsVisible);
    }

    [Fact]
    public async Task Answers_409_before_the_app_has_a_window()
    {
        var (services, fake, trays) = Create(new WindowBridgeOptions());
        fake.HasWindow = false;
        trays.Set(1);
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)]);
        var window = new WindowBridgeClient(fixture.Transport);

        foreach (var call in new Func<Task>[] { () => window.ShowAsync(), () => window.HideAsync() })
        {
            var refused = await Assert.ThrowsAsync<BridgeException>(call);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("no_window", refused.Code);
        }
    }

    [Fact]
    public async Task Quits_after_answering()
    {
        var (services, fake, _) = Create(new WindowBridgeOptions());
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)]);

        await new WindowBridgeClient(fixture.Transport).QuitAsync();
        await fake.Quitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Visibility_changes_reach_the_page_however_they_happen()
    {
        var (services, fake, _) = Create(new WindowBridgeOptions { HideOnClose = true });
        HttpClient? webView = null;
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new WindowBridge(services)], onStarted: x => webView = x);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(webView!, "window.visibility", lifetime.Token);

        // The user closing the window with hide-on-close, as the platform half reports it.
        fake.Raise(false);

        using var hidden = JsonDocument.Parse(await stream.NextAsync("window.visibility", lifetime.Token));
        Assert.False(hidden.RootElement.GetProperty("visible").GetBoolean());
        Assert.True(hidden.RootElement.GetProperty("hideOnClose").GetBoolean());
    }

    [Fact]
    public void The_tray_bridge_reports_its_icons_to_the_window_bridge()
    {
        var trays = new TrayPresence();
        Assert.False(trays.AnyVisible);

        trays.Set(2);
        Assert.True(trays.AnyVisible);

        trays.Set(0);
        Assert.False(trays.AnyVisible);
    }

    static (IServiceProvider Services, FakeWindow Window, TrayPresence Trays) Create(WindowBridgeOptions options)
    {
        var fake = new FakeWindow();
        var trays = new TrayPresence();
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton(trays)
            .AddSingleton<IDesktopWindow>(fake)
            .BuildServiceProvider();

        return (services, fake, trays);
    }

    sealed class FakeWindow : IDesktopWindow
    {
        public bool HasWindow { get; set; } = true;

        public bool IsSupported => true;

        public bool IsVisible { get; private set; } = true;

        public TaskCompletionSource Quitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<bool>? VisibilityChanged;

        public Task<bool> ShowAsync() => this.Set(true);

        public Task<bool> HideAsync() => this.Set(false);

        public void Quit() => this.Quitted.TrySetResult();

        public void Raise(bool visible)
        {
            this.IsVisible = visible;
            this.VisibilityChanged?.Invoke(visible);
        }

        Task<bool> Set(bool visible)
        {
            if (!this.HasWindow)
                return Task.FromResult(false);

            this.Raise(visible);
            return Task.FromResult(true);
        }
    }
}
