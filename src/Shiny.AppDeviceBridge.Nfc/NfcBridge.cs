using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Nfc.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Nfc;

namespace Shiny.AppDeviceBridge.Nfc;

public static class NfcBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/nfc</c> and registers Shiny's <c>INfcManager</c> — there is nothing else to call in code.
    /// <code>
    /// bridge.AddNfcBridge();
    /// </code>
    /// <para>
    /// Android needs <c>android.permission.NFC</c> in the manifest. iOS needs <c>NFCReaderUsageDescription</c> and the
    /// <c>com.apple.developer.nfc.readersession.formats</c> entitlement with <c>TAG</c>. Windows, macOS and Linux read
    /// through a PC/SC reader (Linux: <c>pcscd</c>). Mac Catalyst answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddNfcBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        bridge.Services.AddNfc();
        bridge.AddBridge<NfcBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/nfc</c> over <see cref="Native.INfcManager"/>.
/// <code>
/// GET    /_bridge/nfc                         { "capabilities": [ "ReadNdef", "WriteNdef", … ], "scanning": false }
/// POST   /_bridge/nfc/read                    { "alertMessage": "Hold near the tag" }  →  { "identifier": "04A1…", "records": [ … ] } | 204
/// POST   /_bridge/nfc/write                   { "records": [ { "uri": "https://shinylib.net" } ] }  →  { "written": true }
/// POST   /_bridge/nfc/scan                    { "holdSeconds": 10 }   (listen for nfc.tag first)
/// DELETE /_bridge/nfc/scan
/// POST   /_bridge/nfc/tags/{tagId}/write      { "records": [ … ] }
/// POST   /_bridge/nfc/tags/{tagId}/lock
/// POST   /_bridge/nfc/tags/{tagId}/release
/// </code>
/// <para>
/// A native tag exists only inside the scan loop that received it, so a scan holds each tag — the loop waits — while
/// the page may still write or lock it: until the page releases it, <see cref="NfcScanRequest.HoldSeconds"/> pass, or
/// the scan stops. A tag tapped meanwhile waits its turn.
/// </para>
/// </summary>
public sealed class NfcBridge : IWebAppBridge
{
    internal const string TagEvent = "nfc.tag";
    internal const string ScanEvent = "nfc.scan";
    internal const int MaxHoldSeconds = 60;
    internal const int MaxRecords = 32;
    internal const int MaxMessageBytes = 32 * 1024;
    const int MaxText = 4096;

    readonly Native.INfcManager? nfc;
    readonly WebAppEventSource<NfcTag> tags = new();
    readonly WebAppEventSource<NfcScanEnded> ends = new();
    readonly Lock gate = new();
    ScanSession? scan;
    bool busy;

    public NfcBridge(IServiceProvider services) => this.nfc = services.GetOptionalService<Native.INfcManager>();

    public string Name => "nfc";

    // no NFC hardware, or Mac Catalyst, reports no capabilities
    public bool IsSupported => this.nfc is { Capabilities: not Native.NfcCapabilities.None };

    internal int TagListeners => this.tags.ListenerCount;

