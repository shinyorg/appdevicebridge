using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Gps;
using Shiny.Locations;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// What AddGpsBridge registers on the plain net11.0 build, which serves Linux (GeoClue2 through Shiny.Gps.Linux) and
/// answers 501 elsewhere. GeoClue itself needs a real system bus, so the Linux manager is only asserted on Linux.
/// </summary>
public class GpsBridgeRegistrationTests
{
    [Fact]
    public void Keeps_a_gps_manager_the_app_registered_first_and_adds_the_bridge_delegate()
    {
        var services = new ServiceCollection();
        var appManager = new FakeGpsManager();
        services.AddSingleton<IGpsManager>(appManager);

        new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services)).AddGpsBridge();

        var manager = Assert.Single(services, x => x.ServiceType == typeof(IGpsManager));
        Assert.Same(appManager, manager.ImplementationInstance);
        Assert.Contains(services, x => x.ServiceType == typeof(IGpsDelegate) && x.ImplementationType == typeof(WebAppGpsDelegate));
    }

    [Fact]
    public void Registers_geoclue_only_on_linux()
    {
        var services = new ServiceCollection();
        new AppDeviceBridgeBuilder(new ShinyHttpServerBuilder(services)).AddGpsBridge();

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(services, x => x.ServiceType == typeof(IGpsManager));
            Assert.Contains(services, x => x.ServiceType == typeof(LinuxGpsManager));
        }
        else
        {
            Assert.DoesNotContain(services, x => x.ServiceType == typeof(IGpsManager));
        }
    }

    sealed class FakeGpsManager : IGpsManager
    {
        public event EventHandler<GpsReading> GpsReadingReceived { add { } remove { } }
        public GpsRequest? CurrentListener => null;
        public AccessState GetCurrentStatus(GpsRequest request) => AccessState.Available;
        public Task<AccessState> RequestAccess(GpsRequest request) => Task.FromResult(AccessState.Available);
        public Task<GpsReading?> GetLastReading(TimeSpan? timeout = null) => Task.FromResult<GpsReading?>(null);
        public Task StartListener(GpsRequest request) => Task.CompletedTask;
        public Task StopListener() => Task.CompletedTask;
    }
}
