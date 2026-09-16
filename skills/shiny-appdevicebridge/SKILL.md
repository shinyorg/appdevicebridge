---
name: shiny-appdevicebridge
description: Generate code using Shiny.AppDeviceBridge, a configurable device bridge server that also hosts a web app (Blazor WebAssembly, React, Vue, any static build) inside a .NET MAUI app on Android, iOS, Mac Catalyst, Windows and the maui-labs macOS and Linux heads — served from a loopback HTTP server, updated over the air from a signed release server, and given device access through bridges with typed C# and TypeScript clients
auto_invoke: true
triggers:
  - Shiny.AppDeviceBridge
  - AppDeviceBridge
  - UseAppDeviceBridge
  - AddAppDeviceBridge
  - AppDeviceBridgeOptions
  - AppDeviceBridgeServer
  - AppDeviceBridgePolicies
  - AuthorizeBridges
  - AllowAnyCallerInDebug
  - BridgeCallers
  - IAppDeviceBridgeServerExtension
  - IWebAppBackgroundInvoker
  - UseWebAppHost
  - AddWebAppHost
  - Shiny.AppDeviceBridge.WebView
  - WebAppHost
  - WebAppHostView
  - WebAppHostPage
  - WebAppHostOptions
  - UseBaseline
  - AddWebAppReleases
  - MapWebAppReleases
  - IWebAppBridge
  - WebAppBridgeRoutes
  - WebAppBridgeResults
  - AddWebAppBridge
  - WebAppEventHub
  - WebAppInvoker
  - WebAppNativeCalls
  - WebAppEvents
  - AddWebAppHostClient
  - IBridgeTransport
  - BridgeException
  - BridgeClient
  - BridgeGet
  - BridgePost
  - BridgePut
  - BridgeDelete
  - BridgeEvent
  - BridgeBody
  - BridgeQuery
  - BridgeFile
  - ADB001
  - IHostBridge
  - ISettingsBridge
  - IFilesBridge
  - ILinksBridge
  - IAppBridge
  - IGpsBridge
  - IGeofencesBridge
  - IMotionBridge
  - IBluetoothLEBridge
  - IObdBridge
  - IWifiBridge
  - IDiscoveryBridge
  - IPushBridge
  - INotificationsBridge
  - ITransfersBridge
  - IHealthBridge
  - ISpeechBridge
  - IContactsBridge
  - ICalendarBridge
  - IPhotosBridge
  - IFoldersBridge
  - ITrayBridge
  - IQuickEntryBridge
  - IRpiCameraBridge
  - AddRpiCameraBridge
  - Shiny.AppDeviceBridge.RpiCamera
  - Raspberry Pi camera
  - libcamera
  - MJPEG stream
  - AddTrayIconBridge
  - AddQuickEntryBridge
  - Shiny.AppDeviceBridge.Desktop
  - quick entry
  - global hotkey
  - WebAppFileRoots
  - WebAppFileStore
  - WebAppFileRoot
  - AddPhotosBridge
  - AddFoldersBridge
  - photo picker
  - folder picker
  - background.js
  - ConfigureServer
  - WebAppPolicies
  - "@shinyorg/appdevicebridge"
  - hybrid web app
  - over-the-air web app updates
---

# Shiny.AppDeviceBridge

You are an expert in Shiny.AppDeviceBridge. Use this skill when the user hosts a web app inside a .NET MAUI app,
calls device features from that web app, updates it over the air, or writes a bridge of their own.

**Documentation:** https://shinylib.net/appdevicebridge

## The shape of it

- `Shiny.AppDeviceBridge` is the **bridge server**: a Shiny.Net.HttpServer configured through
  `AppDeviceBridgeOptions` (`UseAppDeviceBridge` in MAUI) — `Server` is a full `HttpServerOptions`,
  `ConfigureServer` adds middleware and endpoints, and every bridge route requires the
  `AppDeviceBridgePolicies.Bridges` policy.
- `Shiny.AppDeviceBridge.WebView` adds the **web app host** (`UseWebAppHost`): the web app served straight from a
  zip (the baseline or a signed download), shown in `WebAppHostView` / `WebAppHostPage`. The WebView trades a
  one-time launch token for an HttpOnly cookie, which the host adds to the bridge policy.
- The server works without the WebView: bridges only, for callers the policy admits.
- **Bridges** are HTTP endpoints under `/_bridge/{name}` (the prefix is configurable) plus one Server-Sent
  Events stream. One package per bridge, one extension method each.
