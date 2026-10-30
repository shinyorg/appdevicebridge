# Plan: Roadmap ideas

Status: proposed · Target: unscheduled · Scope: candidates for after 2.0, not commitments

Gaps found by surveying `src/` (October 2026). None of these exist today. Each one would get its own plan file
before work starts, the way `appfunctions.md` did.

Suggested order: **1 → 2 → 4 → the rest.** Update safety is what separates a hosted web app from a store build,
and biometrics/secure storage is the bridge people will ask for first.

---

## Over-the-air updates

### 1. Automatic rollback of a bad update

**Problem:** if an update installs and then fails to boot (it crashes on load, or the page never comes up), the app
stays broken until a fixed release is published.

**Shape:**
- The page calls `host.markHealthy()` (Blazor `IHostBridge`, TypeScript client) once it has rendered.
- `Shiny.AppDeviceBridge.WebView` keeps the previous version on disk until the new one is marked healthy.
- If the call doesn't arrive within a configurable window (`HealthyTimeout`, plus a limit on failed launches), the
  host reverts to the previous version, blocks the bad version locally, and reports the revert to the update provider.
- The baseline zip is the last resort when no previous version is on disk.
- `background.js` launches don't count toward the health check.

**Open questions:** whether an app that never calls `markHealthy()` gets rollback (opt-in vs opt-out), and how a
required update interacts with a revert (it should not loop forever).

**Touches:** WebView (version store, launch flow), Core (report contract), AspNetCore (accept revert reports),
Blazor and TypeScript clients, simulator.

### 2. Staged rollouts and a kill switch

**Problem:** a release goes to everyone on its channel at once. `Channel` exists
(`ReleaseServerUpdateProvider.Channel`, `FileSystemWebAppReleaseStore`) but there's no percentage or pause.

**Shape:**
- A release gets a `RolloutPercent`. The client sends a stable install id, and the server buckets it by hash, so the
  same install stays in or out as the percentage grows.
- A `Paused` / pulled state on the release server. A pulled release is never offered, and clients that already
  have it move off it at the next check.
- With #1 in place, the server can pause a rollout automatically when the revert rate passes a threshold.

**Touches:** Core (contracts), AspNetCore (store, endpoints), WebView (install id), release notes (security: the
install id is a new identifier sent off-device, so call it out).

### 3. Delta updates

**Problem:** every update downloads the whole zip. Blazor WebAssembly payloads are large, and this is slow on
cellular.

**Shape:** a signed manifest of per-file hashes. The client downloads only the files whose hashes changed, puts the
version together on disk, and verifies the whole manifest signature before switching. It falls back to the full zip
if anything doesn't match.

**Touches:** Core (manifest and signature), AspNetCore (serve individual files), WebView (assembly and verification),
`publish-release.sh`.

---

## New bridges

Every one of these follows the house pattern: a `.Client` project first, the TypeScript tool's csproj, the
simulator's csproj and `BridgeCatalog`, and an explicit platform list where everything else answers `501`.

### 4. Biometrics and secure storage

Secrets kept in Keychain/Keystore (DPAPI on Windows, libsecret on Linux), with an option to require Face ID,
Touch ID or fingerprint to read them, and a separate "authenticate the user" call. Storage is namespaced by
`AppId`, the same way settings are. **Security change:** this is the most sensitive bridge so far, so decide
whether it needs a stricter policy than `AppDeviceBridgePolicies.Bridges` (for example, launch session only and no
remote callers even in debug).

### 5. Camera scanning through `IMediaService`

**Decision:** the bridge is a thin HTTP face over Shiny.Maui.Controls' `IMediaService` (`~/Desktop/dev/controls`) and
the `Scan…` extension methods each analyzer package adds to it. The bridge writes no camera page, analyzer wiring,
reticle, duplicate filter or timeout of its own. `IMediaService.ScanAsync` already owns all of that, and each
extension is the supported way to drive it. When controls gains a new extension, the bridge gains a route.

**The extensions it maps** (every package is on nuget.org at 2.1.0, the same as the `Shiny.Maui.Controls.Camera` the
Camera bridge uses):

