using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>How a request reached the server — the part the path does not show.</summary>
public enum TrafficOrigin
{
    /// <summary>A caller on this device: the app's own WebView, or a browser on the same machine.</summary>
    Device,

    /// <summary>Another machine, over the network.</summary>
    Network,

    /// <summary>Through a tunnel — the internet.</summary>
    Tunnel
}

/// <summary>Why a body does or does not have text behind it.</summary>
public enum TrafficBodyState
{
    /// <summary>There was no body.</summary>
    Empty,

    /// <summary>Text, all of it.</summary>
    Captured,

    /// <summary>Text, the front of it — the rest went past <see cref="TrafficRecorderOptions.MaxBodyBytes"/>.</summary>
    Truncated,

    /// <summary>Not text: an image, a wasm file, a download. Counted, not kept.</summary>
    Binary,

    /// <summary>Text, and deliberately not kept: <see cref="TrafficRecorderOptions.RedactRequestBody"/> said so.</summary>
    Redacted
}

/// <summary>One header, flattened — a header with several values is joined the way it went on the wire.</summary>
public sealed record TrafficHeader(string Name, string Value);

/// <summary>What was kept of one body. <see cref="ByteCount"/> is the real size whether or not the text was kept.</summary>
public sealed record TrafficBody(TrafficBodyState State, string? ContentType, long ByteCount, string? Text)
{
    public static readonly TrafficBody None = new(TrafficBodyState.Empty, null, 0, null);
}

/// <summary>One request and the response it produced, held in memory while the recorder keeps it.</summary>
public sealed class TrafficExchange
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedOn { get; init; }
    public required string Method { get; init; }

    /// <summary>The path, without the query.</summary>
    public required string Path { get; init; }

    /// <summary>The query including its leading '?', with redacted parameters' values replaced; null when there was none.</summary>
    public string? QueryString { get; init; }

    public required TrafficOrigin Origin { get; init; }

    /// <summary>The caller's address and port, or <c>tunnel</c>.</summary>
    public required string RemoteAddress { get; init; }

    public required IReadOnlyList<TrafficHeader> RequestHeaders { get; init; }
    public TrafficBody RequestBody { get; internal set; } = TrafficBody.None;

    public int StatusCode { get; internal set; }
    public IReadOnlyList<TrafficHeader> ResponseHeaders { get; internal set; } = [];
    public TrafficBody ResponseBody { get; internal set; } = TrafficBody.None;

    public TimeSpan Elapsed { get; internal set; }

    /// <summary>Set when the handler threw or the response could not be finished.</summary>
    public string? Error { get; internal set; }

    /// <summary>The path, with the query when there is one.</summary>
    public string Target => this.QueryString is { Length: > 0 } query ? this.Path + query : this.Path;
}

public sealed class TrafficRecorderOptions
{
    /// <summary>Whether recording is on as soon as the server starts. On by default; <see cref="TrafficRecorder.IsRecording"/> switches it later.</summary>
    public bool RecordOnStart { get; set; } = true;

    /// <summary>Exchanges kept. The oldest is dropped past this.</summary>
    public int MaxExchanges { get; set; } = 300;

    /// <summary>Text kept per body. Past this the body is marked truncated; its size is still counted in full.</summary>
    public int MaxBodyBytes { get; set; } = 128 * 1024;

    /// <summary>
    /// Headers whose values are never kept. By default the ones that carry credentials — among them the WebView's launch
    /// cookie. Clear it to see them while debugging authentication.
    /// </summary>
    public ISet<string> RedactedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie"
    };

    /// <summary>Query parameters whose values are never kept. By default <c>token</c> — the WebView's launch token — and <c>access_token</c>.</summary>
    public ISet<string> RedactedQueryParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token"
    };

    /// <summary>
    /// Request bodies not to keep, such as a login post carrying a password. The body is still read and handed on, and its
    /// size recorded; only its text is dropped.
    /// </summary>
    public Func<HttpContext, bool>? RedactRequestBody { get; set; }

    /// <summary>
    /// Requests not to record at all, such as a tool's own polling, which would otherwise push the page's traffic out of
    /// the window. A skipped request is passed straight through, as if recording were off.
    /// </summary>
    public Func<HttpContext, bool>? Skip { get; set; }

    internal void Validate()
    {
        if (this.MaxExchanges <= 0)
            throw new InvalidOperationException("TrafficRecorderOptions.MaxExchanges must be positive.");

        if (this.MaxBodyBytes < 0)
            throw new InvalidOperationException("TrafficRecorderOptions.MaxBodyBytes cannot be negative.");
    }
}

