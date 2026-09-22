namespace Sample.Blazor.Components;

/// <summary>A page of the sample, and the bridges it shows.</summary>
public sealed record NavItem(string Route, string Label, string Icon, params string[] Bridges);

public sealed record NavGroup(string Title, params NavItem[] Items);

/// <summary>Every page of the sample, grouped as the sidebar shows them.</summary>
public static class SampleNav
{
    public static readonly NavGroup[] Groups =
    [
        new("Overview",
            new NavItem("", "Home", "home")),
        new("Device",
            new NavItem("device", "Device", "phone", "app"),
            new NavItem("sensors", "Sensors", "pulse", "sensors"),
            new NavItem("desktop", "Desktop", "monitor", "tray", "quickentry"),
            new NavItem("background", "Background", "clock", "invoke")),
        new("Location",
            new NavItem("location", "Location", "pin", "gps", "geofences", "motion"),
            new NavItem("map", "Maps", "pin", "maps", "directions")),
        new("Media",
            new NavItem("media", "Media", "film"),
            new NavItem("device-camera", "Camera", "camera", "camera"),
            new NavItem("photos", "Photos", "image", "photos"),
            new NavItem("camera", "Pi camera", "chip", "rpicamera"),
            new NavItem("speech", "Speech", "mic", "speech")),
        new("Connectivity",
            new NavItem("wifi", "Wi-Fi", "wifi", "wifi"),
            new NavItem("bluetooth", "Bluetooth", "bluetooth", "ble"),
            new NavItem("obd", "OBD", "car", "obd"),
            new NavItem("discovery", "Discovery", "radar", "discovery"),
            new NavItem("transfers", "Transfers", "transfer", "transfers")),
        new("Messaging",
            new NavItem("push", "Push", "bell", "push"),
            new NavItem("watch", "Watch", "watch", "wearables"),
            new NavItem("notifications", "Notifications", "message", "notifications"),
            new NavItem("links", "Links", "link", "links")),
        new("Data",
            new NavItem("contacts", "Contacts", "users", "contacts"),
            new NavItem("calendar", "Calendar", "calendar", "calendar"),
            new NavItem("health", "Health", "heart", "health"),
            new NavItem("storage", "Storage", "database", "settings", "files"),
            new NavItem("folders", "Folders", "folder", "folders"))
    ];

    /// <summary>The page that shows <paramref name="bridge"/>, if any.</summary>
    public static NavItem? ForBridge(string bridge)
        => Groups.SelectMany(x => x.Items).FirstOrDefault(x => x.Bridges.Contains(bridge));
}
