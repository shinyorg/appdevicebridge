using System.Reflection;
using Shiny.Jobs;
using Shiny.AppDeviceBridge.AppLinks;
using Shiny.AppDeviceBridge.AppSupport;
using Shiny.AppDeviceBridge.BluetoothLE;
using Shiny.AppDeviceBridge.Calendar;
using Shiny.AppDeviceBridge.Camera;
using Shiny.AppDeviceBridge.Contacts;
using Shiny.AppDeviceBridge.Discovery;
using Shiny.AppDeviceBridge.Folders;
using Shiny.AppDeviceBridge.Health;
using Shiny.AppDeviceBridge.HttpTransfers;
using Shiny.AppDeviceBridge.Jobs;
using Shiny.AppDeviceBridge.Locations;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Valhalla;
using Shiny.AppDeviceBridge.Notifications;
using Shiny.AppDeviceBridge.Obd;
using Shiny.AppDeviceBridge.Photos;
using Shiny.AppDeviceBridge.Push;
using Shiny.AppDeviceBridge.Wearables;
using Shiny.AppDeviceBridge.RpiCamera;
using Shiny.AppDeviceBridge.ScreenRecorder;
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
    /// (UseMauiApp, UseMauiAppMacOS, UseMauiAppLinuxGtk4), passing the bridges only it carries.
    /// </summary>
    public static MauiAppBuilder ConfigureSample(this MauiAppBuilder builder, Action<MauiAppDeviceBridgeBuilder>? headBridges = null)
    {
#if DEBUG
        // Records every request the server answers, for the Traffic button in App to show.
        builder.UseTrafficMonitor();
#endif

        return builder
            .UseAppDeviceBridge(
                bridge =>
                {
                    bridge
                        .Configure(o =>
                        {
                            o.AppId = "sample";
#if DEBUG
                            // Android has no entry assembly to read DebuggableAttribute from, so a debug build there is not
                            // detected; this makes its bridges answer the development machine like every other head's.
                            o.IsDebug = true;
#endif
                        })

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

                        // A paired Apple Watch or Wear OS companion app; the page or background.js answers what it sends.
                        .AddWearablesBridge()
                        .AddNotificationsBridge()
                        .AddHttpTransfersBridge()
                        .AddHealthBridge()
                        .AddSpeechBridge()

                        // The whole screen, other apps included, everywhere but iOS and Mac Catalyst. Ten minutes at most,
                        // so a page that went away cannot leave it recording.
                        .AddScreenRecorderBridge(o => o.MaxDuration = TimeSpan.FromMinutes(10))
                        .AddContactsBridge()
                        .AddCalendarBridge()
                        .AddPhotosBridge()

                        // This device's own camera, for a page elsewhere to drive: Open shows the bridge's camera screen
                        // over the web app.
                        .AddCameraBridge()
                        .AddFoldersBridge()

                        // A Raspberry Pi camera through libcamera is as much for a headless Pi as for an app; everywhere
                        // but a Pi with the native shim, the page is told why there is no camera.
                        .AddRpiCameraBridge()

                        // Vector maps and directions. The sample release server serves both the online tiles and the
                        // downloadable regions from samples/Sample.ReleaseServer/maps — fill it with shiny-map-packs; see
                        // the readme there. Online directions use FOSSGIS's public Valhalla, which is for light use such as
                        // trying the sample; a real app runs its own or pays for a hosted one.
                        .AddMapsBridge(o =>
                        {
                            o.OnlineTiles = $"http://{HostMachine}:5199/maps/files/planet.pmtiles";
                            o.OnlineMaxZoom = 14;
                            o.Catalog = new Uri($"http://{HostMachine}:5199/maps/catalog");
                            o.CatalogPublicKey = ReadResource("Sample.dev-public.pem");
                            o.Directions.OnlineRouteUrl = new Uri("https://valhalla1.openstreetmap.de/route");
                        })

                        // Traffic providers and their keys, picked on the Map page while the app runs; see the bridge.
                        .AddBridge<TrafficProvidersBridge>()

                        // Routes on the phone inside a region whose road network was downloaded. Android and iOS; elsewhere
                        // this registers nothing and directions stay online.
                        .AddOnDeviceDirections()

                        // sample://device opens /device. Universal links would add o.Hosts, which needs a domain you control.
                        .AddAppLinksBridge(o =>
                        {
                            o.Schemes.Add("sample");
                            o.NavigateOnColdStart = true;
                        })

                        // Handled as "job:sync" by the page when it is open, by background.js when it is not.
                        .AddWebAppJob("sync", job => job.WithInternet(InternetAccess.Any));

                    headBridges?.Invoke(bridge);
                },
                webApp =>
                {
                    webApp.UseBaseline(typeof(App).Assembly, "Sample.webapp.zip", "1.0.0");

                    // samples/Sample.ReleaseServer. With it not running the check fails fast and the installed build is
                    // served. Plain HTTP and a committed key pair are development conveniences; see samples/keys.
                    // A GitHub repository's releases work the same way:
                    //     webApp.UpdateProvider = new GitHubReleasesUpdateProvider("https://github.com/acme/field-app");
                    webApp.UpdateProvider = new ReleaseServerUpdateProvider(
                        new Uri($"http://{HostMachine}:5199/webapps"),
                        ReadResource("Sample.dev-public.pem")
                    );
                    webApp.CheckTimeout = TimeSpan.FromSeconds(2);

#if DEBUG
                    // `dotnet watch` on Sample.Blazor, on this machine. Pages then come from the dev server — hot reload
                    // and all — while the bridge stays on the device. Unreachable, and the embedded build is served as usual.
                    webApp.DevServer = DevServer();
#endif
                }
            )

            // getUserMedia, navigator.geolocation and <input capture> in the page itself; see Pages/Media.razor.
            .AllowWebPermissions(WebAppWebPermissions.Camera | WebAppWebPermissions.Microphone | WebAppWebPermissions.Geolocation);
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
