using Microsoft.JSInterop;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.HttpTransfers.Client;
using Shiny.AppDeviceBridge.Beacons.Client;
using Shiny.AppDeviceBridge.Geofencing.Client;
using Shiny.AppDeviceBridge.Gps.Client;
using Shiny.AppDeviceBridge.RegionGeofencing.Client;
using Shiny.AppDeviceBridge.Notifications.Client;
using Shiny.AppDeviceBridge.Push.Client;
using Shiny.AppDeviceBridge.Wearables.Client;
using Shiny.AppDeviceBridge.Desktop.Client;

namespace Sample.Blazor;

/// <summary>
/// Native calls the page answers while it is open — GPS, geofences, pushes, watch messages. With the page closed none of
/// these run; the Native calls page lists what arrived.
/// </summary>
public sealed class NativeCallHandlers(WebAppNativeCalls nativeCalls, IQuickEntryBridge quickEntry, IWindowBridge window)
{
    readonly List<string> log = [];
    bool started;

    public IReadOnlyList<string> Calls => this.log;

    public event Action? Changed;

    public async Task StartAsync()
    {
        if (this.started)
            return;

        this.started = true;

        try
        {
            await this.Record("gps", GpsJsonContext.Default.GpsReading, x => $"{x.Latitude:0.0000}, {x.Longitude:0.0000}");
            await this.Record("geofence", GeofencingJsonContext.Default.GeofenceStatus, x => $"{x.Identifier} {x.State}");
            await this.Record("regiongeofence", RegionGeofencingJsonContext.Default.RegionChange, x => $"{(x.Entered ? "entered" : "left")} {x.RegionSet} {x.RegionName ?? x.RegionId}");
            await this.Record("motion", GpsJsonContext.Default.MotionActivity, x => $"{x.Activity} ({x.Confidence})");
            await this.Record("beacon", BeaconsJsonContext.Default.BeaconRegionStatus, x => $"{x.Identifier} {x.State}");
            await this.Record("push.received", PushJsonContext.Default.PushPayload, x => x.Title ?? x.Message ?? "(data only)");
            await this.Record("push.entry", PushJsonContext.Default.PushPayload, x => x.Title ?? x.Message ?? "(data only)");
            await this.Record("notification.entry", NotificationsJsonContext.Default.NotificationEvent, x => $"{x.Id} {x.Action ?? x.Title}");
            await this.Record("notification.received", NotificationsJsonContext.Default.NotificationEvent, x => $"{x.Id} {x.Title}");
            // The tray runs the app while its window is hidden: this page is still loaded then, so it answers the menu.
            await nativeCalls.HandleAsync("tray.click", TrayJsonContext.Default.TrayClick, async click =>
            {
                this.Record("tray.click", $"{click.Id} {click.Button}");
                if (click.Button == TrayClickButton.Double)
                    await window.ShowAsync();
            });
            await nativeCalls.HandleAsync("tray.menu", TrayJsonContext.Default.TrayMenuSelection, async selection =>
            {
                this.Record("tray.menu", $"{selection.Id} {selection.ItemId}");
                if (selection.ItemId == "open")
                    await window.ShowAsync();
                else if (selection.ItemId == "quit")
                    await window.QuitAsync();
            });
            // The page answers quick entry while it is open.
            await nativeCalls.HandleAsync("quickentry.submitted", QuickEntryJsonContext.Default.QuickEntrySubmission, async submission =>
            {
                this.Record("quickentry.submitted", submission.Text);
                await quickEntry.SetPromptAsync(new QuickEntryPromptInput(IsBusy: true, BusyText: "Thinking…"));
                await Task.Delay(600);    // stands in for real work
                await quickEntry.SetPromptAsync(new QuickEntryPromptInput(
                    IsBusy: false,
                    Response: $"The page heard \"{submission.Text}\"{(submission.Suggestion is { Value: { } value } ? $" (suggestion {value})" : "")}."
                ));
            });

            // What this returns is the reply the watch gets while the page is open.
            await nativeCalls.HandleAsync("wearables.message", WearablesJsonContext.Default.WearableMessage, SampleJson.Default.WatchReply, message =>
            {
                this.Record("wearables.message", message.Path);
                return Task.FromResult(new WatchReply(AnsweredBy: "page", Path: message.Path));
            });
            await this.Record("wearables.transfer", WearablesJsonContext.Default.WearableTransfer, x => $"{x.Path} {x.Id}");
            await this.Record("wearables.file", WearablesJsonContext.Default.WearableReceivedFile, x => $"{x.FileName} → {x.File.Root}/{x.File.Path}");

            await this.Record("transfer.completed", TransfersJsonContext.Default.TransferInfo, x => $"{x.Type} {x.Path}");
            await this.Record("transfer.failed", TransfersJsonContext.Default.TransferInfo, x => $"{x.Type} {x.Path}: {x.Error}");
        }
        catch (JSException ex)
        {
            // Opened straight from the dev server in a desktop browser: there is no host, so no /_bridge.
            this.log.Add($"Native calls are unavailable outside the app: {ex.Message}");
        }
    }

    Task<IAsyncDisposable> Record<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> payload, Func<T, string> describe)
        => nativeCalls.HandleAsync(name, payload, x =>
        {
            this.Record(name, describe(x));
            return Task.CompletedTask;
        });

    void Record(string name, string detail)
    {
        this.log.Insert(0, $"{DateTime.Now:T}  {name}  {detail}");

        if (this.log.Count > 50)
            this.log.RemoveAt(this.log.Count - 1);

        this.Changed?.Invoke();
    }
}
