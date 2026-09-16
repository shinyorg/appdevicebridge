using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Extensions.Stores;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// <c>/_bridge/settings</c> — key/value settings over Shiny.Extensions.Stores, in two scopes.
/// <code>
/// GET    /_bridge/settings/{scope}          { "scope": "Secure", "encrypted": true, "keys": ["token"] }
/// DELETE /_bridge/settings/{scope}          removes every key this web app set, and nothing else
/// GET    /_bridge/settings/{scope}/{key}    the value, exactly as it was written; 404 when unset
/// PUT    /_bridge/settings/{scope}/{key}    any JSON value as the body
/// DELETE /_bridge/settings/{scope}/{key}
/// </code>
/// <para>
/// <c>local</c> is the platform's settings store (NSUserDefaults, SharedPreferences, ApplicationData, a
/// file on the desktop). <c>secure</c> is its secure store (Keychain, Android KeyStore, DPAPI) — except on
/// Linux, where Shiny.Extensions.Stores falls back to a plain file. <c>encrypted</c> in the listing says
/// which, so the page can decide whether to put a credential there.
/// </para>
/// <para>
/// The stores are shared with the native app, so every key is namespaced by app id, and listing or
/// clearing only ever sees keys the web app wrote. Stores cannot enumerate their own keys, so an index of
/// them is kept alongside. Keys are letters, digits and <c>. _ - :</c>, up to 128 characters — nothing
/// that needs escaping in a URL.
/// </para>
/// </summary>
public sealed class WebAppSettingsBridge : IWebAppBridge
{
    const int MaxValueBytes = 1024 * 1024;

    readonly Lazy<IKeyValueStore> local;
    readonly Lazy<IKeyValueStore> secure;
    readonly string prefix;
    readonly Lock gate = new();

    public WebAppSettingsBridge(IServiceProvider services, WebAppHostOptions options)
        : this(
            // Lazy: the secure store opens the Keychain or the Android KeyStore, which nothing should pay
            // for until the page actually asks.
            new Lazy<IKeyValueStore>(() => services.GetKeyedService<IKeyValueStore>(StoreKeys.Default) ?? Stores.Default),
            new Lazy<IKeyValueStore>(() => services.GetKeyedService<IKeyValueStore>(StoreKeys.Secure) ?? Stores.Secure),
            options.AppId
        )
    {
    }

    internal WebAppSettingsBridge(IKeyValueStore local, IKeyValueStore secure, string appId)
        : this(new Lazy<IKeyValueStore>(local), new Lazy<IKeyValueStore>(secure), appId)
    {
    }

    WebAppSettingsBridge(Lazy<IKeyValueStore> local, Lazy<IKeyValueStore> secure, string appId)
    {
        this.local = local;
        this.secure = secure;
        this.prefix = $"appdevicebridge:{appId}:";
    }

    public string Name => "settings";

    public bool IsSupported => true;

    /// <summary>
    /// Whether the secure scope is encrypted at rest on this platform. Only Linux falls back to a plain file.
    /// </summary>
    public static bool IsSecureStoreEncrypted => !(OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD());

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/{scope}", this.ListAsync)
        .MapDelete("/{scope}", this.ClearAsync)
        .MapGet("/{scope}/{key}", this.GetAsync)
        .MapPut("/{scope}/{key}", this.SetAsync)
        .MapDelete("/{scope}/{key}", this.RemoveAsync);

    ValueTask ListAsync(HttpContext context)
    {
        if (!this.TryGetScope(context, out var scope, out var store))
            return WebAppBridgeResults.NotFound(context, "Settings scopes are 'local' and 'secure'.");

        List<string> keys;
        lock (this.gate)
            keys = [.. this.ReadIndex(store).Order(StringComparer.Ordinal)];

        return WebAppBridgeResults.Json(
            context,
            new SettingsList(scope, scope == SettingsScope.Secure && IsSecureStoreEncrypted, keys),
            AppDeviceBridgeJsonContext.Default.SettingsList
        );
    }

    ValueTask ClearAsync(HttpContext context)
    {
        if (!this.TryGetScope(context, out _, out var store))
            return WebAppBridgeResults.NotFound(context, "Settings scopes are 'local' and 'secure'.");

        lock (this.gate)
        {
            foreach (var key in this.ReadIndex(store))
                store.Remove(this.ValueKey(key));

            store.Remove(this.IndexKey);
        }

        return WebAppBridgeResults.NoContent(context);
    }