/// <summary>
/// Records every request the server answers, and the response, in memory only — a window onto a running server, not a
/// log. Added with <see cref="TrafficRecorderExtensions.AddTrafficRecorder"/>; read it through <see cref="Snapshot"/> and
/// <see cref="Changed"/>, or show it with <c>TrafficMonitorPage</c> in a MAUI app.
/// <para>
/// It sits first in the bridge server's pipeline, ahead of its own checks, so a request those turn away — a 401, a 403, a
/// 421 — is recorded too. While <see cref="IsRecording"/> is off it is a straight pass-through: nothing is allocated and
/// no body is buffered.
/// </para>
/// </summary>
public sealed class TrafficRecorder
{
    const string Redacted = "(redacted)";

    readonly List<TrafficExchange> exchanges = [];
    readonly TrafficRecorderOptions options;
    readonly TimeProvider time;
    readonly ILogger logger;
    volatile bool recording;

    public TrafficRecorder(TrafficRecorderOptions options, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.options = options;
        this.time = timeProvider ?? TimeProvider.System;
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TrafficRecorder>();
        this.recording = options.RecordOnStart;
    }

    public TrafficRecorderOptions Options => this.options;

    /// <summary>
    /// Whether requests are being recorded. Switching it off also throws away what was recorded: it is every header and
    /// body that crossed the server, and off should mean gone.
    /// </summary>
    public bool IsRecording
    {
        get => this.recording;
        set
        {
            if (this.recording == value)
                return;

            this.recording = value;

            if (!value)
                this.ClearCore();

            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised, on the thread that served the request, when an exchange completes, the list is cleared, or recording is switched.</summary>
    public event EventHandler? Changed;

    /// <summary>Everything held, newest first.</summary>
    /// <remarks>
    /// An exchange is added once the server has finished its request — after the last byte of the response has gone out,
    /// because the response body is captured as it is written. A client can therefore have the whole response before its
    /// exchange is here. To read an exchange straight after making the request, wait for it with <see cref="WaitForAsync"/>
    /// or <see cref="WaitUntilAsync"/>.
    /// </remarks>
    public IReadOnlyList<TrafficExchange> Snapshot()
    {
        lock (this.exchanges)
            return this.exchanges.ToArray();
    }

    /// <summary>
    /// Waits until what is held satisfies <paramref name="condition"/>, and returns it as it was then, newest first.
    /// Completes at once when it already does.
    /// </summary>
    /// <remarks>
    /// <paramref name="condition"/> is checked on the thread that finished the request, whenever <see cref="Changed"/> is
    /// raised; an exception from it fails this wait, never the request.
    /// </remarks>
    public async Task<IReadOnlyList<TrafficExchange>> WaitUntilAsync(
        Func<IReadOnlyList<TrafficExchange>, bool> condition,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(condition);

        var met = new TaskCompletionSource<IReadOnlyList<TrafficExchange>>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Check(object? sender, EventArgs args)
        {
            try
            {
                var held = this.Snapshot();
                if (condition(held))
                    met.TrySetResult(held);
            }
            catch (Exception ex)
            {
                met.TrySetException(ex);
            }
        }

        // Subscribed before the first look, so an exchange added between the two is not missed.
        this.Changed += Check;
        try
        {
            Check(null, EventArgs.Empty);
            return await met.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.Changed -= Check;
        }
    }

    /// <summary>Waits for an exchange matching <paramref name="match"/> and returns the newest such one.</summary>
    /// <remarks>See <see cref="WaitUntilAsync"/>.</remarks>
    public async Task<TrafficExchange> WaitForAsync(Func<TrafficExchange, bool> match, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);

        var held = await this.WaitUntilAsync(x => x.Any(match), cancellationToken).ConfigureAwait(false);
        return held.First(match);
    }

    public TrafficExchange? Find(string id)
    {
        lock (this.exchanges)
            return this.exchanges.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>Throws away everything recorded so far. Whether recording is on is unaffected.</summary>
    public void Clear()
    {
        if (this.ClearCore())
            this.Changed?.Invoke(this, EventArgs.Empty);
    }

    bool ClearCore()
    {
        lock (this.exchanges)
        {
            if (this.exchanges.Count == 0)
                return false;

            this.exchanges.Clear();
            return true;
        }
    }

    /// <summary>The middleware. A pass-through while <see cref="IsRecording"/> is off, and for what <see cref="TrafficRecorderOptions.Skip"/> skips.</summary>
    public ValueTask RecordAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return this.recording && this.options.Skip?.Invoke(context) != true ? this.RecordCoreAsync(context, next) : next(context);
    }

    async ValueTask RecordCoreAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var started = this.time.GetTimestamp();

        var exchange = new TrafficExchange
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedOn = this.time.GetUtcNow(),
            Method = request.Method,
            Path = request.Path,
            QueryString = this.RedactQuery(request.QueryString),
            Origin = OriginOf(context),
            RemoteAddress = Describe(context.Connection),
            RequestHeaders = this.Flatten(request.Headers)
        };

