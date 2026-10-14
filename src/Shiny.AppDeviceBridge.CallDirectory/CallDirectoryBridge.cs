using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.CallDirectory.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.CallDirectory;

namespace Shiny.AppDeviceBridge.CallDirectory;

public static class CallDirectoryBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/calldirectory</c> and registers Shiny's call directory manager — there is nothing else to call in
    /// code. The iOS app also needs <c>&lt;ShinyCallDirectoryExtension&gt;true&lt;/ShinyCallDirectoryExtension&gt;</c> and
    /// <c>&lt;ShinyCallDirectoryAppGroup&gt;</c> in its project file, which build the extension.
    /// <code>
    /// bridge.AddCallDirectoryBridge(o => o.DefaultCountryCode = 44);
    /// </code>
    /// <para>
    /// An app that already called <c>AddCallDirectory</c> keeps its registration and options, and
    /// <paramref name="configure"/> is not used. iOS and Android 10+; everywhere else answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddCallDirectoryBridge<TBuilder>(this TBuilder bridge, Action<Native.CallDirectoryOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (!bridge.Services.Any(x => x.ServiceType == typeof(Native.ICallDirectoryManager)))
            bridge.Services.AddCallDirectory(configure);

        bridge.AddBridge<CallDirectoryBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/calldirectory</c> over <see cref="Native.ICallDirectoryManager"/>.
/// <code>
/// GET    /_bridge/calldirectory                         { "access": "Available", "count": 42 }
/// POST   /_bridge/calldirectory/access                  { "access": "Available" }
/// GET    /_bridge/calldirectory/entries/{phoneNumber}   { "phoneNumber": 18005550100, "label": "Acme Support", "type": "Identify" }
/// PUT    /_bridge/calldirectory/entries                 { "entries": [ … ] }
/// POST   /_bridge/calldirectory/entries/remove          { "phoneNumbers": [ 18005550100 ] }
/// POST   /_bridge/calldirectory/entries/replace         { "entries": [ … ] }
/// DELETE /_bridge/calldirectory/entries
/// POST   /_bridge/calldirectory/reload                  { "result": "Success" }
/// POST   /_bridge/calldirectory/normalize               { "number": "+1 (800) 555-0100" }  →  { "phoneNumber": 18005550100 }
/// </code>
/// </summary>
public sealed class CallDirectoryBridge(IServiceProvider services) : IWebAppBridge
{
    // E.164: at most 15 digits, country code included.
    const long MaxPhoneNumber = 999_999_999_999_999;
    const int MaxLabelLength = 256;

    readonly Native.ICallDirectoryManager? manager = services.GetOptionalService<Native.ICallDirectoryManager>();
    readonly Native.CallDirectoryOptions options = services.GetOptionalService<Native.CallDirectoryOptions>() ?? new();

    public string Name => "calldirectory";