- **Every bridge has a typed client.** Never generate `fetch("/_bridge/…")` or JSON-object bodies in page code;
  use the bridge's client — C# for Blazor, TypeScript for everything else.

## The app (MAUI)

```csharp
builder
    .UseMauiApp<App>()
    .UseAppDeviceBridge(o => o.AppId = "field-app")         // the server: Server, BasePath, ConfigureServer, AuthorizeBridges
    .UseWebAppHost(o =>
    {
        o.UseBaseline(typeof(App).Assembly, "webapp.zip");      // offline, no update server needed
        // o.UpdateServer = new Uri("https://api.example.com/webapps");
        // o.PublicKey = "-----BEGIN PUBLIC KEY-----…";
    })
    .AddAppSupportBridge()
    .AddLocationBridges()
    .AddCalendarBridge()
    .AddPhotosBridge()
    .AddFoldersBridge();

public class App : Application
{
    protected override Window CreateWindow(IActivationState? state) => new(new WebAppHostPage());
}
```

- Bridge extensions register the Shiny service behind them. Do **not** also call `AddGps()`, `AddBluetoothLE()`
  and so on.
- A platform without an implementation answers `501`; `IHostBridge.GetInfoAsync()` lists every bridge with
  `IsSupported`.
- Platform setup is the underlying library's: usage descriptions, manifest permissions, entitlements. Loopback
  needs cleartext to `127.0.0.1` on Android and `NSAllowsLocalNetworking` on Apple platforms.

## Bridge packages

| Package | Registration | Client package / interface |
| --- | --- | --- |
| built in | (always) | `Shiny.AppDeviceBridge.Client`: `IHostBridge`, `ISettingsBridge`, `IFilesBridge`, `ILinksBridge` |
| `.AppSupport` | `AddAppSupportBridge()` | `IAppBridge` — info, orientation, browser, maps, store, launch at login, share, haptics, connectivity, battery, screen, clipboard |
| `.Locations` | `AddGpsBridge()`, `AddGeofenceBridge()`, `AddMotionActivityBridge()` | `IGpsBridge`, `IGeofencesBridge`, `IMotionBridge` |
| `.BluetoothLE` | `AddBluetoothLEBridge()` | `IBluetoothLEBridge` |
| `.Obd` | `AddObdBridge()` | `IObdBridge` |
| `.Wifi` | `AddWifiBridge(hotspot)` | `IWifiBridge` |
| `.Discovery` | `AddDiscoveryBridge(protocols)` | `IDiscoveryBridge` |
| `.Push` | `AddPushBridge()` | `IPushBridge` |
| `.Notifications` | `AddNotificationsBridge()` | `INotificationsBridge` |
| `.HttpTransfers` | `AddHttpTransfersBridge()` | `ITransfersBridge` |
| `.AppLinks` | `AddAppLinksBridge(o => …)` | `ILinksBridge` (built in) |
| `.Health` | `AddHealthBridge()` | `IHealthBridge` |
| `.Speech` | `AddSpeechBridge()` | `ISpeechBridge` |
| `.Contacts` | `AddContactsBridge()` | `IContactsBridge` |
| `.Calendar` | `AddCalendarBridge()` | `ICalendarBridge` |
| `.Photos` | `AddPhotosBridge()` | `IPhotosBridge` |
| `.Folders` | `AddFoldersBridge()` | `IFoldersBridge` |
| `.Desktop` | `AddTrayIconBridge()`, `AddQuickEntryBridge(o => o.HotKey = "Ctrl+Alt+Space")` | `Shiny.AppDeviceBridge.Desktop.Client`: `ITrayBridge`, `IQuickEntryBridge` (desktop only; `501` on mobile) |
| `.RpiCamera` | `services.AddRpiCameraBridge(o => …)` (an `IServiceCollection`, for headless Pis) | `IRpiCameraBridge` — snapshots, captures into a file root, controls; live MJPEG at `rpicamera/stream` for an `<img>` (Linux + native shim only) |
| `.Jobs` | `AddWebAppJob(name, configure)` | native call `job:{name}` with `JobRun` |

Client packages are `Shiny.AppDeviceBridge.{Bridge}.Client`, registered with `Add{Name}BridgeClient()` — the
name from the interface: `IAppBridge` → `AddAppBridgeClient()`, `ITransfersBridge` → `AddTransfersBridgeClient()`,
`ITrayBridge` → `AddTrayBridgeClient()`, `IQuickEntryBridge` → `AddQuickEntryBridgeClient()`. The desktop bridges share
`Shiny.AppDeviceBridge.Desktop.Client`.

