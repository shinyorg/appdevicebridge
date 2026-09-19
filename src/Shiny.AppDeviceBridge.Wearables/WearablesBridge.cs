using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Wearables.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Wearables;

namespace Shiny.AppDeviceBridge.Wearables;

public sealed class WearablesBridgeOptions
{
    /// <summary>The file root files from the wearable are filed into. <c>data</c> by default.</summary>
    public string Root { get; set; } = "data";

    /// <summary>
    /// The folder inside <see cref="Root"/>, created as needed. <c>wearables</c> by default. Each file lands in a folder
    /// named for its transfer id, under the name the sender gave it.
    /// </summary>
    public string Folder { get; set; } = "wearables";

    /// <summary>
    /// Registers Shiny's wearable service. Turn off to call <c>AddWearables</c> yourself; the bridge's delegate is added
    /// either way, and Shiny runs every registered delegate.
    /// </summary>
    public bool RegisterWearableService { get; set; } = true;

    internal void Validate()
    {
        if (!WebAppFileStore.IsValidName(this.Root))
            throw new InvalidOperationException($"WearablesBridgeOptions.Root '{this.Root}' is not a file root name.");

        if (WebAppFilePath.Normalize(this.Folder) is null)
            throw new InvalidOperationException($"WearablesBridgeOptions.Folder '{this.Folder}' is not a relative path.");
    }
}