    // Shiny.Mobile.CallDirectory registers a no-op manager where there is no call directory.
    public bool IsSupported => this.manager is { IsSupported: true };

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.AccessAsync)
        .MapGet("/entries/{phoneNumber}", this.GetEntryAsync)
        .MapPut("/entries", this.SetAsync)
        .MapPost("/entries/remove", this.RemoveAsync)
        .MapPost("/entries/replace", this.ReplaceAllAsync)
        .MapDelete("/entries", this.ClearAsync)
        .MapPost("/reload", this.ReloadAsync)
        .MapPost("/normalize", this.NormalizeAsync);

    ValueTask StatusAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var status = new CallDirectoryStatus(ToContract(await m.GetCurrentAccess()), await m.Count(context.RequestAborted));
        await WebAppBridgeResults.Json(context, status, CallDirectoryJsonContext.Default.CallDirectoryStatus);
    });

    ValueTask AccessAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        // Opens Settings on iOS and the role dialog on Android.
        var access = await services
            .GetRequiredService<IWebAppMainThread>()
            .InvokeAsync(() => m.RequestAccess(context.RequestAborted));

        await WebAppBridgeResults.Json(context, new CallDirectoryAccessResult(ToContract(access)), CallDirectoryJsonContext.Default.CallDirectoryAccessResult);
    });

    ValueTask GetEntryAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (!Int64.TryParse(context.Request.RouteValues["phoneNumber"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || !IsPhoneNumber(number))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a phone number: country code and digits, such as 18005550100.");
            return;
        }

        if (await m.Get(number, context.RequestAborted) is not { } entry)
        {
            await WebAppBridgeResults.NotFound(context, $"{number} is not in the call directory.");
            return;
        }

        await WebAppBridgeResults.Json(context, ToContract(entry), CallDirectoryJsonContext.Default.CallDirectoryEntry);
    });

    ValueTask SetAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await ReadEntriesAsync(context) is { } entries)
        {
            await m.Set(entries, context.RequestAborted);
            await WebAppBridgeResults.NoContent(context);
        }
    });

    ValueTask ReplaceAllAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await ReadEntriesAsync(context) is { } entries)
        {
            await m.ReplaceAll(entries, context.RequestAborted);
            await WebAppBridgeResults.NoContent(context);
        }
    });

    ValueTask RemoveAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, CallDirectoryJsonContext.Default.CallDirectoryRemoveRequest) is not { PhoneNumbers: { } numbers }
            || !numbers.All(IsPhoneNumber))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"phoneNumbers\": [ 18005550100 ] }.");
            return;
        }

        await m.Remove(numbers, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ClearAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        await m.Clear(context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask ReloadAsync(HttpContext context) => this.RunAsync(context, async m =>
    {
        var result = await m.Reload(context.RequestAborted);
        var contract = new CallDirectoryReload(BridgeEnum.Convert<Native.CallDirectoryReloadResult, CallDirectoryReloadResult>(result));
        await WebAppBridgeResults.Json(context, contract, CallDirectoryJsonContext.Default.CallDirectoryReload);
    });

    ValueTask NormalizeAsync(HttpContext context) => this.RunAsync(context, async _ =>
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, CallDirectoryJsonContext.Default.NormalizeNumberRequest) is not { Number: { } number } body
            || body.DefaultCountryCode is < 1 or > 999)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"number\": \"+1 (800) 555-0100\" }, and a defaultCountryCode of 1 to 999.");
            return;
        }

        long normalized;
        try
        {
            normalized = Native.PhoneNumbers.ToCallDirectory(number, body.DefaultCountryCode ?? this.options.DefaultCountryCode);
        }
        catch (FormatException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
            return;
        }

        await WebAppBridgeResults.Json(context, new NormalizedNumber(normalized), CallDirectoryJsonContext.Default.NormalizedNumber);
    });

    /// <summary>The entries of a set or replace, or null once it has answered 400.</summary>
    static async ValueTask<IReadOnlyList<Native.CallDirectoryEntry>?> ReadEntriesAsync(HttpContext context)
    {
        if (await WebAppBridgeResults.ReadBodyAsync(context, CallDirectoryJsonContext.Default.CallDirectoryEntriesRequest) is not { Entries: { } entries })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"entries\": [ { \"phoneNumber\": 18005550100, \"label\": \"…\", \"type\": \"Identify\" } ] }.");
            return null;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var problem = entries[i] switch
            {
                null => "is missing",
                { PhoneNumber: var n } when !IsPhoneNumber(n) => "phoneNumber is a country code and digits, at most 15 of them",
                { Label.Length: > MaxLabelLength } => $"label is at most {MaxLabelLength} characters",
                { Type: var t } when !Enum.IsDefined(t) => "type is Block, Identify or Allow",
                _ => null
            };
            if (problem is not null)
            {
                await WebAppBridgeResults.BadRequest(context, $"entries[{i}]: {problem}.");
                return null;
            }
        }

        return [.. entries.Select(x => new Native.CallDirectoryEntry(
            x.PhoneNumber,
            x.Label,
            BridgeEnum.Convert<CallDirectoryEntryType, Native.CallDirectoryEntryType>(x.Type)
        ))];
    }

    static bool IsPhoneNumber(long number) => number is > 0 and <= MaxPhoneNumber;

    /// <summary>501 where there is no call directory.</summary>
    async ValueTask RunAsync(HttpContext context, Func<Native.ICallDirectoryManager, Task> action)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "The call directory");
            return;
        }
        await action(this.manager!);
    }

    static Shiny.AppDeviceBridge.Client.AccessState ToContract(AccessState access)
        => BridgeEnum.Convert<AccessState, Shiny.AppDeviceBridge.Client.AccessState>(access);

    internal static CallDirectoryEntry ToContract(Native.CallDirectoryEntry entry) => new(
        entry.PhoneNumber,
        entry.Label,
        BridgeEnum.Convert<Native.CallDirectoryEntryType, CallDirectoryEntryType>(entry.Type)
    );
}