| Controls package | Single result | Session (stream) | Native weight |
|---|---|---|---|
| `Camera.Barcode` | `ScanBarcodeAsync(formats)` → `DetectedBarcode?` | `ScanBarcodesAsync(filterDuplicates, formats)` | ML Kit (Android), ZXing.Net (Windows) |
| `Camera.Ocr` | `ScanTextAsync(recognition)` → `RecognizedText` blocks, `ScanTextStringAsync` | `ScanTextBlocksAsync` | ML Kit text (Android), OpenCvSharp4 (Windows) |
| `Camera.Documents` | `ScanCreditCardAsync`, `ScanDriversLicenseAsync`, `ScanPassportAsync`, `ScanHealthCardAsync`, `ScanReceiptAsync`, `ScanInvoiceAsync`, `ScanBusinessCardAsync` | the plural of each | managed parsers over `Camera.Ocr` |
| `Camera.Face` | `DetectFaceAsync` → `DetectedFace` list | `DetectFacesAsync` | ML Kit face (Android) |

**Out of scope:** motion and AI documents. Neither has a MAUI `IMediaService` extension, and adding them is not
planned.

**Controls work, done** (`~/Desktop/dev/controls`, `v2`, ships in controls 2.2.0):
- **`MediaScanOptions.OnEnded(MediaScanEndReason)`** reports why a session ended: `Done`, `Dismissed`, `MaxResults`,
  `Timeout`, `Cancelled`, `Stopped`, `PermissionDenied` or `CameraUnavailable`. This is on MAUI and Blazor.
- **`IMediaService` is safe from any thread.** The modal is built, pushed and popped on the UI thread, so bridge
  handlers can call it directly.
- Found along the way: a MAUI scan could yield past `MaxResults` when results were buffered. It now stops at the cap.

The bridge needs `Shiny.Maui.Controls.Camera.*` **2.2.0** for `OnEnded`. Bump `Shiny.Maui.Controls.Camera` (and
`.Desktop`, to keep them together) in `Directory.Packages.props` from 2.1.0 to 2.2.0 when the bridge work starts.

#### Packaging (decided by native weight)

```
Shiny.AppDeviceBridge.Scanning.Client      contracts + [BridgeClient] IScanningBridge
Shiny.AppDeviceBridge.Scanning             routes, the one-session gate, request → MediaScanOptions; refs Shiny.Maui.Controls.Camera
Shiny.AppDeviceBridge.Scanning.Barcodes    → Camera.Barcode
Shiny.AppDeviceBridge.Scanning.Text        → Camera.Ocr + Camera.Documents (documents need OCR anyway)
Shiny.AppDeviceBridge.Scanning.Faces       → Camera.Face

bridge.AddScanningBridge(s => s
    .AddBarcodes()
    .AddText(t => t.Documents = ScanDocuments.Receipt | ScanDocuments.BusinessCard)
    .AddFaces());
```

Each add-on registers a small "scanner" that turns a request into one extension call and maps the result to a
`.Client` contract. The core never references an analyzer package. `AddScanningBridge` calls `bridge.Maui.UseShinyCamera()`, the same way
`AddCameraBridge` does, and resolves `IMediaService` from DI. A kind that is not
registered answers `501 kind_not_registered`, and `GET` lists the kinds that are.

#### Routes

```
GET    /_bridge/scanning                  { cameraSupported, cameraAccess, kinds: ["barcode","text","receipt",…] }
POST   /_bridge/scanning/access           IMediaService.RequestCameraPermissionAsync
POST   /_bridge/scanning/{kind}           the single-result extension → the result, or 204 when it returns null
POST   /_bridge/scanning/{kind}/session   the streaming extension; answers 202 at once, and each item fires scanning.{kind}
DELETE /_bridge/scanning/session          cancels the stream, which closes the modal
events scanning.barcode | scanning.text | scanning.face | scanning.{document kind} | scanning.ended
```

- **Kinds:** `barcode`, `text`, `face`, `creditCard`, `driversLicense`, `passport`, `healthCard`, `receipt`,
  `invoice`, `businessCard`.
- **The request** maps 1:1 onto `MediaScanOptions`: `title`, `instructions`, `facing`, `torch`, `zoom`/`allowZoom`,
  `scanWindow {x,y,width,height}`, `timeoutSeconds`, `maxResults`, `filterDuplicates`, `vibrateOnResult`. The
  extension's own parameters ride alongside as nullable fields: `formats` for barcodes, recognition options for text.
  The C#-only hooks (`ConfigureCamera`, `ConfigurePage`, effects) are not exposed. The app sets those once through
  `MediaServiceOptions`.
