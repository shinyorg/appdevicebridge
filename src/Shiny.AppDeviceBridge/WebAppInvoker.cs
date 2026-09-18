using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

public enum WebAppInvocationTarget
{
    /// <summary>Nothing handles the call: the page is not listening for it and background.js does not register it.</summary>
    None,

    Page,
    BackgroundScript
}

/// <param name="ResultJson">What the handler returned, as JSON. Null for undefined or nothing.</param>
public sealed record WebAppInvocationResult(
    WebAppInvocationTarget Target,
    bool Succeeded,
    string? ResultJson = null,
    string? Error = null
)
{
    public bool Handled => this.Target != WebAppInvocationTarget.None;

    public static WebAppInvocationResult NotHandled { get; } = new(WebAppInvocationTarget.None, false);
}

/// <summary>
/// Takes a native call when no page can: the WebView host runs the web app's background.js. Without one registered, a
/// call no page takes is reported as not handled.
/// </summary>
public interface IWebAppBackgroundInvoker
{
    Task<WebAppInvocationResult> InvokeAsync(string handler, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// Calls into the web app from native code — a background job, a GPS reading, a geofence transition, a
/// push — wherever the web app currently is.
/// <code>
/// await invoker.InvokeAsync("geofence", new { identifier, state }, MyJson.Default.GeofencePayload, ct);
/// </code>
/// <para>
/// <b>The page first</b>, when it is open, listening, and has declared a handler for the name. The call goes
/// out on the event stream; the page must accept it within <see cref="AppDeviceBridgeOptions.PageAcceptTimeout"/>
/// and then post its result. <b>Otherwise</b> the <see cref="IWebAppBackgroundInvoker"/> takes it — background.js,
/// with the WebView host — which is also what happens when the page does not accept in time, since a WebView that is
/// present but suspended by the OS looks just like a listening one until it fails to answer.
/// </para>
/// <para>
/// The acceptance step is what keeps a call from running twice. Once the host gives up on the page the call
/// is marked expired, and a late accept is refused — so the page never starts work background.js has already
/// done. A page that accepted and then runs out of time is reported as a failure, not retried elsewhere.
/// </para>
/// <para>
/// The page side is <c>/_bridge/invoke/client.js</c>:
/// <code>
/// import { on } from "/_bridge/invoke/client.js";
/// on("job:sync", async ({ name }) => { … });
/// </code>
/// </para>
/// </summary>
public sealed class WebAppInvoker : IWebAppBridge
{
    public const string InvokeEventName = "host.invoke";

    readonly AppDeviceBridgeOptions options;
    readonly WebAppEventSource<WebAppInvokeEvent> calls = new();
    readonly Func<IWebAppBackgroundInvoker?> background;
    readonly ILogger logger;
    readonly Lock gate = new();
    readonly ConcurrentDictionary<string, PendingInvocation> pending = new(StringComparer.Ordinal);
    HashSet<string> pageHandlers = new(StringComparer.Ordinal);

    /// <param name="background">
    /// What takes a call the page cannot — background.js, with the WebView host. Resolved per call, and lazily, because
    /// it commonly depends on the server that maps this bridge.
    /// </param>
    public WebAppInvoker(AppDeviceBridgeOptions options, Func<IWebAppBackgroundInvoker?>? background = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.background = background ?? (() => null);
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebAppInvoker>();
    }

    public string Name => "invoke";

    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes)
    {
        // Baked once with this host's mount points, because the script the page imports has to reach the
        // bridges wherever they actually are.
        var script = ClientScript.Replace("{bridge}", routes.BridgePrefix, StringComparison.Ordinal);

        routes
            .MapEvent(InvokeEventName, ct => this.calls.ListenAsync(this.OnListenerStopped, ct), WebAppInvokeJsonContext.Default.WebAppInvokeEvent)
            .MapGet("/client.js", context => Results.Text(script, "text/javascript").ExecuteAsync(context))
            .MapPut("/handlers", this.DeclareHandlersAsync)
            .MapPost("/{id}/accept", this.AcceptAsync)
            .MapPost("/{id}", this.CompleteAsync);
    }

    public Task<WebAppInvocationResult> InvokeAsync<T>(string handler, T payload, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return this.InvokeAsync(handler, JsonSerializer.Serialize(payload, typeInfo), cancellationToken);
    }

    /// <param name="handler">The name the web app registered, such as <c>job:sync</c> or <c>gps</c>.</param>
    /// <param name="payloadJson">The argument the handler receives, as JSON.</param>
    public async Task<WebAppInvocationResult> InvokeAsync(string handler, string payloadJson, CancellationToken cancellationToken = default)
    {
        if (!IsValidHandlerName(handler))
            throw new ArgumentException($"'{handler}' is not a valid handler name.", nameof(handler));

        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (this.IsPageHandling(handler)
            && await this.InvokePageAsync(handler, payloadJson, cancellationToken).ConfigureAwait(false) is { } fromPage)
            return fromPage;

        return this.background() is { } fallback
            ? await fallback.InvokeAsync(handler, payloadJson, cancellationToken).ConfigureAwait(false)
            : WebAppInvocationResult.NotHandled;
    }

    bool IsPageHandling(string handler)
    {
        if (!this.calls.HasListeners)
            return false;

        lock (this.gate)
            return this.pageHandlers.Contains(handler);
    }

    /// <summary>Null when the page did not accept in time, meaning the call is free to run elsewhere.</summary>
    async Task<WebAppInvocationResult?> InvokePageAsync(string handler, string payloadJson, CancellationToken cancellationToken)
    {
        var invocation = new PendingInvocation();
        var id = Guid.NewGuid().ToString("n");
        this.pending[id] = invocation;

        try
        {
            this.calls.Publish(this.BuildInvokeEvent(id, handler, payloadJson));

            using (var acceptWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                acceptWindow.CancelAfter(this.options.PageAcceptTimeout);

                try
                {
                    await invocation.Accepted.Task.WaitAsync(acceptWindow.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Lost the race only if the page accepted in the same instant — then it owns the call.
                    if (invocation.TryExpire())
                    {
                        this.logger.LogDebug("The page did not accept {Handler} in time; running background.js", handler);
                        return null;
                    }
                }
            }

            using var resultWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resultWindow.CancelAfter(this.options.PageInvocationTimeout);

            try
            {
                return await invocation.Completed.Task.WaitAsync(resultWindow.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WebAppInvocationResult(WebAppInvocationTarget.Page, false, Error: "The page accepted the call but did not finish in time.");
            }
        }
        finally
        {
            this.pending.TryRemove(id, out _);
        }
    }

    async ValueTask DeclareHandlersAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, WebAppInvokeJsonContext.Default.WebAppHandlerDeclaration);

        if (body?.Handlers is not { } names || names.Any(x => !IsValidHandlerName(x)))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"handlers\": [\"job:sync\", \"gps\"] }.");
            return;
        }

