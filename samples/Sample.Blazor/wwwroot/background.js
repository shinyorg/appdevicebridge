// Runs in the app's embedded JavaScript engine when a native call arrives and no page is open to take it —
// a background job, a GPS reading or a geofence transition while the app is in the background.
//
// The same /_bridge endpoints the page uses are available through fetch. There is no DOM, no timers and no
// state between calls: keep what must survive in settings or files. The top level should only register handlers.

webapphost.on("job:sync", async ({ name }) => {
    const runs = (await read("sync-runs")) ?? 0;
    await write("sync-runs", runs + 1);
    await log(`job ${name} ran (run ${runs + 1})`);
});

webapphost.on("gps", async reading => {
    await write("last-background-position", reading);
});

webapphost.on("geofence", async ({ identifier, state }) => {
    await log(`geofence ${identifier}: ${state}`);
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
