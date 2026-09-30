# Plan: Shiny.AppDeviceBridge.AppFunctions

Status: proposed · Target: 1.0 (TBD section of the release notes) · Depends on: `Shiny.AppFunctions` (shiny repo, 5.8 beta)

## Goal

Make it possible for Siri, Spotlight, Shortcuts, Apple Intelligence (App Intents) and Gemini (Android AppFunctions)
to call a hosted web app, and let the page call and observe the same functions.

The main constraint shapes everything else. `Shiny.AppFunctions` generates the Swift App Intents and the Android
`app_functions.xml` from the **app project, at build time**. An over-the-air web update therefore cannot add a
function, rename one or change its parameters; those still need a store release. What the web app *can* own is the
**implementation**. So the split is:

| | Owned by | Ships with |
|---|---|---|
| Function surface: id, description, parameters, result type, entities, Siri phrases | C# in the app project | the store build |
| What a call does, including what Siri says | the page or `background.js` | the web app zip (OTA) |
| Gating (sign-in, feature flags), telemetry | `IAppFunctionDelegate` in C#, as today | the store build |

This is the same pattern as `AddWebAppJob`: native code keeps the part the OS needs, and `WebAppInvoker` sends the
work to the page when it is open and to `background.js` when it is not.

## Scope

- **Phase 1 (the point of the package):** OS → web app. Function handlers and entity queries that forward to the
  web app.
- **Phase 2:** web app → functions. A bridge that lists the functions with their JSON schemas, runs them through
  `AppFunctionDispatcher`, and raises an event whenever any function runs, so an open page can refresh.
- **Out of scope:** exposing bridges as app functions, and registering functions dynamically from the web app. The OS
  doesn't allow the second, and bridges are raw device access, not app actions.

Platforms: **iOS (16+) and Android (16 / API 36+)**, the only platforms `Shiny.AppFunctions` supports. Elsewhere the
forwarding handlers are never called because nothing calls them, and the Phase 2 bridge answers `501`.

---

## Prerequisites in Shiny.AppFunctions (shiny repo)

Without these, forwarding needs a hand-written `JsonTypeInfo` for every request and result, which makes the
one-line handler three lines and means double serialization. Land them first, in `~/Desktop/dev/shiny`:

1. **Raw arguments on the context:** `AppFunctionContext.ArgumentsJson` (string), holding the arguments exactly as
   the platform sent them. The forwarder sends these unchanged, so there's no request serializer and entity
   parameters reach the page as **ids**. That's what the web app wants, since it has its own data.
2. **Raw JSON results:** a way for a handler to return JSON that the dispatcher passes through without calling the
   generated `WriteResult`. Proposed shape: `context.SetResultJson(string json)`, which the dispatcher prefers over the
   typed result when it's set. The handler's typed return value is ignored in that case (return `default!`). The
   Swift and Android layers already consume `AppFunctionOutcome.ResultJson`, so nothing changes below the dispatcher.
   Document that the JSON must match the declared result type. The dispatcher validates shape only for primitives:
   `int`, `double`, `bool` and `string` results are checked, because iOS returns them as typed values.
3. **Raw entity results:** the same passthrough for `IAppEntityQuery<T>`, so the forwarder doesn't have to
   materialize `T`. This is optional; the fallback is a `JsonTypeInfo<T>` supplied by the base class (see 1.2).
4. **Transitive build step:** confirm that the `buildTransitive` targets run when the app gets `Shiny.AppFunctions`
   only through `Shiny.AppDeviceBridge.AppFunctions`. If they don't, the docs say to reference both packages.
5. **Non-supported heads:** confirm that a MAUI app that also targets `maccatalyst` and `windows`, as `Sample.Maui`
   does, builds cleanly there (net10.0 asset, no build step), and that an iOS app with `SupportedOSPlatformVersion` 15
   runs as a no-op on iOS 15.
6. Publish a beta and pin it in `Directory.Packages.props`.

If items 1 and 2 are rejected, Phase 1 still works: the base handler takes `JsonTypeInfo<TRequest>` and
`JsonTypeInfo<TResult>` (see "Fallback" below).

---

## Phase 1: OS → web app

