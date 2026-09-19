// Runs in the app's embedded JavaScript engine when a native call arrives and no page is open to take it —
// a background job, a GPS reading, a geofence transition, a notification tap or a finished transfer while the app
// is in the background.
//
// The same /_bridge endpoints the page uses are available through fetch. There is no DOM, no timers and no
// state between calls: keep what must survive in settings or files. The top level should only register handlers.

appdevicebridge.on("job:sync", async ({ name }) => {
    const runs = (await read("sync-runs")) ?? 0;
    await write("sync-runs", runs + 1);
    await log(`job ${name} ran (run ${runs + 1})`);
});

appdevicebridge.on("gps", async reading => {
    await write("last-background-position", reading);
});

appdevicebridge.on("geofence", async ({ identifier, state }) => {
    await log(`geofence ${identifier}: ${state}`);
});

appdevicebridge.on("motion", async ({ activity, confidence }) => {
    await log(`motion ${activity} (${confidence})`);
});

// A tap on a notification the web app sent. `action` and `text` are set for an action button or a typed reply.
appdevicebridge.on("notification.entry", async ({ id, data, action, text }) => {
    await write("last-notification", { id, data, action, text, at: new Date().toISOString() });
});

// A tray menu earns its keep with the window closed, which is exactly when no page is listening.
appdevicebridge.on("tray.menu", async ({ id, itemId, checked }) => {
    await log(`tray ${id}: ${itemId}${checked === null || checked === undefined ? "" : ` = ${checked}`}`);
});

// Quick entry opens over other applications from its hotkey, usually with the app's own window closed.
appdevicebridge.on("quickentry.submitted", async ({ text }) => {
    await fetch("/_bridge/quickentry/prompt", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isBusy: false, response: `background.js heard "${text}".` })
    });
    await log(`quick entry: ${text}`);
});

appdevicebridge.on("transfer.completed", async ({ id, type, root, path }) => {
    await log(`transfer ${id} (${type}) completed: ${root}/${path}`);
});

appdevicebridge.on("transfer.failed", async ({ id, statusCode, error }) => {
    await log(`transfer ${id} failed: ${statusCode ?? "-"} ${error}`);
});

// A message from the watch with the app closed: what this returns is the reply the watch gets.
appdevicebridge.on("wearables.message", async ({ path }) => {
    await log(`watch message: ${path}`);
    return { answeredBy: "background.js", path };
});

appdevicebridge.on("wearables.transfer", async ({ id, path }) => {
    await log(`watch transfer ${id}: ${path}`);
});

// Already filed into a file root; `file` is the { root, path } the files bridge reads.
appdevicebridge.on("wearables.file", async ({ fileName, file }) => {
    await log(`watch file ${fileName}: ${file.root}/${file.path}`);
});

async function read(key) {
    const response = await fetch(`/_bridge/settings/local/${key}`);
    return response.ok ? response.json() : null;
}

async function write(key, value) {
    await fetch(`/_bridge/settings/local/${key}`, { method: "PUT", body: JSON.stringify(value) });
}

async function log(line) {
    await fetch("/_bridge/files/data/append?path=background.log", {
        method: "POST",
        body: `${new Date().toISOString()}  ${line}\n`
    });
}
