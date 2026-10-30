# Plan: Shiny.AppDeviceBridge.Widgets

Status: proposed · Target: unscheduled · Depends on: a new `Shiny.Mobile.Widgets` in the shiny repo (see Prerequisites)

## Goal

Let a hosted web app put live content on the home screen (iOS and Android first, then the Windows widget board) and
keep it current: from the page while it is open, from `background.js` when it isn't, and from a push.

## The constraint that shapes everything

A widget is not drawn by the app.

- **iOS:** a WidgetKit extension runs out of process, on the system's schedule, under a memory cap of roughly 30 MB.
  It must be SwiftUI, because `WidgetBundle` and `TimelineProvider` have no Objective-C surface. It can't host
  .NET, and it certainly can't host a WebView. `~/Desktop/dev/shiny/plans/live-activity-widget-package.md` records
  the same finding for Live Activities.
- **Android:** an `AppWidgetProvider` runs in the app's process, but the widget is `RemoteViews` drawn by the
  launcher. Only a fixed set of views is allowed, and there's no WebView.
- **Windows 11:** widget board widgets are Adaptive Cards (JSON template + JSON data) supplied by a COM
  `IWidgetProvider` in a packaged app.

So the page can't render the widget. What it can own is the **data**, and which of the app's **templates** shows it.
That's the same split as AppFunctions and Live Activities:

| | Owned by | Ships with |
|---|---|---|
| Widget kinds, sizes and layouts (SwiftUI, `RemoteViews` / Glance, Adaptive Card templates) | the app project | the store build |
| What each widget instance shows, and when it changes | the page or `background.js` | the web app zip (OTA) |
| Where a tap goes | the page, as a route | the web app zip |

An OTA update can change what a widget says, but not add a new kind of widget or change its layout. The docs have to
say this up front.

---

## Prerequisites in the shiny repo: `Shiny.Mobile.Widgets`

The bridge stays thin, as it is for Live Activities. The native work belongs in a Shiny library that a plain MAUI
app could use too.

1. **A shared data store.** `IWidgetStore.Set(kind, WidgetContent)` writes JSON where the widget can read it: the App
   Group container on iOS (`NSUserDefaults(suiteName:)` or a file), the app's files or `SharedPreferences` on Android,
   and the provider's state on Windows. Then it asks the system to redraw: `WidgetCenter.reloadTimelines(ofKind:)`,
   `AppWidgetManager.updateAppWidget`, or `WidgetManager.UpdateWidget`.
2. **`WidgetContent`, a generic content model** like `LiveActivityContent`: title, body, a value with a unit, a
   progress bar, up to N list rows, an image file, a tint, a deep-link route, and a `Data` string dictionary for custom
   templates. It also carries a **timeline**: a list of (`date`, content) entries, so a countdown or schedule moves
   forward without the app running.
3. **Stock templates for each platform**, so the common case needs no Swift or XML:
   - iOS: a SwiftUI `WidgetBundle` with small, medium and accessory layouts reading `WidgetContent`, compiled by
     `swiftc` and embedded as an `AdditionalAppExtensions` item. **This reuses the build targets
     `Shiny.Mobile.LiveActivities` already has** (`ShinyLiveActivityWidget`, `buildTransitive/*.targets`), so factor
     the shared part out rather than copying it. An opt-in property (`ShinyWidgets=true`) turns it on, and
     `ShinyWidgetSource` items replace the stock SwiftUI.
   - Android: an `AppWidgetProvider` in C# that builds `RemoteViews` from `WidgetContent`, plus stock layout XML and
     `appwidget-provider` metadata, contributed as Android resources by the package.
   - Windows: a stock Adaptive Card template and a COM widget provider. This needs a packaged app and a manifest
     extension, so it comes in a later phase.
4. **An App Group.** iOS needs the same App Group entitlement on the app and the extension. The targets should check
   it and fail clearly, not ship a widget that silently shows placeholder content.
5. **Widget instances.** Report which widgets the user has placed (`WidgetCenter.getCurrentConfigurations`,
   `AppWidgetManager.getAppWidgetIds`) and raise an event when one is added or removed, so a server only pushes
   updates for widgets that exist.
6. **Taps.** Each widget opens a deep link (`widgetURL` / `PendingIntent`) in the app's scheme, carrying the route
   and the widget kind.

Push-driven updates use what already exists. iOS 18 has widget push updates through APNs (`WidgetPushHandler`), and
everywhere else a push wakes the app and `background.js` calls the bridge. Treat this as Phase 3, not a prerequisite.

