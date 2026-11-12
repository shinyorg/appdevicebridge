using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Maui;

public static class PasskeysBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/passkeys</c> for the relying party ids the app uses — options for any other id are refused. Call
    /// it again to add more.
    /// <code>
    /// bridge.AddPasskeysBridge("example.com");
    /// </code>
    /// <para>
    /// Each id needs the platform's association with the app: an associated domain (<c>webcredentials:example.com</c>
    /// in the entitlements, and the app in the site's <c>apple-app-site-association</c>) on iOS and Mac Catalyst, and the
    /// app's signing certificate in the site's <c>assetlinks.json</c> on Android. Windows Hello needs nothing. The macOS
    /// AppKit and Linux heads answer 501.
    /// </para>
    /// </summary>
    public static TBuilder AddPasskeysBridge<TBuilder>(this TBuilder bridge, params IEnumerable<string> relyingPartyIds)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(relyingPartyIds);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(PasskeysBridgeOptions))?.ImplementationInstance as PasskeysBridgeOptions;
        if (options is null)
        {
            options = new PasskeysBridgeOptions();
            services.AddSingleton(options);
        }

        foreach (var id in relyingPartyIds)
            options.Add(id);

        if (options.RelyingPartyIds.Count == 0)
            throw new InvalidOperationException("AddPasskeysBridge needs at least one relying party id, such as \"example.com\".");

        bridge.AddBridge<PasskeysBridge>();
        return bridge;
    }
}

/// <summary>The relying party ids the passkeys bridge accepts.</summary>
public sealed class PasskeysBridgeOptions
{
    readonly SortedSet<string> ids = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> RelyingPartyIds => this.ids;

    /// <summary>A relying party id is a host name — <c>example.com</c>, not <c>https://example.com/</c>.</summary>
    internal void Add(string id)
    {
        if (String.IsNullOrWhiteSpace(id) || Uri.CheckHostName(id) != UriHostNameType.Dns)
            throw new InvalidOperationException($"'{id}' is not a relying party id; use the site's host name, such as \"example.com\".");

        this.ids.Add(id);
    }

    internal bool Allows(string id) => this.ids.Contains(id);
}

/// <summary>
/// <c>/_bridge/passkeys</c> over MAUI Essentials' <see cref="IPasskeys"/>. The options and responses are the relying
/// party server's WebAuthn JSON, passed through untouched.
/// <code>
/// GET  /_bridge/passkeys            { "supported": true, "relyingPartyIds": [ "example.com" ] }
/// POST /_bridge/passkeys/create     { "options": { "rp": { "id": "example.com", … }, "challenge": "…", … } }  →  { "id": "…", "response": { … } }
/// POST /_bridge/passkeys/assert     { "options": { "rpId": "example.com", "challenge": "…", … } }            →  { "id": "…", "userHandle": "…", "response": { … } }
/// </code>
/// </summary>
public sealed class PasskeysBridge : IWebAppBridge
{
    readonly IServiceProvider services;
    readonly PasskeysBridgeOptions options;
    readonly IPasskeyAuthenticator authenticator;

    public PasskeysBridge(IServiceProvider services, PasskeysBridgeOptions options) : this(services, options, new EssentialsPasskeyAuthenticator()) { }

    /// <param name="authenticator">The platform authenticator; tests pass a fake so they run on any host.</param>
    internal PasskeysBridge(IServiceProvider services, PasskeysBridgeOptions options, IPasskeyAuthenticator authenticator)
    {
        this.services = services;
        this.options = options;
        this.authenticator = authenticator;
    }

    public string Name => "passkeys";

