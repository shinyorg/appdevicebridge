using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Maui;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>What UseAppDeviceBridge puts on the app's server, whichever order the app registers things in.</summary>
public class MauiRegistrationTests
{
    [Fact]
    public void Listens_on_the_fixed_port_by_default()
        => Assert.Equal(AppDeviceBridgeMauiExtensions.DefaultPort, PortAfter(b => b.UseAppDeviceBridge(o => o.AppId = TestApp.AppId)));

    /// <summary>
    /// The sample registers a bridge on the server's builder before calling UseAppDeviceBridge. That said nothing about the
    /// port, and left the server on Shiny.Net.HttpServer's own default — a new origin for the page.
    /// </summary>
    [Fact]
    public void Keeps_the_fixed_port_when_something_registered_the_server_first()
        => Assert.Equal(AppDeviceBridgeMauiExtensions.DefaultPort, PortAfter(b =>
        {
            b.Services.AddShinyHttpServer(http => http.AddWebAppBridge<EchoBridge>(), autoStart: false);
            b.UseAppDeviceBridge(o => o.AppId = TestApp.AppId);
        }));

    [Fact]
    public void Leaves_a_port_the_app_chose_before()
        => Assert.Equal(8080, PortAfter(b =>
        {
            b.Services.AddShinyHttpServer(http => http.Options.Port = 8080, autoStart: false);
            b.UseAppDeviceBridge(o => o.AppId = TestApp.AppId);
        }));

    [Fact]
    public void Leaves_a_port_the_app_chose_after()
        => Assert.Equal(8080, PortAfter(b =>
        {
            b.UseAppDeviceBridge(o => o.AppId = TestApp.AppId);
            b.Services.AddShinyHttpServer(http => http.Options.Port = 8080, autoStart: false);
        }));

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
