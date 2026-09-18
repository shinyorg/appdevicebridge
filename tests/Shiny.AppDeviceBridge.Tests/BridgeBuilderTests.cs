using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Discovery;
using Shiny.AppDeviceBridge.Maui;
using Shiny.AppDeviceBridge.Notifications;
using Shiny.Net.HttpServer;
using Shiny.Notifications;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The bridge builder with no MAUI in it: endpoint packages that need no MAUI register on it anywhere, and chain on the
/// MAUI builder in an app.
/// </summary>
public class BridgeBuilderTests
{
    [Fact]
    public async Task A_bridge_with_no_maui_registers_on_a_headless_server()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddDiscoveryBridge()),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        Assert.Contains(server.Bridges, x => x is DiscoveryBridge);
    }

    [Fact]
    public async Task Work_runs_where_it_is_without_a_ui_thread()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.AddAppDeviceBridge(), autoStart: false);

        await using var provider = services.BuildServiceProvider();
        var mainThread = provider.GetRequiredService<IWebAppMainThread>();
        var caller = Environment.CurrentManagedThreadId;

        Assert.Equal(caller, await mainThread.InvokeAsync(() => Task.FromResult(Environment.CurrentManagedThreadId)));
    }

    [Fact]
    public void A_maui_app_answers_with_its_dispatcher()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge();

        var registered = builder.Services.Where(x => x.ServiceType == typeof(IWebAppMainThread)).ToList();
        Assert.Equal(typeof(MauiWebAppMainThread), Assert.Single(registered).ImplementationType);
    }

    /// <summary>A bridge package with no MAUI hands back the MAUI builder it was given, so MAUI bridges chain after it.</summary>
    [Fact]
    public void A_bridge_with_no_maui_keeps_the_maui_builder_in_the_chain()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        MauiAppDeviceBridgeBuilder? given = null;
        MauiAppDeviceBridgeBuilder? returned = null;

        builder.UseAppDeviceBridge(bridge =>
        {
            given = bridge;
            returned = bridge.AddDiscoveryBridge().AddNotificationsBridge();
        });

        Assert.Same(given, returned);
    }

    [Fact]
    public void Uses_the_notification_delegate_the_app_chooses()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge.AddNotificationsBridge(o =>
            {
                o.RegisterNotificationService = false;
                o.UseDelegate<QuietNotificationDelegate>();
            })),
            autoStart: false
        );

        var registered = services.Where(x => x.ServiceType == typeof(INotificationDelegate)).ToList();
        Assert.Equal(typeof(QuietNotificationDelegate), Assert.Single(registered).ImplementationType);
    }

    [Fact]
    public void Uses_the_web_apps_notification_delegate_by_default()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge.AddNotificationsBridge(o => o.RegisterNotificationService = false)),
            autoStart: false
        );

        var registered = services.Where(x => x.ServiceType == typeof(INotificationDelegate)).ToList();
        Assert.Equal(typeof(WebAppNotificationDelegate), Assert.Single(registered).ImplementationType);
    }

    [Fact]
    public async Task Initializes_each_extension_once_as_the_server_is_built()
    {
        var extension = new CountingExtension();
        var services = new ServiceCollection();
        services.AddSingleton<IAppDeviceBridgeServerExtension>(extension);
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId)),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<HttpServer>();
        _ = provider.GetRequiredService<HttpServer>();

        Assert.Equal(1, extension.Initialized);
        Assert.Same(provider.GetRequiredService<AppDeviceBridgeServer>(), extension.Services!.GetRequiredService<AppDeviceBridgeServer>());
    }

    sealed class QuietNotificationDelegate(WebAppEventHub events, WebAppInvoker invoker, WebAppNotificationOptions options)
        : WebAppNotificationDelegate(events, invoker, options);

    sealed class CountingExtension : IAppDeviceBridgeServerExtension
    {
        public int Initialized { get; private set; }
        public IServiceProvider? Services { get; private set; }

        public void Initialize(IServiceProvider services)
        {
            this.Initialized++;
            this.Services = services;
        }
    }
}
