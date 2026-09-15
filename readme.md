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
│    ├─ /_bridge/*      AppSupport · GPS · geofences · BLE     │
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
| `Shiny.WebAppHost.Maui` | the app | `UseWebAppHost`, `WebAppHostView`, `WebAppHostPage` |
| `Shiny.WebAppHost.Blazor` | the Blazor WebAssembly app | `AddWebAppHostClient()`: `WebAppBridge` calls, `WebAppEvents`, `WebAppNativeCalls` (C# handlers for jobs, GPS, geofences and push) |
| `Shiny.WebAppHost` | (dependency) | host, updater, install store, session guard, bridge contracts, built-in settings and files endpoints; no MAUI dependency |
| `Shiny.WebAppHost.Core` | (dependency) | protocol contracts, version ordering, release signatures |
| `Shiny.WebAppHost.AspNetCore` | your server | `AddWebAppReleases`, `MapWebAppReleases`, file-system release store |
| `Shiny.WebAppHost.Bridge.AppSupport` | the app | `AddAppSupportBridge()` — device info, orientation, browser, maps, settings, app store |
| `Shiny.WebAppHost.Bridge.Locations` | the app | `AddGpsBridge()`, `AddGeofenceBridge()`, `AddLocationBridges()` |
| `Shiny.WebAppHost.Bridge.BluetoothLE` | the app | `AddBluetoothLEBridge()` |
| `Shiny.WebAppHost.Bridge.Wifi` | the app | `AddWifiBridge(hotspot: false)`: current network and changes, scan, connect, known networks, radio, hotspot |
| `Shiny.WebAppHost.Bridge.Discovery` | the app | `AddDiscoveryBridge(DiscoveryProtocols.All)`: mDNS/Bonjour, SSDP/UPnP, WS-Discovery search, browse, resolve and publish |
| `Shiny.WebAppHost.Bridge.Jobs` | the app | `AddWebAppJob(name, configure)`: background jobs handled by the page or `background.js` |
| `Shiny.WebAppHost.Bridge.Push` | the app | `AddPushBridge()`: register, unregister, token, tags, and optionally push payloads for the web app |

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

## Bridges

| Bridge | Routes | Events |
| --- | --- | --- |
| host (built in) | `GET /_bridge/host`, `POST /_bridge/host/apply-update` | |
| settings (built in) | `GET/DELETE settings/{local\|secure}`, `GET/PUT/DELETE settings/{scope}/{key}` | |
| files (built in) | `GET files`, `GET files/{root}/list`, `GET files/{root}/info`, `GET/PUT files/{root}/content`, `POST files/{root}/append`, `POST files/{root}/directory`, `DELETE files/{root}/entry`, `POST files/{root}/move`, `POST files/{root}/copy`, with paths in `?path=` | |
| AppSupport | `GET app/info`, `POST/DELETE app/orientation`, `POST app/browser`, `POST app/map`, `POST app/settings`, `GET app/store`, `POST app/store/open`, `POST app/store/review` | `app.orientation`, `app.culture`, `app.timezone` |
| GPS | `GET gps/status`, `POST gps/access`, `GET gps/last`, `GET gps/current`, `GET/POST/DELETE gps/listener` | `gps.reading` |
| Geofences | `GET geofences/status`, `POST geofences/access`, `GET/POST/DELETE geofences/regions`, `DELETE geofences/regions/{id}`, `GET geofences/regions/{id}/state` | `geofence.status` |
| Bluetooth LE | `GET ble/status`, `POST ble/access`, `POST/DELETE ble/scan`, `GET ble/peripherals[/{uuid}]`, `POST/DELETE …/connection`, `GET …/rssi`, `GET …/services`, `GET …/characteristics`, `GET/PUT …/characteristics/{c}`, `POST/DELETE …/notifications` | `ble.scan`, `ble.status`, `ble.notification`, `ble.error` |
| Wi-Fi | `GET wifi`, `POST wifi/access`, `GET wifi/networks`, `GET wifi/current`, `POST/DELETE wifi/connection`, `GET wifi/known`, `DELETE wifi/known?id=`, `GET/PUT wifi/radio`, `GET/POST/DELETE wifi/hotspot`, `GET wifi/hotspot/clients` | `wifi.changed`, `wifi.hotspot` |
| Discovery | `POST discovery/{mdns,ssdp,wsd}/search`, `POST discovery/{mdns,ssdp,wsd}/browse`, `GET discovery/mdns/resolve`, `GET discovery/wsd/resolve`, `GET discovery/ssdp/description?udn=`, `POST discovery/{mdns,ssdp,wsd}/publications`, `GET discovery/browses`, `DELETE discovery/browses/{id}`, `GET discovery/publications`, `DELETE discovery/publications/{id}` | `discovery.mdns`, `discovery.ssdp`, `discovery.wsd`, `discovery.error`, `discovery.stopped` |

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

From your own native code, call `WebAppInvoker.InvokeAsync(name, payload, typeInfo)`.

**Jobs:** the OS picks when jobs run: at best every 15 minutes on Android, less predictably on iOS.
Jobs with the same charging and network requirements run together as one native job. On iOS, add
`BGTaskSchedulerPermittedIdentifiers` (`com.shiny.job`, `com.shiny.jobnet`, `com.shiny.jobpower`,
`com.shiny.jobpowernet`) and the `processing` background mode.

**Push:** `AddPushBridge()` always adds `GET /_bridge/push` (access and token),
`POST/DELETE /_bridge/push/registration` and `GET/PUT /_bridge/push/tags`, plus the `push.token` and
`push.unregistered` events. Pushes only reach the web app when you set `DispatchToWebApp`. You still
need Shiny.Push's platform setup: APNs entitlements, and `google-services.json` on Android.

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

- `samples/Sample.Blazor`: the web app, a Blazor WebAssembly app with a page for every bridge, plus `wwwroot/background.js`.
- `samples/Sample.App`: shared MAUI setup using every bridge package. Sample.Blazor is published and zipped into it at build time (`-p:SkipWebAppBuild=true` skips that).
- Heads:
  - `samples/Sample.Maui`: Android, iOS, Mac Catalyst and Windows.
  - `samples/Sample.MacOS`: AppKit.
  - `samples/Sample.Linux`: GTK4.
- `samples/Sample.ReleaseServer`: run it, then `samples/publish-release.sh 1.1.0` publishes an update.

The sample key pair in `samples/keys` is public. It's for development only.