        lock (this.gate)
            this.pageHandlers = new HashSet<string>(names, StringComparer.Ordinal);

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask AcceptAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["id"] ?? String.Empty;

        return this.pending.TryGetValue(id, out var invocation) && invocation.TryAccept()
            ? WebAppBridgeResults.NoContent(context)
            : Gone(context);
    }

    async ValueTask CompleteAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["id"] ?? String.Empty;

        if (!this.pending.TryGetValue(id, out var invocation))
        {
            await Gone(context);
            return;
        }

        if (!invocation.IsAccepted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_accepted", "Accept the call before completing it.");
            return;
        }

        var reply = await WebAppBridgeResults.ReadBodyAsync(context, WebAppInvokeJsonContext.Default.WebAppInvocationReply);
        if (reply is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"ok\": true, \"result\": … } or { \"ok\": false, \"error\": \"…\" }.");
            return;
        }

        var result = reply.Result is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? value.GetRawText() : null;
        invocation.Completed.TrySetResult(new WebAppInvocationResult(WebAppInvocationTarget.Page, reply.Ok, result, reply.Ok ? null : reply.Error ?? "The handler failed."));

        await WebAppBridgeResults.NoContent(context);
    }

    static ValueTask Gone(HttpContext context)
        => WebAppBridgeResults.Error(context, 410, "gone", "The call expired or was handled elsewhere.");

    /// <summary>A page's handlers belong to its connection; with none left, nothing is listening for calls.</summary>
    void OnListenerStopped(int remaining)
    {
        if (remaining > 0)
            return;

        lock (this.gate)
            this.pageHandlers = new HashSet<string>(StringComparer.Ordinal);
    }

    WebAppInvokeEvent BuildInvokeEvent(string id, string handler, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return new WebAppInvokeEvent(id, handler, payload.RootElement.Clone(), (long)this.options.PageAcceptTimeout.TotalMilliseconds);
    }

    internal static bool IsValidHandlerName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 128
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '-');

    sealed class PendingInvocation
    {
        const int Waiting = 0;
        const int AcceptedState = 1;
        const int Expired = 2;

        int state;

        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WebAppInvocationResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAccepted => Volatile.Read(ref this.state) == AcceptedState;

        public bool TryAccept()
        {
            if (Interlocked.CompareExchange(ref this.state, AcceptedState, Waiting) != Waiting)
                return false;

            this.Accepted.TrySetResult();
            return true;
        }

        public bool TryExpire() => Interlocked.CompareExchange(ref this.state, Expired, Waiting) == Waiting;
    }

    const string ClientScript = """
        // Shiny.AppDeviceBridge: answer native calls — background jobs, GPS, geofences, push — from the page.
        //
        //   import { on } from "{bridge}/invoke/client.js";
        //   const off = on("job:sync", async ({ name }) => { ... });
        //
        // While the page is open and listening, calls come here. Otherwise the host runs background.js, which
        // registers the same names with appdevicebridge.on(name, fn).

        const handlers = new Map();
        let events;
        let declaring;

        export function on(name, handler) {
            if (typeof handler !== "function")
                throw new TypeError("handler must be a function");

            handlers.set(name, handler);
            connect();
            declare();

            return () => {
                handlers.delete(name);
                declare();
            };
        }

        function connect() {
            if (events)
                return;

            events = new EventSource("{bridge}/events?topics=host.invoke");

            // The host forgets a page's handlers when its last stream closes, so every connection declares them again.
            events.addEventListener("open", declare);
            events.addEventListener("host.invoke", e => run(JSON.parse(e.data)));
        }

        function declare() {
            // Several on() calls in a row become one request.
            declaring ??= Promise.resolve().then(() => {
                declaring = undefined;
                return fetch("{bridge}/invoke/handlers", {
                    method: "PUT",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ handlers: [...handlers.keys()] })
                });
            });
        }

        async function run({ id, handler, payload }) {
            const fn = handlers.get(handler);
            if (!fn)
                return;

            // Accept before doing anything. A refusal means the host stopped waiting and ran background.js,
            // and doing the work here as well would do it twice.
            const accepted = await fetch(`{bridge}/invoke/${id}/accept`, { method: "POST" });
            if (accepted.status !== 204)
                return;

            let reply;
            try {
                const result = await fn(payload);
                reply = { ok: true, result: result === undefined ? null : result };
            } catch (error) {
                reply = { ok: false, error: String(error?.message ?? error) };
            }

            await fetch(`{bridge}/invoke/${id}`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(reply)
            });
        }
        """;
}

public sealed record WebAppHandlerDeclaration(List<string>? Handlers);

/// <summary>A native call offered to the page, as the <c>host.invoke</c> event.</summary>
/// <param name="AcceptWithinMs">How long the page has to accept before the host runs background.js instead.</param>
public sealed record WebAppInvokeEvent(string Id, string Handler, JsonElement Payload, long AcceptWithinMs);

public sealed record WebAppInvocationReply(bool Ok, JsonElement? Result = null, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WebAppHandlerDeclaration))]
[JsonSerializable(typeof(WebAppInvocationReply))]
[JsonSerializable(typeof(WebAppInvokeEvent))]
partial class WebAppInvokeJsonContext : JsonSerializerContext;