    ValueTask GetAsync(HttpContext context)
    {
        if (!this.TryGetScope(context, out _, out var store))
            return WebAppBridgeResults.NotFound(context, "Settings scopes are 'local' and 'secure'.");

        if (!TryGetKey(context, out var key))
            return InvalidKey(context);

        string? value;
        lock (this.gate)
            value = store.Contains(this.ValueKey(key)) ? store.Get<string>(this.ValueKey(key)) : null;

        return value is null
            ? WebAppBridgeResults.NotFound(context, $"'{key}' is not set.")
            : Results.Text(value, "application/json").ExecuteAsync(context);
    }

    async ValueTask SetAsync(HttpContext context)
    {
        if (!this.TryGetScope(context, out _, out var store))
        {
            await WebAppBridgeResults.NotFound(context, "Settings scopes are 'local' and 'secure'.");
            return;
        }

        if (!TryGetKey(context, out var key))
        {
            await InvalidKey(context);
            return;
        }

        if (context.Request.ContentLength > MaxValueBytes)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status413PayloadTooLarge, "too_large", "A setting can be at most 1 MB.");
            return;
        }

        string json;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var buffer = new char[MaxValueBytes + 1];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), context.RequestAborted);

            if (length > MaxValueBytes)
            {
                await WebAppBridgeResults.Error(context, StatusCodes.Status413PayloadTooLarge, "too_large", "A setting can be at most 1 MB.");
                return;
            }

            json = new string(buffer, 0, length).Trim();
        }

        if (!IsJson(json))
        {
            await WebAppBridgeResults.BadRequest(context, "The body must be a JSON value — an object, array, string, number, true, false or null.");
            return;
        }

        lock (this.gate)
        {
            // The value first: a crash between the two leaves an unlisted value, never a listed key with
            // nothing behind it.
            store.Set(this.ValueKey(key), json);

            var index = this.ReadIndex(store);
            if (index.Add(key))
                this.WriteIndex(store, index);
        }

        await WebAppBridgeResults.NoContent(context);
    }

    ValueTask RemoveAsync(HttpContext context)
    {
        if (!this.TryGetScope(context, out _, out var store))
            return WebAppBridgeResults.NotFound(context, "Settings scopes are 'local' and 'secure'.");

        if (!TryGetKey(context, out var key))
            return InvalidKey(context);

        bool removed;
        lock (this.gate)
        {
            removed = store.Remove(this.ValueKey(key));

            var index = this.ReadIndex(store);
            if (index.Remove(key))
                this.WriteIndex(store, index);
        }

        return removed
            ? WebAppBridgeResults.NoContent(context)
            : WebAppBridgeResults.NotFound(context, $"'{key}' is not set.");
    }

    /// <summary>Case-insensitive: the typed clients send the scope as it is named in <see cref="SettingsScope"/>.</summary>
    bool TryGetScope(HttpContext context, out SettingsScope scope, out IKeyValueStore store)
    {
        var value = context.Request.RouteValues["scope"] ?? String.Empty;

        if (value.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            scope = SettingsScope.Local;
            store = this.local.Value;
            return true;
        }

        if (value.Equals("secure", StringComparison.OrdinalIgnoreCase))
        {
            scope = SettingsScope.Secure;
            store = this.secure.Value;
            return true;
        }

        scope = default;
        store = null!;
        return false;
    }

    static bool TryGetKey(HttpContext context, out string key)
    {
        key = context.Request.RouteValues["key"] ?? String.Empty;
        return IsValidKey(key);
    }

    internal static bool IsValidKey(string key)
        => key.Length is > 0 and <= 128
           && key.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    static ValueTask InvalidKey(HttpContext context)
        => WebAppBridgeResults.BadRequest(context, "Keys are 1–128 letters, digits, '.', '_', '-' or ':'.");

    static bool IsJson(string text)
    {
        if (text.Length == 0)
            return false;

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    string IndexKey => this.prefix + "index";

    string ValueKey(string key) => this.prefix + "v:" + key;

    /// <summary>Newline separated, so it is a plain string to every store — keys cannot contain one.</summary>
    HashSet<string> ReadIndex(IKeyValueStore store)
        => [.. (store.Get<string>(this.IndexKey) ?? String.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    void WriteIndex(IKeyValueStore store, HashSet<string> index)
    {
        if (index.Count == 0)
            store.Remove(this.IndexKey);
        else
            store.Set(this.IndexKey, String.Join('\n', index.Order(StringComparer.Ordinal)));
    }
}

/// <summary>
/// Also registered with Shiny's shared serializer: the Apple and Android secure stores serialize every
/// value, strings included, and a trimmed build has no reflection fallback to find string metadata with.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
partial class WebAppStoreJsonContext : JsonSerializerContext;
