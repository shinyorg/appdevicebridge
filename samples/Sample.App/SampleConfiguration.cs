using System.Reflection;
using Shiny.Jobs;
using Shiny.WebAppHost.Bridge.AppSupport;
using Shiny.WebAppHost.Bridge.BluetoothLE;
using Shiny.WebAppHost.Bridge.Discovery;
using Shiny.WebAppHost.Bridge.Jobs;
using Shiny.WebAppHost.Bridge.Locations;
using Shiny.WebAppHost.Bridge.Push;
using Shiny.WebAppHost.Bridge.Wifi;
using Shiny.WebAppHost.Maui;

namespace Sample;

public static class SampleConfiguration
{
    /// <summary>The development machine, as each emulator or simulator sees it.</summary>
    static string HostMachine => OperatingSystem.IsAndroid() ? "10.0.2.2" : "localhost";

    /// <summary>
    /// Everything the heads share. Each head calls this after choosing its backend
    /// (UseMauiApp, UseMauiAppMacOS, UseMauiAppLinuxGtk4).
    /// </summary>
    public static MauiAppBuilder ConfigureSample(this MauiAppBuilder builder) => builder
        .UseWebAppHost(o =>
        {
            o.AppId = "sample";
            o.UseBaseline(typeof(App).Assembly, "Sample.webapp.zip", "1.0.0");

            // samples/Sample.ReleaseServer. With it not running the check fails fast and the installed build is
            // served. Plain HTTP and a committed key pair are development conveniences; see samples/keys.
            o.UpdateServer = new Uri($"http://{HostMachine}:5199/webapps");
            o.PublicKey = ReadResource("Sample.dev-public.pem");
            o.CheckTimeout = TimeSpan.FromSeconds(2);

#if DEBUG
            // `dotnet watch` on Sample.Blazor, on this machine. Pages then come from the dev server — hot reload and
            // all — while the bridge stays on the device. Unreachable, and the embedded build is served as usual.
            o.DevServer = DevServer();
#endif
        })
        .AddAppSupportBridge()
        .AddLocationBridges()
        .AddBluetoothLEBridge()
        .AddWifiBridge()
        .AddDiscoveryBridge()
        .AddPushBridge(o => o.DispatchToWebApp = true)

        // Handled as "job:sync" by the page when it is open, by background.js when it is not.
        .AddWebAppJob("sync", job => job.WithInternet(InternetAccess.Any));

#if DEBUG
    static Uri? DevServer()
    {
        var configured = typeof(App).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "WebAppDevServer")?.Value;

        if (String.Equals(configured, "off", StringComparison.OrdinalIgnoreCase))
            return null;

        return new Uri(String.IsNullOrWhiteSpace(configured) ? $"http://{HostMachine}:5288" : configured);
    }
#endif

    static string ReadResource(string name)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
