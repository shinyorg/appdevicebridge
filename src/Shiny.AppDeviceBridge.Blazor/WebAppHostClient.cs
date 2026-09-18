using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Blazor;

public static class WebAppHostClientExtensions
{
    /// <summary>
    /// Registers the page's side of the bridge: the transport every typed client sends through, native events, native
    /// calls, and <see cref="WebAppBridge"/> for endpoints of your own. Add a typed client for each bridge you use:
    /// <code>
    /// builder.Services
    ///     .AddWebAppHostClient()
    ///     .AddCalendarBridgeClient()
    ///     .AddWifiBridgeClient();
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppHostClient(this IServiceCollection services)
    {
        services.AddScoped<IBridgeTransport, BlazorBridgeTransport>();
        services.AddScoped<WebAppBridge>();
        services.AddScoped<WebAppEvents>();
        services.AddScoped<WebAppNativeCalls>();

        // The built-in bridges are always there, so their clients are too.
        services.AddHostBridgeClient();
        services.AddSettingsBridgeClient();
        services.AddFilesBridgeClient();
        services.AddLinksBridgeClient();
        return services;
    }
}

/// <summary>
/// The page's transport: requests over its own origin, so the host's session cookie goes along with every call, and
/// native events from the host's one shared event stream.
/// </summary>
public sealed class BlazorBridgeTransport(NavigationManager navigation, WebAppEvents events) : IBridgeTransport
{
    HttpClient? http;
    Task<string>? prefix;

    HttpClient Http => this.http ??= new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { IsAbsoluteUri: false } relative)
            request.RequestUri = new Uri(await this.PrefixAsync() + relative.OriginalString.TrimStart('/'), UriKind.Relative);

        return await this.Http.SendAsync(request, cancellationToken);
    }

    public Task<IAsyncDisposable> SubscribeAsync(string eventName, Func<string, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return events.OnAsync(eventName, payload => handler(payload.GetRawText()));
    }

    /// <summary>
    /// Where this host mounted the bridges, asked once and remembered. The web app updates on its own schedule and the
    /// native host on another, so the prefix is discovered rather than agreed in advance — a page built against one
    /// host keeps working when the next one moves it.
    /// </summary>
    Task<string> PrefixAsync() => this.prefix ??= this.LoadPrefixAsync();

    async Task<string> LoadPrefixAsync()
    {
        try
        {
            var paths = await this.Http.GetFromJsonAsync("_host/config", ClientJsonContext.Default.HostPaths);
            if (paths?.Bridge is { Length: > 0 } bridge)
                return bridge.EndsWith('/') ? bridge : bridge + "/";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // An older host that does not answer /_host/config has not moved anything either.
        }

        return "_bridge/";
    }
}

/// <summary>
/// Untyped calls, for endpoints that have no typed client — ones your own app added. Every bridge this library ships
/// has a typed client (<c>ICalendarBridge</c>, <c>IWifiBridge</c>, …); use that instead.
/// <code>
/// var stats = await bridge.GetAsync("reports/stats", MyJson.Default.Stats);
/// </code>
/// A failure throws <see cref="BridgeException"/>.
/// </summary>
public sealed class WebAppBridge(IBridgeTransport transport)
{
    /// <summary>A GET whose response is read through your own source-generated metadata. A 204 is default.</summary>
    public Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        => BridgeCalls.SendAsync(transport, HttpMethod.Get, path, null, typeInfo, cancellationToken);

    /// <summary>A body sent and a response read, both through your own source-generated metadata.</summary>
    public Task<TResult> SendAsync<TBody, TResult>(
        HttpMethod method,
        string path,
        TBody body,
        JsonTypeInfo<TBody> bodyType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken = default
    ) => BridgeCalls.SendAsync(transport, method, path, BridgeCalls.Json(body, bodyType), resultType, cancellationToken);

