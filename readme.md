# Shiny.WebAppHost

Ship a web app — Blazor WebAssembly, React, Vue, anything that builds to static files — inside a .NET MAUI
app, served from the device itself, updated from your own server, and able to call native services.

- **Served locally.** A loopback [Shiny.Net.HttpServer](https://shinylib.net/httpserver) serves the app
  straight out of its zip. Nothing is extracted, and it works offline.
- **Updated from your server.** At launch the host asks a Shiny.WebAppHost.AspNetCore server whether the
  installed version is still acceptable. A required update downloads before the app shows. An optional
  one downloads in the background and applies next launch.
- **Verified.** Every release is signed with ECDSA P-256 and checked against a public key compiled
  into the app, then checked against its SHA-256 and size before it can be served. Old releases are
  never reinstalled.
- **Bridged.** Native services become same-origin HTTP endpoints under `/_bridge`, plus one
  Server-Sent Events stream. Each bridge is its own package and takes one extension method.
- **Every head.** Android, iOS, Mac Catalyst and Windows, plus the
  [maui-labs](https://github.com/dotnet/maui-labs) macOS (AppKit) and Linux (GTK4) backends.

```
┌─ MAUI app ───────────────────────────────────────────────────┐
│  WebAppHostView ── WebView ──▶ http://127.0.0.1:5780         │
│                                   │                          │
│  WebAppHost ── Shiny.Net.HttpServer (loopback only)          │
│    ├─ session guard   launch token → HttpOnly cookie         │
│    ├─ /_bridge/*      device · location · BLE · push · …     │
│    ├─ /_bridge/events Server-Sent Events                     │
│    └─ static files ◀─ ZipFileSource ◀─ baseline or download  │
│                                              ▲               │
└──────────────────────────────────────────────┼───────────────┘
                                               │ signed release
                          Shiny.WebAppHost.AspNetCore server
```

## Packages

| Package | Use it in | What it does |
| --- | --- | --- |
| `Shiny.WebAppHost.Maui` | the app | `UseWebAppHost`, `WebAppHostView`, `WebAppHostPage`, `AllowWebPermissions` |
| `Shiny.WebAppHost.Blazor` | the Blazor WebAssembly app | `AddWebAppHostClient()`: `WebAppBridge` calls, `WebAppEvents`, `WebAppNativeCalls` (C# handlers for jobs, GPS, geofences and push) |
| `Shiny.WebAppHost` | (dependency) | host, updater, install store, session guard, bridge contracts, built-in settings and files endpoints; no MAUI dependency |
| `Shiny.WebAppHost.Core` | (dependency) | protocol contracts, version ordering, release signatures |
| `Shiny.WebAppHost.AspNetCore` | your server | `AddWebAppReleases`, `MapWebAppReleases`, file-system release store |
| `Shiny.WebAppHost.Bridge.AppSupport` | the app | `AddAppSupportBridge()` — device info, orientation, browser, maps, settings, app store, launch at login, share, haptics and vibration, connectivity, battery, screen and clipboard |
| `Shiny.WebAppHost.Bridge.Locations` | the app | `AddGpsBridge()`, `AddGeofenceBridge()`, `AddLocationBridges()`, `AddMotionActivityBridge()` |
| `Shiny.WebAppHost.Bridge.BluetoothLE` | the app | `AddBluetoothLEBridge()` |
| `Shiny.WebAppHost.Bridge.Obd` | the app | `AddObdBridge()`: OBD-II over Bluetooth LE or Wi-Fi adapters — decoded PIDs, VIN, trouble codes, live readings |
| `Shiny.WebAppHost.Bridge.Wifi` | the app | `AddWifiBridge(hotspot: false)`: current network and changes, scan, connect, known networks, radio, hotspot |
| `Shiny.WebAppHost.Bridge.Discovery` | the app | `AddDiscoveryBridge(DiscoveryProtocols.All)`: mDNS/Bonjour, SSDP/UPnP, WS-Discovery search, browse, resolve and publish |
| `Shiny.WebAppHost.Bridge.Jobs` | the app | `AddWebAppJob(name, configure)`: background jobs handled by the page or `background.js` |
| `Shiny.WebAppHost.Bridge.Push` | the app | `AddPushBridge()`: register, unregister, token, tags, and optionally push payloads for the web app |
| `Shiny.WebAppHost.Bridge.Notifications` | the app | `AddNotificationsBridge()`: local notifications now, scheduled, repeating or at a geofence; pending, cancel, badge, channels; taps handed to the web app |
| `Shiny.WebAppHost.Bridge.HttpTransfers` | the app | `AddHttpTransfersBridge()`: background uploads and downloads to and from file roots, with progress events and completion handlers |
| `Shiny.WebAppHost.Bridge.AppLinks` | the app | `AddAppLinksBridge(o => o.Schemes.Add("myapp"))`: deep links and universal/app links routed to the page |
| `Shiny.WebAppHost.Bridge.Health` | the app | `AddHealthBridge()`: HealthKit and Health Connect permissions, bucketed reads, writes and live readings |
| `Shiny.WebAppHost.Bridge.Speech` | the app | `AddSpeechBridge()`: on-device speech recognition, dictation as events, text-to-speech, voices |
| `Shiny.WebAppHost.Bridge.Contacts` | the app | `AddContactsBridge()`: access, paged search, read, photos, create, update and delete (Android, iOS) |
| `Shiny.WebAppHost.Bridge.Calendar` | the app | `AddCalendarBridge()`: access, calendars, events in a date range, create, update and delete |
| `Shiny.WebAppHost.Bridge.TrayIcon` | the app | `AddTrayIconBridge()`: system tray / menu bar icons, menus, badges, notifications and animation, with clicks handed back to the web app |

## The app

```csharp
builder
    .UseMauiApp<App>()
    .UseWebAppHost(o =>
    {
        o.AppId = "field-app";
        o.UseBaseline(typeof(App).Assembly, "webapp.zip", "1.0.0");   // runs offline on first launch
        o.UpdateServer = new Uri("https://api.example.com/webapps");
        o.PublicKey = """
            -----BEGIN PUBLIC KEY-----
            ...
            -----END PUBLIC KEY-----
            """;
    })
    .AddAppSupportBridge()
    .AddLocationBridges()
    .AddBluetoothLEBridge();
```

```csharp
public class App : Application
{
    protected override Window CreateWindow(IActivationState? state) => new(new WebAppHostPage());
}
```

Updates are optional. Without an `UpdateServer` nothing is checked, downloaded or signed, and the app
simply serves the zip compiled into it — which is a complete setup on its own:

```csharp
o.AppId = "field-app";
o.UseBaseline(typeof(App).Assembly, "webapp.zip");   // version defaults to 1.0.0
```

There is no manifest, no signing key and no network at any point; the install directory is never even
created. Add `UpdateServer` and `PublicKey` later and the embedded build becomes the floor that
downloads are compared against, which is when the `version` argument starts to matter.

Each bridge extension also registers the Shiny service behind it, and calls `UseShiny()` if nothing
has yet. Don't add `AddGps()`, `AddGeofencing()` or `AddBluetoothLE()` yourself. Where a platform has
no implementation, that bridge's endpoints return `501` and `GET /_bridge/host` reports it as
unsupported.

Embed the baseline zip with a `LogicalName`:

```xml
<EmbeddedResource Include="webapp.zip" LogicalName="webapp.zip" />
```

The zip can hold the files at its root or under `wwwroot/`. A zipped Blazor publish works either way,
and its precompressed `.br`/`.gz` files are served as they are.

### Platform setup

| Platform | Required |
| --- | --- |
| Android | Cleartext to `127.0.0.1`: a network security config (see the sample) or `usesCleartextTraffic` |
| iOS / Mac Catalyst | `NSAppTransportSecurity` → `NSAllowsLocalNetworking` |
| Mac Catalyst, sandboxed macOS | `com.apple.security.network.server` entitlement |

Plus the usage descriptions and permissions for whichever bridges you add.

### Options worth knowing

| Option | Default | Why |
| --- | --- | --- |
| `Port` | `5780` | Fixed on purpose. localStorage, IndexedDB and cookies belong to the origin, and the port is part of the origin. |
| `AllowPortFallback` | `true` | If the port is taken, serve on a random one, with empty web storage for that launch. |
| `CheckTimeout` | 5 s | After this, the installed version is shown anyway. |
| `Channel` | stable | Follow a prerelease channel such as `beta`. |
| `BlockOnRequiredUpdateFailure` | `false` | By default, a required download that fails midway is treated as offline. |
| `ApplyOptionalUpdatesImmediately` | `false` | Swap to an optional update and reload as soon as it lands. |
| `RemoteAccess.Enabled` | `false` | Bind past loopback. Every bridge still stays on the device until named — see [Serving the network](#serving-the-network). |

### Camera, microphone and location in the page

The page can use `getUserMedia`, `navigator.geolocation` and `<input type="file" capture>` directly, with no
bridge, but not by default. A WebView denies these unless the app decides for it, and on Android it can't even
ask for the runtime permission. Say which ones the web app may use:

```csharp
builder
    .UseWebAppHost(o => { … })
    .AllowWebPermissions(WebAppWebPermissions.Camera | WebAppWebPermissions.Microphone | WebAppWebPermissions.Geolocation);
```

- **Only the web app gets them.** Requests from any other origin, such as a site the user navigated to or a
  third-party iframe, are denied. One exception: Android's file chooser doesn't say which frame opened it, so
  an iframe the web app embeds can still reach the camera through `<input capture>`.
- **The OS prompt comes when the page first asks.** You still declare the permissions: `CAMERA`, `RECORD_AUDIO`,
  `MODIFY_AUDIO_SETTINGS` and the location permissions on Android; `NSCameraUsageDescription`,
  `NSMicrophoneUsageDescription` and `NSLocationWhenInUseUsageDescription` on Apple platforms, plus the
  `com.apple.security.device.camera` and `com.apple.security.device.audio-input` entitlements when sandboxed.
  On Apple platforms a missing usage description crashes the app when the page asks.
- **File inputs** already work everywhere MAUI's WebView supports them. On Android, `capture` opens the camera
  when `Camera` is allowed; otherwise it opens the file picker. On the macOS (AppKit) head the host adds the open
  panel that head lacks.

| | Camera / microphone | Geolocation |
| --- | --- | --- |
| Android | decided by the host | decided by the host |
| iOS, Mac Catalyst, macOS (AppKit) | decided by the host | WebKit asks the user itself; the usage description is the only gate |
| Windows | decided by the host | decided by the host |
| Linux (GTK4) | denied: WebKitGTK needs a `permission-request` handler, which the host doesn't install | denied |

## The server

```csharp
builder.Services.AddWebAppReleases(o =>
{
    o.SigningKey = builder.Configuration["WebApps:SigningKey"];   // PEM, from your secret store
    o.ReleasesDirectory = "/srv/webapps";
});

app.MapWebAppReleases("/webapps");   // returns the route group: add auth, rate limiting, caching
```

Publishing a release means copying a file:

```
/srv/webapps/field-app/
    app.json              { "minimumVersion": "1.2.0" }
    1.2.0.zip
    1.3.0-beta.1.zip
    1.3.0-beta.1.json     { "channel": "beta", "minimumHostVersion": "2.1", "platforms": ["ios", "android"] }
```

Hashes and sizes are computed and cached. Implement `IWebAppReleaseStore` to serve releases from blob
storage or a database instead. Set `downloadUrl` in the sidecar to serve the zip from a CDN; the
signature still covers it.

Create a key pair once, with `WebAppReleaseSignature.CreateKeyPair()` or openssl:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt > private.pem
openssl ec -in private.pem -pubout > public.pem
```

### Update rules

- **Nothing installed:** any compatible release is required.
- **Below `minimumVersion`:** required, and it installs before the app shows.
- **Newer release available:** optional. It downloads in the background and applies next launch, or
  when the page calls `POST /_bridge/host/apply-update`.
- **Release needs a newer native app:** skipped. Set `minimumHostVersion` when a web build depends
  on bridge endpoints older apps don't have.
- **Offline or server down:** the newer of the bundled and installed builds is served.

## Security model

Binding to loopback keeps other machines out, but not other apps: on Android any app can connect to
`127.0.0.1`. So:

- **Launch token.** Each launch generates a 256-bit token. The WebView's first navigation trades it
  for an `HttpOnly`, `SameSite=Strict` cookie. Every other request without that cookie gets a `403`.
- **Host header.** Requests whose `Host` isn't the loopback origin get a `421`. This blocks DNS
  rebinding.
- **Origin header.** Bridge calls that carry an `Origin` must carry this one.
- **Releases.** A release must pass five checks before it's served: signature, app id, a version
  newer than the installed one, host compatibility, then size and hash. An archive without its entry
  document is refused.
- **Anything not from this device** is held to a second, stricter set of rules, and by default there is
  nothing for it to reach. See below.

### Serving the network

The server is loopback-only until you say otherwise, and saying otherwise does not open the bridges —
they're raw device access, and a caller on the network has no session and no launch token. Each one is
published by name:

```csharp
o.RemoteAccess.Enabled = true;                          // bind past loopback
o.RemoteAccess.AllowBridge("files", "settings");        // and only these, remotely
o.RemoteAccess.ServeWebApp = true;                      // optional: the app's own pages too
```

```
GET http://192.168.1.15:5780/_bridge/files/data/list?path=exports   → 200
GET http://192.168.1.15:5780/_bridge/ble/status                     → 403 remote_denied
```

- **The allowlist is the authorization.** There's no credential. Anything that can reach the port can
  call the bridges you name, and an allowed bridge is fully reachable — every route, every method,
  writes included. Name only what you'd put on an unauthenticated HTTP endpoint.
  `RemoteAccess.Authorize` is the hook for a check of your own; return `false` and the request gets `401`.
  Nothing on this device goes through it, so the WebView is unaffected.
- **The session never leaves the device.** `/_host/start` answers `403` over the network, so a remote
  caller can't trade a token for the cookie even holding one.
- **Host headers must be an IP address**, or a name in `RemoteAccess.AllowedHosts`. That's what stops
  DNS rebinding: a hostile site pointing its own name at the device arrives under that name and gets
  `421`. Add `AllowHost("kiosk.local")` for an mDNS name you control.
- **A browser can't drive it cross-origin.** A remote bridge call carrying an `Origin` that isn't the
  request's own is refused. Clients that aren't browsers send none and are unaffected.
- **The dev server is never relayed.** Remote callers get the installed build, never the proxy to
  `dotnet watch`.
- Nothing here is compiled differently in Debug. If you want it open while testing, set it yourself
  under your app's own `#if DEBUG` — `#if DEBUG` inside the package would be the package's build,
  not yours.

## Bridges

| Bridge | Routes | Events |
| --- | --- | --- |
| host (built in) | `GET /_bridge/host`, `POST /_bridge/host/apply-update` | |
| settings (built in) | `GET/DELETE settings/{local\|secure}`, `GET/PUT/DELETE settings/{scope}/{key}` | |
| files (built in) | `GET files`, `GET files/{root}/list`, `GET files/{root}/info`, `GET/PUT files/{root}/content`, `POST files/{root}/append`, `POST files/{root}/directory`, `DELETE files/{root}/entry`, `POST files/{root}/move`, `POST files/{root}/copy`, with paths in `?path=` | |
| AppSupport | `GET app/info`, `POST/DELETE app/orientation`, `POST app/browser`, `POST app/map`, `POST app/settings`, `GET app/store`, `POST app/store/open`, `POST app/store/review`, `POST app/share`, `POST app/haptics`, `POST/DELETE app/vibrate`, `GET app/connectivity`, `GET app/battery`, `GET app/screen`, `PUT/DELETE app/screen/keep-awake`, `GET/PUT/DELETE app/clipboard`, `GET app/startup`, `POST/DELETE app/startup/registration`, `POST app/startup/settings` | `app.orientation`, `app.culture`, `app.timezone`, `app.connectivity`, `app.battery`, `app.energysaver` |
| GPS | `GET gps/status`, `POST gps/access`, `GET gps/last`, `GET gps/current`, `GET/POST/DELETE gps/listener` | `gps.reading` |
| Geofences | `GET geofences/status`, `POST geofences/access`, `GET/POST/DELETE geofences/regions`, `DELETE geofences/regions/{id}`, `GET geofences/regions/{id}/state` | `geofence.status` |
| Motion activity | `GET motion/status`, `POST motion/access`, `GET motion/current`, `GET/POST/DELETE motion/listener` | `motion.activity` |
| Bluetooth LE | `GET ble/status`, `POST ble/access`, `POST/DELETE ble/scan`, `GET ble/peripherals[/{uuid}]`, `POST/DELETE …/connection`, `GET …/rssi`, `GET …/services`, `GET …/characteristics`, `GET/PUT …/characteristics/{c}`, `POST/DELETE …/notifications` | `ble.scan`, `ble.status`, `ble.notification`, `ble.error` |
| OBD-II | `GET obd/status`, `GET obd/commands`, `POST obd/scan`, `GET obd/adapters`, `POST/DELETE obd/connection`, `POST obd/command`, `GET obd/vin`, `GET/DELETE obd/dtc`, `POST/DELETE obd/monitor` | `obd.reading`, `obd.disconnected` |
| Wi-Fi | `GET wifi`, `POST wifi/access`, `GET wifi/networks`, `GET wifi/current`, `POST/DELETE wifi/connection`, `GET wifi/known`, `DELETE wifi/known?id=`, `GET/PUT wifi/radio`, `GET/POST/DELETE wifi/hotspot`, `GET wifi/hotspot/clients` | `wifi.changed`, `wifi.hotspot` |
| Discovery | `POST discovery/{mdns,ssdp,wsd}/search`, `POST discovery/{mdns,ssdp,wsd}/browse`, `GET discovery/mdns/resolve`, `GET discovery/wsd/resolve`, `GET discovery/ssdp/description?udn=`, `POST discovery/{mdns,ssdp,wsd}/publications`, `GET discovery/browses`, `DELETE discovery/browses/{id}`, `GET discovery/publications`, `DELETE discovery/publications/{id}` | `discovery.mdns`, `discovery.ssdp`, `discovery.wsd`, `discovery.error`, `discovery.stopped` |
| Notifications | `GET notifications`, `POST notifications/access`, `POST notifications/send`, `GET notifications/pending`, `DELETE notifications[?scope=]`, `DELETE notifications/{id}`, `GET/PUT notifications/badge`, `GET/POST notifications/channels`, `DELETE notifications/channels/{id}` | `notification.entry`, `notification.received` |
| HTTP transfers | `GET/POST/DELETE transfers`, `GET/DELETE transfers/{id}`, `POST transfers/{id}/pause`, `POST transfers/{id}/resume` | `transfer.progress`, `transfer.completed`, `transfer.failed`, `transfer.cancelled` |
| App links | `GET/DELETE links/pending` | `app.link` |
| Health | `GET health`, `POST health/access`, `GET/POST health/samples/{type}`, `POST/DELETE health/listeners/{type}` | `health.reading`, `health.stopped` |
| Speech | `GET speech/status`, `POST speech/access`, `POST speech/recognize`, `GET/POST/DELETE speech/listener`, `POST/DELETE speech/speak`, `GET speech/voices?culture=`, `GET speech/cultures` | `speech.partial`, `speech.result`, `speech.keyword`, `speech.ended`, `speech.spoken`, `speech.error` |
| Contacts | `GET contacts`, `POST contacts/access`, `GET/POST contacts/items`, `GET/PUT/DELETE contacts/items/{id}`, `GET contacts/items/{id}/photo` | |
| Calendar | `GET calendar`, `POST calendar/access`, `GET calendar/calendars`, `GET/POST calendar/events`, `GET/PUT/DELETE calendar/events/{id}` | |
| Tray icon | `GET/POST/DELETE tray`, `GET/PUT/DELETE tray/{id}`, `PUT/DELETE tray/{id}/menu`, `POST tray/{id}/menu/show`, `POST tray/{id}/notification`, `PUT/DELETE tray/{id}/animation` | `tray.click`, `tray.menu` |

```js
const info = await (await fetch("/_bridge/app/info")).json();

const events = new EventSource("/_bridge/events");
events.addEventListener("gps.reading", e => console.log(JSON.parse(e.data)));
```

Errors return `{ "code": "...", "message": "..." }`, so pages can switch on `code`.

**Wi-Fi:** what works depends on the platform. iOS can't scan, and Android can't toggle the radio.
`GET /_bridge/wifi` lists the platform's capabilities, and any call it lacks returns `501`. Linux
uses NetworkManager through `Shiny.Net.Wifi.Linux`, which the bridge picks automatically.
`wifi.changed` only runs while a page is listening. Permissions come from Shiny.Net.Wifi: location on
Android; the Access Wi-Fi Information and Hotspot Configuration entitlements on iOS.

**Discovery:**
- **Searching:** `search` returns everything seen during `scanMs`, 5 s by default and 30 s at most.
- **Browsing:** each `browse` streams results as events. At most 8 run at once, and they stop when
  the page's last event stream closes.
- **Publishing:** `publications` keep advertising until deleted. At most 16.
- **Platform setup:** on iOS and Mac Catalyst, list every browsed service type in `NSBonjourServices`
  and set `NSLocalNetworkUsageDescription`. On Android, SSDP and WS-Discovery need
  `CHANGE_WIFI_MULTICAST_STATE`.

**Device:** sharing, haptics, connectivity, battery, the screen and the clipboard come from .NET MAUI
Essentials, so each head's own build decides what works. A feature a backend lacks returns `501` on its
own, and the rest keep working. Files are shared by the same `{ root, path }` as the files bridge:

```js
await fetch("/_bridge/app/share", { method: "POST", body: JSON.stringify({ files: [{ root: "data", path: "photos/cat.jpg" }] }) });
```

`app.connectivity`, `app.battery` and `app.energysaver` only run while a page is listening. On Android,
vibration needs `VIBRATE`, and battery needs `BATTERY_STATS` in the manifest (without it, `GET app/battery`
returns `403`). `vibrate` is capped at 5 seconds.

**Motion activity:** walking, running, cycling, driving or stationary, from the OS's activity recognition.
There's no history, only the latest reading and live ones. `AddLocationBridges()` doesn't include it,
because it needs its own setup: `NSMotionUsageDescription` on iOS (the permission request crashes without it),
and `ACTIVITY_RECOGNITION` with Google Play Services on Android. Other platforms return `501`.

**OBD-II:**
- **Adapters:** ELM327 and OBDLink, one at a time. `scan` finds Bluetooth LE adapters, or with
  `"transport": "wifi"` probes the addresses Wi-Fi adapters ship with. `connection` takes the
  `peripheralUuid` from a scan, or a Wi-Fi `host` and `port`. The host must be a loopback, private or
  link-local IP address.
- **Reading:** `command` takes a name from `GET obd/commands` (`engineRpm`, `vehicleSpeed`,
  `coolantTemperature`…) and answers the decoded value with its unit. `raw` sends a read-only request:
  modes 01, 02, 03, 05, 06, 07, 09, 0A or 22, or an informational AT command. Commands are serialized,
  because ELM327 is half-duplex.
- **Trouble codes:** `GET obd/dtc` returns stored, pending and permanent codes. A list is `null` when
  the vehicle doesn't support that mode. `DELETE obd/dtc` needs `?confirm=true`, because clearing codes
  also resets the emissions readiness monitors.
- **Monitoring:** `monitor` polls up to 10 commands, every 250 ms at most often, and sends each result
  as `obd.reading`. It stops when the page's last event stream closes. After three rounds with no
  answers the connection is dropped with `obd.disconnected`.
- **Platform setup:** Bluetooth as for the Bluetooth LE bridge. Wi-Fi adapters need
  `NSLocalNetworkUsageDescription` on iOS and Mac Catalyst. On Android, the app has to bind to the
  adapter's network, which has no internet. Linux supports Wi-Fi adapters only.

**HTTP transfers:**
- **Queuing:** `POST /_bridge/transfers` with a `type` (`Download`, `UploadMultipart` or `UploadRaw`), a `url`,
  and a file as `root` plus `path`, the same names `/_bridge/files` uses. Uploads can add `method`, `headers`,
  `formDataName` and a small multipart `body`. Downloads can pass `"overwrite": false`.
- **Downloads land whole:** the file is written beside its destination under a hidden name and moved into
  place when it completes. The destination is checked again at that moment.
- **Limits:** http and https only. Loopback URLs are refused unless you change `AllowUrl`. Connection,
  length and proxy headers are refused. At most `MaxTransfers` (32) are queued at once.
- **Ownership:** the page only lists and cancels its own transfers. `DELETE /_bridge/transfers` leaves the native
  app's transfers running.
- **Finishing in the background:** `transfer.completed` and `transfer.failed` reach the page, or background.js
  when no page is open. Progress is an event only, at most every `ProgressInterval` (250 ms) per transfer.
- **Platform setup:** Android needs `FOREGROUND_SERVICE_DATA_SYNC`. On iOS and Mac Catalyst, override
  `HandleEventsForBackgroundUrl` in the app delegate and pass it to
  `Shiny.Hosting.Host.Lifecycle.OnHandleEventsForBackgroundUrl`, or transfers that finish while the app is
  suspended wait for the next launch. macOS and Linux run transfers in-process only.

**App links:** links to the app become routes in the web app. A custom scheme's host is the first path segment,
so `myapp://orders/42` becomes `/orders/42`. An https link on a listed host keeps its path, so
`https://app.example.com/orders/42` also becomes `/orders/42`. Set `MapRoute` to map links another way.
- **Other links:** any other scheme or host is left to the native app.
- **Route checks:** a route must be a local page path. Routes that aren't, including anything under `/_bridge`
  or `/_host`, are refused, whatever `MapRoute` returns.
- **Delivery:** the page gets `app.link` while it's open. `DELETE /_bridge/links/pending` returns the latest
  link and removes it. Call it at boot and on each event, so a link is acted on exactly once.
- **Cold start:** with `NavigateOnColdStart`, a link that launched the app becomes the first page loaded, and is
  consumed.
- **Background:** links never go to `background.js`.

```js
async function openPendingLink() {
    const response = await fetch("/_bridge/links/pending", { method: "DELETE" });
    if (response.status === 200) router.push((await response.json()).route);
}
openPendingLink();
events.addEventListener("app.link", openPendingLink);
```

Platform setup:
- **Android:** a `SingleTop` main activity with `ACTION_VIEW` intent filters, plus `assetlinks.json` for https.
- **Apple:** `CFBundleURLTypes`, plus the Associated Domains entitlement and `apple-app-site-association` for https.
- **maui-labs AppKit:** the head overrides `OpenUrls` and calls `AppLinks.Receive`, because the launching link
  arrives before `MauiProgram`.
- **Windows:** a protocol registration; redirect activations to the first instance for links while running.
- **Linux:** `x-scheme-handler` in the .desktop file; cold starts only.

**Health:**
- **Platforms:** HealthKit on iOS, Health Connect on Android. Other platforms return `501`. On Android
  without Health Connect, every call except `GET health` returns `503`.
- **Access:** ask for access before reading, one entry per type. On iOS, HealthKit never reveals a read
  denial: `granted` can be `true` and reads still come back empty. On Android, reading a type that
  wasn't granted returns `403`.
- **Types:** `GET health` lists every type, with its unit and whether it's bucketed.
- **Reads:** numeric types and blood pressure are totalled or averaged into `minutes`, `hours` or `days`
  buckets. Cycle tracking, workouts and nutrition come back as individual records. A read covers at
  most 366 days and 2,000 buckets.
- **Writes:** `POST health/samples/{type}` takes `start`, `end` and the fields for the type: `value`,
  `systolic`/`diastolic`, `flow`, `workout` and so on.
- **Listeners:** a listener sends `health.reading` for new samples. At most 8 run at once, and they stop
  when the page's last event stream closes.
- **Privacy:** health values are never logged.
- **Platform setup:** on iOS, add the HealthKit entitlement plus `NSHealthShareUsageDescription` and
  `NSHealthUpdateUsageDescription`. On Android, set minSdk 26, add a `android.permission.health.*`
  permission for each record type, the Health Connect `<queries>` entry, and the
  `VIEW_PERMISSION_USAGE` activity-alias. MainActivity also needs a filter for
  `androidx.health.ACTION_SHOW_PERMISSIONS_RATIONALE`.

```js
await fetch("/_bridge/health/access", { method: "POST", body: JSON.stringify({ permissions: [{ type: "StepCount", access: "Read" }] }) });
const today = new Date(); today.setHours(0, 0, 0, 0);
const steps = await (await fetch(`/_bridge/health/samples/StepCount?start=${today.toISOString()}&end=${new Date().toISOString()}&interval=hours`)).json();
```

**Speech:** built on Shiny.Speech, which is still a prerelease package.
- **Recognizing once:** `recognize` listens until a pause and returns `{ "text": … }`. It gives up after
  `timeoutMs`: 15 s by default, 60 s at most. `text` is `null` if nothing was heard.
- **Dictation:** `POST listener` keeps the microphone open and streams `speech.partial` and
  `speech.result` events until you delete it. It also stops when the page's last event stream
  closes, so a page that goes away can't leave the microphone on. `speech.ended` says why it
  stopped: `stopped`, `page_closed` or `error`.
- **One microphone:** a recognition and a listener can't run at once. The second gets `409`
  `microphone_busy`.
- **Speaking:** `speak` waits until the text has been spoken, unless you pass `"wait": false`, which
  returns `202` and raises `speech.spoken` when done. A new utterance interrupts the current one.
  Text is capped at 4,000 characters.
- **Platforms:** Linux has no OS speech engine, so its endpoints return `501`. To use a cloud
  provider or Whisper there, pass `o => o.RegisterSpeechServices = false` and register your own
  `ISpeechToTextService` / `ITextToSpeechService`.
- **Platform setup:** Android needs `RECORD_AUDIO`, plus a `<queries>` entry for
  `android.intent.action.TTS_SERVICE`. Apple platforms need `NSSpeechRecognitionUsageDescription` and
  `NSMicrophoneUsageDescription`, plus the `com.apple.security.device.audio-input` entitlement for sandboxed apps.

**Contacts:**
- **Platforms:** Android and iOS. Shiny.Contacts has no Mac Catalyst, macOS, Windows or Linux backend, so there
  every endpoint returns `501`.
- **Listing:** `GET contacts/items?search=&offset=&limit=` returns one page (50 by default, 500 at most) and
  `hasMore`. `search` matches names, phone numbers and emails.
- **Photos:** photos never go in the JSON. `hasPhoto` says whether one exists, and
  `GET contacts/items/{id}/photo?size=full|thumbnail` returns the image bytes.
- **Writing:** `PUT` changes only the properties you send. An empty list clears one.
- **iOS:** reading `note` and `relationships` needs the `com.apple.developer.contacts.notes` entitlement.
  Without it they come back empty.

**Calendar:**
- **Platforms:** Android, iOS, Mac Catalyst, macOS and Windows.
- **Access:** `POST calendar/access` takes `ReadWrite` (the default), `ReadOnly` or `WriteOnly`. Write-only
  access (iOS 17+) is reported as `Restricted`.
- **Listing:** `GET calendar/events` requires `start` and `end`, at most 366 days apart. Paging works like
  contacts.
- **Reminders:** `reminderMinutes` counts minutes before the start.
- **Read-only fields:** attendees, the organizer and recurrence can be read but not written.
- **Read-only calendars:** writing to one returns `403 read_only`, and so does writing to a system calendar on
  Windows, which only allows writes to app-owned calendars.
- **Deleting:** `DELETE calendar/events/{id}?series=true` removes the rest of a recurring series rather than one
  occurrence.
- **Mac Catalyst, sandboxed macOS:** also need the `com.apple.security.personal-information.calendars`
  entitlement. Without it, access is denied without a prompt.

Both bridges return `403 access_denied` until access has been granted.

**Startup:** part of the app bridge, because the same package is behind it. `GET /_bridge/app/startup` says
whether the app launches when the user logs in. Windows writes it under `HKCU\…\CurrentVersion\Run`
(unpackaged apps only — the OS virtualizes that key for MSIX), macOS 13+ submits the running bundle to
`SMAppService`, and Linux writes `~/.config/autostart/{Identifier}.desktop`. Mobile has no such list, so it
answers `{ "supported": false, "state": "NotSupported" }` and the rest return `501`. `state` is read back from
the OS every time rather than remembered, because the user can turn a registered app off in Task Manager,
System Settings or Login Items without the app hearing about it — which is also why `Enabled` is not the only
success: `DisabledByUser`, `DisabledByPolicy` and `RequiresApproval` mean the user has to finish the job in the
OS, and `POST app/startup/settings` opens the screen where they do. Pass arguments your app can recognise on an
OS-started launch:

```csharp
builder.AddAppSupportBridge(startup: o => o.Arguments.Add("--autostart"));
```

**Tray icon:** the system tray on Windows, the menu bar on macOS, the status notifier area on Linux —
desktop only, `501` elsewhere.
- **Naming an icon:** use `PUT /_bridge/tray/main` rather than `POST /_bridge/tray`. The first call creates
  the icon and later ones adopt it, so a page reload or an applied update doesn't stack up a second icon.
  A `PUT` changes only the properties it sends; `""` clears `tooltip`, `title` or `badge`.
- **Images:** either `{ "root": "data", "path": "icons/tray.png" }` — the same file roots the files bridge
  takes, so the page can't point the tray at anything it couldn't already read — or `{ "data": "…" }` holding
  base64, with or without a `data:` URI prefix, which is how a page ships an icon it drew itself. Set
  `"templateImage": true` for a black-with-alpha image macOS and Linux tint for light and dark menu bars.
- **Menus:** each entry is `Item`, `Check`, `Separator` or `Submenu`, and its `id` is what comes back on
  `tray.menu`; omit it and one is assigned. Ids must be unique across the whole menu.
- **Clicks reach the web app either way:** `tray.click` and `tray.menu` go out as events *and* as calls the
  page handles when it is open and `background.js` handles when it is not — which is the case a tray menu
  exists for. `tray.click` isn't raised on Linux, where the app indicator handles clicks itself and opens the
  menu on its own (so `menu/show` is a no-op there).
- **Lifetime:** icons outlive the page and are removed when the app shuts down, or by
  `DELETE /_bridge/tray/{id}`. `MaxIcons` (4) caps how many exist at once.

```js
await fetch("/_bridge/tray/main", {
    method: "PUT",
    body: JSON.stringify({
        tooltip: "Field App",
        templateImage: true,
        icon: { root: "data", path: "icons/tray.png" },
        menu: { items: [
            { id: "open", label: "Open" },
            { type: "Separator" },
            { id: "sync", type: "Check", label: "Sync", checked: true }
        ] }
    })
});
```

### Settings and files

Both are built into the host and on by default (`EnableSettings`, `EnableFiles`).

**Settings** go through Shiny.Extensions.Stores. `local` is the platform's settings store. `secure` is
its secure store: Keychain, Android KeyStore or DPAPI. On Linux it's a plain file, and the listing's
`encrypted` flag says so. Values can be any JSON and come back exactly as written. Keys are namespaced
by app id, so the page never sees or clears the native app's own settings.

```js
await fetch("/_bridge/settings/secure/token", { method: "PUT", body: JSON.stringify(token) });
const token = await (await fetch("/_bridge/settings/secure/token")).json();
```

**Files** are confined to named roots: `data` (persistent) and `cache` (the OS may clear it), unless
you set `FileRoots`. Paths are relative and use forward slashes. A path is refused if it contains
`..`, `\` or `:`, or passes through a link that leads out of the root. Writes are atomic, parent
directories are created as needed, and `MaxFileWriteBytes` (256 MB) caps a single file.

```js
await fetch("/_bridge/files/data/content?path=photos/cat.jpg", { method: "PUT", body: blob });
const entries = await (await fetch("/_bridge/files/data/list?path=photos")).json();
await fetch("/_bridge/files/data/move", { method: "POST", body: JSON.stringify({ from: "photos/cat.jpg", to: "photos/tabby.jpg" }) });
```

### Writing a bridge

```csharp
public sealed class ClipboardBridge(IClipboard clipboard) : IWebAppBridge
{
    public string Name => "clipboard";
    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", async ctx => await WebAppBridgeResults.Json(ctx, new Clip(await clipboard.GetTextAsync()), MyJson.Default.Clip));
}

public static MauiAppBuilder AddClipboardBridge(this MauiAppBuilder builder)
{
    builder.Services.AddWebAppBridge<ClipboardBridge>();
    return builder;
}
```

Use source-generated JSON contexts (`JsonTypeInfo`). The packages are trim and AOT clean, and bridges
should stay that way.

## Calling the web app from native code

Background jobs, GPS readings, geofence transitions and pushes can call into the web app, whether or
not a page is open.

- **Page open and listening:** the call goes to the page. The page has 2 seconds to accept it, then
  posts its result.
- **No page, or the page doesn't accept in time:** the call runs in `background.js` from the web app's
  zip, inside an embedded JavaScript engine ([Jint](https://github.com/sebastienros/jint)).
- **Accepted by the page:** the page owns the call. A call is never run in both places.

```js
// in the page
import { on } from "/_bridge/invoke/client.js";
on("job:sync", async ({ name }) => { /* ... */ });
```

```js
// background.js: a classic script at the root of the zip
webapphost.on("job:sync", async ({ name }) => {
    const token = await (await fetch("/_bridge/settings/secure/token")).json();
    const data = await fetch("https://api.example.com/sync", { headers: { Authorization: `Bearer ${token}` } });
    await fetch("/_bridge/files/data/content?path=sync.json", { method: "PUT", body: await data.text() });
});
```

`background.js` gets `webapphost.on`, `console` and `fetch` with string bodies. Relative URLs go to the
host's own `/_bridge`, so handlers read and write the same settings and files the page uses. It has
no DOM, no timers, and keeps no state between calls: each call runs the script's top level again,
then the handler. It gets `BackgroundScriptTimeout` (25 s) and 64 MB.

| Source | Handler | Registered by |
| --- | --- | --- |
| Background job | `job:{name}` with `{ name }` | `builder.AddWebAppJob("sync", job => job.WithInternet(InternetAccess.Any))` (Bridge.Jobs) |
| GPS reading delivered in the background | `gps` with a reading | `AddGpsBridge()` |
| Geofence transition | `geofence` with `{ identifier, state }` | `AddGeofenceBridge()` |
| Push | `push.received`, `push.entry` with `{ data, title, message }` | `AddPushBridge(o => o.DispatchToWebApp = true)` (Bridge.Push) |
| Motion activity delivered in the background | `motion` with `{ activity, confidence, timestamp }` | `AddMotionActivityBridge()` (Bridge.Locations) |
| Notification tapped | `notification.entry` with `{ id, title, message, channel, thread, data, action, text }` | `AddNotificationsBridge()` (Bridge.Notifications) |
| Notification presented while the app is open (Apple platforms) | `notification.received` with the same shape | `AddNotificationsBridge()` |
| HTTP transfer finished | `transfer.completed` / `transfer.failed` with the transfer | `AddHttpTransfersBridge()` (Bridge.HttpTransfers) |

From your own native code, call `WebAppInvoker.InvokeAsync(name, payload, typeInfo)`.

**Jobs:** the OS picks when jobs run: at best every 15 minutes on Android, less predictably on iOS.
Jobs with the same charging and network requirements run together as one native job. On iOS, add
`BGTaskSchedulerPermittedIdentifiers` (`com.shiny.job`, `com.shiny.jobnet`, `com.shiny.jobpower`,
`com.shiny.jobpowernet`) and the `processing` background mode.

**Push:** `AddPushBridge()` always adds `GET /_bridge/push` (access and token),
`POST/DELETE /_bridge/push/registration` and `GET/PUT /_bridge/push/tags`, plus the `push.token` and
`push.unregistered` events. Pushes only reach the web app when you set `DispatchToWebApp`. You still
need Shiny.Push's platform setup: APNs entitlements, and `google-services.json` on Android.

**Notifications:** `POST /_bridge/notifications/send` takes a `message` and optionally a `title`, `channel`,
`thread`, `data`, and one trigger: `scheduleDate`, `repeat` (`{ "intervalSeconds": 3600 }` or
`{ "timeOfDay": "09:00:00", "dayOfWeek": "Monday" }`), or `geofence` (`{ "latitude", "longitude", "radiusMeters" }`).
It answers `{ "id": 7 }`. Sending needs no UI, so `background.js` can notify from a job or a geofence.

- **Images:** on iOS and Mac Catalyst, `image: { "root": "data", "path": "photos/cat.jpg" }` attaches a file the
  page wrote through the files bridge. Other platforms ignore it. `GET /_bridge/notifications` reports what the
  platform supports: `badge`, `entry`, `received`, `geofences` and `images`.
- **Ownership:** only notifications the web app sent reach its handlers, unless you set
  `AddNotificationsBridge(o => o.Dispatch = WebAppNotificationDispatch.All)`. The bridge marks its notifications
  with a `webapphost.source` data key, which the page never sees and can't set.
- **Taps:** reach `notification.entry` on Android, iOS and Mac Catalyst. Windows and Linux have no tap callback,
  and on the macOS (AppKit) head nothing runs Shiny's startup tasks, so neither handler fires there yet.
- **Linux:** scheduled notifications only fire while the app runs, and repeating ones don't fire at all in
  Shiny.Notifications.Linux 5.6.3.
- **Platform setup:** on Android, `POST_NOTIFICATIONS`, `SCHEDULE_EXACT_ALARM` for on-time schedules, and a
  drawable named `notification` for the small icon (without it, `send` returns `400`). Geofence triggers need the
  location usage descriptions.

**Blazor WebAssembly:** handlers registered from the page can be C#, through `WebAppNativeCalls` in Shiny.WebAppHost.Blazor. The
background path can't: Jint doesn't run WebAssembly, and a hidden WebView is exactly what iOS
suspends. Write the background handler in JavaScript that stores its results through the bridge;
the Blazor app reads them when it next opens.

## App Store review

The downloaded content is HTML and JavaScript that runs in WebKit. Guideline 2.5.2 and section
3.3.1(B) of the Apple Developer Program License Agreement allow that, as long as updates don't change
the app's primary purpose. Keep native capabilities in the binary (bridges ship with the app), ship a
complete baseline, and use `minimumHostVersion` rather than shipping web features the installed app
can't support.

## Developing the web app with hot reload

In Debug builds the sample sets `DevServer`. The app on the device or emulator then gets its pages from
`dotnet watch` on your machine, while the bridge, settings, files and session stay on the device.

```bash
cd samples/Sample.Blazor
dotnet watch run --launch-profile device       # listens on http://0.0.0.0:5288
```

Start the app from your IDE as usual. Edit a `.razor` file and save, and the change appears in the app
without rebuilding it.

| Target | Dev server | Hot reload |
| --- | --- | --- |
| Android emulator | `http://10.0.2.2:5288` (default) | yes |
| iOS simulator, Mac Catalyst, macOS, Windows, Linux | `http://localhost:5288` (default) | yes |
| Android over USB | `adb reverse tcp:5288 tcp:5288`, then build with `-p:WebAppDevServer=http://localhost:5288` | also `adb reverse` the two socket ports (see below) |
| Any device over Wi-Fi | build with `-p:WebAppDevServer=http://<your machine's LAN address>:5288` | pages only; reload the app to see changes |

**How it works:**
- **At startup:** the host probes `DevServer` for up to 1.5 seconds. If `dotnet watch` isn't running,
  the embedded or installed build is served as usual. `-p:WebAppDevServer=off` switches dev mode off.
- **Pages:** every request outside `/_bridge` and `/_host` is forwarded to the dev server. The page keeps
  its `http://127.0.0.1:5780` origin, so its bridge calls still reach the device.
- **Not forwarded:** the session cookie never leaves the device, and no update check runs.
- **`background.js`:** fetched fresh from the dev server on every call, so edits apply at once.

**The hot reload socket:** `dotnet watch` tells the page to connect to `ws://localhost:<random port>`,
and listens on your machine's loopback only. The host rewrites `localhost` to the dev server's host,
which is enough wherever that host reaches your machine's loopback: the emulator's `10.0.2.2` and the
simulator's `localhost`. A device on Wi-Fi can't reach a loopback-only listener, so it gets live pages
but not live updates. Over USB, forward the socket ports too; they change each time `dotnet watch`
starts:

```bash
curl -s http://localhost:5288/_framework/aspnetcore-browser-refresh.js | grep webSocketUrls
adb reverse tcp:<ws port> tcp:<ws port>
```

## Samples

- `samples/Sample.Blazor`: the web app, a Blazor WebAssembly app with a page for every bridge, a Media page for the camera, microphone and location through the WebView's own APIs, plus `wwwroot/background.js`.
- `samples/Sample.App`: shared MAUI setup using every bridge package except the tray, which each desktop head adds for itself because its dependency is desktop-only. Sample.Blazor is published and zipped into it at build time (`-p:SkipWebAppBuild=true` skips that).
- Heads:
  - `samples/Sample.Maui`: Android, iOS, Mac Catalyst and Windows.
  - `samples/Sample.MacOS`: AppKit.
  - `samples/Sample.Linux`: GTK4.
- `samples/Sample.ReleaseServer`: run it, then `samples/publish-release.sh 1.1.0` publishes an update.

The sample key pair in `samples/keys` is public. It's for development only.