public static class WearablesBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/wearables</c> — the companion app on a paired Apple Watch or Wear OS device — and registers
    /// Shiny's wearable service.
    /// <code>
    /// bridge.AddWearablesBridge();
    /// bridge.AddWearablesBridge(o => o.Folder = "watch");   // where files from the watch are filed
    /// </code>
    /// <para>
    /// What the wearable sends goes to the web app's <c>wearables.*</c> handlers — the page if it is listening,
    /// background.js otherwise — and a <c>wearables.message</c> handler's return value is the reply. The companion app
    /// speaks <see cref="Native.WearableProtocol"/>. On Wear OS both apps share the application id and signing key. iOS and
    /// Android only; every other platform answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddWearablesBridge<TBuilder>(this TBuilder bridge, Action<WearablesBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(WearablesBridgeOptions))?.ImplementationInstance as WearablesBridgeOptions;
        if (options is null)
        {
            options = new WearablesBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        if (services.Any(x => x.ImplementationType == typeof(WebAppWearableDelegate)))
            return bridge;

        if (options.RegisterWearableService)
            services.AddWearables<WebAppWearableDelegate>();
        else
            services.AddSingleton<Native.IWearableDelegate, WebAppWearableDelegate>();

        bridge.AddBridge<WearablesBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/wearables</c> over <see cref="Native.IWearableManager"/>.
/// <code>
/// GET    /_bridge/wearables                  { "supported": true, "paired": true, "appInstalled": true, "reachable": true, "nodes": [...] }
/// POST   /_bridge/wearables/messages         { "path": "sync", "data": {…} }  →  { "data": {…}, "binary": false }
/// GET    /_bridge/wearables/context          { "sent": {…}, "received": {…} }
/// PUT    /_bridge/wearables/context          { "data": {…} }
/// POST   /_bridge/wearables/transfers        { "path": "log", "data": {…} }   →  { "id": "…" }
/// POST   /_bridge/wearables/files            { "path": "maps", "file": { "root": "data", "path": "map.bin" } }  →  { "id": "…" }
/// GET    /_bridge/wearables/transfers        [ { "id", "path", "kind", "progress" } ]
/// DELETE /_bridge/wearables/transfers/{id}
///
/// events:   wearables.status, wearables.message, wearables.context, wearables.transfer, wearables.file, wearables.completed
/// handlers: wearables.message (its return value is the reply), wearables.context, wearables.transfer, wearables.file
/// </code>
/// </summary>
public sealed class WearablesBridge(IServiceProvider services, WebAppFileRoots roots) : IWebAppBridge
{
    readonly Native.IWearableManager? manager = services.GetOptionalService<Native.IWearableManager>();

    public string Name => "wearables";

    public bool IsSupported => this.manager is not null;

    public void Map(WebAppBridgeRoutes routes)
    {
        // The delegate raises these; mapping them here means the topics exist as soon as the server is composed.
        routes.Events.Source(WebAppWearableDelegate.StatusEvent, WearablesJsonContext.Default.WearableStatus);
        routes.Events.Source(WebAppWearableDelegate.MessageEvent, WearablesJsonContext.Default.WearableMessage);
        routes.Events.Source(WebAppWearableDelegate.ContextEvent, WearablesJsonContext.Default.WearableContext);
        routes.Events.Source(WebAppWearableDelegate.TransferEvent, WearablesJsonContext.Default.WearableTransfer);
        routes.Events.Source(WebAppWearableDelegate.FileEvent, WearablesJsonContext.Default.WearableReceivedFile);
        routes.Events.Source(WebAppWearableDelegate.CompletedEvent, WearablesJsonContext.Default.WearableTransferCompleted);

        routes
            .MapGet("", this.StatusAsync)
            .MapPost("/messages", this.SendMessageAsync)
            .MapGet("/context", this.GetContextAsync)
            .MapPut("/context", this.UpdateContextAsync)
            .MapPost("/transfers", this.TransferAsync)
            .MapGet("/transfers", this.PendingAsync)
            .MapDelete("/transfers/{id}", this.CancelAsync)
            .MapPost("/files", this.SendFileAsync);
    }

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var status = await m.GetStatus(context.RequestAborted);
        await WebAppBridgeResults.Json(context, ToContract(status), WearablesJsonContext.Default.WearableStatus);
    });

    ValueTask SendMessageAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, WearablesJsonContext.Default.WearableMessageRequest) is not { Path: not null } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"path\": \"…\", \"data\": … }.");
            return;
        }

        var reply = await m.SendMessage(body.Path, WearablesPayload.ToBytes(body.Data), body.NodeId, context.RequestAborted);
        var (data, binary) = WearablesPayload.FromBytes(reply);
        await WebAppBridgeResults.Json(context, new WearableReply(data, binary), WearablesJsonContext.Default.WearableReply);
    });

    ValueTask GetContextAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var sent = await m.GetContext(context.RequestAborted);
        var received = await m.GetReceivedContext(context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            new WearableContextState(
                sent is null ? null : ToContext(sent, null),
                received is null ? null : ToContext(received.Data, received.NodeId)
            ),
            WearablesJsonContext.Default.WearableContextState
        );
    });

    ValueTask UpdateContextAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, WearablesJsonContext.Default.WearableContextUpdate) is not { } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"data\": … }.");
            return;
        }

        await m.UpdateContext(WearablesPayload.ToBytes(body.Data), context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask TransferAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, WearablesJsonContext.Default.WearableTransferRequest) is not { Path: not null } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"path\": \"…\", \"data\": … }.");
            return;
        }

        var id = await m.Transfer(body.Path, WearablesPayload.ToBytes(body.Data), context.RequestAborted);
        await WebAppBridgeResults.Json(context, new WearableTransferTicket(id), WearablesJsonContext.Default.WearableTransferTicket);
    });

    ValueTask SendFileAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, WearablesJsonContext.Default.WearableFileRequest) is not { Path: not null, File: not null } body)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"path\": \"…\", \"file\": { \"root\": \"…\", \"path\": \"…\" } }.");
            return;
        }

        // Resolved through the file roots, so the page can only send what it could already read; and to a path on disk,
        // because the platform reads the file itself while it transfers.
        if (!roots.TryResolve(body.File.Root, body.File.Path, out var fullPath) || !File.Exists(fullPath))
        {
            await WebAppBridgeResults.NotFound(context, $"No file '{body.File.Path}' in root '{body.File.Root}' on disk.");
            return;
        }

        var id = await m.TransferFile(body.Path, fullPath, body.Metadata, context.RequestAborted);
        await WebAppBridgeResults.Json(context, new WearableTransferTicket(id), WearablesJsonContext.Default.WearableTransferTicket);
    });

    ValueTask PendingAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var pending = await m.GetPendingTransfers(context.RequestAborted);
        IReadOnlyList<WearablePendingTransfer> list = [.. pending.Select(x => new WearablePendingTransfer(x.Id, x.Path, ToKind(x.Kind), x.Progress))];
        await WebAppBridgeResults.Json(context, list, WearablesJsonContext.Default.IReadOnlyListWearablePendingTransfer);
    });

    ValueTask CancelAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var id = context.Request.RouteValues["id"] as string;
        if (String.IsNullOrWhiteSpace(id) || !await m.CancelTransfer(id, context.RequestAborted))
        {
            await WebAppBridgeResults.NotFound(context, $"No pending transfer '{id}'.");
            return;
        }
        await WebAppBridgeResults.NoContent(context);
    });

    /// <summary>501 without a manager; the manager's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.IWearableManager, Task> action)
    {
        if (this.manager is not { } m)
        {
            await WebAppBridgeResults.NotSupported(context, "Wearables");
            return;
        }

        try
        {
            await action(m);
        }
        catch (Native.WearableException ex) when (ex.Code == Native.WearableErrorCode.NotSupported)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
        }
        catch (Native.WearableException ex) when (ex.Code == Native.WearableErrorCode.NotReachable)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_reachable", ex.Message);
        }
        catch (Native.WearableException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status502BadGateway, "wearable_failed", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    internal static WearableStatus ToContract(Native.WearableStatus status) => new(
        status.IsSupported,
        status.IsPaired,
        status.IsAppInstalled,
        status.IsReachable,
        [.. status.Nodes.Select(n => new WearableNode(n.Id, n.DisplayName, n.IsNearby, n.HasApp))]
    );

    internal static Client.WearableContext ToContext(byte[] bytes, string? nodeId)
    {
        var (data, binary) = WearablesPayload.FromBytes(bytes);
        return new Client.WearableContext(data, binary, nodeId);
    }

    internal static Client.WearableTransferKind ToKind(Native.WearableTransferKind kind)
        => BridgeEnum.Convert<Native.WearableTransferKind, Client.WearableTransferKind>(kind);
}