    /// <summary>Any method and content, answered with the successful response for you to read and dispose.</summary>
    public Task<HttpResponseMessage> SendForResponseAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default)
        => BridgeCalls.SendForResponseAsync(transport, method, path, content, cancellationToken);

    /// <summary>A body sent, with nothing to read back.</summary>
    public Task SendAsync<TBody>(HttpMethod method, string path, TBody body, JsonTypeInfo<TBody> bodyType, CancellationToken cancellationToken = default)
        => BridgeCalls.SendAsync(transport, method, path, BridgeCalls.Json(body, bodyType), cancellationToken);

    /// <summary>No body, and a response read through your own source-generated metadata. A 204 is default.</summary>
    public Task<TResult> SendAsync<TResult>(HttpMethod method, string path, JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken = default)
        => BridgeCalls.SendAsync(transport, method, path, null, resultType, cancellationToken);
}

/// <summary>
/// Native events from the host's one event stream, delivered to C#.
/// <code>
/// await using var subscription = await events.OnAsync("gps.reading", reading => position = reading);
/// </code>
/// Handlers run on the renderer's context; a component still calls <c>StateHasChanged</c> itself.
/// </summary>
public sealed class WebAppEvents(IJSRuntime js) : IAsyncDisposable
{
    readonly Dictionary<string, List<Func<JsonElement, Task>>> handlers = new(StringComparer.Ordinal);

    // How the script tells this instance's listeners apart. Not the DotNetObjectReference: Blazor hands the script a new
    // object for it on every call, so the script could never recognise it again to remove it.
    readonly string key = Guid.NewGuid().ToString("N");
    DotNetObjectReference<WebAppEvents>? self;
    IJSObjectReference? module;

    public async Task<IAsyncDisposable> OnAsync(string eventName, Func<JsonElement, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        var script = await this.ModuleAsync();

        if (!this.handlers.TryGetValue(eventName, out var list))
        {
            this.handlers[eventName] = list = [];
            await script.InvokeVoidAsync("subscribe", this.self ??= DotNetObjectReference.Create(this), this.key, eventName);
        }

        list.Add(handler);

        return new Subscription(async () =>
        {
            list.Remove(handler);

            if (list.Count == 0 && this.handlers.Remove(eventName))
                await script.InvokeVoidAsync("unsubscribe", this.key, eventName);
        });
    }

    public Task<IAsyncDisposable> OnAsync(string eventName, Action<JsonElement> handler)
        => this.OnAsync(eventName, e =>
        {
            handler(e);
            return Task.CompletedTask;
        });

    [JSInvokable]
    public async Task OnEvent(string eventName, string data)
    {
        if (!this.handlers.TryGetValue(eventName, out var list))
            return;

        using var document = JsonDocument.Parse(data);
        var payload = document.RootElement.Clone();

        foreach (var handler in list.ToArray())
            await handler(payload);
    }

    async ValueTask<IJSObjectReference> ModuleAsync() => this.module ??= await js.InvokeAsync<IJSObjectReference>("import", WebAppScript.Path);

