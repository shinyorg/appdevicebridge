using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.Http;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost.Bridge.HttpTransfers;

public sealed class WebAppTransferOptions
{
    /// <summary>
    /// Hands finished transfers to the web app's <c>transfer.completed</c> and <c>transfer.failed</c> handlers —
    /// in the page if it is listening, in background.js otherwise. On by default: the page queued the transfer,
    /// so the page is who needs to know, even after the app was suspended.
    /// </summary>
    public bool DispatchToWebApp { get; set; } = true;

    /// <summary>
    /// Registers Shiny's transfer service. Turn off to call <c>AddHttpTransfers</c> yourself, with your own
    /// delegate; the bridge's delegate is added alongside it, and Shiny runs both.
    /// </summary>
    public bool RegisterTransferService { get; set; } = true;

    /// <summary>The most transfers the web app can have queued at once. A page bug should not fill the device.</summary>
    public int MaxTransfers { get; set; } = 32;

    /// <summary>The shortest gap between two <c>transfer.progress</c> events for the same transfer.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether the page may transfer to or from a URL. Loopback is refused by default: a native download is not
    /// bound by CORS, so it would otherwise read other apps' local servers into a file the page can open.
    /// </summary>
    public Func<Uri, bool> AllowUrl { get; set; } = uri => !uri.IsLoopback;

    /// <summary>Android's foreground service has to show a notification while transfers run.</summary>
    public string AndroidNotificationTitle { get; set; } = "Transferring files";

    public string? AndroidNotificationText { get; set; }
}

public static class HttpTransfersBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/transfers</c> and registers Shiny's background transfer service for the platform — there
    /// is nothing else to call.
    /// <code>
    /// builder.AddHttpTransfersBridge();
    /// builder.AddHttpTransfersBridge(o => o.AllowUrl = uri => uri.Host == "api.example.com");
    /// </code>
    /// <para>
    /// Platform setup: <c>FOREGROUND_SERVICE</c> and <c>FOREGROUND_SERVICE_DATA_SYNC</c> on Android. On iOS and
    /// Mac Catalyst, forward <c>HandleEventsForBackgroundUrl</c> from the app delegate to Shiny so transfers that
    /// finish while the app is not running are delivered.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddHttpTransfersBridge(this MauiAppBuilder builder, Action<WebAppTransferOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WebAppTransferOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();

        if (options.RegisterTransferService)
            builder.Services.AddHttpTransfers<WebAppTransferDelegate>();
        else
            AddDelegate(builder.Services);
#elif MACOS
        // Shiny.Net.Http has no macOS build, so the net10.0 one's managed loop runs over Shiny.Core's connectivity.
        builder.Services.EnsureShinyCore();

        if (options.RegisterTransferService)
        {
            builder.Services.AddConnectivity();
            builder.Services.AddHttpClientTransfers<WebAppTransferDelegate>();
        }
        else
        {
            AddDelegate(builder.Services);
        }
#elif TRANSFERS_LINUX
        if (OperatingSystem.IsLinux())
        {
            if (options.RegisterTransferService)
            {
                global::Shiny.LinuxHttpServiceCollectionExtensions.AddConnectivity(builder.Services);
                builder.Services.AddHttpClientTransfers<WebAppTransferDelegate>();
            }
            else
            {
                AddDelegate(builder.Services);
            }
        }
#endif

        builder.Services.AddWebAppBridge<HttpTransfersBridge>();
        return builder;
    }

    static void AddDelegate(IServiceCollection services)
    {
        if (!services.Any(x => x.ServiceType == typeof(IHttpTransferDelegate) && x.ImplementationType == typeof(WebAppTransferDelegate)))
            services.AddSingleton<IHttpTransferDelegate, WebAppTransferDelegate>();
    }
}

