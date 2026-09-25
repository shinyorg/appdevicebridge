# Plan — `Shiny.AppDeviceBridge.Car` (CarPlay and Android Auto)

A bridge that lets the hosted web app drive the car screen. Neither CarPlay nor Android Auto runs a WebView for app
UI, so the page doesn't render in the car: it describes **templates** as JSON, native code draws them with
CarPlay's `CPTemplate`s or the AndroidX Car App Library, and taps come back as events. Navigation apps
additionally get a **map surface**, where a second WebView renders the page-side MapLibre map (`<BridgeMap>`) from
the same loopback server.

Status: **plan only**, nothing built yet. Phase 0 (the review spike) gates everything after it.

## Why this shape

- **Templates are the only UI either platform allows** outside a navigation app's map area. Apple and Google draw
  them, so the car always looks native and passes the driver-distraction rules. The page only supplies content
  and flow.
- **The logic still updates over the air.** Screens, lists and what a tap does live in the web bundle. Only the
  set of template types is fixed in the binary; a new template kind needs a store release, so the page checks
  `GetStatusAsync().Templates` before using one.
- **The map is the one place a WebView can go.** CarPlay gives navigation apps a `CPWindow` (a real `UIWindow`)
  behind `CPMapTemplate`; Android Auto gives them a `Surface`. Putting a WebView there reuses MapLibre GL JS,
  PMTiles region packs and the traffic layers instead of building a native map. It is display-only: no controls
  in the map, input comes from the platform's pan/zoom callbacks.
- **Directions already exist.** `DirectionsRoute`/`RouteManeuver` from `Shiny.AppDeviceBridge.Maps.Client` map
  almost one-to-one onto `CPManeuver`/`CPTravelEstimates` and Car App Library `Step`/`Maneuver`/`TravelEstimate`.

## Store policy (read before building)

- **Downloaded code:** the Apple Developer Program License Agreement (3.3.1(B)) allows interpreted code run by
  WebKit — which covers our JS and Blazor WASM — as long as it doesn't change the app's primary purpose, create a
  storefront, or bypass signing/sandbox. Google Play explicitly exempts JavaScript in a WebView. This is the same
  footing the existing over-the-air updates stand on.
- **CarPlay entitlement** is per category (audio, communication, navigation, EV charging, parking, fueling, quick
  food ordering, driving task). The app must stay inside the granted category; an update that drifts out of it
  risks losing the entitlement even though the update mechanism itself is allowed.
- **Android Auto** apps are reviewed by Play against the car app quality guidelines and their declared category.
- **The unknown:** a `WKWebView` in the CarPlay base view is not forbidden in writing, but the guidelines expect
  map content only there, and there's no known precedent of approval. The Android Auto equivalent
  (`VirtualDisplay` + `Presentation` hosting a `WebView` on the car `Surface`) is a known technique but not a
  Google-documented path. **Phase 0 exists to find out before investing.** Keep the map page map-only, always.

## Platforms

| Head | Templates | Map surface |
|---|---|---|
| iOS | CarPlay (`CPTemplateApplicationScene`) | `WKWebView` in `CPWindow` (navigation entitlement only) |
| Android | Android Auto via Car App Library (`CarAppService`/`Session`/`Screen`) | `WebView` on the `SurfaceContainer` through `VirtualDisplay` + `Presentation` |
| Android Automotive OS | Same as Android Auto while driving | Same; the full web app may run as a *parked app* — out of scope here, see Later |
| Mac Catalyst, macOS, Windows, Linux | 501 | 501 |

## Phases

### Phase 0 — review spike (gate)

A throwaway app, not library code:

1. iOS: a `CPTemplateApplicationScene` delegate with a `CPMapTemplate` whose `CPWindow` hosts a `WKWebView` loading
   `<BridgeMap>` from the loopback server; one `CPListTemplate` of hard-coded places. Run it in the Simulator's
   CarPlay window (I/O → External Displays → CarPlay). Measure frame rate and memory with the phone locked.
