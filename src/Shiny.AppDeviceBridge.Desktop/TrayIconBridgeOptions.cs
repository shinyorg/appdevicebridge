namespace Shiny.AppDeviceBridge.Desktop;

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
