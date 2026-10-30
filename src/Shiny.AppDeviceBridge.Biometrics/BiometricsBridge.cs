using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Biometrics.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Biometrics;

namespace Shiny.AppDeviceBridge.Biometrics;

public static class BiometricsBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/biometrics</c> and registers Shiny's <c>IBiometrics</c> — there is nothing else to call in code.
    /// <code>
    /// bridge.AddBiometricsBridge();
    /// </code>
    /// <para>
    /// iOS needs <c>NSFaceIDUsageDescription</c> in Info.plist. iOS, Mac Catalyst, macOS, Android and Windows; Linux
    /// answers 501.
    /// </para>
    /// </summary>
    public static TBuilder AddBiometricsBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        bridge.Services.AddBiometrics();
        bridge.AddBridge<BiometricsBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/biometrics</c> over <see cref="Native.IBiometrics"/>.
/// <code>
/// GET  /_bridge/biometrics?strength=Weak         { "status": "Available", "types": [ "Face" ], "isDeviceCredentialSet": true }
/// POST /_bridge/biometrics/authenticate          { "reason": "Unlock your vault", "allowDeviceCredential": true }  →  { "status": "Success" }
/// </code>
/// </summary>
public sealed class BiometricsBridge : IWebAppBridge
{
    internal const int MaxTextLength = 256;

    readonly IServiceProvider services;
    readonly Native.IBiometrics? biometrics;
    readonly bool biometricPlatform;

    public BiometricsBridge(IServiceProvider services) : this(services, IsBiometricPlatform) { }

    /// <param name="biometricPlatform">Whether this OS has a biometric prompt; tests pass it so they run on any host.</param>
    internal BiometricsBridge(IServiceProvider services, bool biometricPlatform)
    {
        this.services = services;
        this.biometrics = services.GetOptionalService<Native.IBiometrics>();
        this.biometricPlatform = biometricPlatform;
    }

    public string Name => "biometrics";

    // Shiny.Biometrics reports NotSupported on Linux; a device without the hardware still answers, with status NotSupported
    public bool IsSupported => this.biometrics is not null && this.biometricPlatform;

    internal static bool IsBiometricPlatform =>
        OperatingSystem.IsAndroid() ||
        OperatingSystem.IsIOS() ||
        OperatingSystem.IsMacOS() ||
        OperatingSystem.IsWindows();

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.AvailabilityAsync)
        .MapPost("/authenticate", this.AuthenticateAsync);

    async ValueTask AvailabilityAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Biometrics");
            return;
        }

        var strength = Enum.TryParse<BiometricStrength>(context.Request.Query["strength"].ToString(), true, out var s) && s == BiometricStrength.Weak
            ? Native.BiometricStrength.Weak
            : Native.BiometricStrength.Strong;

        var availability = await this.biometrics!.GetAvailability(strength, context.RequestAborted);
        await WebAppBridgeResults.Json(context, ToContract(availability), BiometricsJsonContext.Default.BiometricAvailability);
    }

    async ValueTask AuthenticateAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Biometrics");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, BiometricsJsonContext.Default.BiometricRequest);
        if (String.IsNullOrWhiteSpace(body?.Reason) || !Fits(body))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"reason\": \"…\" }}; the reason, title, subtitle and button texts are at most {MaxTextLength} characters.");
            return;
        }

        try
        {
            // Android shows BiometricPrompt from the current activity
            var result = await this.services
                .GetRequiredService<IWebAppMainThread>()
                .InvokeAsync(() => this.biometrics!.Authenticate(ToNative(body), context.RequestAborted));

            await WebAppBridgeResults.Json(context, ToContract(result), BiometricsJsonContext.Default.BiometricResult);
        }
        catch (InvalidOperationException ex)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "cannot_prompt", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
    }

    static bool Fits(BiometricRequest r) =>
        r.Reason.Length <= MaxTextLength &&
        (r.Title?.Length ?? 0) <= MaxTextLength &&
        (r.Subtitle?.Length ?? 0) <= MaxTextLength &&
        (r.CancelText?.Length ?? 0) <= MaxTextLength &&
        (r.FallbackText?.Length ?? 0) <= MaxTextLength;

    internal static Native.BiometricRequest ToNative(BiometricRequest r) => new()
    {
        Reason = r.Reason,
        Title = r.Title,
        Subtitle = r.Subtitle,
        CancelText = r.CancelText,
        FallbackText = r.FallbackText,
        AllowDeviceCredential = r.AllowDeviceCredential,
        Strength = r.Strength == BiometricStrength.Weak ? Native.BiometricStrength.Weak : Native.BiometricStrength.Strong,
        ConfirmationRequired = r.ConfirmationRequired
    };

    internal static BiometricAvailability ToContract(Native.BiometricAvailability a)
    {
        var types = new List<BiometricType>();
        if (a.Types.HasFlag(Native.BiometricType.Fingerprint))
            types.Add(BiometricType.Fingerprint);

        if (a.Types.HasFlag(Native.BiometricType.Face))
            types.Add(BiometricType.Face);

        if (a.Types.HasFlag(Native.BiometricType.Iris))
            types.Add(BiometricType.Iris);

        if (a.Types.HasFlag(Native.BiometricType.Unknown))
            types.Add(BiometricType.Unknown);

        return new BiometricAvailability(ToContract(a.Status), types, a.IsDeviceCredentialSet);
    }

    // explicit rather than BridgeEnum.Convert: an unknown native status must never fall back to the contract's first
    // member, which is Available / Success
    internal static BiometricStatus ToContract(Native.BiometricStatus s) => s switch
    {
        Native.BiometricStatus.Available => BiometricStatus.Available,
        Native.BiometricStatus.NotSupported => BiometricStatus.NotSupported,
        Native.BiometricStatus.NotEnrolled => BiometricStatus.NotEnrolled,
        Native.BiometricStatus.LockedOut => BiometricStatus.LockedOut,
        Native.BiometricStatus.PasscodeNotSet => BiometricStatus.PasscodeNotSet,
        Native.BiometricStatus.SecurityUpdateRequired => BiometricStatus.SecurityUpdateRequired,
        _ => BiometricStatus.Unavailable
    };

    internal static BiometricResult ToContract(Native.BiometricResult r) => new(
        r.Status switch
        {
            Native.BiometricResultStatus.Success => BiometricResultStatus.Success,
            Native.BiometricResultStatus.Cancelled => BiometricResultStatus.Cancelled,
            Native.BiometricResultStatus.Fallback => BiometricResultStatus.Fallback,
            Native.BiometricResultStatus.Failed => BiometricResultStatus.Failed,
            Native.BiometricResultStatus.LockedOut => BiometricResultStatus.LockedOut,
            Native.BiometricResultStatus.NotEnrolled => BiometricResultStatus.NotEnrolled,
            Native.BiometricResultStatus.PasscodeNotSet => BiometricResultStatus.PasscodeNotSet,
            Native.BiometricResultStatus.Timeout => BiometricResultStatus.Timeout,
            _ => BiometricResultStatus.NotAvailable
        },
        r.ErrorMessage
    );
}