2. Android: a `CarAppService` with a `NavigationTemplate`, `SurfaceCallback` → `VirtualDisplay` → `Presentation`
   → `WebView`, plus a `PlaceListMapTemplate`/`ListTemplate`. Run it on the Desktop Head Unit (DHU).
3. Confirm the .NET bindings: Microsoft.iOS `CarPlay` namespace (expected complete) and the
   `Xamarin.AndroidX.Car.App.*` packages (check they exist, their Car App API level, and whether
   `androidx.car.app.projected` is bound). If Android bindings are missing or stale, decide between binding the
   AAR ourselves or a thin Kotlin shim — record the decision here.
4. Apply for the CarPlay navigation entitlement and submit the spike (TestFlight external review) and an Android
   Auto internal-test build to Play review. **Only proceed with the map surface if both accept.** If Apple
   rejects the WebView base view, fall back to native MapLibre (`maplibre-native` iOS/Android) behind the same
   bridge contracts — the page wouldn't notice.

### Phase 1 — templates (POI-style apps: EV charging, parking, fueling)

No map WebView. The whole car UI is templates the page describes. This alone is a shippable feature.

### Phase 2 — navigation

`CPMapTemplate`/`NavigationTemplate`, the map surface WebView, route guidance fed from `DirectionsRoute`.

## Packages

| Project | Contents |
|---|---|
| `src/Shiny.AppDeviceBridge.Car.Client` | `ICarBridge` (`[BridgeClient("car", typeof(CarJsonContext))]`), template contracts, `CarJsonContext`. Same TFM/layout as the other `.Client` projects. References `Shiny.AppDeviceBridge.Maps.Client` for route contracts. |
| `src/Shiny.AppDeviceBridge.Car` | `CarBridge : IWebAppBridge`, `CarBridgeExtensions.AddCarBridge()`, `ICarScreen` (per-platform renderer), `CarTemplateMapper.Apple.cs` / `.Android.cs`, `CarMapSurface.Apple.cs` / `.Android.cs`. Multi-targets like Notifications; the neutral build answers 501. |

## Contracts (`Shiny.AppDeviceBridge.Car.Client`) — draft

The lowest common denominator of CarPlay and the Car App Library. Anything one platform can't show is either
dropped with a documented fallback (e.g. grid → list) or answers 501 — decide per field during Phase 1, and list
each in the docs.

