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

async function events() {
    source ??= new EventSource((await paths()).bridge + "events");
    return source;
}

export async function subscribe(dotnet, key, eventName) {
    let listeners = subscribers.get(eventName);

    // One DOM listener per event name for the life of the page; .NET listeners come and go behind it.
    if (!listeners) {
        listeners = new Map();
        subscribers.set(eventName, listeners);
        (await events()).addEventListener(eventName, e => {
            for (const listener of subscribers.get(eventName).values())
                listener.invokeMethodAsync("OnEvent", eventName, e.data);
        });
    }

    listeners.set(key, dotnet);
}

export function unsubscribe(key, eventName) {
    subscribers.get(eventName)?.delete(key);
}

export function unsubscribeAll(key) {
    for (const listeners of subscribers.values())
        listeners.delete(key);
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