### 1.1 Wire contract (`src/Shiny.AppDeviceBridge.AppFunctions.Client`)

CLAUDE.md says a bridge change starts in its `.Client` project, so the payloads are defined there and serialized by
both sides.

Handler name: `appfunction:{functionId}` (for example `appfunction:create_order`). Entity queries use
`appfunction.entity:{entityId}` (for example `appfunction.entity:customer`). Check both against
`WebAppInvoker.IsValidHandlerName` and extend its character set only if needed.

```csharp
// what the page / background.js receives
public sealed record AppFunctionCall(
    string Function,                 // "create_order"
    JsonElement Arguments,           // camelCase, enums as names, entities as ids, dates ISO 8601
    string Platform,                 // "apple" | "android" | "other"
    bool IsForeground,
    string? CallerPackage
);

// what it returns (a bare value is also accepted and treated as { result: value })
public sealed record AppFunctionReply(
    JsonElement? Result = null,
    string? Say = null,
    AppFunctionReplyError? Error = null
);
public sealed record AppFunctionReplyError(string Code, string Message);   // Code: invalidArgument | notFound | denied | appError

// entity queries
public sealed record AppEntityQueryCall(string Entity, string Operation, string? Text, IReadOnlyList<string>? Ids);  // Operation: search | getByIds | suggested
// reply: an array of the entity's JSON
```

Add a `[JsonSerializable]` context and include the project in `tools/Shiny.AppDeviceBridge.TypeScript`, so a generated
`appfunctions.ts` gives the JS side typed `AppFunctionCall`/`AppFunctionReply`. Regenerate and commit, or
`TypeScriptClientTests` fails. Phase 1 declares no `[BridgeClient]` interface; Phase 2 adds it. See 2.4 for the
simulator.

**Bare values:** if a reply isn't an object with at least one of `result`/`say`/`error`, it's the result. That
covers `on("appfunction:count_open_orders", () => 3)`. An object result that happens to use only those keys must be
wrapped. Document this.

### 1.2 Native side (`src/Shiny.AppDeviceBridge.AppFunctions`)

`net10.0`, the same as `Shiny.AppDeviceBridge.Jobs`: each head's own platform build of `Shiny.AppFunctions` is what
runs. The project references `Shiny.AppDeviceBridge` and the `.Client` project, and has a `PackageReference` to
`Shiny.AppFunctions`.

```csharp
// the app — one line per function
public class CreateOrderHandler(WebAppInvoker invoker) : WebAppFunctionHandler<CreateOrder, OrderResult>(invoker);
public class CancelOrderHandler(WebAppInvoker invoker) : WebAppFunctionHandler<CancelOrder>(invoker);
public class CustomerQuery(WebAppInvoker invoker) : WebAppEntityQuery<Customer>(invoker);
```

The generator finds handlers through `AllInterfaces` and skips abstract types (`Analyzer.cs:51`, `:174`), so an
abstract base class in the library plus a concrete class in the app counts as the function's handler. Add a
generator test in the shiny repo that pins this behaviour, since the package depends on it.