```csharp
public sealed record CarStatus(
    bool Supported,                  // this head has a car integration at all
    bool Connected,                  // a car screen is attached right now
    CarPlatform Platform,            // CarPlay, AndroidAuto, AndroidAutomotive, None
    string? Category,                // the entitlement/category the app was built for
    IReadOnlyList<string> Templates, // template kinds this binary can draw: "list", "grid", "pane", "message", "poi", "map"
    int MaxDepth,                    // stack limit (CarPlay 5, Android Auto 5 — read from the platform where possible)
    int MaxListItems                 // current list limit from the car (it varies per head unit)
);

public abstract record CarTemplate(string Id, string? Title);   // polymorphic, "kind" discriminator
public sealed record CarListTemplate(string Id, string? Title, IReadOnlyList<CarListSection> Sections, IReadOnlyList<CarAction>? Actions = null) : CarTemplate(Id, Title);
public sealed record CarListSection(string? Header, IReadOnlyList<CarListItem> Items);
public sealed record CarListItem(string Id, string Text, string? Detail = null, string? Image = null, bool Browsable = false);
public sealed record CarGridTemplate(string Id, string? Title, IReadOnlyList<CarGridItem> Items) : CarTemplate(Id, Title);
public sealed record CarGridItem(string Id, string Text, string Image);
public sealed record CarPaneTemplate(string Id, string? Title, IReadOnlyList<CarPaneRow> Rows, IReadOnlyList<CarAction>? Actions = null) : CarTemplate(Id, Title);   // CPInformationTemplate / PaneTemplate
public sealed record CarPaneRow(string Title, string? Detail = null);
public sealed record CarMessageTemplate(string Id, string? Title, string Message, IReadOnlyList<CarAction>? Actions = null) : CarTemplate(Id, Title);            // CPAlertTemplate-ish / MessageTemplate
public sealed record CarPlaceTemplate(string Id, string? Title, IReadOnlyList<CarPlace> Places) : CarTemplate(Id, Title);                                        // CPPointOfInterestTemplate / PlaceListMapTemplate
public sealed record CarPlace(string Id, string Name, double Latitude, double Longitude, string? Detail = null, string? Image = null);
public sealed record CarAction(string Id, string Text, CarActionStyle Style = CarActionStyle.Default);

public sealed record CarPushRequest(CarTemplate Template);
public sealed record CarRootRequest(CarTemplate Template);

// events
public sealed record CarConnection(bool Connected, CarPlatform Platform);
public sealed record CarSelected(string TemplateId, string ItemId);      // list/grid/place item or action
public sealed record CarPopped(string TemplateId);                       // the driver went back

// Phase 2
public sealed record CarNavigationStart(DirectionsRoute Route, string MapPath = "/car/map");
public sealed record CarNavigationProgress(int LegIndex, int ManeuverIndex, double DistanceRemaining, double DurationRemaining, double ManeuverDistanceRemaining);
public sealed record CarMapInput(CarMapInputKind Kind, double X, double Y, double Scale);   // pan/fling/scale/click, forwarded to the map page

[BridgeClient("car", typeof(CarJsonContext))]
public interface ICarBridge
{
    [BridgeGet]                               Task<CarStatus> GetStatusAsync(CancellationToken ct = default);
    [BridgePut("root")]                       Task SetRootAsync(CarRootRequest request, CancellationToken ct = default);
    [BridgePost("stack")]                     Task PushAsync(CarPushRequest request, CancellationToken ct = default);
    [BridgeDelete("stack")]                   Task PopAsync(CancellationToken ct = default);
    [BridgePut("templates/{id}")]             Task UpdateAsync(string id, CarTemplate template, CancellationToken ct = default);  // refresh in place (CarPlay updateSections / Android invalidate)

    [BridgePost("navigation")]                Task StartNavigationAsync(CarNavigationStart request, CancellationToken ct = default);
    [BridgePut("navigation")]                 Task UpdateNavigationAsync(CarNavigationProgress progress, CancellationToken ct = default);
    [BridgeDelete("navigation")]              Task StopNavigationAsync(CancellationToken ct = default);

    [BridgeEvent("car.connection")]           Task<IAsyncDisposable> OnConnectionAsync(Func<CarConnection, Task> handler);
    [BridgeEvent("car.selected")]             Task<IAsyncDisposable> OnSelectedAsync(Func<CarSelected, Task> handler);
    [BridgeEvent("car.popped")]               Task<IAsyncDisposable> OnPoppedAsync(Func<CarPopped, Task> handler);
    [BridgeEvent("car.map.input")]            Task<IAsyncDisposable> OnMapInputAsync(Func<CarMapInput, Task> handler);
}
```

`ManeuverKind` → `CPManeuver` symbol / Car App Library `Maneuver.TYPE_*`: write the mapping table once, test it
exhaustively (every enum value maps, `Other` falls back to a generic straight arrow).

## Endpoints (`CarBridge`)

```
GET    /_bridge/car                      CarStatus
PUT    /_bridge/car/root                 { template }                → 204
POST   /_bridge/car/stack                { template }                → 204   409 depth_exceeded
DELETE /_bridge/car/stack                                            → 204   409 at root
PUT    /_bridge/car/templates/{id}       template                    → 204   404 not on the stack
POST   /_bridge/car/navigation           { route, mapPath }          → 204   501 without the navigation category
PUT    /_bridge/car/navigation           progress                    → 204   409 not navigating
DELETE /_bridge/car/navigation                                       → 204

events: car.connection, car.selected, car.popped, car.map.input
```

