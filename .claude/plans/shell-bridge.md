# Plan — `Shiny.AppDeviceBridge.Shell`

A plain bridge over Shiny.Maui.Shell's `INavigator`. The hosted web app can do standard navigation — push a
route, go back, pop to root, set tab badges — and hear when navigation happens. Routes are strings. There's no
source generation or route typing and no dependency on anything Shell generates.

Status: **plan only**, nothing built yet.

## Why this shape

- Routes are strings on both sides. The web app never references the MAUI app, and it updates over the air
  independently of it, so compile-time route typing on the page would only hold at build time. A string route
  that doesn't exist fails clearly at runtime instead.
- `INavigator`'s hand-written API (`NavigateTo(string, bool, args)`, `GoBack`, `PopToRoot`, `SetTabBadge`,
  `ClearTabBadge`, `Navigated`) covers standard navigation. Nothing in this bridge touches `[ShellMap]` or
  generated output.
- Typed routes can come later as a layer on top, without changing these endpoints.

## Packages

| Project | Contents |
|---|---|
| `src/Shiny.AppDeviceBridge.Shell.Client` | `IShellBridge` (`[BridgeClient("shell", typeof(ShellJsonContext))]`), contracts, `ShellJsonContext`. Same TFM/layout as the other `.Client` projects. |
| `src/Shiny.AppDeviceBridge.Shell` | `ShellBridge : IWebAppBridge`, `ShellBridgeExtensions.AddShellBridge()`, `ShellArgs` (JSON → navigation args). References `Shiny.Maui.Shell` 7.0.1 + `Microsoft.Maui.Controls`. |

Shiny.Maui.Shell ships `net10.0`, `-android`, `-ios`, `-maccatalyst`, `-windows` — no `-macos`. The bridge
multi-targets like the Notifications bridge; the macOS AppKit and GTK4 heads resolve Shell's `net10.0` build.

## Contracts (`Shiny.AppDeviceBridge.Shell.Client`)

```csharp
public sealed record ShellStatus(
    bool Available,          // a ShinyShell is the current root
    string? Location,        // Shell.Current.CurrentState.Location, e.g. "//main/detail"
    bool Badges              // SetTabBadge works on this platform
);

public sealed record ShellNavigateRequest(
    string Route,
    bool Relative = true,
    IReadOnlyDictionary<string, JsonElement>? Args = null
);

public sealed record ShellBackRequest(
    int Count = 1,
    IReadOnlyDictionary<string, JsonElement>? Args = null
);

public sealed record ShellArgsRequest(IReadOnlyDictionary<string, JsonElement>? Args = null);

public sealed record ShellBadge(int Value);

public enum ShellNavigationType { Push, SetRoot, GoBack, PopToRoot, SwitchShell }   // mirrors Shiny's NavigationType

public sealed record ShellNavigated(string Route, ShellNavigationType Type);

[BridgeClient("shell", typeof(ShellJsonContext))]
public interface IShellBridge
{
    [BridgeGet]                     Task<ShellStatus> GetStatusAsync(CancellationToken ct = default);
    [BridgePost("navigate")]        Task NavigateAsync(ShellNavigateRequest request, CancellationToken ct = default);
    [BridgePost("back")]            Task GoBackAsync(ShellBackRequest request, CancellationToken ct = default);
    [BridgePost("root")]            Task PopToRootAsync(ShellArgsRequest request, CancellationToken ct = default);
    [BridgePut("badges/{route}")]   Task SetTabBadgeAsync(string route, ShellBadge badge, CancellationToken ct = default);
    [BridgeDelete("badges/{route}")] Task ClearTabBadgeAsync(string route, CancellationToken ct = default);
    [BridgeEvent("shell.navigated")] Task<IAsyncDisposable> OnNavigatedAsync(Func<ShellNavigated, Task> handler);
}
```

Add convenience overloads in a static `ShellBridgeExtensions` class in the `.Client` project, the way
`FilesBridgeExtensions` does: `NavigateAsync(string route, bool relative = true)`, `GoBackAsync(int count = 1)`,
`PopToRootAsync()`.

## Endpoints (`ShellBridge`)

```
GET    /_bridge/shell                    ShellStatus
POST   /_bridge/shell/navigate           { route, relative, args }     → 204
POST   /_bridge/shell/back               { count, args }               → 204
POST   /_bridge/shell/root               { args }                      → 204
PUT    /_bridge/shell/badges/{route}     { value }                     → 204   501 where Shell has no badges
DELETE /_bridge/shell/badges/{route}                                   → 204   501 where Shell has no badges

events: shell.navigated   (INavigator.Navigated → { route: ToUri, type })
```

All routes go through `WebAppBridgeRoutes`, so all of them require `AppDeviceBridgePolicies.Bridges`. No policy
change.

### Status codes