## Quick entry

A prompt window over other applications. The page configures it and answers submissions; the answer also goes to
`background.js` when no page is open, so register the handler as a native call, not only an event:

```csharp
await quickEntry.SetPromptAsync(new QuickEntryPromptInput(Placeholder: "Ask…", Suggestions: [new("Sync now", Value: "sync")]));

await nativeCalls.HandleAsync("quickentry.submitted", QuickEntryJsonContext.Default.QuickEntrySubmission, async s =>
{
    await quickEntry.SetPromptAsync(new QuickEntryPromptInput(IsBusy: true));
    await quickEntry.SetPromptAsync(new QuickEntryPromptInput(IsBusy: false, Response: await AnswerAsync(s.Text)));
});
```

Null properties on `QuickEntryPromptInput` / `QuickEntryOptionsInput` leave values unchanged; `Response: ""` clears the
response and `HotKey: ""` removes the hotkey.

## A Blazor page

```csharp
// Program.cs
builder.Services
    .AddWebAppHostClient()            // transport + IHostBridge, ISettingsBridge, IFilesBridge, ILinksBridge
    .AddCalendarBridgeClient()
    .AddPhotosBridgeClient();
```

```razor
@inject ICalendarBridge Calendar
@inject IPhotosBridge Photos
@inject IFilesBridge Files
@implements IAsyncDisposable

@code {
    IAsyncDisposable? subscription;

    protected override async Task OnInitializedAsync()
    {
        var access = await Calendar.RequestAccessAsync(new CalendarAccessRequest());
        var page = await Calendar.GetEventsAsync(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(7), limit: 20);
    }

    async Task PickAsync()
    {
        foreach (var photo in await Photos.PickAsync(new PhotoPickRequest(Limit: 3)))
        {
            var bytes = await Files.ReadBytesAsync(photo.File.Root, photo.File.Path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (subscription is not null)
            await subscription.DisposeAsync();
    }
}
```

Rules:

1. **Catch `BridgeException`**, never `HttpRequestException`: it carries `StatusCode`, the bridge's `Code` and
   `IsNotSupported` (501). Treat 501 as "hide the feature on this platform".
2. **Events are subscriptions.** `await using var sub = await Gps.OnReadingAsync(r => …)` — dispose it when the
   component goes away. Handlers run off the renderer; call `InvokeAsync(StateHasChanged)`.
3. **Settings take your own types** through `ISettingsBridge.GetAsync<T>(scope, key, typeInfo, default)` and
   `SetAsync<T>(…)` with a source-generated `JsonTypeInfo<T>`. Never reflection-based `JsonSerializer` calls.
4. **Files move by `BridgeFile { Root, Path }`.** Bridges that produce files (photos, exports) return one; read it
   through `IFilesBridge`. Bridges that take a file (share, notification image, tray icon) take one.
5. **Native calls** — background work handed to the page — are typed:
   `nativeCalls.HandleAsync("gps", LocationsJsonContext.Default.GpsReading, reading => …)`. The same names run in
   `background.js` when no page is open; that script is JavaScript (Jint), with `fetch` to `/_bridge`.

## A JavaScript / TypeScript page

```ts
import { BridgeError, CalendarBridge, FilesBridge, FoldersBridge, GpsBridge } from "@shinyorg/appdevicebridge";

const calendar = new CalendarBridge();
try {
    await calendar.createEvent({ title: "Standup", start: new Date(), end: new Date(Date.now() + 1_800_000) });
} catch (e) {
    if (e instanceof BridgeError && e.isNotSupported) hideCalendar();
}

const folder = await new FoldersBridge().pick({ root: "documents" });   // null when cancelled
if (folder) await new FilesBridge().writeText(folder.root, "notes.txt", "hello");

const stop = new GpsBridge().onReading(reading => console.log(reading.latitude));
stop();    // unsubscribes
```

- Method names are the C# names in camelCase without `Async`. Required parameters are positional; optional
  ones, and `signal`, go in the trailing options object.
- Events return an unsubscribe function. Enums are string unions (`"ReadWrite"`), dates accept `Date` or ISO
  strings, `byte[]` results come back as `Blob`.
- The clients discover the bridge prefix from `_host/config`, so a moved `BridgePrefix` or `BasePath` needs no
  page change.

## Folders and photos