- **Typed methods per kind** on `IScanningBridge` (`ScanBarcodeAsync`, `ScanReceiptAsync`, `OnBarcodeAsync`, …), and
  **one event per kind** instead of a polymorphic `scanning.result`. That keeps the client source generator and the
  TypeScript generator on ground they already handle.
- Contracts **copy** the controls records into `.Client` (it cannot reference MAUI). Bounding boxes are dropped, since
  they mean nothing once the modal has closed. `DetectedBarcode` has no raw bytes, so there is no `rawBytes` field.
- **Conventions copied from NFC:**
  - Listen before starting a session (`409 not_listening`); the session stops when the last listener goes.
  - One modal at a time (`409 scan_busy`).
  - A request abort disposes the enumerator, which closes the modal.
  - A scan while the Camera bridge holds the lens answers `409 camera_busy`.
  - `scanning.ended` reports `Done` / `Dismissed` / `Timeout` / `MaxResults` / `Stopped` with the count.

  The reason comes straight from `MediaScanOptions.OnEnded`. The bridge adds nothing of its own.

#### Security (call out in the release note)

- **Identity and payment documents are personal data.** In a debug build the bridge policy admits remote callers, so a
  LAN caller could read a scanned card number or passport. Recommendation: `creditCard`, `passport`,
  `driversLicense` and `healthCard` are registered only by naming them in `ScanDocuments` (nothing sensitive by default),
  and `CreditCard.Number` is masked to the last four digits unless `RevealCardNumbers` is set.
- Nothing scans silently: every route opens visible native UI the user can dismiss.

#### Phases

One phase: `Scanning` core, `.Barcodes`, `.Text` (OCR + documents), `.Faces`; client, TypeScript regen, simulator, and
a Sample.Blazor "Scanning" page. It is built against controls 2.2.0.

**Platforms:** iOS, Android, Mac Catalyst, macOS (AppKit) and Windows. Linux, and any head where
`IMediaService.IsCameraSupported` is false, answers `501`.

**Check first:** `IMediaService`'s modal on the maui-labs AppKit head (the Camera bridge's own page works there, the
media service's page is untested), and the Android APK size each ML Kit package adds.

**Out of scope:** `IMediaService` also takes photos and video and picks from the gallery. Capture belongs to the Camera
bridge. A gallery pick (`PickPhotosAsync` into a file root) could be its own small follow-up.

**Simulator:** a canned result per kind set in the panel, and a queued list replayed for sessions.

### 6. NFC

Reading and writing NDEF tags, with background tag dispatch handed to the page or `background.js`. It fits the
field-service and kiosk audience that OBD, Printers and RpiCamera already serve. Platforms: iOS and Android.

### 7. Receiving shares — built

Shipped as `Shiny.AppDeviceBridge.ShareTarget` (`/_bridge/sharetarget`, `IShareTargetBridge`). Shares wait in a
persisted queue, and their files are copied into a file root. Android, iOS and Mac Catalyst files, Windows, and macOS
and Linux through `ShareTarget.ReceiveAsync`. Still open: a stock iOS **share extension** for text and links, which
would need the same extension build step as Live Activities (see `widgets.md`). Today an app has to bring its own
extension.

### 8. Passkeys

Native passkey registration and assertion (ASAuthorization, Android Credential Manager). WebAuthn in a WebView on a
loopback origin is unreliable, and this lets the web app's sign-in use the platform instead. **Security change:**
the relying party id has to be bound to the app's associated domains, never taken from the page as given.

### 9. Home screen widgets

Planned separately in [`widgets.md`](widgets.md).

---

## Developer experience

### 10. Simulator coverage report

The simulator already sees every call. Add a report of the bridge endpoints the page actually used, matched against
each bridge's platform support, so "this page calls an endpoint that answers `501` on Windows" shows up before
anyone tries it on a device. It could also be exposed through the simulator's MCP endpoint.

### 11. `dotnet new` templates

A template that sets up an app (MAUI + WebView host), a Blazor page and a release server together. It would generate
the signing keys and baseline zip wiring and add the platform setup (ATS `NSAllowsLocalNetworking`, Android cleartext
config, the Mac Catalyst network entitlement). Setup touches many pieces, and this removes most of the first-run
friction.

### 12. Per-bridge permission manifest

Each bridge declares the Info.plist keys, Android permissions and entitlements it needs. A build target warns about
missing ones, or adds them. Missing entries are probably the most common cause of a bridge that "doesn't work" on one
platform.
