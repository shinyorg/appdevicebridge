using System.Reflection;
using Shiny.Jobs;
using Shiny.AppDeviceBridge.AppLinks;
using Shiny.AppDeviceBridge.AppSupport;
using Shiny.AppDeviceBridge.BluetoothLE;
using Shiny.AppDeviceBridge.Calendar;
using Shiny.AppDeviceBridge.Contacts;
using Shiny.AppDeviceBridge.Discovery;
using Shiny.AppDeviceBridge.Folders;
using Shiny.AppDeviceBridge.Health;
using Shiny.AppDeviceBridge.HttpTransfers;
using Shiny.AppDeviceBridge.Jobs;
using Shiny.AppDeviceBridge.Locations;
using Shiny.AppDeviceBridge.Notifications;
using Shiny.AppDeviceBridge.Obd;
using Shiny.AppDeviceBridge.Photos;
using Shiny.AppDeviceBridge.Push;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.Speech;
using Shiny.AppDeviceBridge.Wifi;
using Shiny.AppDeviceBridge.Maui;
using Shiny.AppDeviceBridge.WebView;

namespace Sample;

public static class SampleConfiguration
{
    /// <summary>The development machine, as each emulator or simulator sees it.</summary>
    static string HostMachine => OperatingSystem.IsAndroid() ? "10.0.2.2" : "localhost";

    /// <summary>
    /// Everything the heads share. Each head calls this after choosing its backend
    /// (UseMauiApp, UseMauiAppMacOS, UseMauiAppLinuxGtk4).
    /// </summary>
    public static MauiAppBuilder ConfigureSample(this MauiAppBuilder builder)
    {
        // A Raspberry Pi camera through libcamera. Plain IServiceCollection, since it is as much for a headless Pi as for
        // an app; everywhere but a Pi with the native shim, the page is told why there is no camera.
        builder.Services.AddRpiCameraBridge();

        return builder
        .UseAppDeviceBridge(o => o.AppId = "sample")
        .UseWebAppHost(o =>
        {
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

        // getUserMedia, navigator.geolocation and <input capture> in the page itself; see Pages/Media.razor.
        .AllowWebPermissions(WebAppWebPermissions.Camera | WebAppWebPermissions.Microphone | WebAppWebPermissions.Geolocation)

        // Launch at login rides along with the app bridge: same package behind it, and desktop-only in the
        // sense that mobile answers { "supported": false } rather than the endpoints going missing.
        .AddAppSupportBridge(startup: o => o.Arguments.Add("--autostart"))
        .AddLocationBridges()
        .AddMotionActivityBridge()
        .AddBluetoothLEBridge()
        .AddObdBridge()
        .AddWifiBridge()
        .AddDiscoveryBridge()
        .AddPushBridge(o => o.DispatchToWebApp = true)
        .AddNotificationsBridge()
        .AddHttpTransfersBridge()
        .AddHealthBridge()
        .AddSpeechBridge()
        .AddContactsBridge()
        .AddCalendarBridge()
        .AddPhotosBridge()
        .AddFoldersBridge()

        // sample://device opens /device. Universal links would add o.Hosts, which needs a domain you control.
        .AddAppLinksBridge(o =>
        {
            o.Schemes.Add("sample");
            o.NavigateOnColdStart = true;
        })

        // Handled as "job:sync" by the page when it is open, by background.js when it is not.
        .AddWebAppJob("sync", job => job.WithInternet(InternetAccess.Any));
    }

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
