using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Maui;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.Tunnel;
using Shiny.AppDeviceBridge.WebView;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>What UseAppDeviceBridge puts on the app's server, with or without a web app, whichever order the app registers things in.</summary>
public class MauiRegistrationTests
{
    [Fact]
    public void Listens_on_the_fixed_port_by_default()
        => Assert.Equal(AppDeviceBridgeMauiExtensions.DefaultPort, PortAfter(b => b.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId))));

    /// <summary>
    /// The sample registers a bridge on the server's builder before calling UseAppDeviceBridge. That said nothing about the
    /// port, and left the server on Shiny.Net.HttpServer's own default — a new origin for the page.
    /// </summary>
    [Fact]
    public void Keeps_the_fixed_port_when_something_registered_the_server_first()
        => Assert.Equal(AppDeviceBridgeMauiExtensions.DefaultPort, PortAfter(b =>
        {
            b.Services.AddShinyHttpServer(http => http.AddAppDeviceBridge(bridge => bridge.AddBridge<EchoBridge>()), autoStart: false);
            b.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId));
        }));

    [Fact]
    public void Leaves_a_port_the_app_chose_before()
        => Assert.Equal(8080, PortAfter(b =>
        {
            b.Services.AddShinyHttpServer(http => http.Options.Port = 8080, autoStart: false);
            b.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId));
        }));

    [Fact]
    public void Leaves_a_port_the_app_chose_after()
        => Assert.Equal(8080, PortAfter(b =>
        {
            b.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId));
            b.Services.AddShinyHttpServer(http => http.Options.Port = 8080, autoStart: false);
        }));

    [Fact]
    public void With_a_web_app_registers_the_host_the_bridge_server_and_the_bridges_it_is_given()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge(
            bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddBridge<EchoBridge>(),
            _ => { }
        );

        Assert.Equal(TestApp.AppId, Single<AppDeviceBridgeOptions>(builder).AppId);
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(IWebAppBridge) && x.ImplementationType == typeof(EchoBridge));
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(WebAppHost));
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(AppDeviceBridgeServer));
    }

    /// <summary>
    /// A head adds its own bridges after the shared setup: every call lands on the one server, with the one set of options.
    /// </summary>
    [Fact]
    public void Every_call_adds_to_the_same_server()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge(bridge => bridge.Configure(o => o.AppId = TestApp.AppId), _ => { });
        builder.UseAppDeviceBridge(bridge => bridge.AddBridge<EchoBridge>());

        Assert.Equal(TestApp.AppId, Single<AppDeviceBridgeOptions>(builder).AppId);
        Assert.Single(builder.Services, x => x.ServiceType == typeof(IWebAppBridge) && x.ImplementationType == typeof(EchoBridge));
        Assert.Single(builder.Services, x => x.ServiceType == typeof(AppDeviceBridgeServer));
    }

    /// <summary>
    /// The overloads can't be confused, even with lambdas that would compile against either type: one delegate is bridges
    /// only, a second delegate is the web app, and a bool after the first is the startup switch.
    /// </summary>
    [Fact]
    public void The_argument_count_picks_the_overload()
    {
        var withWebApp = MauiApp.CreateBuilder(useDefaults: false);
        withWebApp.UseAppDeviceBridge(_ => { }, _ => { });

        var bridgesOnly = MauiApp.CreateBuilder(useDefaults: false);
        bridgesOnly.UseAppDeviceBridge(_ => { });

        var notStarted = MauiApp.CreateBuilder(useDefaults: false);
        notStarted.UseAppDeviceBridge(_ => { }, false);
        Assert.DoesNotContain(notStarted.Services, x => x.ServiceType == typeof(WebAppHost));

        Assert.Contains(withWebApp.Services, x => x.ServiceType == typeof(WebAppHost));
        Assert.DoesNotContain(bridgesOnly.Services, x => x.ServiceType == typeof(WebAppHost));
        Assert.Contains(bridgesOnly.Services, x => x.ServiceType == typeof(AppDeviceBridgeServer));
    }

    /// <summary>The host version defaults to the app's display version, applied before the app's own configure.</summary>
    [Fact]
    public void A_host_version_the_app_sets_wins()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge(bridge => bridge.Configure(o => o.HostVersion = "9.8.7"));

        Assert.Equal("9.8.7", Single<AppDeviceBridgeOptions>(builder).HostVersion);
    }

    /// <summary>Extensions that need no MAUI take any builder and hand back the one they were given.</summary>
    [Fact]
    public void Server_side_bridges_chain_on_the_maui_builder()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        MauiAppDeviceBridgeBuilder? given = null;
        MauiAppDeviceBridgeBuilder? returned = null;

        builder.UseAppDeviceBridge(bridge =>
        {
            given = bridge;
            returned = bridge
                .AddRpiCameraBridge()
                .AddTunnel()
                .Configure(o => o.AppId = TestApp.AppId)
                .AddBridge<EchoBridge>();
        });

        Assert.NotNull(given);
        Assert.Same(given, returned);
        Assert.Same(builder, given.Maui);
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(IWebAppBridge) && x.ImplementationType == typeof(RpiCameraBridge));
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(AppDeviceBridgeTunnel));
    }

    static T Single<T>(MauiAppBuilder builder)
        => builder.Services.Select(x => x.ImplementationInstance).OfType<T>().Single();

    static int PortAfter(Action<MauiAppBuilder> register)
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        register(builder);

        return builder.Services
            .Select(x => x.ImplementationInstance)
            .OfType<HttpServerOptions>()
            .Single()
            .Port;
    }
}
