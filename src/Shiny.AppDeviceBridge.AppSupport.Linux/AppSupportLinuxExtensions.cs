using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Hosting;

namespace Shiny.AppDeviceBridge.AppSupport.Linux;

public static class AppSupportLinuxExtensions
{
    /// <summary>
    /// Gives the app bridge the battery on the Linux (GTK4) head: <see cref="UPowerBattery"/> as Essentials'
    /// <see cref="IBattery"/>, so <c>GET /_bridge/app/battery</c>, <c>app.battery</c> and <c>app.energysaver</c> report
    /// what UPower and power-profiles-daemon do. It replaces an <see cref="IBattery"/> registered before or after it —
    /// the maui-labs Linux Essentials one included, which never raises its change events. Off Linux it does nothing,
    /// so shared startup code can call it on every head.
    /// <code>
    /// builder
    ///     .UseMauiAppLinuxGtk4&lt;App&gt;()
    ///     .AddLinuxGtk4Essentials()
    ///     .AddAppSupportBridge()
    ///     .AddAppSupportLinux();
    /// </code>
    /// </summary>
    public static MauiAppBuilder AddAppSupportLinux(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (OperatingSystem.IsLinux())
            builder.Services.Replace(ServiceDescriptor.Singleton<IBattery, UPowerBattery>());

        return builder;
    }
}
