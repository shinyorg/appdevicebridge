using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Logging;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Runs the web app's background.js for a native call when no page can take it.
/// <code>
/// // background.js — a classic script at the root of the web app
/// appdevicebridge.on("job:sync", async ({ name }) => {
///     const token = await (await fetch("/_bridge/settings/secure/token")).json();
///     const response = await fetch("https://api.example.com/sync", { headers: { Authorization: `Bearer ${token}` } });
///     await fetch("/_bridge/files/data/content?path=sync.json", { method: "PUT", body: await response.text() });
/// });
/// </code>
/// <para>
/// What the script gets is deliberately small: <c>appdevicebridge.on</c>, <c>console</c>, and <c>fetch</c> with
/// string bodies. Relative URLs go to the host's own bridge, exactly as they do from the page, so the settings
/// and files a handler uses are the same ones the page sees. There is no DOM, no timers, and no state kept
/// between calls — each call gets a fresh engine that runs the script's top level and then the handler, so the
/// top level should do nothing but register handlers.
/// </para>
/// <para>
/// Jint is an interpreter written in .NET: nothing to ship per platform, no JIT, and permitted on iOS. The
/// cost is speed, which background work of this size does not notice.
/// </para>
/// </summary>
sealed class WebAppScriptEngine
{
    const long MemoryLimitBytes = 64 * 1024 * 1024;

