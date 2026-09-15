using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Shiny.WebAppHost.Blazor;

public static class WebAppHostClientExtensions
{
    /// <summary>
    /// Registers <see cref="WebAppBridge"/>, <see cref="WebAppEvents"/> and <see cref="WebAppNativeCalls"/>.
    /// <code>
    /// builder.Services.AddWebAppHostClient();
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppHostClient(this IServiceCollection services)
    {
        services.AddScoped<WebAppBridge>();
        services.AddScoped<WebAppEvents>();
        services.AddScoped<WebAppNativeCalls>();
        return services;
    }
}

/// <summary>A bridge call the host refused. <see cref="Code"/> is the stable error code it sent, when it sent one.</summary>
public sealed class WebAppBridgeException(int statusCode, string? code, string message) : Exception(message)
{
    public int StatusCode => statusCode;

    public string? Code => code;
}

/// <summary>
/// Calls the host's <c>/_bridge</c> endpoints. Paths are relative to <c>/_bridge/</c>:
/// <code>
/// var info = await bridge.GetAsync("app/info");
/// await bridge.PostAsync("gps/listener", new JsonObject { ["backgroundMode"] = "Standard" });
/// </code>
/// <para>
/// Results come back as <see cref="JsonElement"/> — or as your own type through a <see cref="JsonTypeInfo{T}"/>,
/// which keeps a trimmed publish honest — and bodies go out as <see cref="JsonNode"/>, which needs no
/// serialization metadata at all. A 204 is null. Anything unsuccessful throws <see cref="WebAppBridgeException"/>.
/// </para>
/// </summary>
public sealed class WebAppBridge(NavigationManager navigation)
{
    HttpClient? http;

    // Same origin as the page, so the host's session cookie goes along with every call.
    HttpClient Http => this.http ??= new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };

    public Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken = default)
        => this.SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonElement?> PostAsync(string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        => this.SendAsync(HttpMethod.Post, path, JsonContent(body ?? new JsonObject()), cancellationToken);

    public Task<JsonElement?> PutAsync(string path, JsonNode? body, CancellationToken cancellationToken = default)
        => this.SendAsync(HttpMethod.Put, path, JsonContent(body), cancellationToken);

    public Task<JsonElement?> DeleteAsync(string path, CancellationToken cancellationToken = default)
        => this.SendAsync(HttpMethod.Delete, path, null, cancellationToken);

    public async Task<T?> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        using var response = await this.Http.GetAsync(Url(path), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return response.StatusCode == HttpStatusCode.NoContent
            ? default
            : await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
    }

    /// <summary>A body as text — a file's contents, say. Throws for anything unsuccessful, 404 included.</summary>
    public async Task<string> GetStringAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await this.Http.GetAsync(Url(path), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Any method and body: raw JSON for a setting, text or bytes for a file.</summary>
    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, Url(path)) { Content = content };
        using var response = await this.Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(text, ClientJsonContext.Default.String);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return document.RootElement.Clone();
    }

    static StringContent? JsonContent(JsonNode? body)
        => body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    static string Url(string path) => "_bridge/" + path.TrimStart('/');

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        string? code = null;

        try
        {
            if (await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.BridgeError, cancellationToken) is { Message: { } text } error)
            {
                message = text;
                code = error.Code;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not a bridge error body — a 404 from outside any bridge, for instance.
        }

        throw new WebAppBridgeException((int)response.StatusCode, code, message);
    }
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
            await script.InvokeVoidAsync("subscribe", this.self ??= DotNetObjectReference.Create(this), eventName);
        }

        list.Add(handler);

        return new Subscription(async () =>
        {
            list.Remove(handler);

            if (list.Count == 0 && this.handlers.Remove(eventName))
                await script.InvokeVoidAsync("unsubscribe", this.self, eventName);
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
                await this.module.InvokeVoidAsync("unsubscribeAll", this.self);
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
/// is open. When it is not, the host runs the same names in background.js instead.
/// <code>
/// await nativeCalls.HandleAsync("job:sync", async payload =>
/// {
///     await SyncAsync();
///     return new JsonObject { ["ranIn"] = "page" };
/// });
/// </code>
/// <para>
/// The call is accepted before the handler runs, so returning takes as long as the work does; the host allows
/// <c>PageInvocationTimeout</c> (25 seconds by default). A thrown exception is reported to the host as a failure.
/// </para>
/// </summary>
public sealed class WebAppNativeCalls(IJSRuntime js) : IAsyncDisposable
{
    readonly Dictionary<string, Func<JsonElement, Task<JsonNode?>>> handlers = new(StringComparer.Ordinal);
    DotNetObjectReference<WebAppNativeCalls>? self;
    IJSObjectReference? module;

    public async Task<IAsyncDisposable> HandleAsync(string name, Func<JsonElement, Task<JsonNode?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

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

    public Task<IAsyncDisposable> HandleAsync(string name, Func<JsonElement, Task> handler)
        => this.HandleAsync(name, async payload =>
        {
            await handler(payload);
            return (JsonNode?)null;
        });

    [JSInvokable]
    public async Task<string?> OnCall(string name, string payloadJson)
    {
        if (!this.handlers.TryGetValue(name, out var handler))
            throw new InvalidOperationException($"No handler for '{name}'.");

        using var document = JsonDocument.Parse(payloadJson);
        var result = await handler(document.RootElement.Clone());

        return result?.ToJsonString();
    }

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
    /// <summary>Indented JSON for showing a result to a person.</summary>
    public static string ToIndentedJson(this JsonElement? value)
    {
        if (value is not { } element)
            return "(nothing)";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            element.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

static class WebAppScript
{
    public const string Path = "./_content/Shiny.WebAppHost.Blazor/webapphost.js";
}

sealed class Subscription(Func<ValueTask> dispose) : IAsyncDisposable
{
    int disposed;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref this.disposed, 1) == 0 ? dispose() : ValueTask.CompletedTask;
}

sealed record BridgeError(string? Code, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(string))]
partial class ClientJsonContext : JsonSerializerContext;
