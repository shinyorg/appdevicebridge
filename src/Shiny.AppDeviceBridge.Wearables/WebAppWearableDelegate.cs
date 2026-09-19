using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Wearables.Client;
using Native = Shiny.Wearables;

namespace Shiny.AppDeviceBridge.Wearables;

/// <summary>
/// Turns what the wearable sends into page events and calls to the web app's <c>wearables.*</c> handlers — the page
/// when it is listening, background.js otherwise. A <c>wearables.message</c> handler's return value is the reply the
/// wearable gets. Files are filed into a file root first, so the handler receives a <see cref="BridgeFile"/>.
/// <para>
/// Subclass to keep some traffic to the native app: override a member and call the base only for what the web app
/// should see.
/// </para>
/// </summary>
public class WebAppWearableDelegate : Native.IWearableDelegate
{
    internal const string StatusEvent = "wearables.status";
    internal const string MessageEvent = "wearables.message";
    internal const string ContextEvent = "wearables.context";
    internal const string TransferEvent = "wearables.transfer";
    internal const string FileEvent = "wearables.file";
    internal const string CompletedEvent = "wearables.completed";

    readonly WebAppInvoker invoker;
    readonly WebAppFileRoots roots;
    readonly WearablesBridgeOptions options;
    readonly ILogger logger;

    readonly WebAppEventSource<WearableStatus> status;
    readonly WebAppEventSource<WearableMessage> messages;
    readonly WebAppEventSource<Client.WearableContext> contexts;
    readonly WebAppEventSource<WearableTransfer> transfers;
    readonly WebAppEventSource<WearableReceivedFile> files;
    readonly WebAppEventSource<WearableTransferCompleted> completed;

    public WebAppWearableDelegate(
        WebAppEventHub events,
        WebAppInvoker invoker,
        WebAppFileRoots roots,
        WearablesBridgeOptions options,
        ILogger<WebAppWearableDelegate>? logger = null
    )
    {
        this.invoker = invoker;
        this.roots = roots;
        this.options = options;
        this.logger = logger ?? (ILogger)NullLogger.Instance;

        this.status = events.Source(StatusEvent, WearablesJsonContext.Default.WearableStatus);
        this.messages = events.Source(MessageEvent, WearablesJsonContext.Default.WearableMessage);
        this.contexts = events.Source(ContextEvent, WearablesJsonContext.Default.WearableContext);
        this.transfers = events.Source(TransferEvent, WearablesJsonContext.Default.WearableTransfer);
        this.files = events.Source(FileEvent, WearablesJsonContext.Default.WearableReceivedFile);
        this.completed = events.Source(CompletedEvent, WearablesJsonContext.Default.WearableTransferCompleted);
    }


    public virtual Task OnStatusChanged(Native.WearableStatus status)
    {
        this.status.Publish(WearablesBridge.ToContract(status));
        return Task.CompletedTask;
    }


    public virtual async Task<byte[]?> OnMessageReceived(Native.WearableMessage message)
    {
        var (data, binary) = WearablesPayload.FromBytes(message.Data);
        var payload = new WearableMessage(message.Path, data, binary, message.NodeId, message.ExpectsReply);

        // The event is for pages that only display traffic; the handler is the one that answers.
        this.messages.Publish(payload);
        var result = await this.invoker.InvokeAsync(MessageEvent, payload, WearablesJsonContext.Default.WearableMessage).ConfigureAwait(false);

        if (result.Handled && !result.Succeeded)
            this.logger.LogWarning("The web app's wearables.message handler failed for {Path}: {Error}", message.Path, result.Error);

        // Null lets another delegate answer; an empty reply is still sent if none does.
        return result.Succeeded ? WearablesPayload.FromResultJson(result.ResultJson) : null;
    }


    public virtual async Task OnContextReceived(Native.WearableContext context)
    {
        var payload = WearablesBridge.ToContext(context.Data, context.NodeId);
        this.contexts.Publish(payload);
        await this.invoker.InvokeAsync(ContextEvent, payload, WearablesJsonContext.Default.WearableContext).ConfigureAwait(false);
    }


    public virtual async Task OnTransferReceived(Native.WearableTransfer transfer)
    {
        var (data, binary) = WearablesPayload.FromBytes(transfer.Data);
        var payload = new WearableTransfer(transfer.Id, transfer.Path, data, binary, transfer.NodeId);

        this.transfers.Publish(payload);
        await this.invoker.InvokeAsync(TransferEvent, payload, WearablesJsonContext.Default.WearableTransfer).ConfigureAwait(false);
    }


    public virtual async Task OnFileReceived(Native.WearableFile file)
    {
        if (await this.FileAsync(file).ConfigureAwait(false) is not { } payload)
            return;

        this.files.Publish(payload);
        await this.invoker.InvokeAsync(FileEvent, payload, WearablesJsonContext.Default.WearableReceivedFile).ConfigureAwait(false);
    }


    public virtual Task OnTransferCompleted(Native.WearableTransferResult result)
    {
        this.completed.Publish(new WearableTransferCompleted(
            result.Id,
            result.Path,
            WearablesBridge.ToKind(result.Kind),
            result.Succeeded,
            result.Error
        ));
        return Task.CompletedTask;
    }


    /// <summary>
    /// Moves a received file into <see cref="WearablesBridgeOptions.Root"/> — <c>{Folder}/{id}/{name}</c> — so the page
    /// reaches it through the files bridge like anything else it owns. Null when there is nowhere to put it; the file is
    /// then left where Shiny put it.
    /// </summary>
    internal async Task<WearableReceivedFile?> FileAsync(Native.WearableFile file)
    {
        if (!this.roots.TryGet(this.options.Root, out var root))
        {
            this.logger.LogWarning("No file root '{Root}' for the file the wearable sent; it stays at {Path}", this.options.Root, file.LocalPath);
            return null;
        }

        var name = SafeSegment(file.FileName, "file");
        var id = SafeSegment(file.Id, Guid.NewGuid().ToString("N"));
        var folder = WebAppFilePath.Normalize(this.options.Folder) ?? String.Empty;
        var path = String.Join('/', new[] { folder, id, name }.Where(x => x.Length > 0));

        try
        {
            FileWriteResult written;
            await using (var content = File.OpenRead(file.LocalPath))
                written = await root.WriteAsync(path, content, FileWriteMode.Replace, Int64.MaxValue, CancellationToken.None).ConfigureAwait(false);

            TryDelete(file.LocalPath);

            return new WearableReceivedFile(
                file.Id,
                file.Path,
                file.FileName,
                new BridgeFile(root.Name, path),
                written.Entry.Size ?? 0,
                file.Metadata,
                file.NodeId
            );
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Could not file the wearable's file {Name} into '{Root}'", file.FileName, root.Name);
            return null;
        }
    }


    /// <summary>A single, valid path segment from what a sender supplied, or <paramref name="fallback"/>.</summary>
    static string SafeSegment(string? value, string fallback)
    {
        var last = value?.Replace('\\', '/').Split('/')[^1];
        return WebAppFilePath.Normalize(last) is { Length: > 0 } ok && ok != ".." ? ok : fallback;
    }


    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder != null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
