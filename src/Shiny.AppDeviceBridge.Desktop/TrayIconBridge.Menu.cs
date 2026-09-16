using System.Diagnostics.CodeAnalysis;
using Shiny.AppDeviceBridge.Desktop.Client;
using Shiny.Maui.Controls.Desktop.TrayIcon;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>Turning the page's JSON into a platform menu and its images, and saying why when it cannot.</summary>
public sealed partial class TrayIconBridge
{
    bool TryBuildMenu(
        string trayId,
        TrayMenuInput request,
        [NotNullWhen(true)] out TrayMenu? menu,
        out List<TrayMenuNode> nodes,
        [NotNullWhen(false)] out string? error
    )
    {
        menu = null;
        nodes = [];

        var items = request.Items ?? [];
        if (Count(items) > this.options.MaxMenuItems)
        {
            error = $"A tray menu holds at most {this.options.MaxMenuItems} entries, submenus included.";
            return false;
        }

        var built = new TrayMenu();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var generated = 0;

        if (!this.TryBuildItems(trayId, items, built.Items, nodes, ids, ref generated, 1, out error))
            return false;

        menu = built;
        error = null;
        return true;
    }

    bool TryBuildItems(
        string trayId,
        IReadOnlyList<TrayMenuItemInput> requests,
        ICollection<TrayMenuItemBase> target,
        List<TrayMenuNode> nodes,
        HashSet<string> ids,
        ref int generated,
        int depth,
        [NotNullWhen(false)] out string? error
    )
    {
        if (depth > this.options.MaxMenuDepth)
        {
            error = $"Tray submenus nest at most {this.options.MaxMenuDepth} deep.";
            return false;
        }

        foreach (var request in requests)
        {
            var id = request.Id;
            if (String.IsNullOrEmpty(id))
                id = $"item{++generated}";

            if (!IsValidId(id))
            {
                error = $"'{id}' is not a valid menu item id. Use 1-64 letters, digits, '.', '_' or '-'.";
                return false;
            }

            if (!ids.Add(id))
            {
                error = $"Two menu items share the id '{id}'; a click would be ambiguous.";
                return false;
            }

            if (request.Type == TrayMenuItemType.Separator)
            {
                var separator = new TraySeparator { IsVisible = request.Visible };
                target.Add(separator);
                nodes.Add(new TrayMenuNode(id, TrayMenuItemType.Separator, separator, []));
                continue;
            }

            if (String.IsNullOrWhiteSpace(request.Label))
            {
                error = $"Menu item '{id}' needs a label.";
                return false;
            }

            switch (request.Type)
            {
                case TrayMenuItemType.Submenu:
                {
                    var submenu = new TraySubmenu(request.Label)
                    {
                        IsEnabled = request.Enabled,
                        IsVisible = request.Visible
                    };

                    // Filled before the submenu joins the menu: TrayMenu adopts a submenu's children when it
                    // takes the submenu, and only then.
                    List<TrayMenuNode> children = [];
                    if (!this.TryBuildItems(trayId, request.Items ?? [], submenu.Items, children, ids, ref generated, depth + 1, out error))
                        return false;

                    target.Add(submenu);
                    nodes.Add(new TrayMenuNode(id, TrayMenuItemType.Submenu, submenu, children));
                    break;
                }

                case TrayMenuItemType.Check:
                {
                    var check = new TrayCheckMenuItem(request.Label, request.Checked)
                    {
                        IsEnabled = request.Enabled,
                        IsVisible = request.Visible
                    };

                    var itemId = id;
                    var label = request.Label;
                    check.Toggled += (_, value) => this.MenuActivated(trayId, itemId, label, value);

                    target.Add(check);
                    nodes.Add(new TrayMenuNode(id, TrayMenuItemType.Check, check, []));
                    break;
                }

                default:
                {
                    if (!this.TryResolveImage(request.Icon, out var image, out error))
                        return false;

                    var item = new TrayMenuItem(request.Label)
                    {
                        Accelerator = request.Accelerator,
                        IsEnabled = request.Enabled,
                        IsVisible = request.Visible
                    };

                    if (image is not null)
                        item.Icon = image;

                    var itemId = id;
                    var label = request.Label;
                    item.Clicked += (_, _) => this.MenuActivated(trayId, itemId, label, null);

                    target.Add(item);
                    nodes.Add(new TrayMenuNode(id, TrayMenuItemType.Item, item, []));
                    break;
                }
            }
        }

        error = null;
        return true;
    }

    static int Count(IReadOnlyList<TrayMenuItemInput> items)
        => items.Count + items.Sum(x => x.Items is { } children ? Count(children) : 0);

    /// <summary>
    /// The factory a tray icon wants: it re-reads the stream for DPI and theme changes, so this hands back a
    /// way to open the image rather than the image.
    /// </summary>
    bool TryResolveImage(TrayImage? image, out Func<Stream>? factory, [NotNullWhen(false)] out string? error)
    {
        factory = null;
        error = null;

        if (image is null || (image.Data is null or "" && image.Root is null or "" && image.Path is null or ""))
            return true;

        if (image.Data is { Length: > 0 } encoded)
        {
            // A page that draws its own icon on a canvas hands over a data: URI, prefix and all.
            var separator = encoded.IndexOf(',');
            if (separator > 0 && encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                encoded = encoded[(separator + 1)..];

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                error = "An image's 'data' must be base64, optionally as a data: URI.";
                return false;
            }

            if (bytes.Length == 0)
            {
                error = "An image's 'data' decoded to nothing.";
                return false;
            }

            if (bytes.Length > this.options.MaxImageBytes)
            {
                error = $"An image is at most {this.options.MaxImageBytes} bytes; that one decoded to {bytes.Length}.";
                return false;
            }

            factory = () => new MemoryStream(bytes, writable: false);
            return true;
        }

        // The same { root, path } the files bridge takes, so the page cannot point the tray at a file it
        // could not already read.
        if (this.fileRoots is null || !this.fileRoots.TryResolve(image.Root, image.Path, out var fullPath))
        {
            error = "An image is { \"root\": \"…\", \"path\": \"…\" } naming a file root, or { \"data\": \"…\" } holding base64.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"There is no file at {image.Root}/{image.Path}.";
            return false;
        }

        factory = () => File.OpenRead(fullPath);
        return true;
    }
}
