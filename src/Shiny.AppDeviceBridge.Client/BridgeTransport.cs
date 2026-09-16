using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// How a client reaches the host. The page-side package supplies one — Shiny.AppDeviceBridge.Blazor's sends over the
/// page's own origin, so the session cookie goes with every call, and listens on the host's single event stream.
/// </summary>
public interface IBridgeTransport
{
    /// <summary>Sends a request whose URI is relative to the bridge prefix — <c>calendar/events/42</c>.</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>Delivers each occurrence of a native event, as its JSON payload, until the subscription is disposed.</summary>
    Task<IAsyncDisposable> SubscribeAsync(string eventName, Func<string, Task> handler);
}

/// <summary>A call the host refused. <see cref="Code"/> is the stable code it sent — <c>not_supported</c>, <c>access_denied</c> — when it sent one.</summary>
public sealed class BridgeException(HttpStatusCode statusCode, string? code, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Code { get; } = code;

    /// <summary>The bridge exists but this platform has no implementation of it: HTTP 501.</summary>
    public bool IsNotSupported => this.StatusCode == HttpStatusCode.NotImplemented;
}

/// <summary>
/// What generated clients call. Public so a hand-written client for a bridge of your own can use the same plumbing,
/// but you will not normally call it yourself.
/// </summary>
public static class BridgeCalls
{
    /// <summary>The metadata for <typeparamref name="T"/> from a source-generated context — no reflection involved.</summary>
    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerContext context)
        => context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
           ?? throw new InvalidOperationException($"{context.GetType().Name} has no metadata for {typeof(T)}. Add [JsonSerializable(typeof({typeof(T).Name}))] to it.");

    public static async Task SendAsync(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(transport, method, path, content, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> SendAsync<T>(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> result, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(transport, method, path, content, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return default!;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return (await JsonSerializer.DeserializeAsync(stream, result, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>A response body the caller reads and disposes — a file, a photo.</summary>
    public static async Task<Stream> SendForStreamAsync(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(transport, method, path, content, cancellationToken).ConfigureAwait(false);
        return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public static async Task<byte[]> SendForBytesAsync(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(transport, method, path, content, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public static HttpContent Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    public static HttpContent Raw(Stream stream, string contentType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    public static HttpContent Raw(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    public static HttpContent Raw(string text, string contentType)
        => Raw(System.Text.Encoding.UTF8.GetBytes(text), contentType);

    public static Task<IAsyncDisposable> SubscribeAsync<T>(IBridgeTransport transport, string eventName, Func<T, Task> handler, JsonTypeInfo<T> payload)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return transport.SubscribeAsync(eventName, json => handler(JsonSerializer.Deserialize(json, payload)!));
    }

    /// <summary>A route value, escaped as one path segment.</summary>
    public static string Segment(string? value) => Uri.EscapeDataString(value ?? String.Empty);

    /// <summary>
    /// The response itself, once it is known to be a success — for a caller that wants its headers or reads its body its
    /// own way. The caller disposes it. A failure throws <see cref="BridgeException"/> as every other call does.
    /// </summary>
    public static Task<HttpResponseMessage> SendForResponseAsync(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        => SendCoreAsync(transport, method, path, content, cancellationToken);

    static async Task<HttpResponseMessage> SendCoreAsync(IBridgeTransport transport, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
            throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    static async Task<BridgeException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        var message = $"The bridge answered {(int)response.StatusCode} {response.ReasonPhrase}.";

        try
        {
            if (response.Content.Headers.ContentType?.MediaType == "application/json"
                && await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } body
                && JsonSerializer.Deserialize(body, BridgeClientJsonContext.Default.BridgeErrorBody) is { } error)
            {
                code = error.Code;
                message = error.Message ?? message;
            }
        }
        catch (JsonException)
        {
            // Not a bridge error — a 404 from outside any bridge, say. The status line says enough.
        }

        return new BridgeException(response.StatusCode, code, message);
    }

    /// <summary>Keeps the response alive for as long as its body is being read.</summary>
    sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Query string construction for generated clients: invariant culture, ISO 8601 dates, enums by name, nulls left out.</summary>
public sealed class BridgeQuery
{
    readonly List<string> parts = [];

    public BridgeQuery Add(string name, string? value)
    {
        if (value is not null)
            this.parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");

        return this;
    }

    public BridgeQuery Add(string name, bool? value) => this.Add(name, value is { } v ? (v ? "true" : "false") : null);

    public BridgeQuery Add(string name, int? value) => this.Add(name, value?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery Add(string name, long? value) => this.Add(name, value?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery Add(string name, double? value) => this.Add(name, value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery Add(string name, DateTimeOffset? value) => this.Add(name, value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery Add(string name, DateTime? value) => this.Add(name, value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery Add(string name, Guid? value) => this.Add(name, value?.ToString());

    public BridgeQuery Add(string name, TimeSpan? value) => this.Add(name, value?.ToString("c", System.Globalization.CultureInfo.InvariantCulture));

    public BridgeQuery AddEnum<T>(string name, T? value) where T : struct, Enum => this.Add(name, value?.ToString());

    public override string ToString() => this.parts.Count == 0 ? String.Empty : "?" + String.Join("&", this.parts);
}

/// <summary>The body every bridge sends with a failure.</summary>
public sealed record BridgeErrorBody(string? Code, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BridgeErrorBody))]
partial class BridgeClientJsonContext : JsonSerializerContext;