| Case | Answer |
|---|---|
| `INavigator` not registered (no `UseShinyShell`) | `IsSupported = false` → 501 everywhere |
| Registered, but the current root isn't a Shell (e.g. still on a splash page, or `SwitchShell` in progress) | 409 `shell_unavailable` |
| Empty route, `count < 1`, badge < 0, args that aren't a JSON object | 400 |
| Shell throws for an unknown route (`ArgumentException` from `Routing`/`GoToAsync`) | 404 `route_not_found` with the route |
| `SetTabBadge`/`ClearTabBadge` throws `PlatformNotSupportedException` (neutral build: macOS AppKit, Linux) | 501 |
| Badge route isn't a tab in the active Shell | 404 |
| `INavigationConfirmation.CanNavigate()` returns false | Shell's navigator swallows it, so the call returns 204. **Check 7.0.1**: if a result is observable, answer 409 `navigation_cancelled`. |

Every call goes through Shell's `IMainThread` (the navigator already does this internally; verify, and wrap if
it doesn't) so that calls from the HTTP thread are safe on macOS/Linux.

### Args (`ShellArgs`)

JSON → `(string Key, object Value)` tuples for `INavigator`:

- string → `string`; `true`/`false` → `bool`; `null` → skipped
- number → `int` if it fits, else `long`, else `double`
- object / array → the `JsonElement` itself (`.Clone()`d so it outlives the request)
- max 32 keys, keys must be non-empty — otherwise 400

**The ViewModel receives these through `IQueryAttributable`, not `[ShellProperty]`.** String-based
`NavigateTo(route, args)` doesn't set `[ShellProperty]` members; only the generated/typed methods do. The docs
and skill must say so plainly. Verify against 7.0.1 before writing docs, in case string navigation has started
applying `[ShellProperty]`.

### `shell.navigated`

Subscribe to `INavigator.Navigated` when the bridge is created, and map it to `ShellNavigated(ToUri, type)`
through `routes.Events.Source(...)`. Parameters and the ViewModel aren't forwarded: parameters can hold
arbitrary native objects, and page events are for observing, not for passing data. Don't call a background.js
handler; navigation only matters when a page is showing.

## Registration

```csharp
builder
    .UseShinyShell(x => x.AddGeneratedMaps())
    .AddShellBridge();
```

`AddShellBridge()` only calls `AddWebAppBridge<ShellBridge>()`. It doesn't call `UseShinyShell` — the app owns
Shell configuration, and a missing Shell shows up as 501, not a startup crash.

## Security

- No change to the bridge policy, the host-name check or the session.
- New exposure: the page can push **any registered route** with arbitrary query args. That's within "the web app
  is the app", but add an optional filter so an app can restrict it:
  `AddShellBridge(o => o.AllowRoute = route => route is "Scanner" or "Settings")` → 403 `route_not_allowed`.
  Default: all routes allowed. State that in the security doc and release note.
- `SwitchShell` stays out of the bridge: it takes a `Shell` instance or type, which a page can't name safely.

## Tests (`tests/Shiny.AppDeviceBridge.Tests`)

Use a fake `INavigator` behind the real server, as the other bridge tests do:

- navigate/back/root pass route, relative flag, count and converted args through exactly
- arg conversion: each JSON kind, int/long/double boundaries, nested object stays a `JsonElement`, 33 keys → 400
- 501 without `INavigator`; 409 when no Shell is current; 404 on an unknown route; 501 on
  `PlatformNotSupportedException` from badges; 403 from the route filter
- `Navigated` → `shell.navigated` event payload over the event stream
- routes need the bridge policy (a remote/non-session caller is refused)
- `TypeScriptClientTests`: add the `.Client` project to `tools/Shiny.AppDeviceBridge.TypeScript`'s csproj,
  regenerate `clients/typescript/src`, commit the output
- `SimulatorCatalogTests`: add the `.Client` project to `src/Shiny.AppDeviceBridge.Simulator`'s csproj and
  `IShellBridge` to `BridgeCatalog`
- Full suite: `dotnet test tests/Shiny.AppDeviceBridge.Tests/Shiny.AppDeviceBridge.Tests.csproj`

## Sample

The sample host doesn't use Shell today. Options:

1. Move `Sample.App` onto a `ShinyShell` with the WebView page as the root `ShellContent`, plus one native
   `DetailPage` (with an `IQueryAttributable` ViewModel that shows the args it got), and add a "Shell" page to
   `Sample.Blazor` with navigate / back / badge buttons and a `shell.navigated` log.
2. A separate small sample, if putting the main sample on Shell affects other bridges' demos.

Pick option 1 unless it breaks the desktop/tray samples. Check it in the macOS AppKit head with the
`run-appdevicebridge` skill (push native page, back, event log). Badges will return 501 there, which is the
expected result.

## Docs, skill, readme

- Docs: new page `appdevicebridge/shell.mdx` + sidebar entry in `astro.config.mjs`; mention the route filter in
  the security page; `npx astro build`.
