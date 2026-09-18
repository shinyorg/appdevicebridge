// Shiny.AppDeviceBridge.Blazor: the JavaScript half of WebAppEvents and WebAppNativeCalls.

let source;
let client;
let config;
// event name -> Map of subscriber key -> .NET listener.
//
// Keyed by a string the .NET side chose, never by the listener object itself: Blazor revives a
// DotNetObjectReference into a *new* JavaScript object on every interop call, so the object handed to
// unsubscribe is never the one subscribe stored. Keyed by identity, nothing was ever removed — every page that
// listened again added a listener, each event crossed into .NET once per page ever visited, and an event
// arriving after the client was disposed called an object that no longer existed.
const subscribers = new Map();
const handlers = new Map();      // native call name -> function that unregisters it

// Where this host mounted the bridges. The page is served from the mount point, so document.baseURI is
// enough to find _host, and _host answers with the rest. An older host that has no config has moved nothing.
function paths() {
    config ??= fetch(new URL("_host/config", document.baseURI))
        .then(r => (r.ok ? r.json() : Promise.reject(new Error(r.status))))
        .then(p => ({ bridge: p.bridge.endsWith("/") ? p.bridge : p.bridge + "/" }))
        .catch(() => ({ bridge: "/_bridge/" }));

    return config;
}

// The host only runs the native source behind an event while a stream names it, so the stream always carries exactly
// the events something here listens to.
function topics() {
    return [...subscribers].filter(([, listeners]) => listeners.size > 0).map(([name]) => name);
}

let opening;

function events() {
    opening ??= paths().then(p => {
        source = new EventSource(p.bridge + "events?topics=" + encodeURIComponent(topics().join(",")));

        // First on every connection, reconnects included, which start over with the topics of the first URL.
        source.addEventListener("bridge.stream", e => {
            streamId = JSON.parse(e.data).id;
            sync();
        });

        return source;
    });

    return opening;
}

let streamId;
let syncing;
let waiting = [];

// Resolves once the host has the current topics — a subscriber can then start what it listens to without missing the
// first events, or being refused for not listening. Several changes in a row become one request.
function sync() {
    const done = new Promise(resolve => waiting.push(resolve));

    syncing ??= Promise.resolve().then(async () => {
        syncing = undefined;

        // No stream id yet: the stream's open event syncs, and settles these.
        if (!streamId)
            return;

        const settled = waiting;
        waiting = [];

        try {
            await fetch((await paths()).bridge + "events/" + streamId, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ topics: topics() })
            });
        } catch {
            // Reconnecting; the next open event sends the topics again.
        }

        settled.forEach(resolve => resolve());
    });

    // A host that is not there never opens the stream; do not hold the caller forever.
    return Promise.race([done, new Promise(resolve => setTimeout(resolve, 5000))]);
}

export async function subscribe(dotnet, key, eventName) {
    let listeners = subscribers.get(eventName);
    const known = listeners !== undefined;

    if (!known) {
        listeners = new Map();
        subscribers.set(eventName, listeners);
    }

    const added = listeners.size === 0;
    listeners.set(key, dotnet);

    // One DOM listener per event name for the life of the page; .NET listeners come and go behind it.
    if (!known)
        (await events()).addEventListener(eventName, e => {
            for (const listener of subscribers.get(eventName).values())
                listener.invokeMethodAsync("OnEvent", eventName, e.data);
        });

    if (added)
        await sync();
}

// Both resolve once the host has stopped the sources nothing here listens to any more.
export async function unsubscribe(key, eventName) {
    const listeners = subscribers.get(eventName);
    if (listeners?.delete(key) && listeners.size === 0)
        await sync();
}

export async function unsubscribeAll(key) {
    let changed = false;

    for (const listeners of subscribers.values())
        changed = (listeners.delete(key) && listeners.size === 0) || changed;

    if (changed)
        await sync();
}

export async function handle(dotnet, name) {
    // Imported from the host rather than bundled, so the page always speaks the protocol of the host it runs in.
    client ??= paths().then(p => import(p.bridge + "invoke/client.js"));
    const { on } = await client;

    handlers.get(name)?.();
    handlers.set(name, on(name, async payload => {
        const result = await dotnet.invokeMethodAsync("OnCall", name, JSON.stringify(payload ?? null));
        return result == null ? null : JSON.parse(result);
    }));
}

export function unhandle(name) {
    handlers.get(name)?.();
    handlers.delete(name);
}
