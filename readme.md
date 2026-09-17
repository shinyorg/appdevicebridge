# Shiny.AppDeviceBridge

Ship a web app — Blazor WebAssembly, React, Vue, anything that builds to static files — inside a .NET MAUI
app, served from the device itself, updated from your own server, and able to call native services.

- **Served locally.** A loopback [Shiny.Net.HttpServer](https://shinylib.net/httpserver) serves the app
  straight out of its zip. Nothing is extracted, and it works offline.
- **Updated from your server.** At launch the host asks a Shiny.AppDeviceBridge.AspNetCore server whether the
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
│  AppDeviceBridgeServer ── your app's Shiny.Net.HttpServer    │
│    ├─ bridge policy   on this device · launch session        │
│    ├─ /_bridge/*      device · location · BLE · push · …     │
│    ├─ /_bridge/events Server-Sent Events                     │
│    └─ WebAppHost ◀─ ZipFileSource ◀─ baseline or download    │
│                                              ▲               │
└──────────────────────────────────────────────┼───────────────┘
                                               │ signed release
                          Shiny.AppDeviceBridge.AspNetCore server
```

## Packages

| Package | Use it in | What it does |
| --- | --- | --- |
| `Shiny.AppDeviceBridge.Maui` | the app | `UseAppDeviceBridge`: `AddShinyHttpServer` with the bridges on it, started with the app and restarted on resume |
| `Shiny.AppDeviceBridge.WebView` | the app | `UseWebAppHost`, `WebAppHostView`, `WebAppHostPage`, `AllowWebPermissions`: the web app in a WebView, over-the-air updates, the dev server proxy, the launch session and `background.js` |
| `Shiny.AppDeviceBridge.Blazor` | the Blazor WebAssembly app | `AddWebAppHostClient()`: the page's transport, typed clients for the built-in bridges (`IHostBridge`, `ISettingsBridge`, `IFilesBridge`, `ILinksBridge`), `WebAppEvents`, `WebAppNativeCalls` (typed C# handlers for jobs, GPS, geofences and push), and `WebAppBridge` for endpoints of your own |
| `Shiny.AppDeviceBridge.Client` | (dependency) | the typed-client foundation: `IBridgeTransport`, `BridgeException`, the `[BridgeClient]` attributes and the generator that implements them, and the built-in bridges' contracts |
| `Shiny.AppDeviceBridge.{Bridge}.Client` | the web app | one per bridge: its contracts and typed client — `ICalendarBridge`, `IWifiBridge`, … — registered with `Add{Bridge}BridgeClient()` |
| `@shinyorg/appdevicebridge` | a JavaScript or TypeScript web app | the same typed clients in TypeScript, generated from the same declarations (`clients/typescript`) |
| `Shiny.AppDeviceBridge` | (dependency) | the bridge server: `http.AddAppDeviceBridge()` on Shiny.Net.HttpServer's `ShinyHttpServerBuilder`, `AppDeviceBridgeOptions` (mount points, allowed hosts, the bridge policy), bridge contracts, built-in settings, files and native-call endpoints; no MAUI dependency |
| `Shiny.AppDeviceBridge.Tunnel` | the app, or a headless device | `http.AddAppDeviceBridgeTunnel()`: a public HTTPS address for the server, opened and closed while the app runs; everything through it is treated as a remote caller |
| `Shiny.AppDeviceBridge.Core` | (dependency) | protocol contracts, version ordering, release signatures |
| `Shiny.AppDeviceBridge.AspNetCore` | your server | `AddWebAppReleases`, `MapWebAppReleases`, file-system release store |
| `Shiny.AppDeviceBridge.AppSupport` | the app | `AddAppSupportBridge()` — device info, orientation, browser, maps, settings, app store, launch at login, share, haptics and vibration, connectivity, battery, screen and clipboard; and `/_bridge/sensors` — accelerometer, gyroscope, magnetometer, compass, barometer and orientation |
| `Shiny.AppDeviceBridge.Locations` | the app | `AddGpsBridge()`, `AddGeofenceBridge()`, `AddLocationBridges()`, `AddMotionActivityBridge()` |
| `Shiny.AppDeviceBridge.BluetoothLE` | the app | `AddBluetoothLEBridge()` |
| `Shiny.AppDeviceBridge.Obd` | the app | `AddObdBridge()`: OBD-II over Bluetooth LE or Wi-Fi adapters — decoded PIDs, VIN, trouble codes, live readings |
| `Shiny.AppDeviceBridge.Wifi` | the app | `AddWifiBridge(hotspot: false)`: current network and changes, scan, connect, known networks, radio, hotspot |
| `Shiny.AppDeviceBridge.Discovery` | the app | `AddDiscoveryBridge(DiscoveryProtocols.All)`: mDNS/Bonjour, SSDP/UPnP, WS-Discovery search, browse, resolve and publish |
| `Shiny.AppDeviceBridge.Jobs` | the app | `AddWebAppJob(name, configure)`: background jobs handled by the page or `background.js` |
| `Shiny.AppDeviceBridge.Push` | the app | `AddPushBridge()`: register, unregister, token, tags, and optionally push payloads for the web app |
| `Shiny.AppDeviceBridge.Notifications` | the app | `AddNotificationsBridge()`: local notifications now, scheduled, repeating or at a geofence; pending, cancel, badge, channels; taps handed to the web app |
| `Shiny.AppDeviceBridge.HttpTransfers` | the app | `AddHttpTransfersBridge()`: background uploads and downloads to and from file roots, with progress events and completion handlers |
| `Shiny.AppDeviceBridge.AppLinks` | the app | `AddAppLinksBridge(o => o.Schemes.Add("myapp"))`: deep links and universal/app links routed to the page |
| `Shiny.AppDeviceBridge.Health` | the app | `AddHealthBridge()`: HealthKit and Health Connect permissions, bucketed reads, writes and live readings |
| `Shiny.AppDeviceBridge.Speech` | the app | `AddSpeechBridge()`: on-device speech recognition, dictation as events, text-to-speech, voices |
| `Shiny.AppDeviceBridge.Contacts` | the app | `AddContactsBridge()`: access, paged search, read, photos, create, update and delete (Android, iOS) |
| `Shiny.AppDeviceBridge.Calendar` | the app | `AddCalendarBridge()`: access, calendars, events in a date range, create, update and delete |
| `Shiny.AppDeviceBridge.Camera` | the app | `AddCameraBridge()`: this device's own camera driven from a page anywhere — a live MJPEG viewfinder, photos and video filed into a file root, lens, zoom, torch and effects; `CameraBridgeView` for a camera screen of your own |
| `Shiny.AppDeviceBridge.Photos` | the app | `AddPhotosBridge()`: the system photo picker, and the photo library — pages, thumbnails and full-size exports — as files in a file root |
| `Shiny.AppDeviceBridge.Folders` | the app | `AddFoldersBridge()`: the platform's folder picker, and `FolderRoots` for folders the app adds by path — each remembered as a file root across launches |
| `Shiny.AppDeviceBridge.Desktop` | the app | `AddTrayIconBridge()`: system tray / menu bar icons, menus, badges, notifications and animation. `AddQuickEntryBridge()`: a prompt window that opens over other applications from a global hotkey. Both hand what the user does back to the web app |
| `Shiny.AppDeviceBridge.RpiCamera` | the app, or a headless Pi | `http.AddRpiCameraBridge()` on the server's builder: Raspberry Pi cameras through libcamera — snapshots, captures into a file root, sensor controls and a shared live MJPEG stream |

## The app

```csharp
builder
    .UseMauiApp<App>()
    .UseAppDeviceBridge(o => o.AppId = "field-app")
    .UseWebAppHost(o =>
    {
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
builder
    .UseAppDeviceBridge(o => o.AppId = "field-app")
    .UseWebAppHost(o => o.UseBaseline(typeof(App).Assembly, "webapp.zip"));   // version defaults to 1.0.0
```

No web app at all? `UseAppDeviceBridge` alone serves the bridges to callers on the device (any caller in a debug build),
and the server's own builder and `AuthorizeBridges` decide the rest. See [Security](#security-model).

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
and its precompressed `.br`/`.gz` files are served as they are. Only the entry document is sent with `Cache-Control: no-cache`,
so an update is never hidden behind a cached `index.html`; every other file carries no cache header from the host.
`OnPrepareResponse` runs after that for every file, so an app serving the pages over a LAN or a tunnel can cache
fingerprinted assets for a year and still leave the entry document to revalidate:

```csharp
o.OnPrepareResponse = x =>
{
    // Your rule for which names carry a content hash, such as dotnet.runtime.zbexyp8zrs.js.
    if (MyCachePolicy.IsFingerprinted(x.File.Name))
        x.HttpContext.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
};
```

### Platform setup

| Platform | Required |
| --- | --- |
| Android | Cleartext to `127.0.0.1`: a network security config (see the sample) or `usesCleartextTraffic` |
| iOS / Mac Catalyst | `NSAppTransportSecurity` → `NSAllowsLocalNetworking` |
| Mac Catalyst, sandboxed macOS | `com.apple.security.network.server` entitlement |

Plus the usage descriptions and permissions for whichever bridges you add.

### The bridge server

The bridges live on **your app's own Shiny.Net.HttpServer**. `http.AddAppDeviceBridge()` puts them on it, on the same
`ShinyHttpServerBuilder` you configure the server with — address, port, TLS, limits, authentication, endpoints of your
own. `UseAppDeviceBridge` (from `Shiny.AppDeviceBridge.Maui`) does that for a MAUI app, starts the server with the app
and restarts it on resume:

```csharp
builder.UseAppDeviceBridge(o => o.AppId = "field-app");

// The same server, configured wherever suits — before UseAppDeviceBridge or after.
builder.Services.AddShinyHttpServer(http =>
{
    http.Options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    http.AddResponseCompression();
}, autoStart: false);
```

Without MAUI — a headless device, a test — it's the builder alone:

```csharp
services.AddShinyHttpServer(http => http
    .AddAppDeviceBridge(o => o.AppId = "greenhouse")
    .AddRpiCameraBridge());
```

| Option | Default | Why |
| --- | --- | --- |
| `AppId` | required | Names the data directory, namespaces settings, and is what a release server knows the app by. |
| `AllowPortFallback` | `true` | If the port is taken when the bridge server starts the server, serve on a random one, with empty web storage for that launch. |
| `BasePath` | `/` | Serve the web app and the bridges under a path — `/kiosk/`, `/kiosk/_bridge/…`. See [Mount points](#mount-points). |
| `BridgePrefix` | `/_bridge` | Move the bridges when the web app wants that route for itself. |
| `AllowedHosts` | none | Host names accepted besides loopback names and IP addresses. See [Security](#security-model). |
| `AllowAnyCallerInDebug` | `true` | A debug build lets any caller not arriving through a tunnel reach the bridges. See [Who may call the bridges](#who-may-call-the-bridges). |
| `AuthorizeBridges` | device only | Replace who may call the bridges. |
| `MaxFileWriteBytes` | 256 MB | The largest file a bridge takes in one request. The server's request body limit is raised to at least this. |

- **The port.** `UseAppDeviceBridge` listens on loopback port `5780` unless you give the server a port of your own. The
  port is fixed on purpose: localStorage, IndexedDB and cookies belong to the origin, and the port is part of the
  origin.
- **The bridges guard only what they mount.** Your middleware, endpoints, authentication and fallback policy stay
  yours, and a request for anything the bridge server didn't mount passes straight through to them.
- **The bridge policy doesn't depend on your pipeline.** The bridges authenticate and authorize their own requests,
  so they're protected whether or not — and wherever — you call `UseAuthentication` and `UseAuthorization`. Your own
  endpoints need those calls; the bridges don't.
- **The server doesn't need a WebView.** Without `Shiny.AppDeviceBridge.WebView`, the bridges answer callers on the
  device (or anyone `AuthorizeBridges` allows), and a native call no page takes is reported as not handled.

### The web app host

`UseWebAppHost` (from `Shiny.AppDeviceBridge.WebView`) serves a web app from that server and shows it in
`WebAppHostView`:

| Option | Default | Why |
| --- | --- | --- |
| `UseBaseline(...)` | | The zip compiled into the app, served offline on first launch. |
| `UpdateServer`, `PublicKey` | none | Where releases come from, and the key they're signed with. |
| `CheckTimeout` | 5 s | After this, the installed version is shown anyway. |
| `Channel` | stable | Follow a prerelease channel such as `beta`. |
| `BlockOnRequiredUpdateFailure` | `false` | By default, a required download that fails midway is treated as offline. |
| `ApplyOptionalUpdatesImmediately` | `false` | Swap to an optional update and reload as soon as it lands. |
| `DevServer` | none | Take pages from `dotnet watch` in development. See below. |
| `ServeWebAppRemotely` | `false` | Serve the pages to other machines too, when the server is bound past loopback. |
| `ServeWebAppLocally` | `false` | Serve the pages to any caller on this device — a browser opening the loopback address — not only the WebView. |
| `OnPrepareResponse` | none | Headers for each file of the web app, after the host's own: a cache policy for the network, a security header. |
| `ContentTypeOverrides` | empty | Content types by extension (`.bcmap`, `.pfb`, …) for files the built-in map doesn't know. |
| `Variants(...)`, `SelectVariant` | none | Several builds in one package at the same URLs, chosen per request. See [Client variants](#client-variants). |
| `BackgroundScript` | `background.js` | What takes native calls with no page open. |

### Mount points

The app is served at `/` and the bridges at `/_bridge`. Both move:

```csharp
builder.UseAppDeviceBridge(o =>
{
    o.BasePath = "/kiosk";          // http://127.0.0.1:5780/kiosk/
    o.BridgePrefix = "/_native";    // http://127.0.0.1:5780/kiosk/_native/app/info
});
```

- **`_host` doesn't move.** `{base}/_host/start`, `/ping` and `/config` stay directly under `BasePath`,
  because `GET {base}/_host/config` is how a page finds out where everything else is. `BridgePrefix`
  can't be `/_host` or sit under it.
- **`<base href>` is rewritten for you.** A Blazor publish ships `<base href="/" />`, which would send
  every asset request to the origin root. The host rewrites it — in the entry document and in whatever
  the SPA fallback serves — to match `BasePath`, inserting the tag if the document has none. You don't
  need to republish with `--base-href`.
- **The page discovers the prefix, it isn't told it.** `Shiny.AppDeviceBridge.Blazor` and the injected
  `invoke/client.js` both read `{base}/_host/config` and build their URLs from it. That matters because
  the web app updates on its own schedule: a page built against one host keeps working when the next
  host moves the bridges.

```js
const { base, bridge } = await (await fetch(new URL("_host/config", document.baseURI))).json();
//    "/kiosk/"        "/kiosk/_native/"
const info = await (await fetch(bridge + "app/info")).json();
```

The typed clients, C# and TypeScript, discover the prefix the same way. Raw `fetch("/_bridge/...")` calls in
your own code are the one thing that won't follow — build them from `bridge`, or keep the defaults. `/kiosk` without
the trailing slash redirects to `/kiosk/`. Anything outside `BasePath` is left to the rest of your server — your own
endpoints, or a `404`.

### Moving the server at runtime


A LAN switch or a port setting is a stop, a change and a start, on the server itself:

```csharp
await http.StopAsync();
http.Options.Address = shareOnNetwork ? IPAddress.Any : IPAddress.Loopback;
http.Options.Port = port;
await http.StartAsync();
```

Nothing has to follow by hand. `AppDeviceBridgeServer.Origin` is read from the running server, so it is the new
port as soon as the server is up (and null while it is down). The WebView's session cookie stays valid on the new
origin, an open tunnel keeps its public address and goes on serving into the same pipeline, and a tunneled caller is
still refused the loopback names on the new port. A page loaded from the old origin has to be reloaded from the new
one, because web storage belongs to the origin.

### Client variants


One app can ship several builds of its web app — a phone client and a desktop client — in one package, at the same URLs.
The host picks one for every request, documents and assets alike, because `_framework/dotnet.js` exists in both publishes
with different bytes:

```csharp
builder.UseWebAppHost(o =>
{
    o.UseBaseline(typeof(App).Assembly, "webapp.zip");   // mobile/…, desktop/…
    o.Variants("mobile", "desktop");                      // folders in the zip; the first is the default
    o.SelectVariant = ctx =>
        ctx.Request.Cookies["view"]
        ?? (ctx.Request.Headers["Sec-CH-UA-Mobile"].ToString() == "?1" ? "mobile" : "desktop");
});
```

- **One package, one version.** Every variant is a folder at the root of the zip (or under `ArchiveBasePath`), with the
  entry document directly in it or under `wwwroot/`. A package missing any variant's entry document is refused, at
  install and for the baseline. The device downloads the whole package; a phone's browser still only loads its own build.
- **The selector has to be stable.** Build it from the `User-Agent`, the `Sec-CH-UA-Mobile` hint and a cookie of your
  own. To switch a browser to the other build, set your cookie and reload. Every response carries
  `Vary: User-Agent, Sec-CH-UA-Mobile, Cookie`, and the entry document `Accept-CH: Sec-CH-UA-Mobile`.
- **Wrong answers are not errors.** Null, an unknown name or an exception serves the default variant, and logs a warning once.
- **The WebView goes through the selector too.** `background.js` comes from the default variant, and the dev server
  serves one build.

Zipping two publishes into one release:

```bash
dotnet publish Client.Mobile -c Release -o out/mobile
dotnet publish Client.Desktop -c Release -o out/desktop
mkdir -p release/mobile release/desktop
cp -R out/mobile/wwwroot/. release/mobile/ && cp -R out/desktop/wwwroot/. release/desktop/
(cd release && zip -qr ../1.4.0.zip .)
```

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

Bridges are device access, so every bridge route requires one authorization policy,
`AppDeviceBridgePolicies.Bridges`. You decide what it allows; the default keeps them on the device.

### Who may call the bridges

| Build | Default |
| --- | --- |
| Release | A caller **on this device only**: a loopback connection, reaching the server by a loopback name, with no `Origin` or a loopback one. With `Shiny.AppDeviceBridge.WebView`, it must also be the app's own WebView, holding the launch session. |
| Debug | **Any caller not arriving through a tunnel.** A browser on your machine, `curl`, a script, another device on the network. |

> [!CAUTION]
> **Debug builds let any caller in.** In a debug build the default policy admits any caller, so you can drive the bridges from a browser or a script
> while you develop. That includes anything that can reach the port: loopback-only by default, the whole network if you
> bind the server's `Options.Address` past it. A tunnel is the exception — see [Through a tunnel](#through-a-tunnel).
> "Debug" is detected from an attached debugger or the entry assembly's
> `DebuggableAttribute`. Turn it off with `o.AllowAnyCallerInDebug = false`, or set `o.IsDebug` yourself where
> detection isn't reliable. `AuthorizeBridges` replaces the default in every build.

- **Launch session (WebView).** Binding to loopback keeps other machines out but not other apps: on Android any
  app can connect to `127.0.0.1`. Each launch generates a 256-bit token that the WebView's first navigation trades
  for an `HttpOnly`, `SameSite=Strict` cookie. The WebView host adds that cookie to the default bridge policy, so
  another app on the device gets a `401`. The web app's own files need it too (`403` without).
- **Host header.** A request to the bridges, `_host` or the web app under a name the server doesn't answer to gets `421`,
  whoever sent it. Loopback names and IP addresses are accepted, plus any name in `AllowedHosts`. That's what stops DNS
  rebinding: a hostile site pointing its own name at the device arrives under that name.
- **Origin header.** A browser call from another site running on this device carries that site's `Origin`, and
  the default policy refuses it.
- **Releases.** A release must pass five checks before it's served: signature, app id, a version
  newer than the installed one, host compatibility, then size and hash. An archive without its entry
  document is refused.

#### Deciding for yourself

`AuthorizeBridges` replaces the default policy. It's a Shiny.Net.HttpServer policy, so it can use any scheme
you add:

```csharp
builder.Services.AddShinyHttpServer(http =>
{
    http.AddAuthentication().AddApiKey(k => k.AddKey(key, "kiosk", "kiosk"));

    // This device's callers, or anyone presenting the kiosk key.
    http.AddAppDeviceBridge(o => o.AuthorizeBridges(p => p.RequireAssertion(ctx =>
        BridgeCallers.IsOnDevice(ctx.HttpContext) || ctx.User.IsInRole("kiosk"))));
}, autoStart: false);
```

- **The replacement is the whole rule.** The launch session is no longer added, and neither is the debug
  allowance. Whatever the policy allows can reach the device.
- **`BridgeCallers`** has the checks the default uses: `IsOnDevice(HttpContext)`, `IsLocalConnection(HttpContext)`,
  `IsLocal(IPAddress)`, `IsLoopbackHost(host)`. Ask `IsLocalConnection`, not `IsLocal`, whether a request came from
  this device: a tunnel delivers from loopback.
- **The WebView's session is a scheme.** It authenticates as `WebAppSessionAuthenticationHandler.SchemeName`
  with the `appdevicebridge:session` claim, so `p.RequireClaim(WebAppSessionAuthenticationHandler.SessionClaim)`
  keeps the page in a policy of your own.

### Serving the network

Binding the server past loopback doesn't open the bridges. The default policy still refuses anyone off the device in a
release build.

```csharp
builder.Services.AddShinyHttpServer(http =>
{
    http.Options.Address = IPAddress.Any;                              // reachable from the network
    http.AddAppDeviceBridge(o => o.AllowedHosts.Add("kiosk.local"));   // an mDNS name you control, besides IP addresses
}, autoStart: false);

builder.UseWebAppHost(o => o.ServeWebAppRemotely = true);   // optional: the app's own pages too
```

A browser on the device itself — the same machine opening `http://127.0.0.1:5780/` — has no launch session, so in a
release build it gets `403` for the pages too. `ServeWebAppLocally = true` serves it the pages; the bridges still
follow the bridge policy, and a tunneled caller never counts as local.

- **The session never leaves the device.** `/_host/start` answers `403` over the network, and the session
  cookie authenticates nothing when replayed from another machine.
- **The dev server is never relayed.** Remote callers get the installed build, never the proxy to
  `dotnet watch`.
- **Mind the body limit.** `MaxFileWriteBytes` (256 MB) raises the server's request body limit for every route, since the
  server checks it before routing. Once the server is reachable from the network, that includes your own endpoints and
  anonymous callers. Lower it when the bridges don't need large files, and check sizes in your own upload endpoints.
- **Open a bridge remotely with a policy.** Use `AuthorizeBridges` with a credential, as above. A route-level
  allowlist is one assertion away: `ctx.HttpContext.Request.Path.StartsWithSegments("/_bridge/files")`.

### Through a tunnel

`Shiny.AppDeviceBridge.Tunnel` gives the server a public HTTPS address — an SSH reverse forward to a public host such as
Pinggy, in managed code, so it works on phones as well as desktops. The app turns it on and off while it runs; there
are no routes for it, so where that switch lives, and who may flip it, is the app's decision.

```csharp
builder.Services.AddShinyHttpServer(http => http.AddAppDeviceBridgeTunnel(), autoStart: false);

public sealed class Sharing(AppDeviceBridgeTunnel tunnel)
{
    public Task<Uri?> OnAsync(string? pinggyToken) => tunnel.StartAsync(pinggyToken);   // null, with LastError, if it failed
    public Task OffAsync() => tunnel.StopAsync();
}
```

- **A tunnel caller is a remote caller.** A tunnel hands the server its requests from the local end of the
  forward, so every one arrives from `127.0.0.1` — with headers written by someone on the internet. It is never
  `IsOnDevice`, never holds the WebView's session, isn't let in by `AllowAnyCallerInDebug`, and gets the web app's
  pages only with `ServeWebAppRemotely` (never with `ServeWebAppLocally`). Your own endpoints decide for themselves, as for any remote caller.
- **Only the tunnel's own name.** A tunneled request must carry the tunnel's current public host (or a name in
  `AllowedHosts`, for a custom domain in front of it). One claiming `127.0.0.1` or `localhost` gets `421`.
- **Bind to the address.** `PublicUrl` and `State` raise `PropertyChanged`, on a background thread. A free tunnel
  reconnects at a new address, and the old host stops being accepted the moment it goes.
- **It doesn't start the listener.** Tunneled requests go straight into the server's pipeline, so a server stopped
  because sharing on the local network is off can still be reached through a tunnel that's on.
- **Your own tunnel.** Register an `IAppDeviceBridgeTunnel` whose `PublicUrl` is the address, and the server accepts
  that host the same way.

### Your own endpoints

Your API sits beside the web app and the bridges on the same server, configured the Shiny.Net.HttpServer way —
middleware, raw routes, source-generated `[Route]` classes, authentication:

```csharp
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;     // AllowAnonymous, RequireAuthorization, AddApiKey

builder.Services.AddShinyHttpServer(http =>
{
    http.AddAuthentication().AddApiKey(k => k.AddKey(key, "kiosk", "admin"));
    http.AddAuthorization(a =>
    {
        a.SetFallbackPolicy(p => p.RequireAuthenticatedUser());
        a.AddPolicy("admin", p => p.RequireRole("admin"));
    });

    http.Configure(server =>
    {
        server.UseAuthentication();
        server.UseAuthorization();

        server.MapOrderEndpoints();                                         // [Route("/api/orders")]
        server.MapGet("/api/health", ctx => …).AllowAnonymous();
        server.MapGet("/api/admin", ctx => …).RequireAuthorization("admin");
        server.MapGet("/api/draft", ctx => …).RequireAuthorization(WebAppPolicies.Session);
    });
}, autoStart: false);
```

- **The WebView is one more scheme.** Its session authenticates like any other, so the page calls your endpoints with
  no extra setup once your pipeline has `UseAuthentication`. `WebAppPolicies.Session` accepts the WebView and nothing
  else, however valid another credential is.
- **Bridges keep their own policy.** Your endpoints' fallback and policies don't reach them, so a key that opens
  `/api/admin` opens nothing on the device unless `AuthorizeBridges` says so. An endpoint you map under the bridge
  prefix is guarded as a bridge.
- **Where you map it is where it's served.** `BasePath` moves the web app and the bridges, not your endpoints.
- **Every `AddAuthorization` call applies**, yours and the bridges' alike (Shiny.Net.HttpServer 1.2).

With the WebView host, a path under `BasePath` that matches none of your endpoints is the web app's, and without the
WebView's session that's a `403`, so a caller outside the page can't probe which routes exist.

## Bridges

| Bridge | Routes | Events |
| --- | --- | --- |
| host (built in) | `GET /_bridge/host`, `POST /_bridge/host/apply-update` | |
| settings (built in) | `GET/DELETE settings/{local\|secure}`, `GET/PUT/DELETE settings/{scope}/{key}` | |
| files (built in) | `GET files`, `GET files/{root}/list`, `GET files/{root}/info`, `GET/PUT files/{root}/content`, `POST files/{root}/append`, `POST files/{root}/directory`, `DELETE files/{root}/entry`, `POST files/{root}/move`, `POST files/{root}/copy`, with paths in `?path=` | |
| AppSupport | `GET app/info`, `POST/DELETE app/orientation`, `POST app/browser`, `POST app/map`, `POST app/settings`, `GET app/store`, `POST app/store/open`, `POST app/store/review`, `POST app/share`, `POST app/haptics`, `POST/DELETE app/vibrate`, `GET app/connectivity`, `GET app/battery`, `GET app/screen`, `PUT/DELETE app/screen/keep-awake`, `GET/PUT/DELETE app/clipboard`, `GET app/startup`, `POST/DELETE app/startup/registration`, `POST app/startup/settings` | `app.orientation`, `app.culture`, `app.timezone`, `app.connectivity`, `app.battery`, `app.energysaver` |
| Sensors | `GET sensors`, `POST/DELETE sensors/{sensor}`, `DELETE sensors` | `sensors.accelerometer`, `sensors.gyroscope`, `sensors.magnetometer`, `sensors.compass`, `sensors.barometer`, `sensors.orientation`, `sensors.shake` |
| GPS | `GET gps/status`, `POST gps/access`, `GET gps/last`, `GET gps/current`, `GET/POST/DELETE gps/listener` | `gps.reading` |
| Geofences | `GET geofences/status`, `POST geofences/access`, `GET/POST/DELETE geofences/regions`, `DELETE geofences/regions/{id}`, `GET geofences/regions/{id}/state` | `geofence.status` |
| Motion activity | `GET motion/status`, `POST motion/access`, `GET motion/current`, `GET/POST/DELETE motion/listener` | `motion.activity` |
| Bluetooth LE | `GET ble/status`, `POST ble/access`, `POST/DELETE ble/scan`, `GET ble/peripherals[/{uuid}]`, `POST/DELETE …/connection`, `GET …/rssi`, `GET …/services`, `GET …/characteristics`, `GET/PUT …/characteristics/{c}`, `POST/DELETE …/notifications` | `ble.scan`, `ble.status`, `ble.notification`, `ble.error` |
| OBD-II | `GET obd/status`, `GET obd/commands`, `POST obd/scan`, `GET obd/adapters`, `POST/DELETE obd/connection`, `POST obd/command`, `POST obd/raw`, `GET obd/vin`, `GET/DELETE obd/dtc`, `POST/DELETE obd/monitor` | `obd.reading`, `obd.disconnected` |
| Wi-Fi | `GET wifi`, `POST wifi/access`, `GET wifi/networks`, `GET wifi/current`, `POST/DELETE wifi/connection`, `GET wifi/known`, `DELETE wifi/known?id=`, `GET/PUT wifi/radio`, `GET/POST/DELETE wifi/hotspot`, `GET wifi/hotspot/clients` | `wifi.changed`, `wifi.hotspot` |
| Discovery | `POST discovery/{mdns,ssdp,wsd}/search`, `POST discovery/{mdns,ssdp,wsd}/browse`, `GET discovery/mdns/resolve`, `GET discovery/wsd/resolve`, `GET discovery/ssdp/description?udn=`, `POST discovery/{mdns,ssdp,wsd}/publications`, `GET discovery/browses`, `DELETE discovery/browses/{id}`, `GET discovery/publications`, `DELETE discovery/publications/{id}` | `discovery.mdns`, `discovery.ssdp`, `discovery.wsd`, `discovery.error`, `discovery.stopped` |
| Notifications | `GET notifications`, `POST notifications/access`, `POST notifications/send`, `GET notifications/pending`, `DELETE notifications[?scope=]`, `DELETE notifications/{id}`, `GET/PUT notifications/badge`, `GET/POST notifications/channels`, `DELETE notifications/channels/{id}` | `notification.entry`, `notification.received` |
| HTTP transfers | `GET/POST/DELETE transfers`, `GET/DELETE transfers/{id}`, `POST transfers/{id}/pause`, `POST transfers/{id}/resume` | `transfer.progress`, `transfer.completed`, `transfer.failed`, `transfer.cancelled` |
| App links | `GET/DELETE links/pending` | `app.link` |
| Health | `GET health`, `POST health/access`, `GET/POST health/samples/{type}`, `POST/DELETE health/listeners/{type}` | `health.reading`, `health.stopped` |
| Speech | `GET speech/status`, `POST speech/access`, `POST speech/recognize`, `GET/POST/DELETE speech/listener`, `POST/DELETE speech/speak`, `GET speech/voices?culture=`, `GET speech/cultures` | `speech.partial`, `speech.result`, `speech.keyword`, `speech.ended`, `speech.spoken`, `speech.error` |
| Contacts | `GET contacts`, `POST contacts/access`, `GET/POST contacts/items`, `GET/PUT/DELETE contacts/items/{id}`, `GET contacts/items/{id}/photo` | |
| Calendar | `GET calendar`, `POST calendar/access`, `GET calendar/calendars`, `GET/POST calendar/events`, `GET/PUT/DELETE calendar/events/{id}` | |
| Photos | `GET photos`, `POST photos/access`, `POST photos/pick`, `GET photos/library`, `GET photos/library/{id}/thumbnail`, `POST photos/library/{id}/export` | |
| Folders | `GET folders`, `POST folders/pick`, `DELETE folders/{root}` | |
| Tray icon | `GET/POST/DELETE tray`, `GET/PUT/DELETE tray/{id}`, `PUT/DELETE tray/{id}/menu`, `POST tray/{id}/menu/show`, `POST tray/{id}/notification`, `PUT/DELETE tray/{id}/animation` | `tray.click`, `tray.menu` |
| Quick entry | `GET quickentry`, `PUT quickentry/options`, `POST quickentry/{show,hide,toggle}`, `GET/PUT quickentry/prompt`, `POST quickentry/prompt/reset`, `POST quickentry/glow/{show,hide,pulse}` | `quickentry.submitted`, `quickentry.suggestion`, `quickentry.cancelled`, `quickentry.microphone`, `quickentry.opened`, `quickentry.closed` |
| Device camera | `GET camera`, `POST camera/access`, `POST camera/open`, `POST camera/close`, `POST camera/photo`, `POST/DELETE camera/recording`, `PUT camera/settings`, `GET camera/preview` (MJPEG) | `camera.status` |
| Pi camera | `GET rpicamera`, `GET rpicamera/snapshot`, `POST rpicamera/capture`, `GET rpicamera/stream` (MJPEG), `GET/PUT rpicamera/controls`, `DELETE rpicamera/streams` | |

The routes are the wire protocol. A page doesn't build them by hand: every bridge has a typed client, in C# for
Blazor and in TypeScript for everything else, generated from one declaration so the two can't drift — see
[Typed clients](#typed-clients).

```csharp
@inject IAppBridge App
@inject IGpsBridge Gps

var info = await App.GetInfoAsync();
await using var readings = await Gps.OnReadingAsync(reading => { position = reading; return Task.CompletedTask; });
```

```ts
import { AppBridge, GpsBridge } from "@shinyorg/appdevicebridge";

const info = await new AppBridge().getInfo();
const stop = new GpsBridge().onReading(reading => console.log(reading.latitude));
```

Errors return `{ "code": "...", "message": "..." }`. The clients throw `BridgeException` (C#) or `BridgeError`
(TypeScript) carrying the status and `code`, so pages can switch on either.

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

```ts
await new AppBridge().share({ files: [{ root: "data", path: "photos/cat.jpg" }] });
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
  `coolantTemperature`…) and answers the decoded value with its unit. `POST obd/raw` sends a read-only request:
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

```ts
const links = new LinksBridge();

async function openPendingLink() {
    const link = await links.consume();
    if (link) router.push(link.route);
}

openPendingLink();
links.onLink(openPendingLink);
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

```ts
const health = new HealthBridge();
await health.requestAccess({ permissions: [{ type: "StepCount", access: "Read" }] });

const today = new Date(); today.setHours(0, 0, 0, 0);
const steps = await health.getSamples("StepCount", today, new Date(), { interval: "Hours" });
```

**Speech:** built on Shiny.Speech, which is still a prerelease package.
- **Recognizing once:** `recognize` listens until a pause and returns `{ "text": … }`. It gives up after
  `timeoutMs`: 15 s by default, 60 s at most. `text` is `null` if nothing was heard.
- **Dictation:** `POST listener` keeps the microphone open and streams `speech.partial` and
  `speech.result` events until you delete it. It also stops when the page's last event stream
  closes, so a page that goes away can't leave the microphone on. `speech.ended` says why it
  stopped: `Stopped`, `PageClosed` or `Error`.
- **One microphone:** a recognition and a listener can't run at once. The second gets `409`
  `microphone_busy`.
- **Speaking:** `speak` waits until the text has been spoken, unless you pass `"wait": false`, which
  returns `204` straight away and raises `speech.spoken` when done. A new utterance interrupts the current one.
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

**Photos:**
- **Picker:** `POST photos/pick` shows the system photo picker — no permission needed — and copies what the user
  chose into a file root (`cache` by default) under `photos/`. The answer lists each as a `{ root, path }` the page
  reads through the files bridge. An empty list means the user cancelled. Every head has a picker.
- **Library:** browse every photo on the device, newest first: `GET photos/library?offset=&limit=` (200 at most),
  `GET photos/library/{id}/thumbnail?size=` for a JPEG that fits a square (32–1024 px), and
  `POST photos/library/{id}/export` to copy the original into a file root. It needs access —
  `POST photos/access` — and answers `403` without it.
- **Platforms:** PhotoKit on iOS, Mac Catalyst and macOS; MediaStore on Android; the user's Pictures folder on
  Windows. Linux has no photo library, so the library endpoints return `501` there.
- **Platform setup:** `NSPhotoLibraryUsageDescription` on Apple platforms, plus the
  `com.apple.security.personal-information.photos-library` entitlement where the app is sandboxed.
  `READ_MEDIA_IMAGES` on Android 13 and later, `READ_EXTERNAL_STORAGE` before.

**Folders:**
- **Picking:** `POST folders/pick` with `{ "root": "documents" }` shows the platform's folder picker. The folder
  becomes a file root under that name — `/_bridge/files/documents/…` — and answers `204` if the user cancels.
  Picking again under the same name replaces the folder. The app's own roots can't be replaced.
- **Remembered:** picked folders come back as roots every time the app starts, until
  `DELETE folders/{root}` forgets one. `GET folders` lists them, with `available: false` for one that was
  moved, deleted or had its access revoked since.
- **Platforms:** Apple platforms keep a security-scoped bookmark; Android keeps a persisted Storage Access
  Framework grant; Windows and Linux (GTK's file dialog) keep the path.
- **Android folders aren't paths.** The files bridge reads and writes them through the Storage Access Framework,
  but bridges that hand the OS a file path — sharing, transfers, notification images — refuse them.

**Tray icon** (`Shiny.AppDeviceBridge.Desktop`, `AddTrayIconBridge()`): the system tray on Windows, the menu bar on
macOS, the status notifier area on Linux — desktop only, `501` elsewhere.
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

```ts
await new TrayBridge().put("main", {
    tooltip: "Field App",
    templateImage: true,
    icon: { root: "data", path: "icons/tray.png" },
    menu: { items: [
        { id: "open", label: "Open" },
        { type: "Separator" },
        { id: "sync", type: "Check", label: "Sync", checked: true }
    ] }
});
```

**Quick entry** (`Shiny.AppDeviceBridge.Desktop`, `AddQuickEntryBridge()`): Shiny's quick entry prompt as a borderless,
always-on-top window that opens over other applications, in the style of Spotlight or a desktop assistant. The page
configures it, hears what's submitted and writes the answer back. Desktop only: macOS (AppKit and Catalyst), Windows and
Linux; `501` elsewhere.

```csharp
builder
    .AddQuickEntryBridge(
        o => o.HotKey = "Ctrl+Alt+Space",                         // toggles the window from anywhere
        quickEntry => quickEntry.ScreenGlow = ScreenGlowTrigger.WhileBusy
    );
```

- **Opening it:** the global hotkey, `POST quickentry/show` or `toggle`, or a tray click that calls one. `PUT
  quickentry/options` changes the hotkey (`""` removes it), size, placement and dismissal while the app runs;
  `hotKeyRegistered: false` in the status means another application already owns the combination.
- **The prompt:** `PUT quickentry/prompt` sets the placeholder, suggestions (each with an optional `value` that comes
  back when it's chosen), `isBusy` with `busyText`, and `response`, the text shown under the entry. Only what you send
  changes; `""` clears the response and `[]` the suggestions. What you set survives a window that rebuilds its prompt.
- **Answering:** `quickentry.submitted` goes out as an event *and* as a call the page handles when it's open and
  `background.js` handles when it isn't, which is the usual case for a window summoned over other applications. Set
  `isBusy`, do the work, then set `response`.
- **The glow:** `glow/show`, `glow/hide` and `glow/pulse` drive the colour wash around the screen's edge; the
  `glow` option lights it while the window is open or while the prompt is busy.
- **Custom content:** if the app replaces the prompt through `QuickEntryOptions.ContentFactory`, the prompt routes
  answer `409` and the window routes still work.

```ts
const quickEntry = new QuickEntryBridge();

await quickEntry.setPrompt({ placeholder: "Ask Field App…", suggestions: [{ text: "Sync now", value: "sync" }] });
quickEntry.onSubmitted(async ({ text, suggestion }) => {
    await quickEntry.setPrompt({ isBusy: true, busyText: "Thinking…" });
    await quickEntry.setPrompt({ isBusy: false, response: await answer(text, suggestion?.value) });
});
```

**Device camera** (`Shiny.AppDeviceBridge.Camera`, `AddCameraBridge()`): this device's own camera, driven from a page anywhere — the
reason being a phone on a mount, framed and fired from a laptop across the room. Not the camera of the machine showing the
page: that's `getUserMedia` (see [Camera, microphone and location in the page](#camera-microphone-and-location-in-the-page)).
Android, iOS, Mac Catalyst, macOS and Windows, through Shiny's `CameraView`; Linux has no camera and answers `501`.

```csharp
builder.AddCameraBridge(o =>
{
    o.Root = "data";                 // captures are filed here…
    o.Folder = "camera";             // …as camera/IMG_20260916-201502.jpg, camera/VID_….mov
});
```

- **A camera screen, not a background service.** The camera runs on a view on screen — iOS won't run one in the
  background at all — so `GET camera` says whether one is `live`, and until it is every command answers
  `409 camera_not_open`. That's a normal state: show it as "open the camera on the device", not as an error.
- **Opening it from afar.** `POST camera/open` (`202`) asks the device to show its camera. Unless something handles
  `CameraBridgeSession.OpenRequested`, the bridge pushes its own `CameraBridgePage` over the web app — shutter, photo and
  video, flip, torch, close. `POST camera/close` closes it. For a camera screen of your own, put a `CameraBridgeView` on
  it, set `PresentWhenOpened = false` and navigate there from `OpenRequested`; the buttons on that screen and the
  requests from a page run the same methods.
- **Driving it.** `POST camera/photo` and `POST`/`DELETE camera/recording` capture at full resolution and file into the
  file root, answering `{ file: { root, path }, kind, size, … }` for the files bridge. `PUT camera/settings` is a
  patch — `videoMode`, `facing`, `cameraId`, `active`, `filter`, `torchOn`, `zoom` — so two viewers don't undo each
  other; changing the camera mid-recording is a `422 camera_state`. Phones flip front and back; desktops list
  `cameras` and set `choosesCamera`, since none of theirs is front or back.
- **The viewfinder.** `GET camera/preview` is `multipart/x-mixed-replace` MJPEG an `<img>` plays: small (720 px) and 12
  frames a second by default (`PreviewMaxEdge`, `PreviewFramesPerSecond`, `PreviewQuality`), because latency matters more
  than smoothness when framing. The device encodes only while someone watches, and a slow viewer gets the newest frame.
- **Everyone follows the device.** `camera.status` carries the whole status whenever anything changes — somebody picks
  the phone up and switches to video, and every page's controls follow.
- **Filing elsewhere.** Register an `ICameraCaptureStore` before `AddCameraBridge` to file captures through your own
  storage.
- **Platform setup:** `NSCameraUsageDescription` and `NSMicrophoneUsageDescription` on Apple platforms (plus the camera
  and audio-input entitlements where sandboxed), `CAMERA` and `RECORD_AUDIO` on Android, the `webcam` and `microphone`
  capabilities on Windows. `POST camera/access` prompts where the platform will.

```html
<img src="_bridge/camera/preview?t=1726517702" />   <!-- a new query each time it's shown -->
```

```ts
const camera = new CameraBridge();
camera.onStatus(status => render(status));
await camera.open();
const photo = await camera.takePhoto();              // { file: { root: "data", path: "camera/IMG_….jpg" }, … }
await camera.updateSettings({ videoMode: true, zoom: 2 });
```

**Sensors** (`Shiny.AppDeviceBridge.AppSupport`, added by `AddAppSupportBridge()`): the accelerometer (g), gyroscope
(rad/s), magnetometer (µT), compass (degrees from magnetic north), barometer (hPa) and orientation (a quaternion), over
.NET MAUI Essentials.
- **Start, then listen.** `POST sensors/compass` with `{ "speed": "UI", "minIntervalMs": 16 }` starts the sensor; readings
  arrive only as events. Posting again with another speed restarts it; another interval changes only what gets through.
- **Throttled.** Readings closer together than `minIntervalMs` (16 by default, at least 5) are dropped before they
  reach the page, so `Fastest` can't flood the event stream. `sensors.shake` is never dropped, and fires only while the
  accelerometer runs.
- **Stopped for you.** Every sensor stops when the page's last event stream closes, so a page that navigates away or
  closes doesn't leave one draining the battery. `DELETE sensors` stops them all.
- **Platforms.** Whatever Essentials offers on Android, iOS, Mac Catalyst and Windows, and only the sensors the device
  has. `GET sensors` says which; starting one that isn't there answers `501`. The macOS (AppKit) and Linux (GTK4) heads
  have none.

```ts
const sensors = new SensorsBridge();
sensors.onCompass(({ heading }) => needle.style.rotate = `${-heading}deg`);
await sensors.start("Compass", { speed: "UI", minIntervalMs: 50 });
```

**Pi camera** (`Shiny.AppDeviceBridge.RpiCamera`, `http.AddRpiCameraBridge()`): Raspberry Pi cameras through libcamera, with no
`rpicam-apps` process to launch. Linux with the native shim only; everywhere else `GET rpicamera` says why there is no
camera and the rest answer `501`.

```csharp
// The server's own builder: a camera appliance is usually a headless Pi, running the bridge server with no MAUI.
services.AddShinyHttpServer(http => http
    .AddAppDeviceBridge(o => o.AppId = "greenhouse")
    .AddRpiCameraBridge(o =>
    {
        o.StreamWidth = 1280;
        o.StreamHeight = 720;
        o.Camera.NativeLibraryPath = "/opt/greenhouse/native";
    }));

await services.BuildServiceProvider().GetRequiredService<AppDeviceBridgeServer>().StartAsync();
```

- **The native shim.** `libshinyrpi_camera.so` links against libcamera's C++ ABI, so it's built against the libcamera the
  device runs: on the Pi with `native/shinyrpi-camera/build.sh`, or with the arm64 `Dockerfile` beside it. A missing or
  mismatched shim doesn't stop the app; the status carries the loader's message.
- **Snapshots and captures.** `GET rpicamera/snapshot` answers `image/jpeg`; `POST rpicamera/capture` writes the JPEG into
  a file root, where a transfer, a share or the files bridge picks it up. Frames are encoded on the device, from the
  sensor's NV12 or YUV.
- **One stream per camera.** `GET rpicamera/stream` is `multipart/x-mixed-replace` MJPEG that an `<img>` plays directly.
  The camera is exclusive, so every viewer shares one session: the first viewer decides size, quality and frame rate
  (capped by `MaxFps`), a slow viewer skips to the newest frame, and a snapshot during a stream comes from it. The
  session closes when the last viewer leaves, `MaxStreamDuration` ends a viewer that never does, and
  `DELETE rpicamera/streams` ends them all.
- **Controls.** `GET rpicamera/controls` lists what the attached sensor accepts; `PUT` applies values to a running stream
  and to every session opened after. A control the sensor lacks is a `400 unsupported_control`: a Camera Module 3 has
  autofocus, a v2 doesn't.
- **Busy.** Another process holding the camera (`rpicam-still`, say) is a `409 camera_unavailable`.

```html
<img src="_bridge/rpicamera/stream?fps=10" />
```

```ts
const camera = new RpiCameraBridge();
const photo = await camera.capture({ root: "data", path: "photos/now.jpg" });
await camera.setControls({ values: [{ control: "Brightness", value: 0.2 }] });
```

### Typed clients

Every bridge ships a `.Client` package: its request and response contracts, and an interface such as
`ICalendarBridge` whose implementation is generated at build time. The native bridge serializes the same
contracts, so the page and the device agree on every shape by construction.

```csharp
// Program.cs
builder.Services
    .AddWebAppHostClient()          // transport + host, settings, files, links
    .AddCalendarBridgeClient()
    .AddWifiBridgeClient();

// a page
@inject ICalendarBridge Calendar

var created = await Calendar.CreateEventAsync(new NewCalendarEvent
{
    Title = "Standup",
    Start = DateTimeOffset.Now.AddHours(1),
    End = DateTimeOffset.Now.AddHours(1.5)
});
```

- **Errors** throw `BridgeException` with `StatusCode`, the bridge's `Code` and `IsNotSupported` for `501`.
- **Events** are methods too: `await using var sub = await Wifi.OnChangedAsync(e => …)`.
- **Files and binaries:** a method returning `Task<Stream>` or `Task<byte[]>` reads the body raw; a
  `[BridgeBody("text/plain")]` parameter sends one.
- **TypeScript:** `clients/typescript` holds the same clients, generated from the same assemblies by
  `tools/Shiny.AppDeviceBridge.TypeScript` — `new CalendarBridge().createEvent({ title, start, end })`. Required
  members are required, optional parameters go in an options object with an `AbortSignal`, and events return an
  unsubscribe function. A test fails when the committed TypeScript falls behind the C# declarations.
  Install it with `npm install @shinyorg/appdevicebridge`. It ships compiled ES modules with type declarations, at the
  same version as the NuGet packages.
- **Your own endpoints** can still be called untyped through `WebAppBridge.GetAsync<T>(path, typeInfo)` and
  `SendAsync<TBody, TResult>(…)`, or given a `[BridgeClient]` interface of their own.

### Settings and files

Both are built into the host and on by default (`EnableSettings`, `EnableFiles`).

**Settings** go through Shiny.Extensions.Stores. `local` is the platform's settings store. `secure` is
its secure store: Keychain, Android KeyStore or DPAPI. On Linux it's a plain file, and the listing's
`encrypted` flag says so. Values can be any JSON and come back exactly as written. Keys are namespaced
by app id, so the page never sees or clears the native app's own settings.

```csharp
await Settings.SetAsync(SettingsScope.Secure, "token", token, MyJson.Default.String);
var saved = await Settings.GetAsync(SettingsScope.Secure, "token", MyJson.Default.String);
```

**Files** are confined to named roots: `data` (persistent) and `cache` (the OS may clear it), unless
you set `FileRoots` — plus any folder picked through the folders bridge or added by the app. Paths are relative and use
forward slashes. A path is refused if it contains `..`, `\`, `:`, a control character or anything a file name
can't hold on some platform (`< > " | ? *`), or passes through a link that leads out of the root. Writes are atomic, parent
directories are created as needed, and `MaxFileWriteBytes` (256 MB) caps a single file — and sets the floor for the
server's request body limit, whichever bridges are registered, so your own upload endpoints get it too.

```ts
const files = new FilesBridge();
await files.write("data", "photos/cat.jpg", blob);
const entries = await files.list("data", { path: "photos" });
await files.move("data", { from: "photos/cat.jpg", to: "photos/tabby.jpg" });
```

**Folders the app maps while it runs** — a folder chosen in its own dialog, a share an administrator published — go
through `FolderRoots` (`Shiny.AppDeviceBridge.Folders`). Each becomes a root the page uses by name, and is restored the
next time the app runs, on every platform:

```csharp
public sealed class Shares(FolderRoots folders)
{
    public PickedFolder Publish(string name, string path) => folders.Add(name, path, displayName: name);   // absolute path, existing directory
    public bool Unpublish(string name) => folders.Forget(name);
}
```

The page never names a path — that's the app's decision — but it hears every change as the `files.roots` event
(`IFilesBridge.OnRootsChangedAsync`, `FilesBridge.onRootsChanged`). A folder that no longer opens stays in
`FolderRoots.All` with `Available` false. On Apple platforms a folder the app currently has security-scoped access to is
kept as a bookmark. Failures are `WebAppFileException`, with the code the folders bridge answers with.

Lower down, `WebAppFileRoots` holds every root and raises `Changed`. `WebAppFileRoot` is a directory on disk (an absolute
path); a store that isn't one — an Android Storage Access Framework tree — implements `WebAppFileStore`, and
`GetLocalPath` returns null so path-only bridges refuse its files.

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

A bridge with no MAUI dependency hangs off the server's builder instead: `http.AddWebAppBridge<ClipboardBridge>()`.

Use source-generated JSON contexts (`JsonTypeInfo`). The packages are trim and AOT clean, and bridges
should stay that way.

To give it a typed client, declare the API once in a plain `net10.0` project that references
`Shiny.AppDeviceBridge.Client`. The generator implements the interface, and the same contracts serialize on
both sides:

```csharp
[BridgeClient("clipboard", typeof(ClipboardJson))]
public interface IClipboardBridge
{
    [BridgeGet] Task<Clip> GetAsync(CancellationToken cancellationToken = default);
    [BridgePut] Task SetAsync(Clip clip, CancellationToken cancellationToken = default);
    [BridgeEvent("clipboard.changed")] Task<IAsyncDisposable> OnChangedAsync(Func<Clip, Task> handler);
}

public sealed record Clip(string? Text);

[JsonSerializable(typeof(Clip))]
public partial class ClipboardJson : JsonSerializerContext;

// the page
builder.Services.AddWebAppHostClient().AddClipboardBridgeClient();
```

Route tokens (`[BridgeGet("items/{id}")]`) bind parameters by name, a complex parameter on a POST or PUT is the
JSON body, and everything else is the query string. A declaration the generator can't turn into a request —
a complex type on a GET, a route token with no parameter — is build error `ADB001`.

## Calling the web app from native code

Background jobs, GPS readings, geofence transitions and pushes can call into the web app, whether or
not a page is open.

- **Page open and listening:** the call goes to the page. The page has 2 seconds to accept it, then
  posts its result.
- **No page, or the page doesn't accept in time:** the call runs in `background.js` from the web app's
  zip, inside an embedded JavaScript engine ([Jint](https://github.com/sebastienros/jint)).
- **Accepted by the page:** the page owns the call. A call is never run in both places.

```csharp
// in a Blazor page
await nativeCalls.HandleAsync("job:sync", AppDeviceBridgeJsonContext.Default.JobRun, MyJson.Default.SyncResult, async job =>
{
    await SyncAsync();
    return new SyncResult(RanIn: "page");
});
```

```js
// in any other page
import { on } from "/_bridge/invoke/client.js";
on("job:sync", async ({ name }) => { /* ... */ });
```

```js
// background.js: a classic script at the root of the zip
appdevicebridge.on("job:sync", async ({ name }) => {
    const token = await (await fetch("/_bridge/settings/secure/token")).json();
    const data = await fetch("https://api.example.com/sync", { headers: { Authorization: `Bearer ${token}` } });
    await fetch("/_bridge/files/data/content?path=sync.json", { method: "PUT", body: await data.text() });
});
```

`background.js` gets `appdevicebridge.on`, `console` and `fetch` with string bodies. Relative URLs go to the
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
  with a `appdevicebridge.source` data key, which the page never sees and can't set.
- **Taps:** reach `notification.entry` on Android, iOS and Mac Catalyst. Windows and Linux have no tap callback,
  and on the macOS (AppKit) head nothing runs Shiny's startup tasks, so neither handler fires there yet.
- **Linux:** scheduled notifications only fire while the app runs, and repeating ones don't fire at all in
  Shiny.Notifications.Linux 5.6.3.
- **Platform setup:** on Android, `POST_NOTIFICATIONS`, `SCHEDULE_EXACT_ALARM` for on-time schedules, and a
  drawable named `notification` for the small icon (without it, `send` returns `400`). Geofence triggers need the
  location usage descriptions.

**Blazor WebAssembly:** handlers registered from the page can be C#, through `WebAppNativeCalls` in Shiny.AppDeviceBridge.Blazor.
Payloads are the bridges' own contracts — `GpsReading` for `gps`, `PushPayload` for `push.received`,
`TransferInfo` for `transfer.completed` — read through their JSON contexts. The
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
