using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.AgeSignals.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.AgeSignals;

namespace Shiny.AppDeviceBridge.AgeSignals;

public static class AgeSignalsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/agesignals</c> and registers Shiny's <c>IAgeSignals</c> — there is nothing else to call in code.
    /// <code>
    /// bridge.AddAgeSignalsBridge();
    /// </code>
    /// <para>
    /// Apple needs the <c>com.apple.developer.declared-age-range</c> entitlement; without it the request never returns.
    /// Google Play answers only for an app installed from Play. iOS, macOS and Android; everywhere else answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddAgeSignalsBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        bridge.Services.AddAgeSignals();
        bridge.AddBridge<AgeSignalsBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/agesignals</c> over <see cref="Native.IAgeSignals"/>.
/// <code>
/// GET  /_bridge/agesignals                       { "isSupported": true, "isEligible": true, "ageRangeRequired": true, … }
/// POST /_bridge/agesignals/request               { "gates": [ 13, 16, 18 ] }  →  { "status": "Shared", "lowerBound": 18, "source": "Confirmed", … }
/// POST /_bridge/agesignals/significant-update    { "description": "Adds chat" }  →  { "shown": true }
/// </code>
/// </summary>
public sealed class AgeSignalsBridge : IWebAppBridge
{
    const int MaxGates = 3;
    const int MaxDescriptionLength = 1024;

    readonly IServiceProvider services;
    readonly Native.IAgeSignals? ageSignals;
    readonly bool storePlatform;

    public AgeSignalsBridge(IServiceProvider services) : this(services, IsStorePlatform) { }

    /// <param name="storePlatform">Whether this OS has a store age API; tests pass it so they run on any host.</param>
    internal AgeSignalsBridge(IServiceProvider services, bool storePlatform)
    {
        this.services = services;
        this.ageSignals = services.GetOptionalService<Native.IAgeSignals>();
        this.storePlatform = storePlatform;
    }

    public string Name => "agesignals";

    // Shiny.AgeSignals reports NotSupported off the stores' platforms (Mac Catalyst included); an older iOS, macOS or
    // Android still answers, with isSupported false.
    public bool IsSupported => this.ageSignals is not null && this.storePlatform;

    internal static bool IsStorePlatform =>
        OperatingSystem.IsAndroid() ||
        OperatingSystem.IsMacOS() ||
        (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst());

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.EligibilityAsync)
        .MapPost("/request", this.RequestAsync)
        .MapPost("/significant-update", this.SignificantUpdateAsync);

    ValueTask EligibilityAsync(HttpContext context) => this.RunAsync(context, async a =>
    {
        var eligibility = await a.GetEligibility(context.RequestAborted);
        await WebAppBridgeResults.Json(context, ToContract(eligibility), AgeSignalsJsonContext.Default.AgeSignalsEligibility);
    });

    ValueTask RequestAsync(HttpContext context) => this.RunAsync(context, async a =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, AgeSignalsJsonContext.Default.AgeRangeRequest);
        if (body?.Gates is not { Count: > 0 and <= MaxGates } gates || !IsAscending(gates))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"gates\": [ 13, 16, 18 ] }}: one to {MaxGates} positive ages, ascending.");
            return;
        }

        // Apple presents its sheet from the key window.
        var signal = await this.services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => a.RequestAgeRange(
                gates[0],
                gates.Count > 1 ? gates[1] : null,
                gates.Count > 2 ? gates[2] : null,
                context.RequestAborted
            ));

        await WebAppBridgeResults.Json(context, ToContract(signal), AgeSignalsJsonContext.Default.AgeSignal);
    });

    ValueTask SignificantUpdateAsync(HttpContext context) => this.RunAsync(context, async a =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, AgeSignalsJsonContext.Default.SignificantUpdateRequest);
        if (String.IsNullOrWhiteSpace(body?.Description) || body.Description.Length > MaxDescriptionLength)
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"description\": \"…\" }}, at most {MaxDescriptionLength} characters.");
            return;
        }

        var shown = await this.services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => a.ShowSignificantUpdate(body.Description, context.RequestAborted));

        await WebAppBridgeResults.Json(context, new SignificantUpdateResult(shown), AgeSignalsJsonContext.Default.SignificantUpdateResult);
    });

    static bool IsAscending(IReadOnlyList<int> gates)
    {
        for (var i = 0; i < gates.Count; i++)
        {
            if (gates[i] <= 0 || (i > 0 && gates[i] <= gates[i - 1]))
                return false;
        }
        return true;
    }

    /// <summary>501 off the stores' platforms; the store's failures as the errors the page switches on.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.IAgeSignals, Task> action)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Age signals");
            return;
        }

        try
        {
            await action(this.ageSignals!);
        }
        catch (Native.AgeSignalsException ex)
        {
            var (status, code) = ToError(ex.Reason);
            var message = ex.NativeCode is { } native ? $"{ex.Message} (store code {native})" : ex.Message;
            await WebAppBridgeResults.Error(context, status, code, message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    internal static (int Status, string Code) ToError(Native.AgeSignalsErrorReason reason) => reason switch
    {
        Native.AgeSignalsErrorReason.NotAvailable => (StatusCodes.Status503ServiceUnavailable, "not_available"),
        Native.AgeSignalsErrorReason.InvalidAccount => (StatusCodes.Status409Conflict, "invalid_account"),
        Native.AgeSignalsErrorReason.Network => (StatusCodes.Status503ServiceUnavailable, "network"),
        Native.AgeSignalsErrorReason.Transient => (StatusCodes.Status503ServiceUnavailable, "transient"),
        Native.AgeSignalsErrorReason.StoreUnavailable => (StatusCodes.Status503ServiceUnavailable, "store_unavailable"),
        Native.AgeSignalsErrorReason.AppNotOwned => (StatusCodes.Status403Forbidden, "app_not_owned"),
        Native.AgeSignalsErrorReason.NoUserInterface => (StatusCodes.Status409Conflict, "no_user_interface"),
        _ => (StatusCodes.Status502BadGateway, "age_signals_failed")
    };

    internal static AgeSignalsEligibility ToContract(Native.AgeSignalsEligibility e) => new(
        e.IsSupported,
        e.IsEligible,
        e.AgeRangeRequired,
        e.SignificantChangeRequiresParentalConsent,
        e.SignificantChangeRequiresAdultNotification
    );

    internal static AgeSignal ToContract(Native.AgeSignal s) => new(
        BridgeEnum.Convert<Native.AgeSignalStatus, AgeSignalStatus>(s.Status),
        s.LowerBound,
        s.UpperBound,
        BridgeEnum.Convert<Native.AgeRangeSource, AgeRangeSource>(s.Source),
        s.CommunicationLimits,
        s.SignificantChange is { } change ? BridgeEnum.Convert<Native.SignificantChangeStatus, SignificantChangeStatus>(change) : null,
        s.SignificantChangeApprovedThrough,
        s.InstallId
    );
}