---

## The bridge

`Shiny.AppDeviceBridge.Widgets` + `Shiny.AppDeviceBridge.Widgets.Client` (`IWidgetsBridge`), registered with
`bridge.AddWidgetsBridge(o => o.AddKind("order-status"))`. Kinds are declared in C# because they have to match what
the store build ships. A request for an undeclared kind answers `404`.

```
GET    /_bridge/widgets                       { "supported": true, "kinds": ["order-status"], "installed": [ { "kind", "family", "id" } ] }
PUT    /_bridge/widgets/{kind}                { "content": { "title": "Order 42", "body": "Out for delivery", "route": "/orders/42" },
                                                "timeline": [ { "at": "…", "content": {…} } ] }
DELETE /_bridge/widgets/{kind}                back to the placeholder
POST   /_bridge/widgets/reload                ask the system to redraw every kind

events:   widgets.installed, widgets.removed
handlers: widgets.refresh, which the system asks for on its own schedule; answered by the page or background.js
```

- **Images** are a `BridgeFile` in a file root. The bridge copies the file into the shared container, downscaled to
  the platform's limit (WidgetKit drops images over its budget without saying so). A URL is not accepted, because the
  extension can't be trusted to fetch over the network in time.
- **Taps** go through the AppLinks bridge: the widget's link is in the app's scheme, `WebAppLinks.Receive` maps it to
  the route, and the page sees an ordinary `app.link`. So the Widgets bridge requires `AddAppLinksBridge` with a
  scheme, and says so with a clear startup exception if it's missing.
- **Size limits:** cap the content JSON (for example 16 KB per kind) and the timeline length (for example 50
  entries), and answer `413` beyond them. The iOS extension reads the whole thing within its memory cap.
- **Refresh:** iOS asks the timeline provider for new entries on its own schedule. The stock provider serves the
  stored timeline and, when it runs out, records that a refresh is due. The next time the app runs, the
  `widgets.refresh` handler runs in the page or `background.js`. Pair it with `Shiny.AppDeviceBridge.Jobs` for apps
  that need regular updates.

### Security

What a widget shows is visible on the home screen and, for accessory families, on the lock screen. That's a
deliberate disclosure the page makes, so document it. The shared container is readable only by the app and its own
extensions. Nothing new is exposed to other apps, and the bridge stays under `AppDeviceBridgePolicies.Bridges`. Treat
the route a widget opens as untrusted input: it goes through `WebAppLinks.IsLocalRoute` like any other link.

---

## Phases

1. **Android:** the `AppWidgetProvider` and `RemoteViews` are C#, so no extension build is needed. This proves the
   contracts, the bridge, the simulator panel and the docs.
2. **iOS:** the stock SwiftUI bundle, the shared build targets with Live Activities, App Group wiring, and timelines.
3. **Push updates:** iOS 18 widget pushes, and the `widgets.refresh` handler through `background.js`.
4. **Windows widget board:** Adaptive Cards, which map naturally onto `WidgetContent`, but packaging and COM
   activation make this its own piece of work.
5. **Mac Catalyst / macOS:** WidgetKit on macOS uses the same SwiftUI, so this mostly needs the build targets to
   cover those TFMs.

Platforms not covered answer `501`. Linux has no widget host to target.

## Tests

- Contracts, kind validation, size limits, and `BridgeFile` image copying, against a fake `IWidgetStore`.
- Route mapping through `WebAppLinks`.
- `SimulatorCatalogTests` and `TypeScriptClientTests` once the `.Client` project exists.
- The real widgets need devices: an Android emulator for Phase 1 and the iOS simulator for Phase 2, driven through
  the sample app.

## Open questions

1. Should content be keyed per kind, or per placed widget instance? Per instance is what lets one kind show two
   different orders. It costs a configuration intent (`AppIntentConfiguration` on iOS, a configure activity on
   Android). Start per kind, and design the contract so an optional `instanceId` can be added without a breaking
   change.
2. Should `Shiny.Mobile.Widgets` and `Shiny.Mobile.LiveActivities` share one build package for the Swift extension
   step? Two copies of the signing and `AdditionalAppExtensions` logic will drift. (Strongly yes.)
3. Interactive widgets (iOS 17 `Button(intent:)`, Android `RemoteViews` click actions that don't open the app):
   these would call into the app without opening it, which leads straight into the AppFunctions plan. Leave them out
   of v1.