        if (request.HasBody)
            exchange.RequestBody = await this.CaptureRequestBodyAsync(context);

        var tee = new TeeBodyControl(context.Response.BodyControl, context.Response, this.options.MaxBodyBytes);
        context.Response.Bind(tee);

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            exchange.Error = ex.Message;
            throw;
        }
        finally
        {
            // The connection completes its own producer, not whatever the response ended up bound to, so anything still in
            // the tee's writer would never reach the wire.
            try
            {
                await tee.FinishAsync();
            }
            catch (Exception ex)
            {
                exchange.Error ??= ex.Message;
            }

            exchange.StatusCode = context.Response.StatusCode;
            exchange.ResponseHeaders = this.Flatten(context.Response.Headers);
            exchange.ResponseBody = tee.Capture;
            exchange.Elapsed = this.time.GetElapsedTime(started);

            this.Add(exchange);
        }
    }

    /// <summary>
    /// Reads the body once, hands the handler a rewound copy, and keeps the text when it is text. A body past the cap, or not
    /// text, is still read in full — its size is recorded — but only what fits is kept.
    /// </summary>
    async Task<TrafficBody> CaptureRequestBodyAsync(HttpContext context)
    {
        var request = context.Request;
        var contentType = request.ContentType;

        try
        {
            var buffered = new MemoryStream();
            await request.Body.CopyToAsync(buffered, context.RequestAborted);
            buffered.Position = 0;
            request.Body = buffered;

            var bytes = buffered.Length;
            if (bytes == 0)
                return TrafficBody.None;

            if (this.options.RedactRequestBody?.Invoke(context) == true)
                return new TrafficBody(TrafficBodyState.Redacted, contentType, bytes, null);

            if (!IsText(contentType))
                return new TrafficBody(TrafficBodyState.Binary, contentType, bytes, null);

            var kept = (int)Math.Min(bytes, this.options.MaxBodyBytes);
            return new TrafficBody(
                kept < bytes ? TrafficBodyState.Truncated : TrafficBodyState.Captured,
                contentType,
                bytes,
                Encoding.UTF8.GetString(buffered.GetBuffer(), 0, kept)
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A body that could not be recorded is no reason to fail the request it belongs to.
            this.logger.LogDebug(ex, "Could not record the request body for {Path}", request.Path);
            return TrafficBody.None;
        }
    }

    void Add(TrafficExchange exchange)
    {
        lock (this.exchanges)
        {
            // Switched off while this request was in flight: it belongs to what "off" threw away.
            if (!this.recording)
                return;

            // Newest first — the order it is read in, so nothing showing it has to sort.
            this.exchanges.Insert(0, exchange);

            var max = this.options.MaxExchanges;
            if (this.exchanges.Count > max)
                this.exchanges.RemoveRange(max, this.exchanges.Count - max);
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    static TrafficOrigin OriginOf(HttpContext context)
    {
        if (context.Connection.IsTunneled)
            return TrafficOrigin.Tunnel;

        return BridgeCallers.IsLocalConnection(context) ? TrafficOrigin.Device : TrafficOrigin.Network;
    }

    static string Describe(ConnectionInfo connection)
    {
        if (connection.IsTunneled)
            return "tunnel";

        return connection.RemoteIpAddress is { } ip ? $"{ip}:{connection.RemotePort}" : "unknown";
    }

    IReadOnlyList<TrafficHeader> Flatten(HeaderDictionary headers)
    {
        var list = new List<TrafficHeader>(headers.Count);
        foreach (var header in headers)
        {
            var value = this.options.RedactedHeaders.Contains(header.Key) ? Redacted : Join(header.Value);
            list.Add(new TrafficHeader(header.Key, value));
        }

        list.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    static string Join(StringValues values) => values.Count switch
    {
        0 => "",
        1 => values[0] ?? "",
        _ => String.Join(", ", values.ToArray())
    };

    /// <summary>The query as it arrived, with the value of every redacted parameter replaced.</summary>
    internal string? RedactQuery(string? query)
    {
        if (String.IsNullOrEmpty(query) || this.options.RedactedQueryParameters.Count == 0)
            return String.IsNullOrEmpty(query) ? null : query;

        var pairs = query.TrimStart('?').Split('&');
        var changed = false;

        for (var i = 0; i < pairs.Length; i++)
        {
            var equals = pairs[i].IndexOf('=');
            var name = Uri.UnescapeDataString((equals < 0 ? pairs[i] : pairs[i][..equals]).Replace('+', ' '));

            if (equals >= 0 && this.options.RedactedQueryParameters.Contains(name))
            {
                pairs[i] = pairs[i][..(equals + 1)] + Redacted;
                changed = true;
            }
        }

        return changed ? "?" + String.Join('&', pairs) : query;
    }

    /// <summary>Whether a body is worth keeping the text of.</summary>
    internal static bool IsText(string? contentType)
    {
        if (String.IsNullOrWhiteSpace(contentType))
            return false;

        var media = contentType.AsSpan();
        var semicolon = media.IndexOf(';');
        if (semicolon >= 0)
            media = media[..semicolon];

        media = media.Trim();

        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        // application/problem+json, image/svg+xml and every other json or xml under a more specific name.
        if (media.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var known in TextTypes)
        {
            if (media.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static readonly string[] TextTypes =
    [
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/ecmascript",
        "application/x-www-form-urlencoded",
        "application/graphql"
    ];

    /// <summary>
    /// Sits between the response and the connection, counting every byte and keeping the text ones.
    /// <para>
    /// Nothing has set a content type when the middleware runs, so the decision waits for the first byte or for the headers
    /// going out — the last moment before they leave and the first there is a type to read. <see cref="Writer"/> is built
    /// over <see cref="Stream"/> so both write paths meet in one place and no byte is counted twice or missed.
    /// </para>
    /// </summary>
    sealed class TeeBodyControl(IResponseBodyControl inner, HttpResponse response, int maxBodyBytes) : IResponseBodyControl
    {
        readonly MemoryStream captured = new();

        Stream? stream;
        PipeWriter? writer;
        string? contentType;
        bool decided;
        bool keeping;
        bool finished;
        long byteCount;

        public TrafficBody Capture
        {
            get
            {
                if (this.byteCount == 0)
                    return TrafficBody.None;

                if (!this.keeping)
                    return new TrafficBody(TrafficBodyState.Binary, this.contentType, this.byteCount, null);

                return new TrafficBody(
                    this.captured.Length < this.byteCount ? TrafficBodyState.Truncated : TrafficBodyState.Captured,
                    this.contentType,
                    this.byteCount,
                    Encoding.UTF8.GetString(this.captured.GetBuffer(), 0, (int)this.captured.Length)
                );
            }
        }

        public bool HasStarted => inner.HasStarted;

        public Stream Stream => this.stream ??= new TeeStream(this, inner.Stream);

        public PipeWriter Writer => this.writer ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            // A response that sends its headers before any body — a download, an event stream — settles it here.
            this.Decide();
            return inner.StartAsync(cancellationToken);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

        public async ValueTask FinishAsync()
        {
            if (this.finished)
                return;

            this.finished = true;

            if (this.writer is { } pending)
                await pending.FlushAsync(CancellationToken.None);
        }

        void Decide()
        {
            if (this.decided)
                return;

            this.decided = true;
            this.contentType = response.ContentType;
            this.keeping = IsText(this.contentType);
        }

        void Observe(ReadOnlySpan<byte> buffer)
        {
            this.Decide();
            this.byteCount += buffer.Length;

            if (!this.keeping)
                return;

            var room = maxBodyBytes - (int)this.captured.Length;
            if (room > 0)
                this.captured.Write(buffer[..Math.Min(room, buffer.Length)]);
        }

        /// <summary>Every body byte passes through here on its way to the connection.</summary>
        sealed class TeeStream(TeeBodyControl owner, Stream inner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => this.Write(new ReadOnlySpan<byte>(buffer, offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                owner.Observe(buffer);
                inner.Write(buffer);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => this.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.Observe(buffer.Span);
                return inner.WriteAsync(buffer, cancellationToken);
            }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}

public static class TrafficRecorderExtensions
{
    /// <summary>
    /// Records every request the server answers — bridges, the web app's files and the app's own endpoints — in memory, for
    /// <see cref="TrafficRecorder"/> to show. Meant for development: register it in a debug build.
    /// <code>
    /// services.AddShinyHttpServer(http =>
    /// {
    ///     http.AddAppDeviceBridge();
    /// #if DEBUG
    ///     http.AddTrafficRecorder(o => o.RedactRequestBody = ctx => ctx.Request.Path == "/api/login");
    /// #endif
    /// });
    /// </code>
    /// <para>
    /// Credentials are not kept: the values of <see cref="TrafficRecorderOptions.RedactedHeaders"/> and
    /// <see cref="TrafficRecorderOptions.RedactedQueryParameters"/> are replaced. Every call configures the same options.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddTrafficRecorder(this ShinyHttpServerBuilder http, Action<TrafficRecorderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.AddAppDeviceBridge();

        var services = http.Services;
        if (services.FirstOrDefault(x => x.ServiceType == typeof(TrafficRecorderOptions))?.ImplementationInstance is not TrafficRecorderOptions options)
        {
            options = new TrafficRecorderOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);

        services.TryAddSingleton(sp => new TrafficRecorder(
            sp.GetRequiredService<TrafficRecorderOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()
        ));

        return http;
    }
}