    public bool IsSupported => this.authenticator.IsSupported;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.StatusAsync)
        .MapPost("/create", this.CreateAsync)
        .MapPost("/assert", this.AssertAsync);

    ValueTask StatusAsync(HttpContext context) => WebAppBridgeResults.Json(
        context,
        new PasskeyStatus(this.IsSupported, [.. this.options.RelyingPartyIds]),
        PasskeysJsonContext.Default.PasskeyStatus
    );

    async ValueTask CreateAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Passkeys");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, PasskeysJsonContext.Default.PasskeyCreateRequest);
        var rpId = body is null ? null : CreationRelyingParty(body.Options);
        if (!await this.AllowedAsync(context, rpId, "{ \"options\": { \"rp\": { \"id\": \"example.com\" }, … } }"))
            return;

        var json = body!.Options.GetRawText();
        await this.CeremonyAsync(context, async () =>
        {
            var credential = await this.authenticator.CreateAsync(json, body.PreferImmediatelyAvailable, context.RequestAborted);
            await WebAppBridgeResults.Json(
                context,
                new PasskeyCreateResult(credential.Id, Parse(credential.Json)),
                PasskeysJsonContext.Default.PasskeyCreateResult
            );
        });
    }

    async ValueTask AssertAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Passkeys");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, PasskeysJsonContext.Default.PasskeyAssertRequest);
        var rpId = body is null ? null : RequestRelyingParty(body.Options);
        if (!await this.AllowedAsync(context, rpId, "{ \"options\": { \"rpId\": \"example.com\", … } }"))
            return;

        var json = body!.Options.GetRawText();
        await this.CeremonyAsync(context, async () =>
        {
            var credential = await this.authenticator.AssertAsync(json, body.PreferImmediatelyAvailable, context.RequestAborted);
            await WebAppBridgeResults.Json(
                context,
                new PasskeyAssertResult(credential.Id, credential.UserHandle, Parse(credential.Json)),
                PasskeysJsonContext.Default.PasskeyAssertResult
            );
        });
    }

    // A WebAuthn client derives the relying party from the page's origin. The page here is on loopback, so the id comes
    // from the options instead — and only an id the app registered is passed to the authenticator.
    async ValueTask<bool> AllowedAsync(HttpContext context, string? rpId, string expected)
    {
        if (String.IsNullOrWhiteSpace(rpId))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {expected}: the relying party server's WebAuthn options, with the relying party id.");
            return false;
        }

        if (!this.options.Allows(rpId))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "rp_not_allowed", $"The app does not accept passkeys for '{rpId}'. Add it with AddPasskeysBridge.");
            return false;
        }
        return true;
    }

    async ValueTask CeremonyAsync(HttpContext context, Func<Task> ceremony)
    {
        try
        {
            // The sheet is shown from the current window or activity
            await this.services.GetRequiredService<IWebAppMainThread>().InvokeAsync(ceremony);
        }
        catch (FeatureNotSupportedException)
        {
            await WebAppBridgeResults.NotSupported(context, "Passkeys");
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "cancelled", "The passkey sheet was dismissed.");
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "failed", ex.Message);
        }
    }

    internal static string? CreationRelyingParty(JsonElement options) =>
        options.ValueKind == JsonValueKind.Object &&
        options.TryGetProperty("rp", out var rp) &&
        rp.ValueKind == JsonValueKind.Object &&
        rp.TryGetProperty("id", out var id) &&
        id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    internal static string? RequestRelyingParty(JsonElement options) =>
        options.ValueKind == JsonValueKind.Object &&
        options.TryGetProperty("rpId", out var id) &&
        id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>A passkey the authenticator created or used, with its WebAuthn response JSON.</summary>
internal sealed record PasskeyCredential(string Id, string? UserHandle, string Json);

/// <summary>The platform authenticator, behind a seam: Essentials' response types cannot be built outside it.</summary>
internal interface IPasskeyAuthenticator
{
    bool IsSupported { get; }
    Task<PasskeyCredential> CreateAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken);
    Task<PasskeyCredential> AssertAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken);
}

sealed class EssentialsPasskeyAuthenticator : IPasskeyAuthenticator
{
    public bool IsSupported
    {
        get
        {
            // The AppKit and GTK4 heads have no passkeys in Essentials, which throws there rather than answering false
            try
            {
                return Microsoft.Maui.Authentication.Passkeys.Default.IsSupported;
            }
            catch (Exception ex) when (ex is NotImplementedException or PlatformNotSupportedException or FeatureNotSupportedException)
            {
                return false;
            }
        }
    }

    public async Task<PasskeyCredential> CreateAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken)
    {
        var response = await Microsoft.Maui.Authentication.Passkeys.Default.CreateAsync(
            new PasskeyCreationOptions(optionsJson) { PreferImmediatelyAvailable = preferImmediatelyAvailable },
            cancellationToken
        );
        return new PasskeyCredential(response.Id, null, response.ToString());
    }

    public async Task<PasskeyCredential> AssertAsync(string optionsJson, bool preferImmediatelyAvailable, CancellationToken cancellationToken)
    {
        var response = await Microsoft.Maui.Authentication.Passkeys.Default.AssertAsync(
            new PasskeyRequestOptions(optionsJson) { PreferImmediatelyAvailable = preferImmediatelyAvailable },
            cancellationToken
        );
        return new PasskeyCredential(response.Id, response.UserHandle, response.ToString());
    }
}