    public async ValueTask DisposeAsync()
    {
        if (this.module is not null)
        {
            try
            {
                await this.module.InvokeVoidAsync("unsubscribeAll", this.key);
                await this.module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        this.self?.Dispose();
    }
}

/// <summary>
/// Handles native calls — <c>job:{name}</c>, <c>gps</c>, <c>geofence</c>, <c>push.received</c> — in C# while the page
/// is open. When it is not, the host runs the same names in background.js instead. Payloads are each bridge's contracts,
/// read through their source-generated metadata:
/// <code>
/// await nativeCalls.HandleAsync("gps", LocationsJsonContext.Default.GpsReading, reading => SaveAsync(reading));
///
/// await nativeCalls.HandleAsync("job:sync", AppDeviceBridgeJsonContext.Default.JobRun, MyJson.Default.SyncResult, async job =>
/// {
///     await SyncAsync();
///     return new SyncResult(RanIn: "page");
/// });
/// </code>
/// <para>
/// The call is accepted before the handler runs, so returning takes as long as the work does; the host allows
/// <c>PageInvocationTimeout</c> (25 seconds by default). A thrown exception is reported to the host as a failure.
/// </para>
/// </summary>
public sealed class WebAppNativeCalls(IJSRuntime js) : IAsyncDisposable
{
    readonly Dictionary<string, Func<string, Task<string?>>> handlers = new(StringComparer.Ordinal);
    DotNetObjectReference<WebAppNativeCalls>? self;
    IJSObjectReference? module;

    /// <summary>A call whose result goes back to the host — what a job reports, for one.</summary>
    public Task<IAsyncDisposable> HandleAsync<TPayload, TResult>(
        string name,
        JsonTypeInfo<TPayload> payloadType,
        JsonTypeInfo<TResult> resultType,
        Func<TPayload, Task<TResult>> handler
    )
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(handler);

        return this.RegisterAsync(name, async json =>
        {
            var result = await handler(JsonSerializer.Deserialize(json, payloadType)!);
            return JsonSerializer.Serialize(result, resultType);
        });
    }

    /// <summary>A call with nothing to report back.</summary>
    public Task<IAsyncDisposable> HandleAsync<TPayload>(string name, JsonTypeInfo<TPayload> payloadType, Func<TPayload, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(handler);

        return this.RegisterAsync(name, async json =>
        {
            await handler(JsonSerializer.Deserialize(json, payloadType)!);
            return null;
        });
    }

    async Task<IAsyncDisposable> RegisterAsync(string name, Func<string, Task<string?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var script = await this.ModuleAsync();
        var isNew = !this.handlers.ContainsKey(name);
        this.handlers[name] = handler;

        if (isNew)
            await script.InvokeVoidAsync("handle", this.self ??= DotNetObjectReference.Create(this), name);

        return new Subscription(async () =>
        {
            if (this.handlers.Remove(name))
                await script.InvokeVoidAsync("unhandle", name);
        });
    }

    [JSInvokable]
    public Task<string?> OnCall(string name, string payloadJson)
        => this.handlers.TryGetValue(name, out var handler)
            ? handler(payloadJson)
            : throw new InvalidOperationException($"No handler for '{name}'.");

    async ValueTask<IJSObjectReference> ModuleAsync() => this.module ??= await js.InvokeAsync<IJSObjectReference>("import", WebAppScript.Path);

    public async ValueTask DisposeAsync()
    {
        if (this.module is not null)
        {
            try
            {
                foreach (var name in this.handlers.Keys)
                    await this.module.InvokeVoidAsync("unhandle", name);

                await this.module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        this.handlers.Clear();
        this.self?.Dispose();
    }
}

public static class JsonElementExtensions
{
    /// <summary>
    /// Indented JSON for showing a result to a person: "Café", not "Caf\u00E9". Shown as text, never put into a script or
    /// markup unencoded, so the relaxed escaping is safe here.
    /// </summary>
    public static string ToIndentedJson(this JsonElement? value)
    {
        if (value is not { } element)
            return "(nothing)";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            element.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

static class WebAppScript
{
    public const string Path = "./_content/Shiny.AppDeviceBridge.Blazor/appdevicebridge.js";
}

sealed class Subscription(Func<ValueTask> dispose) : IAsyncDisposable
{
    int disposed;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref this.disposed, 1) == 0 ? dispose() : ValueTask.CompletedTask;
}

/// <summary>
/// The answer to <c>_host/config</c>. Declared here rather than taken from Shiny.AppDeviceBridge, because this
/// package deliberately references nothing — it ships inside a trimmed WebAssembly app.
/// </summary>
sealed record HostPaths(string? Base, string? Bridge);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HostPaths))]
partial class ClientJsonContext : JsonSerializerContext;
