---
name: run-appdevicebridge
description: Run, start, launch, screenshot, click through, hot-reload, or stop the Shiny.AppDeviceBridge sample on this Mac — the Blazor WebAssembly app inside the MAUI macOS AppKit head, with dotnet watch and the release server. Use when asked to run the sample, see a change in the app, drive its UI, or test hot reload.
---

# Run the Shiny.AppDeviceBridge sample

Paths are relative to the repository root (`webapphost/`). macOS only.

The sample is three processes:

- `samples/Sample.ReleaseServer` — the update server, `:5199`
- `dotnet watch` on `samples/Sample.Blazor` — the dev server, `:5288`
- `samples/Sample.MacOS` — the AppKit head. Its loopback host (`:5780`) proxies pages from the dev server and serves
  the native bridges.

`.claude/skills/run-appdevicebridge/driver.sh` starts them in the right order, screenshots the app window, clicks in it,
and triggers hot reload. Screenshots land in `artifacts/run-appdevicebridge/`.

## Prerequisites

Verified on this machine: .NET SDK 10.0.401 with the `maui` and `macos` workloads, Xcode 27.0, and `cliclick` at
`/opt/homebrew/bin/cliclick`. The terminal needs Screen Recording permission for `screencapture`.

## Run (agent path)

```bash
.claude/skills/run-appdevicebridge/driver.sh up
.claude/skills/run-appdevicebridge/driver.sh status
```

`up` is idempotent and takes about 25 seconds cold. It returns once the app's loopback server answers, but a Debug
Blazor app shows "Starting…" for another 10–20 seconds. Wait before the first screenshot:

```bash
perl -e 'select(undef,undef,undef,20)' && .claude/skills/run-appdevicebridge/driver.sh shot home
```

Clicks take points from the window's top-left, title bar included. Screenshots are 2x, so divide image pixels by 2.
The window is 1280×720 points:

```bash
.claude/skills/run-appdevicebridge/driver.sh click 459 55     # the "Device" nav link
.claude/skills/run-appdevicebridge/driver.sh shot device
```

Hot reload changes the Home page `<h1>` in place and waits for `dotnet watch` to report it:

```bash
.claude/skills/run-appdevicebridge/driver.sh heading "AppDeviceBridge + Blazor (hot reloaded)"
.claude/skills/run-appdevicebridge/driver.sh heading "AppDeviceBridge + Blazor"
```

The rest:

```bash
.claude/skills/run-appdevicebridge/driver.sh logs     # dotnet watch output
.claude/skills/run-appdevicebridge/driver.sh test     # the full unit and integration suite
.claude/skills/run-appdevicebridge/driver.sh down
```

## Run (human path)

```bash
cd samples/Sample.Blazor && dotnet watch run --launch-profile device
```

With that running, start `samples/Sample.MacOS` from your IDE. Edits to `Sample.Blazor` go to the app through the
dev server.

## Gotchas

- **`shot` and `click` bring the app forward and move the real mouse.** Don't run them while someone is using the Mac.
  A click sent while another app was in front landed in that app.
- **Screenshot by window id, not screen rectangle.** A rectangle capture picked up a Discord splash floating over the
  app. `shot` uses `screencapture -l`.
- **Start order matters.** The app only uses the dev server if it answers when the app starts. If `dotnet watch`
  starts later, the app quietly serves the embedded build. `up` starts the servers first.
- **Apple builds need `-p:ValidateXcodeVersion=false`.** The installed macOS SDK (26.5) expects Xcode 26.6; this
  machine has 27.0.
- **Hot reload can leave the page broken.** A Razor edit logs "C# and Razor changes applied", but the rebuild deletes
  and rewrites the fingerprinted `_framework/dotnet.<hash>.js`. The page reloads in between and stops at "Could not
  start: Failed to start platform. Reason: TypeError: Importing a module script failed." It stays there; `up`
  relaunches the app and it loads.
- **Save files in place.** Saving a `wwwroot` file by writing a temp file and renaming it crashed `dotnet watch`
  (`Unexpected true - file HotReloadMSBuildWorkspace.cs line 158`), and the Blazor app exited. `heading` writes in
  place. To recover: `down` then `up`.
- **`dotnet watch` needs `NBGV_CacheMode=None`.** Without it, it failed with `An item with the same key has already
  been added. Key: ProjectInstanceId { ProjectPath = …/nerdbank.gitversioning/…/PrivateP2PCaching.proj }`. The setting
  is committed in `Directory.Build.props`.
- **gps and geofences are struck through on AppKit.** Shiny.Locations has no macOS build, so this is expected.
- **Use the AppKit head.** The Mac Catalyst head (`samples/Sample.Maui -f net10.0-maccatalyst`) builds, but exits at
  launch with code 133 and leaves no crash report. Unresolved.
- **No `sleep`.** Agent shells here block it, so the driver waits with `perl -e 'select(...)'`.

- **Drive it with MAUI DevFlow, not `click`/`shot`, when someone is at the Mac.** Sample.MacOS registers the DevFlow agent
  and its WebView (`DevFlowWebView.cs`, Debug only), and `up` launches the app with `open -g`, behind whatever is in front.
  `maui devflow webview Runtime evaluate -ah 127.0.0.1 "<js>"` runs script in the page; `maui devflow ui screenshot
  -ah 127.0.0.1 --overwrite --output x.png` captures the window. Pass `-ah 127.0.0.1` (`localhost` resolves to `::1`,
  the agent listens on IPv4), and retry "Another DevFlow session is driving this app": the agent holds a lease of a
  few seconds after each command. A window that was never on screen screenshots blank and throttles the page's timers;
  the Maps page's **Snapshot** button renders the map to an `<img>` whose `src` can be read instead.
- **After editing any JS in Sample.Blazor, restart `dotnet watch`** (`down` then `up`). The page's import map points at
  fingerprinted file names that watch does not refresh, so the edited module fails with "Importing a module script
  failed" until it restarts. Edit wwwroot files in place: a temp-file-and-rename save serves them as empty bodies.
- **`dev server did not start` again and again:** earlier `dotnet watch` processes that crashed can hang and keep the
  watcher; `pgrep -fl "dotnet watch run --launch-profile device"` and kill them before `up`.

## Troubleshooting

- **`shot` says `could not create image from window`** while `status` is all green: the Mac's screen is locked
  (`ioreg -n Root -d1 -a | grep -A1 CGSSessionScreenIsLocked`). Nothing can be captured or clicked until it is
  unlocked; drive the bridges with `curl` instead — a Debug build answers any caller.
- **`up` fails with `dev server did not start`** and `devserver.log` ends in `Failed to start the EventStream` then
  `PAL_SEHException`: `dotnet watch` could not start its file watcher. Build and `open` the app without it; the
  embedded build is served.

- **`_LSOpenURLsWithCompletionHandler() failed with error -600`** came from `open` running while the previous instance
  was still exiting. `up` now waits for it to exit.
- **`could not create image from rect`** came from the old rectangle capture during a display change. `shot` now
  captures by window id.
- **The window spins forever and `status` shows `app loopback :5780 000`** happened when `WebAppHostView` only started
  from `Loaded`, which the maui-labs AppKit backend never raises. It now also starts from `OnHandlerChanged`; if the
  spinner is back, that has regressed.
- **The window says `The web app host could not be created: InvalidOperationException: Unable to resolve service for
  type 'Shiny.IPlatform' while attempting to activate 'Shiny.Push.PushManager'`.** A bridge registered a Shiny
  service on macOS without `EnsureShinyCore()` (`src/Shared/ShinyCoreHosting.cs`).