    internal bool Scanning
    {
        get
        {
            lock (this.gate)
                return this.scan != null;
        }
    }

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.AccessAsync)
        .MapPost("/read", this.ReadAsync)
        .MapPost("/write", this.WriteAsync)
        .MapPost("/scan", this.StartScanAsync)
        .MapDelete("/scan", this.StopScanAsync)
        .MapPost("/tags/{tagId}/write", this.WriteTagAsync)
        .MapPost("/tags/{tagId}/lock", this.LockTagAsync)
        .MapPost("/tags/{tagId}/release", this.ReleaseTagAsync)
        .MapEvent(TagEvent, ct => this.tags.ListenAsync(this.OnTagListenerStopped, ct), NfcJsonContext.Default.NfcTag)
        .MapEvent(ScanEvent, ct => this.ends.ListenAsync(null, ct), NfcJsonContext.Default.NfcScanEnded);


    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, n =>
        WebAppBridgeResults.Json(context, new NfcStatus(ToContract(n.Capabilities), this.Scanning), NfcJsonContext.Default.NfcStatus).AsTask());


    ValueTask AccessAsync(HttpContext context) => this.RunAsync(context, async n =>
    {
        var access = await n.RequestAccess(context.RequestAborted);
        await WebAppBridgeResults.Json(context, new NfcAccessResult(BridgeEnum.Convert<global::Shiny.AccessState, global::Shiny.AppDeviceBridge.Client.AccessState>(access)), NfcJsonContext.Default.NfcAccessResult);
    });


    ValueTask ReadAsync(HttpContext context) => this.RunAsync(context, async n =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, NfcJsonContext.Default.NfcReadRequest) ?? new NfcReadRequest();
        if (body.AlertMessage?.Length > MaxText)
        {
            await WebAppBridgeResults.BadRequest(context, $"The alert message is at most {MaxText} characters.");
            return;
        }

        await this.OneTapAsync(context, async () =>
        {
            var result = await Native.NfcManagerExtensions.ReadOne(n, new Native.NfcScanOptions { AlertMessage = body.AlertMessage }, context.RequestAborted);
            if (result is null)
                await WebAppBridgeResults.NoContent(context);
            else
                await WebAppBridgeResults.Json(context, ToContract(result), NfcJsonContext.Default.NfcReadResult);
        });
    });


    ValueTask WriteAsync(HttpContext context) => this.RunAsync(context, async n =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, NfcJsonContext.Default.NfcWriteRequest);
        if (ToMessage(body?.Records, out var problem) is not { } message || body!.AlertMessage?.Length > MaxText)
        {
            await WebAppBridgeResults.BadRequest(context, problem ?? $"The alert message is at most {MaxText} characters.");
            return;
        }

        await this.OneTapAsync(context, async () =>
        {
            var written = await Native.NfcManagerExtensions.WriteOne(n, message, new Native.NfcScanOptions { AlertMessage = body.AlertMessage }, context.RequestAborted);
            await WebAppBridgeResults.Json(context, new NfcWriteResult(written), NfcJsonContext.Default.NfcWriteResult);
        });
    });


    ValueTask StartScanAsync(HttpContext context) => this.RunAsync(context, async n =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, NfcJsonContext.Default.NfcScanRequest) ?? new NfcScanRequest();
        if (body.HoldSeconds is < 0 or > MaxHoldSeconds || body.AlertMessage?.Length > MaxText)
        {
            await WebAppBridgeResults.BadRequest(context, $"holdSeconds is 0 to {MaxHoldSeconds}, and the alert message at most {MaxText} characters.");
            return;
        }

        if (!this.tags.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", $"Listen for {TagEvent} first: tags arrive as events.");
            return;
        }

        // the scan reports its failures as nfc.scan; catch the ones the page can act on before it starts
        var access = await n.RequestAccess(context.RequestAborted);
        if (access is global::Shiny.AccessState.Disabled or global::Shiny.AccessState.NotSupported)
        {
            await Unavailable(context, access);
            return;
        }

        ScanSession session;
        lock (this.gate)
        {
            if (this.busy)
                session = null!;
            else
            {
                this.busy = true;
                session = this.scan = new ScanSession(this, n, body);
            }
        }

        if (session is null)
        {
            await Busy(context);
            return;
        }

        session.Start();
        await WebAppBridgeResults.NoContent(context);
    });


    async ValueTask StopScanAsync(HttpContext context)
    {
        await this.StopScan();
        await WebAppBridgeResults.NoContent(context);
    }


    ValueTask WriteTagAsync(HttpContext context) => this.RunAsync(context, async _ =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, NfcJsonContext.Default.NfcTagWriteRequest);
        if (ToMessage(body?.Records, out var problem) is not { } message)
        {
            await WebAppBridgeResults.BadRequest(context, problem!);
            return;
        }

        await this.HeldTagAsync(context, tag => tag.WriteNdef(message, context.RequestAborted));
    });


    ValueTask LockTagAsync(HttpContext context) => this.RunAsync(context, _ => this.HeldTagAsync(context, tag => tag.MakeReadOnly(context.RequestAborted)));


    async ValueTask ReleaseTagAsync(HttpContext context)
    {
        ScanSession? session;
        lock (this.gate)
            session = this.scan;

        session?.Release(context.Request.RouteValues["tagId"] as string);
        await WebAppBridgeResults.NoContent(context);
    }


    async Task HeldTagAsync(HttpContext context, Func<Native.INfcTag, Task> operation)
    {
        ScanSession? session;
        lock (this.gate)
            session = this.scan;

        var tagId = context.Request.RouteValues["tagId"] as string;
        if (session is null || !await session.UseAsync(tagId, operation))
        {
            await WebAppBridgeResults.NotFound(context, "That tag is no longer held - it was released, its hold ran out, or the scan stopped. Scan with holdSeconds to write to a tag from nfc.tag.");
            return;
        }
        await WebAppBridgeResults.NoContent(context);
    }


    async Task OneTapAsync(HttpContext context, Func<Task> operation)
    {
        lock (this.gate)
        {
            if (this.busy)
            {
                operation = () => Busy(context).AsTask();
            }
            else
            {
                this.busy = true;
            }
        }

        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the page went away; leaving the scan loop has already ended the native session
        }
        finally
        {
            lock (this.gate)
            {
                if (this.scan is null)
                    this.busy = false;
            }
        }
    }


    // stops with the last nfc.tag listener, so a page that navigates away does not leave the iOS sheet up
    void OnTagListenerStopped(int remaining)
    {
        if (remaining == 0)
            _ = this.StopScan();
    }


    async Task StopScan()
    {
        ScanSession? session;
        lock (this.gate)
            session = this.scan;

        if (session is not null)
            await session.StopAsync();
    }


    void OnScanEnded(ScanSession session, NfcScanEnded ended)
    {
        lock (this.gate)
        {
            if (ReferenceEquals(this.scan, session))
            {
                this.scan = null;
                this.busy = false;
            }
        }
        this.ends.Publish(ended);
    }


    /// <summary>501 without NFC; Shiny.Nfc's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.INfcManager, Task> action)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "NFC");
            return;
        }

        try
        {
            await action(this.nfc!);
        }
        catch (Exception ex) when (ToError(ex) is { } error)
        {
            await WebAppBridgeResults.Error(context, error.Status, error.Code, ex.Message);
        }
    }


    internal static (int Status, string Code)? ToError(Exception ex) => ex switch
    {
        Native.NfcTagLostException => (410, "tag_lost"),
        Native.NfcTagReadOnlyException => (StatusCodes.Status409Conflict, "tag_read_only"),
        Native.NdefMessageTooLargeException => (StatusCodes.Status413PayloadTooLarge, "message_too_large"),
        Native.NdefFormatException => (StatusCodes.Status422UnprocessableEntity, "invalid_ndef"),
        Native.NfcNotSupportedException => (StatusCodes.Status409Conflict, "tag_not_supported"),
        Native.NfcException => (StatusCodes.Status502BadGateway, "nfc_failed"),
        InvalidOperationException => (StatusCodes.Status409Conflict, "nfc_busy"),
        ArgumentException => (StatusCodes.Status400BadRequest, "bad_request"),
        _ => null
    };


    static ValueTask Busy(HttpContext context)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "nfc_busy", "An NFC scan or one-tap read or write is already running - only one NFC session can be open at a time.");


    static ValueTask Unavailable(HttpContext context, global::Shiny.AccessState access)
        => WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "nfc_unavailable", $"NFC is not available ({access}) - on Android, ask the user to turn NFC on in Settings.");


    #region Mapping

    internal static IReadOnlyList<NfcCapability> ToContract(Native.NfcCapabilities capabilities)
    {
        var list = new List<NfcCapability>();
        if (capabilities.HasFlag(Native.NfcCapabilities.ReadNdef))
            list.Add(NfcCapability.ReadNdef);

        if (capabilities.HasFlag(Native.NfcCapabilities.WriteNdef))
            list.Add(NfcCapability.WriteNdef);

        if (capabilities.HasFlag(Native.NfcCapabilities.MakeReadOnly))
            list.Add(NfcCapability.MakeReadOnly);

        if (capabilities.HasFlag(Native.NfcCapabilities.TagIdentifier))
            list.Add(NfcCapability.TagIdentifier);

        return list;
    }


    internal static NfcReadResult ToContract(Native.NfcReadResult result)
        => new(ToHex(result.Identifier), result.Message is { } m ? ToContract(m) : null);


    internal static IReadOnlyList<NfcRecord> ToContract(Native.NdefMessage message)
        => message.Records.Select(ToContract).ToList();


    internal static NfcRecord ToContract(Native.NdefRecord record)
    {
        string? uri = null, text = null, language = null;
        if (record.TryGetUri(out var u))
        {
            uri = u;
        }
        else if (record.TryGetText(out var t))
        {
            text = t.Text;
            language = t.LanguageCode;
        }
        else if (record.TryGetSmartPoster(out var poster))
        {
            uri = poster.Uri;
            text = poster.Titles.FirstOrDefault()?.Text;
            language = poster.Titles.FirstOrDefault()?.LanguageCode;
        }

        return new NfcRecord(
            BridgeEnum.Convert<Native.NdefTypeNameFormat, NfcTypeNameFormat>(record.TypeNameFormat),
            record.TypeString,
            record.Payload.ToArray(),
            record.Id.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(record.Id.Span),
            uri,
            text,
            language,
            record.MimeType
        );
    }


    internal static Native.NdefMessage? ToMessage(IReadOnlyList<NfcWriteRecord>? records, out string? problem)
    {
        problem = null;
        if (records is not { Count: > 0 and <= MaxRecords })
        {
            problem = $"Expected {{ \"records\": [ {{ \"uri\": \"https://…\" }} ] }} with 1 to {MaxRecords} records.";
            return null;
        }

        var natives = new List<Native.NdefRecord>(records.Count);
        foreach (var record in records)
        {
            var native = ToNative(record, out problem);
            if (native is null)
                return null;

            natives.Add(native);
        }

        var message = new Native.NdefMessage(natives);
        if (message.Length > MaxMessageBytes)
        {
            problem = $"The message is {message.Length} bytes; the bridge takes at most {MaxMessageBytes}.";
            return null;
        }
        return message;
    }


    internal static Native.NdefRecord? ToNative(NfcWriteRecord r, out string? problem)
    {
        problem = null;
        var kinds = (r.Uri is not null ? 1 : 0)
            + (r.Text is not null ? 1 : 0)
            + (r.MimeType is not null ? 1 : 0)
            + (r.AndroidPackage is not null ? 1 : 0)
            + (r.Format is not null ? 1 : 0);

        if (kinds != 1)
        {
            problem = "Each record sets exactly one of uri, text, mimeType (with data), androidPackage, or format (with type and data).";
            return null;
        }

        if (new[] { r.Uri, r.Text, r.Language, r.MimeType, r.AndroidPackage, r.Type, r.Id }.Any(x => x?.Length > MaxText))
        {
            problem = $"Record strings are at most {MaxText} characters.";
            return null;
        }

        try
        {
            var record = r switch
            {
                { Uri: { } uri } => Native.NdefRecord.CreateUri(uri),
                { Text: { } text } => Native.NdefRecord.CreateText(text, String.IsNullOrWhiteSpace(r.Language) ? "en" : r.Language),
                { MimeType: { } mime } => Native.NdefRecord.CreateMime(mime, r.Data ?? []),
                { AndroidPackage: { } package } => Native.NdefRecord.CreateAndroidApplication(package),
                _ => new Native.NdefRecord(
                    BridgeEnum.Convert<NfcTypeNameFormat, Native.NdefTypeNameFormat>(r.Format!.Value),
                    r.Type is null ? default : System.Text.Encoding.ASCII.GetBytes(r.Type),
                    r.Data ?? []
                )
            };

            return r.Id is null
                ? record
                : new Native.NdefRecord(record.TypeNameFormat, record.Type, record.Payload, System.Text.Encoding.UTF8.GetBytes(r.Id));
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return null;
        }
    }


    static string? ToHex(byte[]? identifier) => identifier is { Length: > 0 } id ? Convert.ToHexString(id) : null;

    #endregion


    /// <summary>One native scan, run as a loop that holds each tag while the page may still use it.</summary>
    sealed class ScanSession(NfcBridge bridge, Native.INfcManager nfc, NfcScanRequest request)
    {
        readonly CancellationTokenSource stop = new();
        readonly Lock gate = new();
        Held? held;
        Task? loop;


        sealed record Held(string Id, Native.INfcTag Tag, TaskCompletionSource Released, SemaphoreSlim Operation);


        public void Start() => this.loop = Task.Run(this.RunAsync);


        async Task RunAsync()
        {
            var ended = new NfcScanEnded(NfcScanEndReason.Dismissed);
            try
            {
                var options = new Native.NfcScanOptions { AlertMessage = request.AlertMessage };
                await foreach (var tag in nfc.Scan(options, this.stop.Token))
                    await this.HandleAsync(tag);
            }
            catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
            {
                ended = new NfcScanEnded(NfcScanEndReason.Stopped);
            }
            catch (Exception ex)
            {
                ended = new NfcScanEnded(NfcScanEndReason.Failed, ex.Message);
            }
            bridge.OnScanEnded(this, ended);
        }


        async Task HandleAsync(Native.INfcTag tag)
        {
            IReadOnlyList<NfcRecord>? records = null;
            string? readError = null;
            try
            {
                records = await tag.ReadNdef(this.stop.Token) is { } message ? ToContract(message) : null;
            }
            catch (Native.NfcException ex)
            {
                readError = ex.Message;
            }

            var id = Guid.NewGuid().ToString("N");
            Held? current = null;
            if (request.HoldSeconds > 0)
            {
                current = new Held(id, tag, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new SemaphoreSlim(1, 1));
                lock (this.gate)
                    this.held = current;
            }

            bridge.tags.Publish(new NfcTag(id, ToHex(tag.Identifier), tag.IsWritable, tag.MaxSize, records, readError));
            if (current is null)
                return;

            try
            {
                await Task.WhenAny(current.Released.Task, Task.Delay(TimeSpan.FromSeconds(request.HoldSeconds), this.stop.Token));
            }
            finally
            {
                lock (this.gate)
                    this.held = null;

                // the native tag dies with this loop iteration; let a write already started finish first
                await current.Operation.WaitAsync();
                current.Operation.Dispose();
            }
        }


        /// <summary>Runs <paramref name="operation"/> on the held tag; false when <paramref name="tagId"/> is not it.</summary>
        public async Task<bool> UseAsync(string? tagId, Func<Native.INfcTag, Task> operation)
        {
            Held? current;
            lock (this.gate)
                current = this.held is { } h && h.Id == tagId ? h : null;

            if (current is null)
                return false;

            try
            {
                await current.Operation.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                return false; // released while waiting
            }

            try
            {
                lock (this.gate)
                {
                    if (!ReferenceEquals(this.held, current))
                        return false;
                }
                await operation(current.Tag);
                return true;
            }
            finally
            {
                current.Operation.Release();
            }
        }


        public void Release(string? tagId)
        {
            lock (this.gate)
            {
                if (this.held is { } h && h.Id == tagId)
                    h.Released.TrySetResult();
            }
        }


        public async Task StopAsync()
        {
            if (!this.stop.IsCancellationRequested)
                this.stop.Cancel();

            if (this.loop is { } running)
                await running;
        }
    }
}