/// <summary>
/// <c>/_bridge/transfers</c> over <see cref="IHttpTransferManager"/>. Only the web app's own transfers are listed
/// or cancelled; the native app's are left alone.
/// <code>
/// GET    /_bridge/transfers                { "transfers": [ … ] }
/// POST   /_bridge/transfers                { "type": "Download", "url": "https://…", "root": "cache", "path": "a/b.zip" }
///                                          { "type": "UploadMultipart" | "UploadRaw", "url", "root", "path", "method",
///                                            "headers": { }, "useMeteredConnection": true, "formDataName": "file",
///                                            "body": { "content", "contentType", "formDataName" } }   201
/// DELETE /_bridge/transfers                cancels every web app transfer
/// GET    /_bridge/transfers/{id}
/// DELETE /_bridge/transfers/{id}
/// POST   /_bridge/transfers/{id}/pause
/// POST   /_bridge/transfers/{id}/resume
///
/// events:   transfer.progress, transfer.completed, transfer.failed, transfer.cancelled
/// handlers: transfer.completed, transfer.failed
/// </code>
/// <para>
/// A download is written beside its destination under a hidden name and moved into place when it completes, so
/// the page never reads a partial file at the path it asked for.
/// </para>
/// </summary>
public sealed class HttpTransfersBridge : IWebAppBridge, IDisposable
{
    const int MaxHeaders = 32;
    const int MaxHeaderLength = 8 * 1024;
    const int MaxBodyLength = 1024 * 1024;

    static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "Expect",
        "TE", "Trailer", "Proxy-Authorization", "Proxy-Connection",