- Release note under `## 1.0 TBD` (version.json `1.0.0-beta` → heading per existing file):
  `<RN type="feature">` — Shell bridge: navigate/back/root/tab badges and `shell.navigated` over Shiny.Maui.Shell;
  Android, iOS, Mac Catalyst, Windows; macOS AppKit and Linux except badges (501).
- `skills/shiny-appdevicebridge/SKILL.md`: section + `triggers:` (`IShellBridge`, `AddShellBridge`,
  `Shiny.AppDeviceBridge.Shell`, `shell.navigated`), including the `IQueryAttributable` rule.
- `readme.md`: package table + bridges table rows; `WhenCovered` in the hosting guidance.

## When a native page covers the web app (`Shiny.AppDeviceBridge.WebView`)

Either behavior can be right, so the app chooses. This lives in the WebView package, not the Shell bridge: any
MAUI navigation (a modal, a plain `NavigationPage` push) can cover the host view, not just Shell.

```csharp
public enum WebAppCoveredBehavior
{
    /// <summary>The page keeps running and handles calls itself — today's behavior.</summary>
    KeepRunning,

    /// <summary>The page is paused where the platform allows it, and handler calls go to background.js.</summary>
    Pause
}

// WebAppHostOptions
public WebAppCoveredBehavior WhenCovered { get; set; } = WebAppCoveredBehavior.KeepRunning;
```

- **Detecting "covered":** `WebAppHostView` hooks its containing `Page`'s `Appearing`/`Disappearing` when it
  attaches, and unhooks them when it detaches. This is plain MAUI, so it works with or without Shell. Check that
  the maui-labs AppKit and GTK4 backends raise both events. They don't raise `Loaded`, so this may need the same
  handler-based fallback `WebAppHostView` already uses.
- **`KeepRunning`:** changes nothing. Background jobs, GPS and other `WebAppInvoker` calls go to the page
  while it keeps listening, and to background.js when it doesn't — both as today.
- **`Pause`:**
  - **Routing (all platforms):** the invoker treats the page as not handling calls, so background jobs, GPS and
    other handler calls go straight to background.js instead of waiting out `PageAcceptTimeout` (2 s) for a page
    that can't answer. This needs a hook on `WebAppInvoker` in `Shiny.AppDeviceBridge` (e.g.
    `SetPageAvailable(bool)`, checked in `IsPageHandling`). It has to be public: the WebView package isn't in
    `InternalsVisibleTo`, and adding a friend assembly for this isn't worth it.
  - **Native pause:** Android calls `WebView.OnPause()`/`OnResume()` (per view — not `PauseTimers()`, which is
    process-wide). Windows calls `CoreWebView2.TrySuspendAsync()` only if the control is hidden, which it isn't
    while covered, so Windows gets routing only. iOS, Mac Catalyst, macOS and GTK have no per-view pause, so
    they also get routing only. Document this per platform.
  - **Uncovered again:** resume the WebView and mark the page available again. Page events published while it
    was paused may be lost. Check what the event stream delivers after an Android `OnResume()`, and tell pages
    to re-read state on `shell.navigated` back to their route.
- **What it covers:** every caller of `WebAppInvoker.InvokeAsync`, because the switch sits in `IsPageHandling`:
  jobs (`job:*`, all four job kinds), `gps`, `geofence`, `motion`, HTTP transfer completed/failed, push,
  `notification.entry`/`.received`, and the tray icon and quick entry (Desktop). A new delegate that goes
  through the invoker is covered automatically.
- **What it doesn't cover:** page events. Those delegates also `Publish` to the event stream, and that path
  doesn't go through the invoker. Under `Pause`, the event is still sent (to a paused Android WebView it may be
  buffered or lost), while background.js does the work — the same split the delegates already rely on, so
  nothing is handled twice.
- **Not a security change:** the setting only moves who handles calls; nothing new is admitted.
- **Tests:** a fake covered/uncovered signal through the host view → invoker skips the page immediately (no
  accept wait) under `Pause`, and keeps today's behavior under `KeepRunning`. Check on a real Android device
  that background jobs and GPS go to background.js while a native page is pushed, and back to the page after
  pop.
- **Docs:** add `WhenCovered` to the hosting page (defaults and per-platform table) and link it from the Shell
  page. Add its own `<RN type="feature">` line.

## Decided

- **Dialogs:** not included — the page uses its own UI.
- **Multi-segment navigation (`INavigationBuilder`):** not included — send an absolute route instead.
- **Results from native pages:** not included for now.

## Later (not this pass)

Typed routes: a generator in the MAUI app that reads `[ShellMap]`/`[ShellProperty]` (user source, so no
generator chain) and publishes a route manifest at `GET /_bridge/shell/routes`, plus optional typed page-side
helpers from a committed manifest. They'd call the same `/navigate` endpoint, so nothing above changes.
