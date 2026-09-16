// The runtime every generated bridge client calls through. Hand-written; the bridge modules beside it are generated.

/** How a client reaches the host. `browserTransport()` is the one a page served by the host uses. */
export interface BridgeTransport {
    /** Sends a request whose path is relative to the bridge prefix, e.g. `calendar/events/42`. */
    send(method: string, path: string, init?: BridgeRequestInit): Promise<Response>;

    /** Delivers each occurrence of a native event as its JSON text. Returns a function that stops delivery. */
    subscribe(eventName: string, handler: (json: string) => void): () => void;
}

export interface BridgeRequestInit {
    json?: unknown;
    body?: Blob | ArrayBuffer | Uint8Array | string;
    contentType?: string;
    signal?: AbortSignal;
}

/** A call the host refused. `code` is the stable code it sent — `not_supported`, `access_denied` — when it sent one. */
export class BridgeError extends Error {
    constructor(
        readonly status: number,
        readonly code: string | undefined,
        message: string
    ) {
        super(message);
        this.name = "BridgeError";
    }

    /** The bridge exists but this platform has no implementation of it: HTTP 501. */
    get isNotSupported(): boolean {
        return this.status === 501;
    }
}

let shared: BridgeTransport | undefined;

/**
 * The page's transport: requests over its own origin, so the host's session cookie goes with every call, and native
 * events from one shared EventSource. The bridge prefix is discovered from `_host/config` rather than assumed, so a page
 * keeps working when the host mounts the bridges somewhere other than `/_bridge`.
 */
export function browserTransport(): BridgeTransport {
    return (shared ??= createBrowserTransport());
}

function createBrowserTransport(): BridgeTransport {
    let prefix: Promise<string> | undefined;
    let source: EventSource | undefined;
    const listeners = new Map<string, Set<(json: string) => void>>();

    const bridgePrefix = () =>
        (prefix ??= fetch(new URL("_host/config", document.baseURI), { credentials: "same-origin" })
            .then(r => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
            .then((p: { bridge: string }) => (p.bridge.endsWith("/") ? p.bridge : p.bridge + "/"))
            .catch(() => "/_bridge/"));

    return {
        async send(method, path, init) {
            const headers: Record<string, string> = {};
            let body: BodyInit | undefined;

            if (init?.json !== undefined) {
                headers["Content-Type"] = "application/json";
                body = JSON.stringify(init.json);
            } else if (init?.body !== undefined) {
                headers["Content-Type"] = init.contentType ?? "application/octet-stream";
                body = init.body as BodyInit;
            }

            return fetch((await bridgePrefix()) + path.replace(/^\//, ""), {
                method,
                headers,
                body,
                credentials: "same-origin",
                signal: init?.signal
            });
        },

        subscribe(eventName, handler) {
            let handlers = listeners.get(eventName);

            if (!handlers) {
                handlers = new Set();
                listeners.set(eventName, handlers);

                // One DOM listener per event name, added once the prefix is known; handlers come and go behind it.
                void bridgePrefix().then(p => {
                    source ??= new EventSource(p + "events");
                    source.addEventListener(eventName, e => {
                        for (const h of listeners.get(eventName) ?? [])
                            h((e as MessageEvent<string>).data);
                    });
                });
            }

            handlers.add(handler);
            return () => handlers!.delete(handler);
        }
    };
}

async function ensureSuccess(response: Response): Promise<Response> {
    if (response.ok)
        return response;

    let code: string | undefined;
    let message = `The bridge answered ${response.status} ${response.statusText}.`;

    if (response.headers.get("Content-Type")?.startsWith("application/json")) {
        try {
            const error = (await response.json()) as { code?: string; message?: string };
            code = error.code;
            message = error.message ?? message;
        } catch {
            // Not a bridge error; the status line says enough.
        }
    }

    throw new BridgeError(response.status, code, message);
}

export async function call<T>(transport: BridgeTransport, method: string, path: string, init?: BridgeRequestInit): Promise<T> {
    const response = await ensureSuccess(await transport.send(method, path, init));
    return (response.status === 204 ? null : await response.json()) as T;
}

export async function callVoid(transport: BridgeTransport, method: string, path: string, init?: BridgeRequestInit): Promise<void> {
    await ensureSuccess(await transport.send(method, path, init));
}

export async function callBlob(transport: BridgeTransport, method: string, path: string, init?: BridgeRequestInit): Promise<Blob> {
    return (await ensureSuccess(await transport.send(method, path, init))).blob();
}

/** A route value, escaped as one path segment. */
export function segment(value: string | number | boolean): string {
    return encodeURIComponent(String(value));
}

/** A date as the host reads it: ISO 8601. */
export function dateText(value: Date | string | null | undefined): string | undefined {
    return value instanceof Date ? value.toISOString() : (value ?? undefined);
}

/** A query string with null and undefined left out, or empty when nothing is left. */
export function query(values: Record<string, string | number | boolean | null | undefined>): string {
    const parts = Object.entries(values)
        .filter(([, v]) => v !== null && v !== undefined)
        .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);

    return parts.length ? "?" + parts.join("&") : "";
}
