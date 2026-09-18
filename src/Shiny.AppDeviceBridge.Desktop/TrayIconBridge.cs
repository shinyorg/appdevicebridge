using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AppDeviceBridge.Desktop.Client;
using Shiny.Maui.Controls.Desktop.TrayIcon;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Desktop;

public static class TrayIconBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/tray</c> and registers <c>ITrayIconFactory</c> for the platform — there is nothing
    /// else to call.
    /// <code>
    /// builder.AddTrayIconBridge(o => o.MaxIcons = 1);
    /// </code>
    /// <para>
    /// Desktop only: NSStatusItem on macOS and Mac Catalyst, Shell_NotifyIcon on Windows, and
    /// ayatana-appindicator over D-Bus on Linux. Everywhere else <c>GET /_bridge/tray</c> answers
    /// <c>{ "supported": false }</c> and the rest answer 501, so a shared web app can keep the call in and
    /// hide the UI.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddTrayIconBridge(this MauiAppBuilder builder, Action<TrayIconBridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new TrayIconBridgeOptions();
        configure?.Invoke(options);

        builder.UseTrayIcon();
        builder.Services.TryAddSingleton(options);
        builder.Services.AddWebAppBridge<TrayIconBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/tray</c> over <see cref="ITrayIconFactory"/> — the system tray on Windows, the menu bar on
/// macOS, the status notifier area on Linux.
/// <code>
/// GET    /_bridge/tray                      { "supported": true, "maxIcons": 4, "icons": [ … ] }
/// POST   /_bridge/tray                      creates one and answers its id
/// DELETE /_bridge/tray                      removes them all
/// GET    /_bridge/tray/{id}
/// PUT    /_bridge/tray/{id}                 creates it or changes only the properties sent
/// DELETE /_bridge/tray/{id}
/// PUT    /_bridge/tray/{id}/menu            { "items": [ { "id": "open", "label": "Open" }, … ] }
/// DELETE /_bridge/tray/{id}/menu
/// POST   /_bridge/tray/{id}/menu/show       no-op on Linux, where the indicator opens its own menu
/// POST   /_bridge/tray/{id}/notification    { "title": "…", "message": "…" }
/// PUT    /_bridge/tray/{id}/animation       { "frames": [ … ], "intervalMs": 500 }
/// DELETE /_bridge/tray/{id}/animation
///
/// events: tray.click, tray.menu
/// </code>
/// <para>
/// A tray icon outlives the page that made it, which is the point — the menu still works with the window
/// closed. So clicks go out as events <em>and</em> as calls the web app handles in the page when it is open
/// and in <c>background.js</c> when it is not, the same way notification taps and jobs do. And so a page
/// should claim an icon by name with <c>PUT /_bridge/tray/main</c> rather than <c>POST /_bridge/tray</c>:
/// the first call creates it and a reload adopts the same one instead of stacking up another.
/// </para>
/// </summary>
public sealed partial class TrayIconBridge : IWebAppBridge, IDisposable
{
    readonly ITrayIconFactory? factory;
    readonly WebAppFileRoots? fileRoots;
    readonly TrayIconBridgeOptions options;
    readonly WebAppInvoker? invoker;
    readonly WebAppEventSource<TrayClick> clicks = new();
    readonly WebAppEventSource<TrayMenuSelection> menuSelections = new();

    // One at a time: every call touches the same icons and most of the work happens on the UI thread anyway.
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, TrayEntry> icons = new(StringComparer.Ordinal);
    int generated;
    bool disposed;

    public TrayIconBridge(IServiceProvider services)
    {
        this.factory = services.GetOptionalService<ITrayIconFactory>();
        this.fileRoots = services.GetOptionalService<WebAppFileRoots>();
        this.options = services.GetOptionalService<TrayIconBridgeOptions>() ?? new TrayIconBridgeOptions();
        this.invoker = services.GetOptionalService<WebAppInvoker>();
    }

    const string ClickEvent = "tray.click";
    const string MenuEvent = "tray.menu";

    public string Name => "tray";

    public bool IsSupported => this.factory is not null && PlatformSupported;

    /// <summary>
    /// The factory is registered on every platform — on the ones without a tray it throws when asked for an
    /// icon, so the check has to happen before that.
    /// </summary>
    static bool PlatformSupported
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsLinux();

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent(ClickEvent, ct => this.clicks.ListenAsync(ct), TrayJsonContext.Default.TrayClick)
        .MapEvent(MenuEvent, ct => this.menuSelections.ListenAsync(ct), TrayJsonContext.Default.TrayMenuSelection)
        .MapGet("", this.StatusAsync)
        .MapPost("", this.CreateAsync)
        .MapDelete("", this.RemoveAllAsync)
        .MapGet("/{id}", this.GetAsync)
        .MapPut("/{id}", this.PutAsync)
        .MapDelete("/{id}", this.RemoveAsync)
        .MapPut("/{id}/menu", this.SetMenuAsync)
        .MapDelete("/{id}/menu", this.ClearMenuAsync)
        .MapPost("/{id}/menu/show", this.ShowMenuAsync)
        .MapPost("/{id}/notification", this.NotifyAsync)
        .MapPut("/{id}/animation", this.StartAnimationAsync)
        .MapDelete("/{id}/animation", this.StopAnimationAsync);

