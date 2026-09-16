using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.TrayIcon.Client;

/// <summary>
/// An image: a file the page could already read through the files bridge (<see cref="Root"/> and <see cref="Path"/>), or
/// base64 bytes it built itself (<see cref="Data"/>, with or without a <c>data:</c> URI prefix).
/// </summary>
public sealed record TrayImage(string? Root = null, string? Path = null, string? Data = null);

public enum TrayMenuItemType
{
    /// <summary>A command. Choosing it raises <c>tray.menu</c>.</summary>
    Item,

    /// <summary>A checkbox. Toggling it raises <c>tray.menu</c> with the new checked state.</summary>
    Check,

    Separator,

    /// <summary>A nested menu. Its own items carry the commands.</summary>
    Submenu
}

/// <summary>One menu entry.</summary>
/// <param name="Id">What comes back on <c>tray.menu</c>; assigned when null.</param>
/// <param name="Accelerator">A shortcut such as <c>Ctrl+S</c>.</param>
/// <param name="Items">A submenu's entries.</param>
public sealed record TrayMenuItemInput(
    string? Id = null,
    TrayMenuItemType Type = TrayMenuItemType.Item,
    string? Label = null,
    string? Accelerator = null,
    bool Enabled = true,
    bool Visible = true,
    bool Checked = false,
    TrayImage? Icon = null,
    IReadOnlyList<TrayMenuItemInput>? Items = null
);

/// <summary>A menu: at most 64 entries in all, nested at most 4 deep.</summary>
public sealed record TrayMenuInput(IReadOnlyList<TrayMenuItemInput>? Items = null);

/// <summary>An icon to create, or the changes to one. Properties left null are left as they are.</summary>
/// <param name="TemplateImage">On macOS, let the menu bar tint the icon; use black with alpha.</param>
/// <param name="Badge">A short count or label drawn over the icon.</param>
public sealed record TrayIconInput(
    string? Id = null,
    string? Tooltip = null,
    string? Title = null,
    bool? Visible = null,
    bool? TemplateImage = null,
    string? Badge = null,
    TrayImage? Icon = null,
    TrayMenuInput? Menu = null
);

public sealed record TrayNotification(string? Title = null, string? Message = null);

/// <param name="Frames">2–24 images.</param>
/// <param name="IntervalMs">Time per frame; at least 50 ms.</param>
public sealed record TrayAnimation(IReadOnlyList<TrayImage>? Frames = null, int IntervalMs = 500);

/// <param name="Checked">Null for anything but a check item.</param>
public sealed record TrayMenuItemInfo(
    string Id,
    TrayMenuItemType Type,
    string? Label,
    bool Enabled,
    bool Visible,
    bool? Checked,
    IReadOnlyList<TrayMenuItemInfo>? Items
);

public sealed record TrayIconInfo(
    string Id,
    string? Tooltip,
    string? Title,
    bool Visible,
    bool TemplateImage,
    string? Badge,
    bool HasIcon,
    bool Animating,
    IReadOnlyList<TrayMenuItemInfo> Menu
);

public sealed record TrayStatus(bool Supported, int MaxIcons, IReadOnlyList<TrayIconInfo> Icons);

public enum TrayClickButton { Primary, Secondary, Double }

/// <param name="Id">The icon clicked.</param>
/// <param name="X">Screen coordinates, where the platform reports them.</param>
public sealed record TrayClick(string Id, TrayClickButton Button, int X, int Y);

/// <param name="Id">The icon whose menu it was.</param>
/// <param name="Checked">The new state of a check item; null for anything else.</param>
public sealed record TrayMenuSelection(string Id, string ItemId, string? Label, bool? Checked);

/// <summary>Serialization for every tray contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TrayIconInput))]
[JsonSerializable(typeof(TrayMenuInput))]
[JsonSerializable(typeof(TrayNotification))]
[JsonSerializable(typeof(TrayAnimation))]
[JsonSerializable(typeof(TrayIconInfo))]
[JsonSerializable(typeof(TrayStatus))]
[JsonSerializable(typeof(TrayClick))]
[JsonSerializable(typeof(TrayMenuSelection))]
public partial class TrayJsonContext : JsonSerializerContext;