    readonly WebAppHostOptions options;
    readonly Func<WebAppHost> host;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);

    // The bridge answers only its own session cookie, which a cookie container would not send on our behalf.
    readonly HttpClient loopback = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    // Everything else goes through the platform's own handler, so proxies and certificate trust behave as the app's do.
    readonly HttpClient external = new() { Timeout = Timeout.InfiniteTimeSpan };

    LoadedScript? loaded;

    public WebAppScriptEngine(WebAppHostOptions options, Func<WebAppHost> host, ILogger logger)
    {
        this.options = options;
        this.host = host;
        this.logger = logger;
    }

    public async Task<WebAppInvocationResult> InvokeAsync(string handler, string payloadJson, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this.options.BackgroundScriptTimeout);

        try
        {
            // One run at a time: Jint engines are single-threaded, and handlers commonly share settings and files.
            await this.gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut();
        }

        try
        {
            var script = await this.LoadAsync(timeout.Token).ConfigureAwait(false);

            if (script is null)
                return WebAppInvocationResult.NotHandled;

            if (script.Error is { } error)
                return new WebAppInvocationResult(WebAppInvocationTarget.BackgroundScript, false, Error: $"{this.options.BackgroundScript} could not be loaded: {error}");

            if (!script.Handlers.Contains(handler))
                return WebAppInvocationResult.NotHandled;

            // Off the caller's thread: fetch blocks the engine thread while it waits.
            return await Task.Run(() => this.Run(script, handler, payloadJson, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut();
        }
        finally
        {
            this.gate.Release();
        }

        WebAppInvocationResult TimedOut() => new(
            WebAppInvocationTarget.BackgroundScript,
            false,
            Error: $"{this.options.BackgroundScript} did not finish within {this.options.BackgroundScriptTimeout}."
        );
    }

    /// <summary>
    /// Compiles the script once per build and learns which handlers it registers, by running its top level
    /// with <c>fetch</c> refused. A build without the script is cached as having no handlers.
    /// </summary>
    async Task<LoadedScript?> LoadAsync(CancellationToken cancellationToken)
    {
        var webHost = this.host();
        WebAppPackage? package = null;
        string? source;

        if (webHost.DevServer is not null)
        {
            // Development: read fresh from the dev server on every call, so an edit to the script applies at once.
            source = await webHost.ReadDevServerFileAsync(this.options.BackgroundScript, cancellationToken).ConfigureAwait(false);

            if (source is null)
                return new LoadedScript(null, default, new HashSet<string>(), null);
        }
        else
        {
            package = await webHost.EnsureActivatedAsync(cancellationToken).ConfigureAwait(false);

            if (package is null)
                return null;

            if (this.loaded is { } current && Equals(current.Package, package))
                return current;

            if (!webHost.Source.TryGetFile(this.options.BackgroundScript, out var file))
                return this.loaded = new LoadedScript(package, default, new HashSet<string>(), null);

            await using var stream = await file.Open(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            source = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var prepared = Engine.PrepareScript(source, this.options.BackgroundScript);
            var handlers = new HashSet<string>(StringComparer.Ordinal);

            var engine = this.CreateEngine(
                cancellationToken,
                handlers.Add,
                (_, _, _, _) => throw new InvalidOperationException("fetch is only available inside a handler.")
            );

            engine.Evaluate(prepared);

            this.logger.LogInformation("{Script} registers {Handlers}", this.options.BackgroundScript, String.Join(", ", handlers));
            return this.loaded = new LoadedScript(package, prepared, handlers, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ExecutionCanceledException)
        {
            this.logger.LogError(ex, "{Script} from {Source} could not be loaded", this.options.BackgroundScript, package?.Version.ToString() ?? "the dev server");
            return this.loaded = new LoadedScript(package, default, new HashSet<string>(), ex.Message);
        }
    }

    WebAppInvocationResult Run(LoadedScript script, string handler, string payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            var engine = this.CreateEngine(
                cancellationToken,
                _ => true,
                (url, method, headers, body) => this.Fetch(url, method, headers, body, cancellationToken)
            );

            engine.Evaluate(script.Prepared);

            var function = engine.Invoke("__appdevicebridgeHandler", handler);
            if (function.Type is Types.Undefined or Types.Null)
                return WebAppInvocationResult.NotHandled;

            var payload = new JsonParser(engine).Parse(payloadJson);
            var result = engine.Invoke(function, JsValue.Undefined, [payload]).UnwrapIfPromise(cancellationToken);

            string? resultJson = null;
            if (result.Type is not (Types.Undefined or Types.Null))
            {
                var serialized = new Jint.Native.Json.JsonSerializer(engine).Serialize(result);
                resultJson = serialized.Type == Types.String ? serialized.ToString() : null;
            }

            return new WebAppInvocationResult(WebAppInvocationTarget.BackgroundScript, true, resultJson);
        }
        catch (ExecutionCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "{Script} handler {Handler} failed", this.options.BackgroundScript, handler);
            return new WebAppInvocationResult(WebAppInvocationTarget.BackgroundScript, false, Error: ex.Message);
        }
    }

    Engine CreateEngine(CancellationToken cancellationToken, Func<string, bool> register, Func<string, string, string, string?, string> fetch)
    {
        var engine = new Engine(o => o
            .CancellationToken(cancellationToken)
            .LimitMemory(MemoryLimitBytes)
            .LimitRecursion(512)

            // Network and argument failures become JavaScript errors a handler can catch; anything else —
            // cancellation above all — still ends the run.
            .CatchClrExceptions(ex => ex is HttpRequestException or InvalidOperationException or ArgumentException or UriFormatException or JsonException)
        );

        // ClrFunction rather than SetValue(Delegate): the delegate overload binds its arguments by reflection,
        // which a trimmed device build cannot promise to keep.
        engine.SetValue("__appdevicebridgeRegister", new ClrFunction(engine, "__appdevicebridgeRegister", (_, args) =>
        {
            register(Arg(args, 0) ?? String.Empty);
            return JsValue.Undefined;
        }));

        engine.SetValue("__appdevicebridgeLog", new ClrFunction(engine, "__appdevicebridgeLog", (_, args) =>
        {
            this.Log(Arg(args, 0) ?? "info", Arg(args, 1) ?? String.Empty);
            return JsValue.Undefined;
        }));

        engine.SetValue("__appdevicebridgeFetch", new ClrFunction(engine, "__appdevicebridgeFetch", (_, args) =>
            JsString.Create(fetch(Arg(args, 0) ?? String.Empty, Arg(args, 1) ?? "GET", Arg(args, 2) ?? "{}", Arg(args, 3)))
        ));

        engine.Execute(HostScript, "appdevicebridge-host.js");

        return engine;
    }

    static string? Arg(JsValue[] args, int index)
        => index < args.Length && args[index].Type is not (Types.Undefined or Types.Null) ? args[index].ToString() : null;

    void Log(string level, string message)
    {
        var logLevel = level switch
        {
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information
        };

        this.logger.Log(logLevel, "{Script}: {Message}", this.options.BackgroundScript, message);
    }

    /// <summary>Returns <c>{ status, headers, body }</c> as JSON; the host script dresses it as a Response.</summary>
    string Fetch(string url, string method, string headersJson, string? body, CancellationToken cancellationToken)
    {
        var webHost = this.host();
        var toBridge = url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);

        Uri target;
        if (toBridge)
        {
            var origin = webHost.EnsureServerAsync(cancellationToken).GetAwaiter().GetResult();
            target = new Uri(origin, url);
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out target!) || target.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException($"fetch in background.js takes an absolute http(s) URL or a path on the bridge, not '{url}'.");
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), target);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8);

        using (var headers = JsonDocument.Parse(headersJson))
        {
            foreach (var header in headers.RootElement.EnumerateObject())
            {
                var value = header.Value.ToString();

                if (String.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                else if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                    request.Content?.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        if (toBridge)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", $"{WebAppSession.CookieName}={webHost.Session.Token}");
        }

        var client = toBridge ? this.loopback : this.external;

        // Blocking is deliberate: this runs on the engine's own worker thread, and a promise resolved later from
        // another thread is exactly what a single-threaded engine cannot take.
        using var response = client.SendAsync(request, cancellationToken).GetAwaiter().GetResult();
        var text = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status", (int)response.StatusCode);
            writer.WriteStartObject("headers");

            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                writer.WriteString(name.ToLowerInvariant(), String.Join(", ", values));

            writer.WriteEndObject();
            writer.WriteString("body", text);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    sealed record LoadedScript(WebAppPackage? Package, Prepared<Script> Prepared, IReadOnlySet<string> Handlers, string? Error);

    const string HostScript = """
        (function (global) {
            const handlers = Object.create(null);

            global.appdevicebridge = Object.freeze({
                on(name, handler) {
                    if (typeof name !== "string" || typeof handler !== "function")
                        throw new TypeError("appdevicebridge.on(name, handler) takes a string and a function");

                    handlers[name] = handler;
                    __appdevicebridgeRegister(name);
                }
            });

            global.__appdevicebridgeHandler = name => handlers[name];

            const write = level => (...args) => __appdevicebridgeLog(level, args.map(a => typeof a === "string" ? a : JSON.stringify(a)).join(" "));
            global.console = { log: write("info"), info: write("info"), debug: write("debug"), warn: write("warn"), error: write("error") };

            global.fetch = (input, init) => {
                try {
                    const options = init || {};
                    if (options.body !== undefined && options.body !== null && typeof options.body !== "string")
                        throw new TypeError("fetch in background.js sends string bodies; use JSON.stringify");

                    const raw = JSON.parse(__appdevicebridgeFetch(
                        String(input),
                        String(options.method || "GET").toUpperCase(),
                        JSON.stringify(options.headers || {}),
                        options.body ?? null
                    ));

                    return Promise.resolve({
                        url: String(input),
                        status: raw.status,
                        ok: raw.status >= 200 && raw.status < 300,
                        headers: {
                            get: name => raw.headers[String(name).toLowerCase()] ?? null,
                            has: name => String(name).toLowerCase() in raw.headers
                        },
                        text: () => Promise.resolve(raw.body),
                        json: () => Promise.resolve(JSON.parse(raw.body))
                    });
                } catch (error) {
                    return Promise.reject(error);
                }
            };
        })(globalThis);
        """;
}