    async ValueTask StatusAsync(HttpContext context)
    {
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            await WebAppBridgeResults.Json(
                context,
                new TrayStatus(this.IsSupported, this.options.MaxIcons, [.. this.icons.Values.Select(Describe)]),
                TrayJsonContext.Default.TrayStatus
            );
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask CreateAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrayJsonContext.Default.TrayIconInput) ?? new TrayIconInput();
        await this.UpsertAsync(context, body.Id, body);
    }

    async ValueTask PutAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrayJsonContext.Default.TrayIconInput) ?? new TrayIconInput();
        await this.UpsertAsync(context, context.Request.RouteValues["id"], body);
    }

    ValueTask GetAsync(HttpContext context)
        => this.WithEntry(context, entry => WebAppBridgeResults.Json(context, Describe(entry), TrayJsonContext.Default.TrayIconInfo));

    async ValueTask RemoveAsync(HttpContext context)
    {
        if (!this.EnsureSupported(context, out var notSupported))
        {
            await notSupported;
            return;
        }

        TrayEntry? entry;
        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            var id = context.Request.RouteValues["id"] ?? String.Empty;
            if (this.icons.Remove(id, out entry))
                await MainAsync(entry.Dispose);
        }
        finally
        {
            this.gate.Release();
        }

        await (entry is null
            ? WebAppBridgeResults.NotFound(context, $"No tray icon '{context.Request.RouteValues["id"]}'.")
            : WebAppBridgeResults.NoContent(context));
    }

    async ValueTask RemoveAllAsync(HttpContext context)
    {
        if (!this.EnsureSupported(context, out var notSupported))
        {
            await notSupported;
            return;
        }

        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            TrayEntry[] entries = [.. this.icons.Values];
            this.icons.Clear();

            await MainAsync(() =>
            {
                foreach (var entry in entries)
                    entry.Dispose();
            });
        }
        finally
        {
            this.gate.Release();
        }

        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask SetMenuAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrayJsonContext.Default.TrayMenuInput);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"items\": [ { \"id\": \"open\", \"label\": \"Open\" }, … ] }.");
            return;
        }

        await this.WithEntry(context, async entry =>
        {
            if (!this.TryBuildMenu(entry.Id, body, out var menu, out var nodes, out var error))
            {
                await WebAppBridgeResults.BadRequest(context, error);
                return;
            }

            await MainAsync(() => entry.Icon.SetMenu(menu));
            entry.Nodes = nodes;
            await WebAppBridgeResults.Json(context, Describe(entry), TrayJsonContext.Default.TrayIconInfo);
        });
    }

    ValueTask ClearMenuAsync(HttpContext context)
        => this.WithEntry(context, async entry =>
        {
            await MainAsync(() => entry.Icon.SetMenu(new TrayMenu()));
            entry.Nodes = [];
            await WebAppBridgeResults.NoContent(context);
        });

    ValueTask ShowMenuAsync(HttpContext context)
        => this.WithEntry(context, async entry =>
        {
            await MainAsync(entry.Icon.ShowMenu);
            await WebAppBridgeResults.NoContent(context);
        });

    async ValueTask NotifyAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrayJsonContext.Default.TrayNotification);
        if (body is not { Title.Length: > 0, Message.Length: > 0 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"title\": \"…\", \"message\": \"…\" }.");
            return;
        }

        await this.WithEntry(context, async entry =>
        {
            // Best effort by contract: a desktop with no notification daemon simply shows nothing.
            await MainAsync(() => entry.Icon.ShowNotification(body.Title, body.Message));
            await WebAppBridgeResults.NoContent(context);
        });
    }

    async ValueTask StartAnimationAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrayJsonContext.Default.TrayAnimation);
        if (body?.Frames is not { Count: > 1 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"frames\": [ …, … ], \"intervalMs\": 500 } with at least two frames.");
            return;
        }

        if (body.Frames.Count > this.options.MaxAnimationFrames)
        {
            await WebAppBridgeResults.BadRequest(context, $"An animation runs at most {this.options.MaxAnimationFrames} frames.");
            return;
        }

        List<Func<Stream>> frames = [];
        foreach (var frame in body.Frames)
        {
            if (!this.TryResolveImage(frame, out var resolved, out var error))
            {
                await WebAppBridgeResults.BadRequest(context, error);
                return;
            }

            if (resolved is null)
            {
                await WebAppBridgeResults.BadRequest(context, "Every animation frame needs an image.");
                return;
            }

            frames.Add(resolved);
        }

        var interval = TimeSpan.FromMilliseconds(body.IntervalMs);
        if (interval < this.options.MinAnimationInterval)
            interval = this.options.MinAnimationInterval;

        await this.WithEntry(context, async entry =>
        {
            await MainAsync(() => entry.Icon.StartAnimation(frames, interval));
            await WebAppBridgeResults.Json(context, Describe(entry), TrayJsonContext.Default.TrayIconInfo);
        });
    }

    ValueTask StopAnimationAsync(HttpContext context)
        => this.WithEntry(context, async entry =>
        {
            await MainAsync(entry.Icon.StopAnimation);
            await WebAppBridgeResults.NoContent(context);
        });

    async ValueTask UpsertAsync(HttpContext context, string? requestedId, TrayIconInput body)
    {
        if (!this.EnsureSupported(context, out var notSupported))
        {
            await notSupported;
            return;
        }

        var id = String.IsNullOrEmpty(requestedId) ? null : requestedId;
        if (id is not null && !IsValidId(id))
        {
            await WebAppBridgeResults.BadRequest(context, "A tray icon id is 1-64 letters, digits, '.', '_' or '-'.");
            return;
        }

        // Resolved before anything is created, so a bad image leaves no half-built icon in the tray.
        if (!this.TryResolveImage(body.Icon, out var image, out var imageError))
        {
            await WebAppBridgeResults.BadRequest(context, imageError);
            return;
        }

        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            var entry = id is not null && this.icons.TryGetValue(id, out var existing) ? existing : null;
            var creating = entry is null;

            if (entry is null)
            {
                if (this.icons.Count >= this.options.MaxIcons)
                {
                    await WebAppBridgeResults.Error(
                        context,
                        StatusCodes.Status409Conflict,
                        "tray_limit",
                        $"This app allows {this.options.MaxIcons} tray icon(s) at once. Remove one first."
                    );
                    return;
                }

                id ??= this.NextIconId();

                ITrayIcon icon;
                try
                {
                    icon = await MainAsync(this.factory!.Create);
                }
                catch (PlatformNotSupportedException ex)
                {
                    await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
                    return;
                }

                entry = new TrayEntry(id, icon);
                this.Watch(entry);
            }

            TrayMenu? menu = null;
            if (body.Menu is not null)
            {
                if (!this.TryBuildMenu(entry.Id, body.Menu, out menu, out var nodes, out var menuError))
                {
                    if (creating)
                        await MainAsync(entry.Dispose);

                    await WebAppBridgeResults.BadRequest(context, menuError);
                    return;
                }

                entry.Nodes = nodes;
            }

            // An icon nobody asked to hide should be in the tray the moment it exists.
            var visible = body.Visible ?? (creating ? true : null);

            await MainAsync(() =>
            {
                if (body.Tooltip is not null)
                    entry.Icon.Tooltip = NullWhenEmpty(body.Tooltip);

                if (body.Title is not null)
                    entry.Icon.Title = NullWhenEmpty(body.Title);

                if (body.Badge is not null)
                    entry.Icon.Badge = NullWhenEmpty(body.Badge);

                if (body.TemplateImage is { } template)
                    entry.Icon.IsTemplateImage = template;

                if (image is not null)
                {
                    entry.Icon.SetIcon(image);
                    entry.HasIcon = true;
                }

                if (menu is not null)
                    entry.Icon.SetMenu(menu);

                if (visible is { } show)
                    entry.Icon.IsVisible = show;
            });

            this.icons[entry.Id] = entry;

            await WebAppBridgeResults.Json(
                context,
                Describe(entry),
                TrayJsonContext.Default.TrayIconInfo,
                creating ? StatusCodes.Status201Created : StatusCodes.Status200OK
            );
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask WithEntry(HttpContext context, Func<TrayEntry, ValueTask> action)
    {
        if (!this.EnsureSupported(context, out var notSupported))
        {
            await notSupported;
            return;
        }

        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            var id = context.Request.RouteValues["id"] ?? String.Empty;
            if (!this.icons.TryGetValue(id, out var entry))
            {
                await WebAppBridgeResults.NotFound(context, $"No tray icon '{id}'.");
                return;
            }

            await action(entry);
        }
        finally
        {
            this.gate.Release();
        }
    }

    bool EnsureSupported(HttpContext context, out ValueTask result)
    {
        if (this.IsSupported)
        {
            result = ValueTask.CompletedTask;
            return true;
        }

        result = WebAppBridgeResults.NotSupported(context, "A system tray icon");
        return false;
    }

    void Watch(TrayEntry entry)
    {
        entry.Icon.PrimaryClick += (_, e) => this.Clicked(entry.Id, TrayClickButton.Primary, e);
        entry.Icon.SecondaryClick += (_, e) => this.Clicked(entry.Id, TrayClickButton.Secondary, e);
        entry.Icon.DoubleClick += (_, e) => this.Clicked(entry.Id, TrayClickButton.Double, e);
    }

    void Clicked(string id, TrayClickButton button, TrayClickEventArgs args)
        => this.Raise(this.clicks, ClickEvent, new TrayClick(id, button, args.X, args.Y), TrayJsonContext.Default.TrayClick);

    void MenuActivated(string id, string itemId, string? label, bool? isChecked)
        => this.Raise(this.menuSelections, MenuEvent, new TrayMenuSelection(id, itemId, label, isChecked), TrayJsonContext.Default.TrayMenuSelection);

    /// <summary>
    /// Published to whatever listens for the event, and called on the web app so <c>background.js</c> can act on
    /// it when the window is closed — which is when a tray menu earns its keep.
    /// </summary>
    void Raise<T>(WebAppEventSource<T> source, string name, T payload, JsonTypeInfo<T> typeInfo)
    {
        source.Publish(payload);

        if (this.invoker is { } target)
            _ = Forget(target.InvokeAsync(name, payload, typeInfo));
    }

    static async Task Forget(Task<WebAppInvocationResult> call)
    {
        try
        {
            await call;
        }
        catch
        {
            // Raised from a native click handler: nothing here is worth taking the app down for.
        }
    }

    string NextIconId()
    {
        string id;
        do
        {
            id = $"tray{++this.generated}";
        }
        while (this.icons.ContainsKey(id));

        return id;
    }

    static bool IsValidId(string id)
        => id.Length <= 64 && id.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    static string? NullWhenEmpty(string value) => value.Length == 0 ? null : value;

    static TrayIconInfo Describe(TrayEntry entry) => new(
        entry.Id,
        entry.Icon.Tooltip,
        entry.Icon.Title,
        entry.Icon.IsVisible,
        entry.Icon.IsTemplateImage,
        entry.Icon.Badge,
        entry.HasIcon,
        entry.Icon.IsAnimating,
        Describe(entry.Nodes)
    );

    static IReadOnlyList<TrayMenuItemInfo> Describe(List<TrayMenuNode> nodes) =>
    [
        .. nodes.Select(node => new TrayMenuItemInfo(
            node.Id,
            node.Type,
            node.Item switch
            {
                TrayMenuItem item => item.Label,
                TrayCheckMenuItem check => check.Label,
                TraySubmenu submenu => submenu.Label,
                _ => null
            },
            node.Item.IsEnabled,
            node.Item.IsVisible,
            node.Item is TrayCheckMenuItem state ? state.IsChecked : null,
            node.Children.Count == 0 ? null : Describe(node.Children)
        ))
    ];

    static Task MainAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            return dispatcher.DispatchAsync(action);

        action();
        return Task.CompletedTask;
    }

    static Task<T> MainAsync<T>(Func<T> func)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(func) : Task.FromResult(func());

    public void Dispose()
    {
        TrayEntry[] entries;

        this.gate.Wait();
        try
        {
            if (this.disposed)
                return;

            this.disposed = true;
            entries = [.. this.icons.Values];
            this.icons.Clear();
        }
        finally
        {
            this.gate.Release();
        }

        if (entries.Length == 0)
            return;

        void Remove()
        {
            foreach (var entry in entries)
                entry.Dispose();
        }

        // Removing an icon is a UI-thread job, and Dispose cannot await its way onto one.
        if (Application.Current?.Dispatcher is { IsDispatchRequired: true } dispatcher)
            dispatcher.Dispatch(Remove);
        else
            Remove();
    }
}

/// <summary>One live tray icon, and what the page needs told back about it.</summary>
sealed class TrayEntry(string id, ITrayIcon icon) : IDisposable
{
    public string Id => id;

    public ITrayIcon Icon => icon;

    /// <summary>Whether an image was ever set. The platform icons have no readable image to ask.</summary>
    public bool HasIcon { get; set; }

    /// <summary>The menu as the page named it: the platform items carry no ids of their own.</summary>
    public List<TrayMenuNode> Nodes { get; set; } = [];

    public void Dispose() => icon.Dispose();
}

sealed record TrayMenuNode(string Id, TrayMenuItemType Type, TrayMenuItemBase Item, List<TrayMenuNode> Children);
