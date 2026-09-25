using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge;
using Shiny.AppDeviceBridge.Maps;
using Shiny.Extensions.Stores;
using Shiny.Net.HttpServer;

namespace Sample;

/// <summary>
/// <c>/_bridge/sample-traffic</c>: lets the Map page pick the traffic providers and enter their API keys while the sample runs,
/// so each provider can be tried without a key in the repository or a rebuild.
/// <code>
/// GET    /_bridge/sample-traffic                    { flow, incidents, keys: ["tomtom", …] }   which keys are set, never the keys
/// PUT    /_bridge/sample-traffic/keys/{provider}    { "key": "…" }                              tomtom, here, azure-maps
/// DELETE /_bridge/sample-traffic/keys/{provider}
/// PUT    /_bridge/sample-traffic/selection          { "flow": "here", "incidents": true }      flow: none, tomtom, here, azure-maps
/// </code>
/// <para>
/// Keys go into the device's secure store (Keychain, Android KeyStore, DPAPI; a plain file on Linux) and are write-only: the
/// page can set or clear one, but no route returns it. The selection is applied to <see cref="MapsOptions"/> at once and kept
/// for the next launch. A bridge like any other, so it is behind the bridge policy — this is a sample's convenience, not a
/// pattern for shipping third-party keys to users.
/// </para>
/// </summary>
sealed class TrafficProvidersBridge : IWebAppBridge
{
    const string TomTom = "tomtom";
    const string Here = "here";
    const string AzureMaps = "azure-maps";
    const string None = "none";
    static readonly string[] Providers = [TomTom, Here, AzureMaps];

    readonly MapsOptions maps;
    readonly Lazy<IKeyValueStore> secure;
    readonly Lazy<IKeyValueStore> local;
    readonly Lock gate = new();

    public TrafficProvidersBridge(MapsOptions maps, IServiceProvider services)
    {
        this.maps = maps;
        this.secure = new(() => services.GetKeyedService<IKeyValueStore>(StoreKeys.Secure) ?? Stores.Secure);
        this.local = new(() => services.GetKeyedService<IKeyValueStore>(StoreKeys.Default) ?? Stores.Default);

        // What the user picked last time, so traffic is back on at the next launch without visiting the page.
        this.Apply();
    }

    public string Name => "sample-traffic";

    public bool IsSupported => true;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("", this.GetAsync)
        .MapPut("/keys/{provider}", this.SetKeyAsync)
        .MapDelete("/keys/{provider}", this.ClearKeyAsync)
        .MapPut("/selection", this.SelectAsync);

    ValueTask GetAsync(HttpContext context)
        => WebAppBridgeResults.Json(context, this.Settings(), TrafficProvidersJsonContext.Default.TrafficProviderSettings);

    async ValueTask SetKeyAsync(HttpContext context)
    {
        if (Provider(context) is not { } provider)
        {
            await WebAppBridgeResults.NotFound(context, "No such traffic provider.");
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrafficProvidersJsonContext.Default.TrafficProviderKey);
        if (String.IsNullOrWhiteSpace(body?.Key))
        {
            await WebAppBridgeResults.BadRequest(context, "A key is required.");
            return;
        }

        lock (this.gate)
        {
            this.secure.Value.Set(KeyName(provider), body.Key.Trim());
            this.Apply();
        }

        await this.GetAsync(context);
    }

    async ValueTask ClearKeyAsync(HttpContext context)
    {
        if (Provider(context) is not { } provider)
        {
            await WebAppBridgeResults.NotFound(context, "No such traffic provider.");
            return;
        }

        lock (this.gate)
        {
            this.secure.Value.Remove(KeyName(provider));
            this.Apply();
        }

        await this.GetAsync(context);
    }

    async ValueTask SelectAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, TrafficProvidersJsonContext.Default.TrafficProviderSelection);
        var flow = body?.Flow ?? None;

        if (flow != None && !Providers.Contains(flow))
        {
            await WebAppBridgeResults.BadRequest(context, $"'{flow}' is not a traffic provider.");
            return;
        }

        lock (this.gate)
        {
            this.local.Value.Set("sample.traffic.flow", flow);
            this.local.Value.Set("sample.traffic.incidents", body?.Incidents == true ? "true" : "false");
            this.Apply();
        }

        await this.GetAsync(context);
    }

    /// <summary>Builds the providers from the stored selection and keys. A selection whose key is missing draws nothing.</summary>
    void Apply()
    {
        var flow = this.Flow;
        var key = flow == None ? null : this.Key(flow);

        this.maps.Traffic = key is null ? null : flow switch
        {
            TomTom => new TomTomTrafficProvider(key),
            Here => new HereTrafficProvider(key),
            AzureMaps => new AzureMapsTrafficProvider(key),
            _ => null
        };

        this.maps.TrafficIncidents = this.Incidents && this.Key(TomTom) is { } tomTom ? new TomTomIncidentProvider(tomTom) : null;
    }

    TrafficProviderSettings Settings() => new(
        this.Flow,
        this.Incidents,
        [.. Providers.Where(x => this.Key(x) is not null)],
        this.maps.Traffic is not null,
        this.maps.TrafficIncidents is not null
    );

    string Flow => this.local.Value.Get<string>("sample.traffic.flow") is { } flow && (flow == None || Providers.Contains(flow)) ? flow : None;

    bool Incidents => this.local.Value.Get<string>("sample.traffic.incidents") == "true";

    string? Key(string provider) => this.secure.Value.Get<string>(KeyName(provider)) is { Length: > 0 } key ? key : null;

    static string KeyName(string provider) => $"sample.traffic.key.{provider}";

    static string? Provider(HttpContext context)
        => context.Request.RouteValues["provider"] is { } provider && Providers.Contains(provider) ? provider : null;
}

/// <param name="Flow"><c>none</c>, <c>tomtom</c>, <c>here</c> or <c>azure-maps</c>.</param>
/// <param name="Keys">The providers whose key is set. The keys themselves never leave the device's secure store.</param>
/// <param name="FlowActive">Whether a flow provider is running: the selection has its key.</param>
/// <param name="IncidentsActive">Whether TomTom incidents are running: selected, with the TomTom key set.</param>
sealed record TrafficProviderSettings(string Flow, bool Incidents, IReadOnlyList<string> Keys, bool FlowActive, bool IncidentsActive);

sealed record TrafficProviderKey(string? Key);

sealed record TrafficProviderSelection(string? Flow, bool Incidents);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TrafficProviderSettings))]
[JsonSerializable(typeof(TrafficProviderKey))]
[JsonSerializable(typeof(TrafficProviderSelection))]
partial class TrafficProvidersJsonContext : JsonSerializerContext;
