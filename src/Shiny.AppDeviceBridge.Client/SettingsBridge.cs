using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// Key/value settings in the platform's stores. Values are any JSON; keys are 1–128 letters, digits, <c>.</c>,
/// <c>_</c>, <c>-</c> or <c>:</c>. The typed <see cref="SettingsBridgeExtensions.GetAsync{T}"/> and
/// <see cref="SettingsBridgeExtensions.SetAsync{T}"/> are what a page normally calls.
/// </summary>
[BridgeClient("settings", typeof(AppDeviceBridgeJsonContext))]
public interface ISettingsBridge
{
    /// <summary>The keys this web app has set in a scope, and whether the scope is encrypted at rest.</summary>
    [BridgeGet("{scope}")]
    Task<SettingsList> ListAsync(SettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>Removes every key this web app set in a scope, and nothing the native app set.</summary>
    [BridgeDelete("{scope}")]
    Task ClearAsync(SettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>A value exactly as it was written. Fails with 404 when the key is not set.</summary>
    [BridgeGet("{scope}/{key}")]
    Task<JsonElement> GetValueAsync(SettingsScope scope, string key, CancellationToken cancellationToken = default);

    /// <summary>Sets a key to any JSON value, at most 1 MB.</summary>
    [BridgePut("{scope}/{key}")]
    Task SetValueAsync(SettingsScope scope, string key, [BridgeBody] JsonElement value, CancellationToken cancellationToken = default);

    /// <summary>Removes a key. Fails with 404 when it is not set.</summary>
    [BridgeDelete("{scope}/{key}")]
    Task RemoveAsync(SettingsScope scope, string key, CancellationToken cancellationToken = default);
}

/// <summary>Which store a setting lives in.</summary>
public enum SettingsScope
{
    /// <summary>The platform's settings: NSUserDefaults, SharedPreferences, ApplicationData, a file on the desktop.</summary>
    Local,

    /// <summary>
    /// The platform's secure store: Keychain, Android KeyStore, DPAPI. Linux has none and uses a plain file —
    /// <see cref="SettingsList.Encrypted"/> says which.
    /// </summary>
    Secure
}

/// <summary>The keys in a scope.</summary>
/// <param name="Scope">The scope listed.</param>
/// <param name="Encrypted">Whether values in this scope are encrypted at rest on this device.</param>
/// <param name="Keys">The keys this web app has set, in ordinal order.</param>
public sealed record SettingsList(SettingsScope Scope, bool Encrypted, IReadOnlyList<string> Keys);

/// <summary>Settings as values of your own types.</summary>
public static class SettingsBridgeExtensions
{
    /// <summary>A setting as <typeparamref name="T"/>, or <paramref name="defaultValue"/> when it is not set.</summary>
    public static async Task<T?> GetAsync<T>(
        this ISettingsBridge settings,
        SettingsScope scope,
        string key,
        JsonTypeInfo<T> typeInfo,
        T? defaultValue = default,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            var value = await settings.GetValueAsync(scope, key, cancellationToken).ConfigureAwait(false);
            return value.Deserialize(typeInfo);
        }
        catch (BridgeException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return defaultValue;
        }
    }

    /// <summary>Sets a setting to a value of <typeparamref name="T"/>.</summary>
    public static Task SetAsync<T>(
        this ISettingsBridge settings,
        SettingsScope scope,
        string key,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return settings.SetValueAsync(scope, key, JsonSerializer.SerializeToElement(value, typeInfo), cancellationToken);
    }
}
