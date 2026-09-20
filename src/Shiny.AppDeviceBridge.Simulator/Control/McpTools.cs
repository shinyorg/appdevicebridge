using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>
/// <see cref="SimulatorControl"/> as MCP tools, so an agent can drive a running simulator: set what a bridge answers, fire
/// an event at the page, play a trail, and read what the page actually asked for.
/// <para>
/// The descriptions are the only documentation an agent gets, so they say what the tool is <em>for</em>, not what it is
/// called. Every tool is built explicitly rather than discovered by reflection, which keeps the trim and AOT analyzers
/// quiet and keeps the list of what an agent can do in one readable place.
/// </para>
/// </summary>
public static class McpTools
{
    /// <summary>
    /// Every tool, over the control returned by <paramref name="control"/>. It is a function because the tools are built
    /// as the container is configured, and the control they drive is only resolvable once it has been built; nothing
    /// calls it until a tool is invoked, which is long after.
    /// </summary>
    public static IReadOnlyList<McpServerTool> Create(Func<SimulatorControl> control)
    {
        ArgumentNullException.ThrowIfNull(control);

        return
        [
            Tool(
                "get_status",
                "Where the simulator is serving, the platform and app id it reports, and which trails are loaded. Start here.",
                () => control().GetStatus(),
                readOnly: true
            ),
            Tool(
                "list_bridges",
                "Every simulated bridge: its name, whether it is switched on, how many routes it has, how often the page has called it, and the events it raises.",
                () => control().ListBridges(),
                readOnly: true
            ),
            Tool(
                "describe_bridge",
                "One bridge in full: every route with what it answers right now and a sample of its contract, and every event with its payload and how many page streams are listening.",
                ([Description("The bridge's name, as list_bridges gives it: wifi, gps, ble.")] string bridge) => control().DescribeBridge(bridge),
                readOnly: true
            ),
            Tool(
                "get_route",
                "One route: what it answers now, and 'sample' — the generated shape of its contract, which is what a value has to look like.",
                (
                    [Description("The bridge's name: wifi.")] string bridge,
                    [Description("The route's key within the bridge: 'GET current', 'POST connection', or 'GET' for its root.")] string key
                ) => control().GetRoute(bridge, key),
                readOnly: true
            ),
            Tool(
                "set_route",
                "Sets what a route answers from now on: a value, a 204 null, or an error. The value is checked against the bridge's contract first, so the simulator never sends the page something a real device could not. What you leave out is kept, so a delay can be added without restating the value.",
                (
                    [Description("The bridge's name: wifi.")] string bridge,
                    [Description("The route's key: 'GET current'.")] string key,
                    [Description("'value' for a 200, 'null' for a 204 the page's client reads as null, or 'error'. Defaults to 'value' when a value is given, otherwise leaves the mode alone.")] string? mode = null,
                    [Description("What it answers in value mode — the contract's shape, as get_route's 'sample' shows it.")] JsonNode? value = null,
                    [Description("The error's HTTP status in error mode: 501 not supported, 403 denied, 409 wrong state. Defaults to 501.")] int? status = null,
                    [Description("The error's code in error mode: not_supported, access_denied, bad_request, not_found, conflict, bridge_failed.")] string? code = null,
                    [Description("The error's message in error mode.")] string? message = null,
                    [Description("Milliseconds to hold before answering — to show the page's loading state or trip its timeout.")] int? delayMs = null,
                    [Description("For a route that answers with bytes (a photo, a camera frame): a file on this machine to send.")] string? file = null
                ) => control().SetRoute(bridge, key, mode, value, status, code, message, delayMs, file)
            ),
            Tool(
                "set_route_sequence",
                "Makes a route answer a different value on each call, the last one repeating once they run out — for a page that polls: queued, then running, then done. Every value is checked against the contract before any is used. A later set_route replaces the whole sequence.",
                (
                    [Description("The bridge's name.")] string bridge,
                    [Description("The route's key: 'GET status'.")] string key,
                    [Description("The values, in the order the page will receive them. Each must match the contract, as get_route's 'sample' shows it.")] JsonNode?[] values,
                    [Description("Milliseconds to hold before each answer.")] int? delayMs = null
                ) => control().SetRouteSequence(bridge, key, values, delayMs)
            ),
            Tool(
                "reset_route",
                "Puts a route back to the generated sample of its contract, undoing whatever was set.",
                (
                    [Description("The bridge's name.")] string bridge,
                    [Description("The route's key.")] string key
                ) => control().ResetRoute(bridge, key),
                idempotent: true
            ),
            Tool(
                "set_bridge_supported",
                "Switches a whole bridge off or on. Off, every one of its routes answers 501 and the host reports it missing — exactly what the page sees on a platform that does not have it.",
                (
                    [Description("The bridge's name.")] string bridge,
                    [Description("False to make it answer 501 everywhere.")] bool supported
                ) => control().SetBridgeSupported(bridge, supported),
                idempotent: true
            ),
            Tool(
                "set_platform",
                "What the host reports as the platform, which is what the page's platform checks read: android, ios, maccatalyst, macos, windows or linux.",
                ([Description("android, ios, maccatalyst, macos, windows or linux.")] string platform) => control().SetPlatform(platform),
                idempotent: true
            ),
            Tool(
                "set_sticky_writes",
                "Whether a PUT or POST whose body is the same contract its GET returns becomes what that GET answers next, so the page reads back what it wrote. On by default.",
                ([Description("False to make writes leave the matching GET alone.")] bool on) => control().SetStickyWrites(on),
                idempotent: true
            ),
            Tool(
                "fire_event",
                "Raises a native event on the page's event stream, as the device would. Check the returned listener count: zero means the page has not subscribed and the event went nowhere.",
                (
                    [Description("The event's name: wifi.changed, gps.reading, ble.scanresult.")] string name,
                    [Description("The payload. Its contract is checked. Omit it to send the event's current payload. '$now' and '$uuid' are filled in.")] JsonNode? payload = null
                ) => control().FireEvent(name, payload)
            ),
            Tool(
                "set_event_payload",
                "Sets what an event carries when it is fired without one — the payload a trail step or the terminal UI sends.",
                (
                    [Description("The event's name.")] string name,
                    [Description("The payload, matching the event's contract.")] JsonNode payload
                ) => control().SetEventPayload(name, payload),
                idempotent: true
            ),
            Tool(
                "list_trails",
                "The loaded trails and where each player is.",
                () => control().ListTrails(),
                readOnly: true
            ),
            Tool(
                "load_trail",
                "Adds a trail written out here — a timed script of events fired and answers changed — replacing one of the same name. Shape: { name, loop?, steps: [ { delayMs, note?, event?, payload?, bridge?, route?, mode?, value?, status?, code?, message?, responseDelayMs?, supported? } ] }.",
                ([Description("The trail, as a whole object with a name and steps.")] JsonNode trail) => control().LoadTrail(trail)
            ),
            Tool(
                "load_trail_file",
                "Loads a trail from a file on this machine: a .gpx, which plays as a GPS walk at its recorded pace, or a .trail.json.",
                ([Description("Path to a .gpx or .trail.json file.")] string path) => control().LoadTrailFile(path)
            ),
            Tool(
                "play_trail",
                "Plays a loaded trail from its first step. It runs in the background — the call returns at once, so watch it with get_traffic or wait_for_request.",
                (
                    [Description("The trail's name, as list_trails gives it.")] string name,
                    [Description("Delays are divided by this: 4 plays four times as fast.")] double? speed = null,
                    [Description("Start over after the last step, until stopped.")] bool? loop = null
                ) => control().PlayTrail(name, speed, loop)
            ),
            Tool(
                "pause_trail",
                "Pauses a playing trail, or resumes a paused one.",
                ([Description("The trail's name.")] string name) => control().PauseTrail(name)
            ),
            Tool(
                "stop_trail",
                "Stops a trail. What it already changed stays changed.",
                ([Description("The trail's name.")] string name) => control().StopTrail(name),
                idempotent: true
            ),
            Tool(
                "remove_trail",
                "Stops a trail and forgets it.",
                ([Description("The trail's name.")] string name) => control().RemoveTrail(name),
                destructive: true
            ),
            Tool(
                "apply_scenario",
                "Applies a whole setup at once — the platform, which bridges are off, what routes answer, event payloads and trails. Returns what could not be applied, one line each. Shape: { platform?, stickyWrites?, bridges: { wifi: { supported?, routes: { 'GET current': { mode, value, … } }, events: { 'wifi.changed': { … } } } }, trails: [] }.",
                ([Description("The scenario object.")] JsonNode scenario) => control().ApplyScenario(scenario)
            ),
            Tool(
                "apply_scenario_file",
                "Applies a scenario saved as a file on this machine.",
                ([Description("Path to a scenario .json file.")] string path) => control().ApplyScenarioFile(path)
            ),
            Tool(
                "capture_scenario",
                "Everything set up right now, as a scenario object — save it beside the app to replay this exact setup later, or hand it back as a reproduction.",
                () => control().CaptureScenario(),
                readOnly: true
            ),
            Tool(
                "get_traffic",
                "What the page actually requested and what it got back, newest first. This is how to tell whether the page called a bridge at all, and what it sent.",
                (
                    [Description("Looked for in 'METHOD path?query', case-insensitively: 'GET /_bridge/wifi', 'wifi', 'POST'. Omit for everything.")] string? match = null,
                    [Description("How many to return, newest first. Default 50.")] int limit = 50,
                    [Description("Include request and response bodies, cut to 4 KB each. Off by default to keep the list readable.")] bool bodies = false
                ) => control().GetTraffic(match, limit, bodies),
                readOnly: true
            ),
            Tool(
                "clear_traffic",
                "Throws away what has been recorded, so the next look holds only what happened after this. Useful right before driving the page.",
                () => control().ClearTraffic(),
                destructive: true
            ),
            Tool(
                "set_recording",
                "Whether requests are being recorded at all. Switching it off also throws away what was recorded.",
                ([Description("False to stop recording and discard what is held.")] bool on) => control().SetRecording(on),
                idempotent: true
            ),
            Tool(
                "get_activity",
                "The simulator's own log, newest first: values changed, events fired, trail steps played — including what you changed.",
                ([Description("How many lines, newest first. Default 50.")] int limit = 50) => control().GetActivity(limit),
                readOnly: true
            ),
            Tool(
                "wait_for_request",
                "Waits for the page to make a matching request and returns it. This is the other half of set_route: set the answer, then wait for the page to ask, instead of guessing how long it takes. Only requests made after this call count.",
                (
                    [Description("Looked for in 'METHOD path?query': 'GET /_bridge/wifi/current'.")] string match,
                    [Description("How long to wait, in milliseconds. Default 10000.")] int timeoutMs = 10_000,
                    [Description("Include the request and response bodies. On by default — it is usually the point.")] bool bodies = true,
                    CancellationToken cancellationToken = default
                ) => control().WaitForRequestAsync(match, timeoutMs, bodies, cancellationToken),
                readOnly: true
            ),
            Tool(
                "wait_for_quiet",
                "Waits until the page has stopped making requests for a while, so a check runs after it has finished reacting rather than in the middle of it.",
                (
                    [Description("How long nothing must be requested, in milliseconds. Default 750.")] int quietMs = 750,
                    [Description("How long to wait for that to happen, in milliseconds. Default 10000.")] int timeoutMs = 10_000,
                    CancellationToken cancellationToken = default
                ) => control().WaitForQuietAsync(quietMs, timeoutMs, cancellationToken),
                readOnly: true
            )
        ];
    }

    static McpServerTool Tool(string name, string description, Delegate handler, bool readOnly = false, bool destructive = false, bool idempotent = false) =>
        McpServerTool.Create(handler, new McpServerToolCreateOptions
        {
            Name = name,
            Description = description,
            ReadOnly = readOnly,
            Destructive = destructive,
            Idempotent = idempotent,
            OpenWorld = false,
            SerializerOptions = ControlJsonContext.Default.Options
        });
}