`WebAppFunctionHandler<TRequest, TResult>` : `IAppFunctionHandler<TRequest, TResult>`:
1. Build an `AppFunctionCall` from `context.ArgumentsJson`, `Platform`, `IsForeground` and `CallerPackage`.
2. Call `invoker.InvokeAsync($"appfunction:{context.FunctionId}", call, AppFunctionsClientJson.Default.AppFunctionCall, ct)`.
3. Map the `WebAppInvocationResult`:

   | Invocation result | Outcome |
   |---|---|
   | `NotHandled` (no page listening, `background.js` doesn't register the name) | `AppFunctionException(AppError, "{App} can't do that right now.")`, and log a warning naming the handler. The message is configurable in options. |
   | `Succeeded == false` (JS threw) | `AppError` with the JS error message in the log. The user-facing message is the generic one, never a raw JS stack. |
   | Reply with `error` | `AppFunctionException` with the mapped code and the reply's message, which the page writes for the user |
   | Reply with `say` | `context.Say(say)` |
   | Reply `result` | `context.SetResultJson(result)`. For `WebAppFunctionHandler<TRequest>` (no result) the result is ignored. |

4. Cancellation: `ct` passes through to `InvokeAsync`, and a timeout surfaces as `Cancelled`.

`WebAppEntityQuery<T>` : `IAppEntityQuery<T>` works the same way, with `appfunction.entity:{entityId}` and the
three operations. The entity id comes from the `[AppEntity]` attribute. The base class reads it from
`dispatcher.Registry` rather than reflecting over the attribute (trim/AOT).

**Fallback, if prerequisites 1–3 don't land:** overloads that take `JsonTypeInfo<TRequest>` / `JsonTypeInfo<TResult>`
/ `JsonTypeInfo<List<T>>`. They are more verbose in the app but need no change to AppFunctions. Pick one of the two
designs before writing code; don't ship both. Per CLAUDE.md, no leftover shims.

**Registration:** none of its own. `AddAppFunctions()` (generated) registers the handlers, and `WebAppInvoker` is
already registered by the bridge server. If `WebAppInvoker` turns out to be scoped to the bridge server rather than
the root container, add `bridge.AddWebAppFunctions()` to expose it to the dispatcher's scope. Check this first.

### 1.3 Timing

The limits interact, so they get one explicit section in the docs and tests for each boundary:

| Limit | Value | Owner |
|---|---|---|
| Host ready after a cold start | 10 s (`AppFunctionsHost.ReadyTimeout`) | AppFunctions |
| Page accepts | `PageAcceptTimeout` (2 s) | AppDeviceBridge |
| `background.js` runs | `BackgroundScriptTimeout` (25 s) | AppDeviceBridge |
| Siri / App Intents budget | about 10–30 s, and not documented | the OS |
| Android AppFunction budget | set by the system | the OS |

A cold start from Siri that ends in `background.js` can spend 10 s before the host is up and then up to 25 s in the
script. Add an `AppFunctionTimeout` option (default 20 s) on the forwarder that caps the whole call and fails with
`Cancelled` and a spoken message, so the user doesn't get Siri's generic failure. Measure the real Siri budget on a
device before settling the default. It's a known unknown until then.

### 1.4 Gates and the foreground

- `AppFunctionGate.OpenApp` on iOS runs the function again in the foreground. By then the WebView is usually loading.
  The page may not be listening within `PageAcceptTimeout`, so the call falls through to `background.js`, which is
  wrong for "sign in first". Add a check to the forwarder: when `context.IsForeground` is set, wait for the page to
  start listening (up to `AppFunctionTimeout`) before falling back. That needs a small `WebAppInvoker` addition,
  either `WaitForListenerAsync(handler, timeout)` or an `InvokeOptions.PreferPage`. Test both the case where the page
  arrives in time and the case where it doesn't.
- Delegates stay in C#. Document an `IAppFunctionDelegate` that reads a sign-in flag the page stores in
  `/_bridge/settings`, so the web app can gate calls without native changes.

### 1.5 Page side

- **JS:** `on("appfunction:create_order", async ({ arguments: a }) => ({ result: {...}, say: "…" }))` in the page or
  `background.js`. There's no new runtime code; `/_bridge/invoke/client.js` already covers it. The generated
  TypeScript types give the payload shape.
- **Blazor:** a helper in the `.Client` package on top of `nativeCalls.HandleAsync`:
  `nativeCalls.HandleAppFunctionAsync<TArgs, TResult>("create_order", argsTypeInfo, resultTypeInfo, async (args, call) => new(result, Say: "…"))`.
  It deserializes the arguments, wraps the reply and turns an `AppFunctionReplyException` into `error`.

### 1.6 Tests (`tests/Shiny.AppDeviceBridge.Tests`)

Run everything in-process through `AppFunctionDispatcher.Execute` with a real `WebAppInvoker` and a fake
`IWebAppBackgroundInvoker` or a page client, as the existing invoker tests do:

- Arguments reach the handler unchanged: entity parameters as ids, enums as names.
- Reply mapping: bare value, `{ result }`, `{ say }`, `{ error }` for each code, JS throw, `NotHandled`.
- The result JSON is passed through to `AppFunctionOutcome.ResultJson`, and `Dialog` carries `say`.
- A function with no result ignores the returned value.
- Entity query forwarding for all three operations, and an id the query doesn't return giving `NotFound`.
- Delegates still run before the forwarder, and a `Deny` never reaches the web app.
- `AppFunctionTimeout` produces `Cancelled`.
- The foreground wait from 1.4.
- `Platform`, `IsForeground` and `CallerPackage` reach the payload.

A test app project needs the generator. Add a small test-only project with `[AppFunction]` records and forwarding
handlers so the generated `AddAppFunctions()` is exercised for real, rather than hand-rolling an `IAppFunctionRegistry`.

### 1.7 Sample

- `Sample.Maui` (the app project the generator must scan): an orders-style function set of 2–3 functions, one
  entity and one `[AppShortcut]`, all forwarding. **They must be declared in `Sample.Maui`, not `Sample.App`**,
  because `Sample.App` is a class library and would be ignored silently.
- `Sample.Blazor`: an **App Functions** page that handles the calls and shows a log of recent calls, and
  `wwwroot/background.js` handlers for the same functions so they work with no page open.
- Verify end to end on an iOS simulator (Spotlight → the shortcut's title) and an Android 16 emulator
  (`adb shell cmd app_function execute-app-function …`). Include a cold start with the app killed, which is the
  `background.js` path. Record which of these were actually run.

---

## Phase 2: web app → functions (bridge)

### 2.1 Contract (`.Client`)

```csharp
[BridgeClient("appfunctions")]
public interface IAppFunctionsBridge
{
    [BridgeGet] Task<IReadOnlyList<AppFunctionInfo>> GetFunctionsAsync(CancellationToken ct = default);
    [BridgePost("{id}")] Task<AppFunctionRunResult> RunAsync(string id, JsonElement arguments, CancellationToken ct = default);
    [BridgeEvent("appfunctions.invoked")] Task<IAsyncDisposable> OnInvokedAsync(Func<AppFunctionInvoked, Task> handler);
}

public sealed record AppFunctionInfo(string Id, string Title, string Description, JsonElement ParametersSchema, string? ResultType, bool OpensApp);
public sealed record AppFunctionRunResult(string Status, JsonElement? Result, string? Say, string? ErrorCode, string? Message);
public sealed record AppFunctionInvoked(string Function, string Platform, string Status, string? ErrorCode, DateTimeOffset At);
```

- `GetFunctionsAsync` returns `dispatcher.Registry.Functions`, including the generated `search_{entity}`, with
  `GetParametersJsonSchema()`. This is the tool list an in-page AI assistant needs.
- `RunAsync` calls `AppFunctionDispatcher.Execute(new AppFunctionInvocation(id, AppFunctionPlatform.Other), …)`, so
  delegates apply and an `OpenApp` gate refuses the call, as the AppFunctions docs describe for in-process calls.
  **Loop guard:** a forwarding handler called from the page would send the call straight back to the page. When the
  caller is the page itself, fail with `InvalidArgument` "handled by the web app; call it directly", or allow it and
  document it. Decide this during implementation. The first option is simpler and is the recommendation.
- `appfunctions.invoked` is raised by an `IAppFunctionDelegate` in this package (`OnInvoked`), so the page can refresh
  after Siri or Gemini changed state in the background. It carries no arguments or results, only metadata; the
  page reloads its own data. The page misses events while it's closed; that's fine, because it reloads when it opens.

### 2.2 Registration & policy

`bridge.AddAppFunctionsBridge()`. Routes sit under the guarded prefix and require
`AppDeviceBridgePolicies.Bridges`. There's no change to the default policy, host-name check or session. It answers
`501` where `AppFunctionDispatcher` isn't registered (the app didn't call `AddAppFunctions()`). On other platforms
the functions still run in-process, since the dispatcher is platform-neutral, so `GetFunctionsAsync`/`RunAsync` work
there too. Only the OS entry points are iOS/Android. Say exactly this in the release note.

**Security note for the docs:** `RunAsync` lets the page run any declared function with the delegates applied. That
is the app's own surface, not new device access, but a delegate that only gates on `Platform == Apple` would now be
bypassed by `Other`. Call this out on the docs page.

### 2.3 Tests

- The function list matches the registry, and the schemas are present.
- Running a function goes through delegates, and `Deny` and `OpenApp` are refused.
- The loop guard.
- The event fires for Siri-style (`Apple`) invocations and for failures.
- `501` without `AddAppFunctions()`.
- Policy: a remote caller is rejected in a release configuration, as the other bridge policy tests check.

### 2.4 Simulator & TypeScript

- Add the `.Client` project to `src/Shiny.AppDeviceBridge.Simulator`'s csproj and `IAppFunctionsBridge` to
  `BridgeCatalog` (`SimulatorCatalogTests`). Simulator answers: a configurable function list, results you set, and
  a "fire `appfunctions.invoked`" action.
- Also useful for Phase 1: a simulator action "call `appfunction:{id}` on the page" with arguments you type, so the
  page's handlers can be tested without Siri. If the simulator can't send `host.invoke` calls yet, that's a separate
  simulator feature that would help Jobs/GPS/push too, so track it as its own item.
- Regenerate `clients/typescript/src` and commit it.

---

## Definition of done (per CLAUDE.md, for each phase)

1. **Code + tests:** the full suite (`dotnet test tests/Shiny.AppDeviceBridge.Tests/Shiny.AppDeviceBridge.Tests.csproj`)
   passes. Trim/AOT analyzers are clean: no reflection over attributes, and all JSON goes through source-generated
   contexts. Platforms are stated: iOS/Android for OS calls, `501` rules as above.
2. **Docs** (`~/Desktop/dev/documentation/src/content/docs/appdevicebridge/`): a new `appfunctions.mdx` page
   (sidebar entry in `astro.config.mjs`) covering the ownership split, forwarding handlers, the reply shape, the
   timing table, foreground/gates, the version-skew guidance and the Phase 2 bridge. Link it from the "Calling the
   web app from native code" section and from the AppFunctions docs (`client/appfunctions`). Release notes go under
   `## 1.0 TBD` (`type="feature"`). `npx astro build` passes.
3. **Skill** (`skills/shiny-appdevicebridge/SKILL.md`): a section with the default pattern (declare in C# in the app
   project, forward with `WebAppFunctionHandler`, handle in the page and `background.js`). Add `triggers:`
   `AppFunctions`, `WebAppFunctionHandler`, `WebAppEntityQuery`, `AddAppFunctionsBridge`, `App Intents`, `Siri`,
   `Gemini`.
4. **readme.md:** the package table, the bridges table (Phase 2), and a new row in the "Calling the web app from
   native code" table: `appfunction:{id}` / `appfunction.entity:{id}`, registered by a `WebAppFunctionHandler`.

## Guidance to document: version skew

The function list changes only with store releases, while the web app changes over the air, so a new web app will
run in old app builds. The docs should tell web apps to:

- register handlers for every function any supported app build declares;
- read arguments defensively (a parameter added in a newer build is missing in an older one);
- use `GET /_bridge/appfunctions` (Phase 2) to discover what this build declares, when that matters.

## Open questions

1. Prerequisites 1–3: are they accepted in Shiny.AppFunctions, or do we use the `JsonTypeInfo` fallback? This blocks
   Phase 1's API shape.
2. The real Siri time budget on a device, which sets the `AppFunctionTimeout` default.
3. Loop guard for page → forwarding function: refuse the call (recommended) or allow it?
4. Is `WebAppInvoker` resolvable from the dispatcher's DI scope in all hosts (MAUI, and the macOS/Linux heads where
   it doesn't matter but shouldn't throw)?
5. Should the Blazor helper live in `.Client` or in `Shiny.AppDeviceBridge.Blazor`? Follow wherever the Jobs /
   `nativeCalls` helpers live today.

## Order of work

1. Settle prerequisites and open question 1 with the shiny repo, then release an AppFunctions beta.
2. `.Client` contracts, TypeScript regeneration, simulator catalog.
3. `WebAppFunctionHandler` / `WebAppEntityQuery` and the test-only generator project, then tests.
4. The foreground wait (1.4) with its `WebAppInvoker` addition, then tests.
5. The sample: Blazor page, `background.js`, `Sample.Maui` declarations. Verify on the iOS simulator and the
   Android 16 emulator.
6. Docs, skill and readme for Phase 1; release notes.
7. Phase 2 bridge, tests, simulator, docs; release notes.
