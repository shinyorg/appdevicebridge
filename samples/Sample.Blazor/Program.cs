using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Sample.Blazor;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Calendar.Client;
using Shiny.AppDeviceBridge.Photos.Client;
using Shiny.AppDeviceBridge.Folders.Client;
using Shiny.AppDeviceBridge.Obd.Client;
using Shiny.AppDeviceBridge.Notifications.Client;
using Shiny.AppDeviceBridge.TrayIcon.Client;
using Shiny.AppDeviceBridge.AppSupport.Client;
using Shiny.AppDeviceBridge.Discovery.Client;
using Shiny.AppDeviceBridge.HttpTransfers.Client;
using Shiny.AppDeviceBridge.Locations.Client;
using Shiny.AppDeviceBridge.Speech.Client;
using Shiny.AppDeviceBridge.Health.Client;
using Shiny.AppDeviceBridge.BluetoothLE.Client;
using Shiny.AppDeviceBridge.Contacts.Client;
using Shiny.AppDeviceBridge.Wifi.Client;
using Shiny.AppDeviceBridge.Push.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddWebAppHostClient()
    .AddCalendarBridgeClient()
    .AddPhotosBridgeClient()
    .AddFoldersBridgeClient()
    .AddObdBridgeClient()
    .AddNotificationsBridgeClient()
    .AddTrayBridgeClient()
    .AddAppBridgeClient()
    .AddDiscoveryBridgeClient()
    .AddTransfersBridgeClient()
    .AddGpsBridgeClient()
    .AddGeofencesBridgeClient()
    .AddMotionBridgeClient()
    .AddSpeechBridgeClient()
    .AddHealthBridgeClient()
    .AddBluetoothLEBridgeClient()
    .AddContactsBridgeClient()
    .AddWifiBridgeClient()
    .AddPushBridgeClient();
builder.Services.AddScoped<NativeCallHandlers>();

await builder.Build().RunAsync();
