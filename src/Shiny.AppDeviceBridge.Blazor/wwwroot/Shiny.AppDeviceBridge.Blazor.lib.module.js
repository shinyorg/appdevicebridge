// Shiny.AppDeviceBridge.Blazor: a Blazor JavaScript initializer, run by Blazor itself once it has started — the app's root
// components have rendered by then. It tells the native host the page is up, so the splash screen in front of it comes down
// on a drawn page rather than on Blazor's "Loading…" text. Outside a host (a plain browser, the simulator) the call fails
// quietly: there is no splash screen to take down.

let signalled = false;

async function signalReady() {
    if (signalled)
        return;

    signalled = true;

    try {
        let bridge = "/_bridge/";

        try {
            const config = await fetch(new URL("_host/config", document.baseURI));
            if (config.ok) {
                const paths = await config.json();
                bridge = paths.bridge.endsWith("/") ? paths.bridge : paths.bridge + "/";
            }
        } catch {
            // An older host that moved nothing.
        }

        await fetch(new URL(bridge + "host/ready", document.baseURI), { method: "POST" });
    } catch {
        // Not in a host. Nothing waits for this.
    }
}

// Standalone Blazor WebAssembly (blazor.webassembly.js).
export function afterStarted() {
    signalReady();
}

// A Blazor Web App running interactive WebAssembly (blazor.web.js).
export function afterWebAssemblyStarted() {
    signalReady();
}