- `IFoldersBridge.PickAsync(new FolderPickRequest("documents"))` shows the platform folder picker and makes the
  folder a **file root** named `documents`, remembered across launches (security-scoped bookmark on Apple, a
  persisted SAF grant on Android, the path on Windows and Linux). `ForgetAsync(root)` releases it. Null means
  cancelled. The app's own roots (`data`, `cache`) cannot be replaced.
- On Android a picked folder has no path: the files bridge works, but share, transfers and notification images
  refuse it.
- `IPhotosBridge.PickAsync` needs no permission. `GetLibraryAsync`/`GetThumbnailAsync`/`ExportAsync` need
  `RequestAccessAsync()` and `NSPhotoLibraryUsageDescription` / `READ_MEDIA_IMAGES`. The library is 501 on Linux.

## Writing a bridge with a typed client

1. **Contracts project** (`net10.0`, references `Shiny.AppDeviceBridge.Client`): records plus a
   `[BridgeClient]` interface and its `JsonSerializerContext`.

```csharp
[BridgeClient("orders", typeof(OrdersJson))]
public interface IOrdersBridge
{
    [BridgeGet("{id}")] Task<Order> GetAsync(string id, CancellationToken cancellationToken = default);
    [BridgeGet] Task<OrderPage> ListAsync(int offset = 0, int limit = 50, CancellationToken cancellationToken = default);
    [BridgePost] Task<Order> CreateAsync(NewOrder order, CancellationToken cancellationToken = default);
    [BridgeDelete("{id}")] Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    [BridgeEvent("orders.changed")] Task<IAsyncDisposable> OnChangedAsync(Func<Order, Task> handler);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(OrderPage))]
[JsonSerializable(typeof(NewOrder))]
public partial class OrdersJson : JsonSerializerContext;
```

   Binding: a parameter named like a route token fills it; a complex type on POST/PUT (or `[BridgeBody]`) is the
   body; simple types are query parameters (`[BridgeQuery("name")]` renames). `Task<Stream>`/`Task<byte[]>` read raw.
   Anything else — a complex type on GET/DELETE, an unmatched token, two bodies — is build error **ADB001**.
2. **Native bridge** (MAUI project, references the contracts):

```csharp
public sealed class OrdersBridge(IOrderStore store, WebAppEventHub events) : IWebAppBridge
{
    public string Name => "orders";
    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/{id}", async ctx => await (await store.FindAsync(ctx.Request.RouteValues["id"]!) is { } order
            ? WebAppBridgeResults.Json(ctx, order, OrdersJson.Default.Order)
            : WebAppBridgeResults.NotFound(ctx, "No such order.")));
}

builder.Services.AddWebAppBridge<OrdersBridge>();
```

   Answer failures with `WebAppBridgeResults.Error(ctx, status, code, message)`, `NotSupported` (501),
   `BadRequest`, `NotFound`. Publish events with `events.Publish(name, payload, typeInfo)`.
3. Map platform enums onto contract enums with `BridgeEnum.Convert<TFrom, TTo>` (by name) rather than casting.

## File roots at runtime

`WebAppFileRoots` (a singleton) holds every root. `Add(WebAppFileStore)` / `Remove(name)` for roots a bridge
creates; `TryResolve(root, path, out fullPath)` for bridges that need a disk path. Subclass `WebAppFileStore`
for storage that is not a directory; paths reach it pre-checked by `WebAppFilePath.Normalize`; throw
`WebAppFileException` for failures the page should see.

## Security — do not loosen

- Bridges are device access. The default policy admits callers on this device only (plus the WebView's session
  with the WebView host). **Debug builds admit any caller** (`AllowAnyCallerInDebug`, on by default) — say so when
  a user binds `Server.Address` past loopback.
- To open bridges to others, generate `o.AuthorizeBridges(p => …)` with a real credential from
  `o.AddAuthentication(...)`, keeping `BridgeCallers.IsOnDevice(ctx.HttpContext)` for the device. Never a policy
  that allows everyone in release.
- The app's own endpoints go through `o.ConfigureServer((server, services) => …)`, `o.AddAuthentication`,
  `o.AddAuthorization`; they are authenticated by default, and `WebAppPolicies.Session` accepts only the WebView.
  Bridge policy and endpoint policies are separate.
- Update downloads are ECDSA P-256 signed; keep `PublicKey` compiled into the app.

## Trim and AOT

Everything is trim/AOT-clean. Generated code must use source-generated `JsonTypeInfo`, never reflection-based
serialization or `JsonObject` bodies.