        // Shiny's retry bookkeeping rides in the headers and is parsed as a number.
        "ErrorRetries", "AuthRetries"
    };

    readonly IHttpTransferManager? manager;
    readonly WebAppEventHub events;
    readonly WebAppTransferOptions options;
    readonly WebAppTransferScope scope;
    readonly ConcurrentDictionary<string, (long Ticks, HttpTransferState Status)> lastProgress = new();

    public HttpTransfersBridge(IServiceProvider services, WebAppHostOptions hostOptions, WebAppEventHub events)
    {
        this.manager = services.GetOptionalService<IHttpTransferManager>();
        this.options = services.GetOptionalService<WebAppTransferOptions>() ?? new WebAppTransferOptions();
        this.scope = new WebAppTransferScope(hostOptions);
        this.events = events;

        if (this.manager is not null)
            this.manager.UpdateReceived += this.OnUpdateReceived;
    }

    public string Name => "transfers";

    public bool IsSupported => this.manager is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.ListAsync)
        .MapPost("", this.CreateAsync)
        .MapDelete("", this.CancelAllAsync)
        .MapGet("/{id}", ctx => this.WithTransfer(ctx, this.GetAsync))
        .MapDelete("/{id}", ctx => this.WithTransfer(ctx, this.CancelAsync))
        .MapPost("/{id}/pause", ctx => this.WithTransfer(ctx, this.PauseAsync))
        .MapPost("/{id}/resume", ctx => this.WithTransfer(ctx, this.ResumeAsync));

    async ValueTask ListAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "HTTP transfers");
            return;
        }

        var transfers = await this.GetOwnedAsync(m);
        await WebAppBridgeResults.Json(
            context,
            new WebAppTransferList([.. transfers.Select(x => this.scope.Describe(x.Id, x.Transfer))]),
            TransfersBridgeJsonContext.Default.WebAppTransferList
        );
    }

    async ValueTask CreateAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "HTTP transfers");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, TransfersBridgeJsonContext.Default.WebAppTransferCreate);
        if (body?.Type is not { } type)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"type\": \"Download\" | \"UploadMultipart\" | \"UploadRaw\", \"url\", \"root\", \"path\" }.");
            return;
        }

        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected an absolute http or https \"url\".");
            return;
        }

        if (!this.options.AllowUrl(uri))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "url_not_allowed", "The app does not allow transfers to or from this URL.");
            return;
        }

        if (this.scope.FindRoot(body.Root) is not { } root)
        {
            await WebAppBridgeResults.NotFound(context, $"No file root '{body.Root}'.");
            return;
        }

        if (root.Resolve(body.Path) is not { } fullPath || root.IsRoot(fullPath) || Directory.Exists(fullPath))
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_path",
                "\"path\" must name a file inside the root: relative, using '/', and without '..', '\\' or ':'."
            );
            return;
        }

        if (ValidateRequest(body, type) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        var owned = await this.GetOwnedAsync(m);
        if (owned.Count >= this.options.MaxTransfers)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status429TooManyRequests, "too_many_transfers", $"The web app already has {owned.Count} transfers queued.");
            return;
        }

        var id = Guid.NewGuid().ToString("n");
        string localPath;

        if (type == TransferType.Download)
        {
            if (!body.Overwrite && File.Exists(fullPath))
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "exists", "The destination exists and \"overwrite\" is false.");
                return;
            }

            // Both managers expect the directory to be there: the managed loop refuses to queue without it.
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            localPath = WebAppTransferScope.StagingPath(fullPath, id);
        }
        else
        {
            if (!File.Exists(fullPath))
            {
                await WebAppBridgeResults.NotFound(context, "The file to upload does not exist.");
                return;
            }

            localPath = fullPath;
        }

        var request = new HttpTransferRequest(
            this.scope.ToIdentifier(id, body.Overwrite),
            uri.AbsoluteUri,
            type,
            localPath,
            body.UseMeteredConnection,
            body.Body is { } content
                ? new TransferHttpContent(content.Content, content.ContentType ?? "text/plain") { ContentFormDataName = content.FormDataName }
                : null,
            body.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) : null
        )
        {
            HttpMethod = body.Method?.ToUpperInvariant(),
            FileFormDataName = body.FormDataName ?? "file"
        };

        HttpTransfer transfer;
        try
        {
            transfer = await m.Queue(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "queue_failed", ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(
            context,
            this.scope.Describe(id, transfer),
            TransfersBridgeJsonContext.Default.WebAppTransferInfo,
            StatusCodes.Status201Created
        );
    }

    async ValueTask CancelAllAsync(HttpContext context)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "HTTP transfers");
            return;
        }

        // Not CancelAll: that would take the native app's transfers down with the page's.
        foreach (var (_, transfer) in await this.GetOwnedAsync(m))
            await m.Cancel(transfer.Identifier);

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask GetAsync(HttpContext context, IHttpTransferManager m, string id, HttpTransfer transfer)
        => WebAppBridgeResults.Json(context, this.scope.Describe(id, transfer), TransfersBridgeJsonContext.Default.WebAppTransferInfo);

    async ValueTask CancelAsync(HttpContext context, IHttpTransferManager m, string id, HttpTransfer transfer)
    {
        await m.Cancel(transfer.Identifier);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask PauseAsync(HttpContext context, IHttpTransferManager m, string id, HttpTransfer transfer)
    {
        await m.Pause(transfer.Identifier);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask ResumeAsync(HttpContext context, IHttpTransferManager m, string id, HttpTransfer transfer)
    {
        await m.Resume(transfer.Identifier);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask WithTransfer(HttpContext context, Func<HttpContext, IHttpTransferManager, string, HttpTransfer, ValueTask> action)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "HTTP transfers");
            return;
        }

        var id = context.Request.RouteValues["id"] ?? String.Empty;
        var transfer = WebAppTransferScope.IsValidId(id)
            ? (await this.GetOwnedAsync(m)).FirstOrDefault(x => x.Id == id).Transfer
            : null;

        if (transfer is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No transfer '{id}'.");
            return;
        }

        await action(context, m, id, transfer);
    }

    async Task<List<(string Id, HttpTransfer Transfer)>> GetOwnedAsync(IHttpTransferManager m)
    {
        var owned = new List<(string, HttpTransfer)>();

        foreach (var transfer in await m.GetTransfers())
        {
            if (this.scope.TryGetId(transfer.Identifier, out var id))
                owned.Add((id, transfer));
        }

        return owned;
    }

    static string? ValidateRequest(WebAppTransferCreate body, TransferType type)
    {
        if (body.Method is { } method && method.ToUpperInvariant() is not ("GET" or "POST" or "PUT" or "PATCH"))
            return "\"method\" must be GET, POST, PUT or PATCH.";

        if (body.Headers is { } headers)
        {
            if (headers.Count > MaxHeaders)
                return $"At most {MaxHeaders} headers.";

            foreach (var (name, value) in headers)
            {
                if (!IsToken(name) || ForbiddenHeaders.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    return $"Header '{name}' is not allowed.";

                if (value is null || value.Length > MaxHeaderLength || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                    return $"Header '{name}' has an invalid value.";
            }
        }

        if (body.FormDataName is { } formName && !IsFormName(formName))
            return "\"formDataName\" must be 1–128 characters without quotes or line breaks.";

        if (body.Body is { } content)
        {
            if (type != TransferType.UploadMultipart)
                return "\"body\" is only sent with UploadMultipart.";

            if (content.Content is null || content.Content.Length > MaxBodyLength)
                return $"\"body.content\" is required and at most {MaxBodyLength} characters.";

            if (content.ContentType is { } contentType && !MediaTypeHeaderValue.TryParse(contentType, out _))
                return "\"body.contentType\" is not a valid media type.";

            if (content.FormDataName is { } contentName && !IsFormName(contentName))
                return "\"body.formDataName\" must be 1–128 characters without quotes or line breaks.";
        }

        return null;
    }

    // RFC 9110 token characters.
    static bool IsToken(string? value)
        => !String.IsNullOrEmpty(value)
           && value.Length <= 128
           && value.All(c => Char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    static bool IsFormName(string value)
        => value.Length is > 0 and <= 128 && value.AsSpan().IndexOfAny("\"\r\n\0") < 0;

    void OnUpdateReceived(object? sender, HttpTransferResult result)
    {
        if (!this.scope.TryGetId(result.Request.Identifier, out var id))
            return;

        switch (result.Status)
        {
            case HttpTransferState.Completed or HttpTransferState.Error:
                // Reported by the delegate, once the file is in place or the failure is known.
                this.lastProgress.TryRemove(id, out _);
                break;

            case HttpTransferState.Canceled:
                this.lastProgress.TryRemove(id, out _);
                this.events.Publish(
                    "transfer.cancelled",
                    this.scope.Describe(id, result.Request, result.Status, result.Progress),
                    TransfersBridgeJsonContext.Default.WebAppTransferInfo
                );
                break;

            default:
                var next = (Environment.TickCount64, result.Status);

                // A state change always goes out; progress within a state is throttled.
                if (this.lastProgress.TryGetValue(id, out var last))
                {
                    if (last.Status == result.Status && next.Item1 - last.Ticks < (long)this.options.ProgressInterval.TotalMilliseconds)
                        return;

                    if (!this.lastProgress.TryUpdate(id, next, last))
                        return;
                }
                else if (!this.lastProgress.TryAdd(id, next))
                {
                    return;
                }

                this.events.Publish(
                    "transfer.progress",
                    this.scope.Describe(id, result.Request, result.Status, result.Progress),
                    TransfersBridgeJsonContext.Default.WebAppTransferInfo
                );
                break;
        }
    }

    public void Dispose()
    {
        if (this.manager is not null)
            this.manager.UpdateReceived -= this.OnUpdateReceived;
    }
}

/// <summary>
/// Moves finished downloads into place, and tells the web app — <c>transfer.completed</c> and
/// <c>transfer.failed</c>, as events for a listening page and as handler calls that reach background.js when there
/// is none. Transfers the native app queued are ignored. Subclass to decide per transfer.
/// </summary>
public partial class WebAppTransferDelegate(
    WebAppHostOptions hostOptions,
    WebAppTransferOptions options,
    WebAppEventHub events,
    WebAppInvoker invoker
) : IHttpTransferDelegate
{
    // A field, not the parameter: the Android half of this partial class reads it, and primary constructor
    // parameters are only in scope in the declaration that has them.
    protected WebAppTransferOptions Options { get; } = options;

    readonly WebAppTransferScope scope = new(hostOptions);

    public virtual async Task OnCompleted(HttpTransferRequest request)
    {
        if (!this.scope.TryGetId(request.Identifier, out var id))
            return;

        var info = this.scope.Describe(id, request, HttpTransferState.Completed, TransferProgress.Empty);

        if (request.Type == TransferType.Download && this.scope.GetDownloadTarget(request, id) is { } target)
        {
            var failure = MoveIntoPlace(request.LocalFilePath, target);

            if (failure is not null)
            {
                await this.HandleAsync("transfer.failed", info with { Status = HttpTransferState.Error, Error = failure });
                return;
            }
        }

        await this.HandleAsync("transfer.completed", info);
    }

    public virtual async Task OnError(HttpTransferRequest request, int statusCode, Exception ex)
    {
        if (!this.scope.TryGetId(request.Identifier, out var id))
            return;

        if (request.Type == TransferType.Download)
            TryDelete(request.LocalFilePath);

        var info = this.scope.Describe(id, request, HttpTransferState.Error, TransferProgress.Empty) with
        {
            StatusCode = statusCode == 0 ? null : statusCode,
            Error = ex.Message
        };

        await this.HandleAsync("transfer.failed", info);
    }

    /// <summary>Whether a finished transfer goes to the web app's handlers. <see cref="WebAppTransferOptions.DispatchToWebApp"/> by default.</summary>
    protected virtual bool ShouldDispatch(string handler, WebAppTransferInfo transfer) => this.Options.DispatchToWebApp;

    async Task HandleAsync(string name, WebAppTransferInfo info)
    {
        events.Publish(name, info, TransfersBridgeJsonContext.Default.WebAppTransferInfo);

        if (this.ShouldDispatch(name, info))
            await invoker.InvokeAsync(name, info, TransfersBridgeJsonContext.Default.WebAppTransferInfo);
    }

    static string? MoveIntoPlace(string stagingPath, WebAppTransferScope.DownloadTarget target)
    {
        try
        {
            // Resolved again rather than trusted from queue time: a link placed along the path since then must
            // not carry the file out of the root.
            if (target.Root.Resolve(target.Path) is not { } destination || Directory.Exists(destination))
            {
                TryDelete(stagingPath);
                return "The destination is no longer a valid path in its root.";
            }

            if (!target.Overwrite && File.Exists(destination))
            {
                TryDelete(stagingPath);
                return "The destination exists and overwrite was false.";
            }

            File.Move(stagingPath, destination, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(stagingPath);
            return ex.Message;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial file the OS will not let go of is not worth failing the callback over.
        }
    }
}

#if ANDROID
public partial class WebAppTransferDelegate : IAndroidForegroundServiceDelegate
{
    public virtual void Configure(AndroidX.Core.App.NotificationCompat.Builder builder)
    {
        builder.SetContentTitle(this.Options.AndroidNotificationTitle);

        if (this.Options.AndroidNotificationText is { } text)
            builder.SetContentText(text);
    }
}
#endif

/// <summary>
/// What the bridge and the delegate agree on: which Shiny transfers are the web app's, where their files live, and
/// how they look to the page.
/// </summary>
sealed class WebAppTransferScope
{
    const string StagingSuffix = ".transfer";

    // Overwrite is carried in the identifier because nothing else about a request survives an app restart except
    // what Shiny persists, and the headers are sent to the server.
    const string KeepMarker = "k";

    readonly string prefix;
    readonly IReadOnlyList<WebAppFileRoot> roots;

    public WebAppTransferScope(WebAppHostOptions options)
    {
        // Not ':' — Shiny's file repository names files after identifiers, and Windows refuses the colon.
        this.prefix = $"webapp-{options.AppId}-";
        this.roots = options.ResolveFileRoots();
    }

    public sealed record DownloadTarget(WebAppFileRoot Root, string Path, bool Overwrite);

    public string ToIdentifier(string id, bool overwrite) => overwrite ? this.prefix + id : this.prefix + KeepMarker + id;

    public bool TryGetId(string? identifier, out string id)
    {
        id = String.Empty;

        if (identifier is null || !identifier.StartsWith(this.prefix, StringComparison.Ordinal))
            return false;

        var rest = identifier[this.prefix.Length..];
        if (rest.StartsWith(KeepMarker, StringComparison.Ordinal) && rest.Length == 33)
            rest = rest[1..];

        if (!IsValidId(rest))
            return false;

        id = rest;
        return true;
    }

    public static bool IsValidId(string id) => id.Length == 32 && id.All(Char.IsAsciiHexDigitLower);

    public WebAppFileRoot? FindRoot(string? name)
        => this.roots.FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A hidden sibling of the destination, unique to the transfer so two downloads to one path do not collide.</summary>
    public static string StagingPath(string destination, string id)
        => Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{id}{StagingSuffix}");

    public DownloadTarget? GetDownloadTarget(HttpTransferRequest request, string id)
    {
        var suffix = $".{id}{StagingSuffix}";
        var name = Path.GetFileName(request.LocalFilePath);

        if (!name.StartsWith('.') || !name.EndsWith(suffix, StringComparison.Ordinal) || name.Length <= suffix.Length + 1)
            return null;

        var destination = Path.Combine(Path.GetDirectoryName(request.LocalFilePath)!, name[1..^suffix.Length]);
        var overwrite = !request.Identifier.EndsWith(KeepMarker + id, StringComparison.Ordinal);

        return this.Locate(destination) is { } located ? new DownloadTarget(located.Root, located.Path, overwrite) : null;
    }

    public WebAppTransferInfo Describe(string id, HttpTransfer transfer)
        => this.Describe(id, transfer.Request, transfer.Status, new TransferProgress(0, transfer.BytesToTransfer, transfer.BytesTransferred), transfer.CreatedAt);

    public WebAppTransferInfo Describe(string id, HttpTransferRequest request, HttpTransferState status, TransferProgress progress, DateTimeOffset? createdAt = null)
    {
        var file = request.Type == TransferType.Download && this.GetDownloadTarget(request, id) is { } target
            ? (target.Root, target.Path)
            : this.Locate(request.LocalFilePath);

        double? percent = status == HttpTransferState.Completed
            ? 1
            : progress.BytesToTransfer is > 0 and var total
                ? Math.Round(progress.BytesTransferred / (double)total, 4)
                : null;

        return new WebAppTransferInfo(
            id,
            request.Type,
            request.Uri,
            file?.Root.Name,
            file?.Path,
            status,
            progress.BytesTransferred,
            progress.BytesToTransfer,
            progress.BytesPerSecond > 0 ? progress.BytesPerSecond : null,
            percent,
            createdAt
        );
    }

    (WebAppFileRoot Root, string Path)? Locate(string fullPath)
    {
        foreach (var root in this.roots)
        {
            var relative = root.ToRelative(fullPath);

            // ToRelative climbs out with '..' for a path in another root; Resolve refuses that, and a link.
            if (root.Resolve(relative) is { } resolved && String.Equals(resolved, fullPath, StringComparison.Ordinal))
                return (root, relative);
        }

        return null;
    }
}

public sealed record WebAppTransferCreate(
    TransferType? Type,
    string? Url,
    string? Root,
    string? Path,
    string? Method = null,
    Dictionary<string, string>? Headers = null,
    bool UseMeteredConnection = true,
    string? FormDataName = null,
    WebAppTransferBody? Body = null,
    bool Overwrite = true
);

public sealed record WebAppTransferBody(string Content, string? ContentType = null, string? FormDataName = null);

public sealed record WebAppTransferInfo(
    string Id,
    TransferType Type,
    string Url,
    string? Root,
    string? Path,
    HttpTransferState Status,
    long BytesTransferred,
    long? BytesToTransfer,
    long? BytesPerSecond,
    double? PercentComplete,
    DateTimeOffset? CreatedAt,
    int? StatusCode = null,
    string? Error = null
);

public sealed record WebAppTransferList(IReadOnlyList<WebAppTransferInfo> Transfers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WebAppTransferCreate))]
[JsonSerializable(typeof(WebAppTransferInfo))]
[JsonSerializable(typeof(WebAppTransferList))]
partial class TransfersBridgeJsonContext : JsonSerializerContext;
