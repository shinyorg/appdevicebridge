using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.AppSupport.Client;

namespace Shiny.AppDeviceBridge.AppSupport;

/// <summary>
/// The launch-at-login half of <c>/_bridge/app</c>, over <see cref="IStartupService"/> — whether this app starts
/// when the user logs in.
/// <code>
/// GET    /_bridge/app/startup                { "supported": true, "state": "Enabled" }
/// POST   /_bridge/app/startup/registration   launch at login
/// DELETE /_bridge/app/startup/registration   stop launching at login
/// POST   /_bridge/app/startup/settings       opens the OS screen; { "opened": false } where there is none
/// </code>
/// <para>
/// Desktop only: Windows writes the app under <c>HKCU\…\CurrentVersion\Run</c> (unpackaged apps only — the OS
/// virtualizes that key for MSIX), macOS 13+ submits the running bundle to <c>SMAppService</c>, and Linux writes
/// <c>~/.config/autostart/{Identifier}.desktop</c>. Mobile has no such list and answers
/// <c>{ "supported": false, "state": "NotSupported" }</c>.
/// </para>
/// <para>
/// <c>state</c> is read back from the OS on every call rather than remembered, because the user can turn a
/// registered app off in Task Manager, System Settings or Login Items without the app hearing about it. A
/// registration that comes back <c>DisabledByUser</c>, <c>DisabledByPolicy</c> or <c>RequiresApproval</c> did not
/// fail — the user has to finish it in the OS, which is what <c>startup/settings</c> is for.
/// </para>
/// </summary>
public sealed partial class AppSupportBridge
{
    readonly IStartupService? startup;

    void MapStartup(WebAppBridgeRoutes routes) => routes
        .MapGet("/startup", this.StartupStateAsync)
        .MapPost("/startup/registration", this.RegisterStartupAsync)
        .MapDelete("/startup/registration", this.UnregisterStartupAsync)
        .MapPost("/startup/settings", this.OpenStartupSettingsAsync);

    /// <summary>Always answers, so a page can decide whether to show a "run at startup" toggle at all.</summary>
    ValueTask StartupStateAsync(HttpContext context)
        => this.startup is { IsSupported: true } s
            ? StartupResult(context, () => s.GetState(context.RequestAborted), true)
            : WebAppBridgeResults.Json(
                context,
                new Contracts.StartupStatus(false, Contracts.StartupState.NotSupported),
                Contracts.AppJsonContext.Default.StartupStatus
            );

    ValueTask RegisterStartupAsync(HttpContext context)
        => this.WithStartup(context, s => s.Register(context.RequestAborted));

    ValueTask UnregisterStartupAsync(HttpContext context)
        => this.WithStartup(context, s => s.Unregister(context.RequestAborted));

    async ValueTask OpenStartupSettingsAsync(HttpContext context)
    {
        if (this.startup is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Startup registration");
            return;
        }

        // Presents an OS window, so it belongs on the main thread; Linux has nothing to open and says so.
        var opened = await OnMainThread(s.OpenSettings);
        await WebAppBridgeResults.Json(context, new Contracts.StartupSettingsResult(opened), Contracts.AppJsonContext.Default.StartupSettingsResult);
    }

    async ValueTask WithStartup(HttpContext context, Func<IStartupService, Task<StartupServiceState>> action)
    {
        if (this.startup is not { IsSupported: true } s)
        {
            await WebAppBridgeResults.NotSupported(context, "Startup registration");
            return;
        }

        await StartupResult(context, () => action(s), true);
    }

    /// <summary>
    /// The OS rejecting a registration, and a misconfigured identifier or executable path, both surface as
    /// <see cref="InvalidOperationException"/>; the page gets one code it can switch on.
    /// </summary>
    static async ValueTask StartupResult(HttpContext context, Func<Task<StartupServiceState>> action, bool supported)
    {
        try
        {
            await WebAppBridgeResults.Json(
                context,
                new Contracts.StartupStatus(supported, Convert<StartupServiceState, Contracts.StartupState>(await action())),
                Contracts.AppJsonContext.Default.StartupStatus
            );
        }
        catch (InvalidOperationException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "startup_failed", ex.Message);
        }
    }
}
