using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Sample.Blazor;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Calendar.Client;
using Shiny.AppDeviceBridge.Camera.Client;
using Shiny.AppDeviceBridge.Photos.Client;
using Shiny.AppDeviceBridge.RpiCamera.Client;
using Shiny.AppDeviceBridge.Folders.Client;
using Shiny.AppDeviceBridge.Obd.Client;
using Shiny.AppDeviceBridge.Notifications.Client;
using Shiny.AppDeviceBridge.Desktop.Client;
using Shiny.AppDeviceBridge.AppSupport.Client;
using Shiny.AppDeviceBridge.Discovery.Client;
using Shiny.AppDeviceBridge.HttpTransfers.Client;
using Shiny.AppDeviceBridge.Locations.Client;
using Shiny.AppDeviceBridge.ScreenRecorder.Client;
using Shiny.AppDeviceBridge.Speech.Client;
using Shiny.AppDeviceBridge.Health.Client;
using Shiny.AppDeviceBridge.BluetoothLE.Client;
using Shiny.AppDeviceBridge.Contacts.Client;
using Shiny.AppDeviceBridge.Wifi.Client;
using Shiny.AppDeviceBridge.Push.Client;
using Shiny.AppDeviceBridge.Wearables.Client;
using Shiny.AppDeviceBridge.Maps.Blazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddWebAppHostClient()
    .AddCalendarBridgeClient()
    .AddCameraBridgeClient()
    .AddPhotosBridgeClient()
    .AddRpiCameraBridgeClient()
    .AddFoldersBridgeClient()
    .AddObdBridgeClient()
    .AddNotificationsBridgeClient()
    .AddTrayBridgeClient()
    .AddQuickEntryBridgeClient()
    .AddAppBridgeClient()
    .AddSensorsBridgeClient()
    .AddDiscoveryBridgeClient()
    .AddTransfersBridgeClient()
    .AddGpsBridgeClient()
    .AddGeofencesBridgeClient()
    .AddMotionBridgeClient()
    .AddSpeechBridgeClient()
    .AddScreenRecorderBridgeClient()
    .AddHealthBridgeClient()
    .AddBluetoothLEBridgeClient()
    .AddContactsBridgeClient()
    .AddWifiBridgeClient()
    .AddPushBridgeClient()
    .AddWearablesBridgeClient()
    .AddBridgeMaps();
builder.Services.AddScoped<NativeCallHandlers>();

await builder.Build().RunAsync();
