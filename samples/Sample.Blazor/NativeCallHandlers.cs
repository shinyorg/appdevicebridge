using Microsoft.JSInterop;
using Shiny.AppDeviceBridge.Blazor;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.HttpTransfers.Client;
using Shiny.AppDeviceBridge.Locations.Client;
using Shiny.AppDeviceBridge.Notifications.Client;
using Shiny.AppDeviceBridge.Push.Client;
using Shiny.AppDeviceBridge.Wearables.Client;
using Shiny.AppDeviceBridge.Desktop.Client;

namespace Sample.Blazor;

/// <summary>
/// The page's side of background work. While the app is open these handle the native calls; when it is not,
/// wwwroot/background.js handles the same names.
/// </summary>
public sealed class NativeCallHandlers(WebAppNativeCalls nativeCalls, IQuickEntryBridge quickEntry)
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
            await nativeCalls.HandleAsync("job:sync", AppDeviceBridgeJsonContext.Default.JobRun, SampleJson.Default.JobResult, async job =>
            {
                this.Record("job:sync", job.Name);
                await Task.Delay(250);    // stands in for real work
                return new JobResult(RanIn: "page");
            });

            await this.Record("gps", LocationsJsonContext.Default.GpsReading, x => $"{x.Latitude:0.0000}, {x.Longitude:0.0000}");
            await this.Record("geofence", LocationsJsonContext.Default.GeofenceStatus, x => $"{x.Identifier} {x.State}");
            await this.Record("motion", LocationsJsonContext.Default.MotionActivity, x => $"{x.Activity} ({x.Confidence})");
            await this.Record("push.received", PushJsonContext.Default.PushPayload, x => x.Title ?? x.Message ?? "(data only)");
            await this.Record("push.entry", PushJsonContext.Default.PushPayload, x => x.Title ?? x.Message ?? "(data only)");
            await this.Record("notification.entry", NotificationsJsonContext.Default.NotificationEvent, x => $"{x.Id} {x.Action ?? x.Title}");
            await this.Record("notification.received", NotificationsJsonContext.Default.NotificationEvent, x => $"{x.Id} {x.Title}");
            await this.Record("tray.click", TrayJsonContext.Default.TrayClick, x => $"{x.Id} {x.Button}");
            await this.Record("tray.menu", TrayJsonContext.Default.TrayMenuSelection, x => $"{x.Id} {x.ItemId}");
            // The page answers quick entry while it is open; background.js answers with the window closed.
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

            // What this returns is the reply the watch gets; background.js answers the same name with the app closed.
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
