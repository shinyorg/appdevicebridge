// Shiny.WebAppHost.Blazor: the JavaScript half of WebAppEvents and WebAppNativeCalls.

let source;
let client;
const subscribers = new Map();   // event name -> Set of .NET listeners
const handlers = new Map();      // native call name -> function that unregisters it

function events() {
    source ??= new EventSource("/_bridge/events");
    return source;
}

export function subscribe(dotnet, eventName) {
    let listeners = subscribers.get(eventName);

    // One DOM listener per event name for the life of the page; .NET listeners come and go behind it.
    if (!listeners) {
        listeners = new Set();
        subscribers.set(eventName, listeners);
        events().addEventListener(eventName, e => {
            for (const listener of subscribers.get(eventName))
                listener.invokeMethodAsync("OnEvent", eventName, e.data);
        });
    }

    listeners.add(dotnet);
}

export function unsubscribe(dotnet, eventName) {
    subscribers.get(eventName)?.delete(dotnet);
}

export function unsubscribeAll(dotnet) {
    for (const listeners of subscribers.values())
        listeners.delete(dotnet);
}

export async function handle(dotnet, name) {
    // Imported from the host rather than bundled, so the page always speaks the protocol of the host it runs in.
    client ??= import("/_bridge/invoke/client.js");
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