### Status codes

| Case | Answer |
|---|---|
| No car integration on this head | 501 |
| No car connected | 409 `car_not_connected` |
| Template kind this binary can't draw, or one the platform can't (e.g. grid on a category that forbids it) | 501 `template_not_supported` with the kind |
| Stack deeper than the platform allows | 409 `depth_exceeded` |
| Too many list items | truncate to `MaxListItems` and say so in the docs (both platforms truncate themselves anyway) — or 400; decide in Phase 1 |
| Duplicate ids within a template, empty text, image path outside the served app | 400 |
| Navigation calls when the app isn't built with the navigation category | 501 |

Every renderer call is marshalled to the main thread (CarPlay's interface controller and the Car App Library's
`Screen` are both main-thread only).

### Images

Template images are paths into the served web app (`/img/charger.png`), fetched by the native side from the
loopback server and cached per template. Reject absolute URLs — the car can't wait on the network and it keeps the
content inside the signed bundle.

## Where the car logic runs

The car can connect while the phone is locked and the phone's WebView was never created (CarPlay launches the app
into only the car scene). So:

- `car.connection`, `car.selected` and `car.popped` go through `WebAppInvoker` like the other native calls: the
  page if one is handling calls, otherwise **`background.js`**. A background.js handler can call the car bridge
  back to push the next template. This is the default path for POI apps.
- For navigation, the car map WebView is itself a page: it loads `mapPath`, joins the launch session and can act
  as the handling page for car events while it is alive.
- Document the rule plainly: *car logic must work from background.js*; the phone page is optional.

## The map surface (Phase 2)

- **CarPlay:** on `didConnect`, create a `WKWebView` inside the `CPWindow`'s root view controller, configured like
  the main host view (same scheme handling, same launch session), loading `mapPath`. `CPMapTemplateDelegate`
  pan/zoom callbacks → `car.map.input`. Map buttons (`CPMapButton`) are native, defined by the page through the
  template contract, never HTML.
- **Android Auto:** `AppManager.SetSurfaceCallback` → on `OnSurfaceAvailable`, `DisplayManager.CreateVirtualDisplay`
  on the surface at its dpi → `Presentation` with a `WebView` loading `mapPath`. Resize/recreate on
  `OnVisibleAreaChanged`/`OnStableAreaChanged` and pass the visible area to the page so it can pad the map.
  `OnScroll`/`OnFling`/`OnScale`/`OnClick` → `car.map.input`.
- **The map page** is a normal route in the web app (the sample adds `/car/map`), updated over the air with the
  rest of the bundle. It must stay map-only: no buttons, no text input, no app UI.
- **Guidance UI** (maneuver card, travel estimates, lane guidance later) is native, fed from
  `CarNavigationStart`/`CarNavigationProgress`. Voice prompts stay the app's job (Speech bridge).

## Registration

```csharp
builder.UseAppDeviceBridge(bridge => bridge
    .AddMapsBridge(...)
    .AddCarBridge(o => o.Category = CarCategory.Navigation));
```

`AddCarBridge()` registers the bridge and the platform pieces it can register from a library. The app still has
to, and the docs must list step by step:

