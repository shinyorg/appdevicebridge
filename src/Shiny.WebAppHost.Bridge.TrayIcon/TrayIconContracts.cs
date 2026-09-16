using System.Text.Json.Serialization;

namespace Shiny.WebAppHost.Bridge.TrayIcon;

/// <summary>Limits on what the page may ask the tray for. Sensible as they are; raise them if your app needs to.</summary>
public sealed class TrayIconBridgeOptions
{
    /// <summary>How many tray icons may exist at once. One is almost always right.</summary>
    public int MaxIcons { get; set; } = 4;

    /// <summary>How many entries one menu may hold, counting separators and every level of submenu.</summary>
    public int MaxMenuItems { get; set; } = 64;

    /// <summary>How deep submenus may nest.</summary>
    public int MaxMenuDepth { get; set; } = 4;

    /// <summary>The decoded size of an inline <c>data</c> image. A tray icon is a few KB; this is generous.</summary>
    public int MaxImageBytes { get; set; } = 512 * 1024;

    /// <summary>How many frames one animation may cycle.</summary>
    public int MaxAnimationFrames { get; set; } = 24;

    /// <summary>The fastest an animation may cycle, however small an interval the page asks for.</summary>
    public TimeSpan MinAnimationInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Where an icon comes from: a file the page could already read through <c>/_bridge/files</c>, or bytes it
/// built itself.
/// <code>
/// { "root": "data", "path": "icons/tray.png" }
/// { "data": "iVBORw0KGgo…" }            base64, with or without a data: URI prefix
/// </code>
/// </summary>
public sealed record TrayImage(string? Root = null, string? Path = null, string? Data = null);

public enum TrayMenuItemType
{
    /// <summary>A command. Clicking it sends <c>tray.menu</c>.</summary>
    Item,

    /// <summary>A checkbox. Toggling it sends <c>tray.menu</c> with the new <c>checked</c>.</summary>
    Check,

    Separator,

    /// <summary>A nested menu. Its own <c>items</c> carry the commands.</summary>
    Submenu
}

/// <summary>One entry in a tray menu. <c>id</c> is what comes back on <c>tray.menu</c>; omit it and one is assigned.</summary>
public sealed record TrayMenuItemRequest(
    string? Id = null,
    TrayMenuItemType Type = TrayMenuItemType.Item,
    string? Label = null,
    string? Accelerator = null,
    bool Enabled = true,
    bool Visible = true,
    bool Checked = false,
    TrayImage? Icon = null,
    List<TrayMenuItemRequest>? Items = null
);

public sealed record TrayMenuRequest(List<TrayMenuItemRequest>? Items = null);

/// <summary>
/// Creating or updating an icon. On an update only the properties present are changed — send <c>""</c> to
/// clear <c>tooltip</c>, <c>title</c> or <c>badge</c>.
/// </summary>
public sealed record TrayIconRequest(
    string? Id = null,
    string? Tooltip = null,
    string? Title = null,
    bool? Visible = null,
    bool? TemplateImage = null,
    string? Badge = null,
    TrayImage? Icon = null,
    TrayMenuRequest? Menu = null
);

public sealed record TrayNotificationRequest(string? Title = null, string? Message = null);

public sealed record TrayAnimationRequest(List<TrayImage>? Frames = null, int IntervalMs = 500);

public sealed record TrayMenuItemResponse(
    string Id,
    TrayMenuItemType Type,
    string? Label,
    bool Enabled,
    bool Visible,
    bool? Checked,
    IReadOnlyList<TrayMenuItemResponse>? Items
);

public sealed record TrayIconResponse(
    string Id,
    string? Tooltip,
    string? Title,
    bool Visible,
    bool TemplateImage,
    string? Badge,
    bool HasIcon,
    bool Animating,
    IReadOnlyList<TrayMenuItemResponse> Menu
);

public sealed record TrayStatusResponse(bool Supported, int MaxIcons, IReadOnlyList<TrayIconResponse> Icons);

public enum TrayClickButton
{
    Primary,
    Secondary,
    Double
}

/// <summary>The payload of <c>tray.click</c>, in the page and in <c>background.js</c>.</summary>
public sealed record TrayClickPayload(string Id, TrayClickButton Button, int X, int Y);

/// <summary>The payload of <c>tray.menu</c>. <c>checked</c> is null for anything but a check item.</summary>
public sealed record TrayMenuPayload(string Id, string ItemId, string? Label, bool? Checked);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TrayIconRequest))]
[JsonSerializable(typeof(TrayMenuRequest))]
[JsonSerializable(typeof(TrayNotificationRequest))]
[JsonSerializable(typeof(TrayAnimationRequest))]
[JsonSerializable(typeof(TrayIconResponse))]
[JsonSerializable(typeof(TrayStatusResponse))]
[JsonSerializable(typeof(TrayClickPayload))]
[JsonSerializable(typeof(TrayMenuPayload))]
partial class TrayBridgeJsonContext : JsonSerializerContext;