- **iOS:** the CarPlay entitlement in `Entitlements.plist`; a `CPTemplateApplicationSceneSessionRoleApplication`
  scene in `Info.plist` pointing at the bridge's scene delegate (`Shiny.AppDeviceBridge.Car.CarSceneDelegate`);
  the app must use scenes (check how MAUI's `AppDelegate`/scene manifest coexists with an extra car scene).
- **Android:** the `CarAppService` in the manifest with the category intent filter,
  `com.google.android.gms.car.application` meta-data + `automotive_app_desc.xml`, `minCarApiLevel`, and
  `androidx.car.app.NAVIGATION_TEMPLATES`/`ACCESS_SURFACE` permissions for navigation.

## Security

- Every route requires `AppDeviceBridgePolicies.Bridges`. No change to the policy, host-name check or session.
- The car map WebView is a second page on the loopback server: it must get the launch session the same way the
  main host view does, not a new admission path. Call it out in the security docs.
- Images only from the served app (see above).

## Tests (`tests/Shiny.AppDeviceBridge.Tests`)

A fake `ICarScreen` behind the real server, as the other bridge tests do:

- root/push/pop/update reach the renderer with the exact template; depth and duplicate-id rules; 409 when not
  connected; 501 on the neutral build and for unsupported kinds
- polymorphic template JSON round-trips through `CarJsonContext` (trim/AOT-safe — no reflection-based
  polymorphism)
- `ManeuverKind` mapping tables for both platforms cover every value
- `car.selected`/`car.popped`/`car.connection` go to the page when it handles calls and to background.js otherwise
- routes need the bridge policy
- `TypeScriptClientTests`: add the `.Client` project to `tools/Shiny.AppDeviceBridge.TypeScript`'s csproj, regenerate
  `clients/typescript/src`, commit
- `SimulatorCatalogTests`: add the `.Client` project to the simulator's csproj and `ICarBridge` to `BridgeCatalog`.
  The simulator could render templates as HTML in a "car screen" panel — useful for demos; decide if in scope
- Full suite: `dotnet test tests/Shiny.AppDeviceBridge.Tests/Shiny.AppDeviceBridge.Tests.csproj`
- Manual: CarPlay Simulator window and the Android DHU; macOS AppKit head only proves the 501.

## Sample

`Sample.Blazor` gets a "Car" page (status, a button to push a list of nearby places) plus a `background.js`
handler that answers `car.selected` by pushing a detail pane — proving the phone page is optional. Phase 2 adds
`/car/map` and a "navigate here" action that feeds a Directions route into `StartNavigationAsync`.

## Docs, skill, readme

- Docs: new page `appdevicebridge/car.mdx` (templates, per-platform table, setup steps, store policy section,
  the background.js rule) + sidebar entry in `astro.config.mjs`; security page note on the second WebView;
  `npx astro build`.
- Release note under `## 1.0 TBD` (or whichever version is current when it lands): `<RN type="feature">` — Car
  bridge: CarPlay and Android Auto templates driven by the page; iOS and Android; 501 elsewhere. Phase 2 gets its
  own note.
- `skills/shiny-appdevicebridge/SKILL.md`: section + `triggers:` (`ICarBridge`, `AddCarBridge`,
  `Shiny.AppDeviceBridge.Car`, `CarPlay`, `Android Auto`, `car.selected`).
- `readme.md`: package table + bridges table rows.

## Open questions

- Android bindings: existing `Xamarin.AndroidX.Car.App.*` vs our own binding vs a Kotlin shim (Phase 0).
- Does MAUI's single-window scene setup on iOS tolerate an extra CarPlay scene without app changes?
- `WKWebView` performance/throttling in the CarPlay scene with the phone locked; WebView in `Presentation` on DHU.
- List truncation: silent truncate vs 400.
- Whether to expose CarPlay-only templates (now playing, tab bar, voice control) as 501-on-Android, or leave them
  out until there's an audio/communication use case.

## Later (not this pass)

- **Android Automotive OS parked apps:** the full web app in a normal WebView while parked (video/games/browser
  categories). Separate head, separate review.
- Audio apps (now playing) — needs a native media session; not a web-app story.
- Lane guidance, junction images, and the CarPlay instrument-cluster / HUD scenes.
- Native MapLibre fallback for the map surface, if Phase 0 review rejects the WebView.
