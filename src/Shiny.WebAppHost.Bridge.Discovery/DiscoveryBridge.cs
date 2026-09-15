using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;

namespace Shiny.WebAppHost.Bridge.Discovery;

[Flags]
public enum DiscoveryProtocols
{
    None = 0,

    /// <summary>mDNS / DNS-SD — Bonjour, Zeroconf: printers, AirPlay, Chromecast, anything advertising <c>_service._tcp</c>.</summary>
    Mdns = 1,

    /// <summary>SSDP / UPnP — media servers, routers, smart TVs.</summary>
    Ssdp = 2,

    /// <summary>WS-Discovery — ONVIF cameras, network scanners.</summary>
    WsDiscovery = 4,

    All = Mdns | Ssdp | WsDiscovery
}

public static class DiscoveryBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/discovery</c> and registers Shiny's discovery services for the protocols asked for —
    /// there is nothing else to call.
    /// <para>
    /// Platform setup is Shiny.Net.Discovery's: on iOS and Mac Catalyst, <c>NSLocalNetworkUsageDescription</c>
    /// and every browsed service type in <c>NSBonjourServices</c> (iOS refuses the rest silently); on Android,
    /// <c>CHANGE_WIFI_MULTICAST_STATE</c> for SSDP and WS-Discovery.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddDiscoveryBridge(this MauiAppBuilder builder, DiscoveryProtocols protocols = DiscoveryProtocols.All)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (protocols.HasFlag(DiscoveryProtocols.Mdns))
            builder.Services.AddMdns();

        if (protocols.HasFlag(DiscoveryProtocols.Ssdp))
            builder.Services.AddSsdp();

        if (protocols.HasFlag(DiscoveryProtocols.WsDiscovery))
            builder.Services.AddWsDiscovery();

        builder.Services.AddWebAppBridge<DiscoveryBridge>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/discovery</c> over <see cref="IMdnsManager"/>, <see cref="ISsdpManager"/> and
/// <see cref="IWsDiscoveryManager"/>.
/// <code>
/// POST   /_bridge/discovery/mdns/search          { "serviceType": "_http._tcp", "scanMs": 5000 }       the services seen in the window
/// POST   /_bridge/discovery/mdns/browse          { "serviceType": "_http._tcp" }                        { "id": … }; results as discovery.mdns
/// GET    /_bridge/discovery/mdns/resolve?instanceName=…&amp;serviceType=…
/// POST   /_bridge/discovery/mdns/publications    { "instanceName": "Kitchen", "serviceType": "_myapp._tcp", "port": 8080, "txtRecords": {…} }
///
/// POST   /_bridge/discovery/ssdp/search          { "searchTarget": "ssdp:all", "scanMs": 5000 }
/// POST   /_bridge/discovery/ssdp/browse          { "searchTarget": "upnp:rootdevice" }                  results as discovery.ssdp
/// GET    /_bridge/discovery/ssdp/description?udn=…                                                      a device found by search or browse
/// POST   /_bridge/discovery/ssdp/publications    { "udn": "uuid:…", "location": "http://…/device.xml" }
///
/// POST   /_bridge/discovery/wsd/search           { "types": [{ "name": "NetworkVideoTransmitter", "namespace": "http://www.onvif.org/ver10/network/wsdl" }] }
/// POST   /_bridge/discovery/wsd/browse           same body                                             results as discovery.wsd
/// GET    /_bridge/discovery/wsd/resolve?endpointReference=…
/// POST   /_bridge/discovery/wsd/publications     { "endpointReference": "urn:uuid:…", "types": […], "addresses": ["http://…"] }
///
/// GET    /_bridge/discovery/browses              DELETE /_bridge/discovery/browses/{id}
/// GET    /_bridge/discovery/publications         DELETE /_bridge/discovery/publications/{id}
///
/// events: discovery.mdns, discovery.ssdp, discovery.wsd, discovery.error, discovery.stopped
/// </code>
/// <para>
/// Browses belong to the page that started them: they stop when its last event stream closes, since their
/// results would have nowhere to go. Publications keep advertising until deleted, or until the app exits.
/// </para>
/// </summary>
public sealed class DiscoveryBridge : IWebAppBridge, IDisposable, IAsyncDisposable
{
    const int MaxBrowses = 8;
    const int MaxPublications = 16;

    readonly IMdnsManager? mdns;
    readonly ISsdpManager? ssdp;
    readonly IWsDiscoveryManager? wsd;
    readonly WebAppEventHub events;
    readonly ConcurrentDictionary<string, Browse> browses = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Publication> publications = new(StringComparer.Ordinal);

    // GetDescription needs the device as it was advertised, so every device the page has seen is kept by UDN.
    readonly ConcurrentDictionary<string, SsdpDevice> seenDevices = new(StringComparer.OrdinalIgnoreCase);

    public DiscoveryBridge(IServiceProvider services, WebAppEventHub events)
    {
        this.mdns = services.GetOptionalService<IMdnsManager>();
        this.ssdp = services.GetOptionalService<ISsdpManager>();
        this.wsd = services.GetOptionalService<IWsDiscoveryManager>();
        this.events = events;

        events.SubscribersChanged += this.OnSubscribersChanged;
    }

    public string Name => "discovery";

    public bool IsSupported => this.mdns is not null || this.ssdp is not null || this.wsd is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapPost("/mdns/search", ctx => With(ctx, this.mdns, "mDNS", m => this.MdnsSearchAsync(ctx, m)))
        .MapPost("/mdns/browse", ctx => With(ctx, this.mdns, "mDNS", m => this.MdnsBrowseAsync(ctx, m)))
        .MapGet("/mdns/resolve", ctx => With(ctx, this.mdns, "mDNS", m => MdnsResolveAsync(ctx, m)))
        .MapPost("/mdns/publications", ctx => With(ctx, this.mdns, "mDNS", m => this.MdnsPublishAsync(ctx, m)))
        .MapPost("/ssdp/search", ctx => With(ctx, this.ssdp, "SSDP", s => this.SsdpSearchAsync(ctx, s)))
        .MapPost("/ssdp/browse", ctx => With(ctx, this.ssdp, "SSDP", s => this.SsdpBrowseAsync(ctx, s)))
        .MapGet("/ssdp/description", ctx => With(ctx, this.ssdp, "SSDP", s => this.SsdpDescriptionAsync(ctx, s)))
        .MapPost("/ssdp/publications", ctx => With(ctx, this.ssdp, "SSDP", s => this.SsdpPublishAsync(ctx, s)))
        .MapPost("/wsd/search", ctx => With(ctx, this.wsd, "WS-Discovery", w => WsdSearchAsync(ctx, w)))
        .MapPost("/wsd/browse", ctx => With(ctx, this.wsd, "WS-Discovery", w => this.WsdBrowseAsync(ctx, w)))
        .MapGet("/wsd/resolve", ctx => With(ctx, this.wsd, "WS-Discovery", w => WsdResolveAsync(ctx, w)))
        .MapPost("/wsd/publications", ctx => With(ctx, this.wsd, "WS-Discovery", w => this.WsdPublishAsync(ctx, w)))
        .MapGet("/browses", this.ListBrowsesAsync)
        .MapDelete("/browses/{id}", this.StopBrowseAsync)
        .MapGet("/publications", this.ListPublicationsAsync)
        .MapDelete("/publications/{id}", this.UnpublishAsync);

    // ---- mDNS

    async ValueTask MdnsSearchAsync(HttpContext context, IMdnsManager manager)
    {
        if (await ReadMdnsConfigAsync(context) is not { } request)
            return;

        var services = await CollectAsync(
            ct => manager.Browse(request.Config, ct),
            r => (r.Status == MdnsBrowseStatus.Found, r.Service.FullName, MdnsServiceResponse.From(r.Service)),
            request.Window,
            context.RequestAborted
        );

        await Json(context, services, DiscoveryBridgeJsonContext.Default.ListMdnsServiceResponse);
    }

    async ValueTask MdnsBrowseAsync(HttpContext context, IMdnsManager manager)
    {
        if (await ReadMdnsConfigAsync(context) is not { } request)
            return;

        await this.StartBrowseAsync(
            context,
            "mdns",
            request.Config.ServiceType,
            ct => manager.Browse(request.Config, ct),
            (id, r) => this.events.Publish(
                "discovery.mdns",
                new MdnsBrowseEvent(id, r.Status, MdnsServiceResponse.From(r.Service)),
                DiscoveryBridgeJsonContext.Default.MdnsBrowseEvent
            )
        );
    }

    static async ValueTask MdnsResolveAsync(HttpContext context, IMdnsManager manager)
    {
        var query = context.Request.Query;
        var instanceName = query["instanceName"].ToString();
        var serviceType = query["serviceType"].ToString();

        if (instanceName.Length == 0 || serviceType.Length == 0)
        {
            await WebAppBridgeResults.BadRequest(context, "Pass ?instanceName=…&serviceType=….");
            return;
        }

        var service = await manager.Resolve(instanceName, serviceType, Milliseconds(query["timeoutMs"].ToString(), 5_000), context.RequestAborted);

        await (service is null
            ? WebAppBridgeResults.NotFound(context, $"'{instanceName}.{serviceType}' did not answer.")
            : Json(context, MdnsServiceResponse.From(service), DiscoveryBridgeJsonContext.Default.MdnsServiceResponse));
    }

    async ValueTask MdnsPublishAsync(HttpContext context, IMdnsManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.MdnsPublishRequest);

        if (body is not { InstanceName.Length: > 0, ServiceType.Length: > 0, Port: > 0 and <= 65535 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"instanceName\": \"…\", \"serviceType\": \"_name._tcp\", \"port\": 1-65535 }.");
            return;
        }

        await this.PublishAsync(context, "mdns", async () =>
        {
            var publication = await manager.Publish(
                new MdnsServiceRegistration(body.InstanceName, body.ServiceType, body.Port)
                {
                    Domain = body.Domain ?? MdnsConstants.LocalDomain,
                    TxtRecords = body.TxtRecords
                },
                context.RequestAborted
            );

            // The instance name may have been changed to settle a conflict on the network; report the one in use.
            return (publication, $"{publication.InstanceName}.{publication.ServiceType}.{publication.Domain}");
        });
    }

    static async ValueTask<(MdnsBrowseConfig Config, TimeSpan Window)?> ReadMdnsConfigAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.MdnsSearchRequest);

        if (body is not { ServiceType.Length: > 0 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"serviceType\": \"_name._tcp\" }.");
            return null;
        }

        var config = new MdnsBrowseConfig(body.ServiceType)
        {
            Domain = body.Domain ?? MdnsConstants.LocalDomain,
            ResolveServices = body.ResolveServices
        };

        return (config, Window(body.ScanMs));
    }

    // ---- SSDP

    async ValueTask SsdpSearchAsync(HttpContext context, ISsdpManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.SsdpSearchRequest) ?? new SsdpSearchRequest();

        var devices = await CollectAsync(
            ct => manager.Browse(body.ToConfig(), ct),
            r =>
            {
                this.seenDevices[r.Device.Udn] = r.Device;
                return (r.Status == SsdpBrowseStatus.Found, r.Device.Udn, SsdpDeviceResponse.From(r.Device));
            },
            Window(body.ScanMs),
            context.RequestAborted
        );

        await Json(context, devices, DiscoveryBridgeJsonContext.Default.ListSsdpDeviceResponse);
    }

    async ValueTask SsdpBrowseAsync(HttpContext context, ISsdpManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.SsdpSearchRequest) ?? new SsdpSearchRequest();
        var config = body.ToConfig();

        await this.StartBrowseAsync(
            context,
            "ssdp",
            config.SearchTarget,
            ct => manager.Browse(config, ct),
            (id, r) =>
            {
                this.seenDevices[r.Device.Udn] = r.Device;
                this.events.Publish(
                    "discovery.ssdp",
                    new SsdpBrowseEvent(id, r.Status, SsdpDeviceResponse.From(r.Device)),
                    DiscoveryBridgeJsonContext.Default.SsdpBrowseEvent
                );
            }
        );
    }

    async ValueTask SsdpDescriptionAsync(HttpContext context, ISsdpManager manager)
    {
        var udn = context.Request.Query["udn"].ToString();

        if (!this.seenDevices.TryGetValue(udn, out var device))
        {
            await WebAppBridgeResults.NotFound(context, $"No device '{udn}' has been seen. Search or browse for it first.");
            return;
        }

        // Shiny's fetcher only follows description URLs on the local network the device answered from.
        var description = await manager.GetDescription(device, ct: context.RequestAborted);
        await Json(context, description, DiscoveryBridgeJsonContext.Default.UpnpDeviceDescription);
    }

    async ValueTask SsdpPublishAsync(HttpContext context, ISsdpManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.SsdpPublishRequest);

        if (body is not { Udn.Length: > 0 } || !Uri.TryCreate(body.Location, UriKind.Absolute, out var location))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"udn\": \"uuid:…\", \"location\": \"http://…/description.xml\" }.");
            return;
        }

        await this.PublishAsync(context, "ssdp", async () =>
        {
            var publication = await manager.Publish(
                new SsdpDeviceRegistration(body.Udn, location)
                {
                    DeviceType = body.DeviceType,
                    ServiceTypes = body.ServiceTypes ?? [],
                    IsRootDevice = body.IsRootDevice,
                    MaxAge = body.MaxAgeSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : SsdpConstants.DefaultMaxAge,
                    Server = body.Server
                },
                context.RequestAborted
            );

            return (publication, publication.Udn);
        });
    }

    // ---- WS-Discovery

    static async ValueTask WsdSearchAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        if (await ReadProbeAsync(context) is not { } request)
            return;

        var targets = await CollectAsync(
            ct => manager.Browse(request.Config, ct),
            r => (r.Status == WsDiscoveryStatus.Found, r.Target.EndpointReference, WsdTargetResponse.From(r.Target)),
            request.Window,
            context.RequestAborted
        );

        await Json(context, targets, DiscoveryBridgeJsonContext.Default.ListWsdTargetResponse);
    }

    async ValueTask WsdBrowseAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        if (await ReadProbeAsync(context) is not { } request)
            return;

        await this.StartBrowseAsync(
            context,
            "wsd",
            String.Join(", ", request.Config.Types),
            ct => manager.Browse(request.Config, ct),
            (id, r) => this.events.Publish(
                "discovery.wsd",
                new WsdBrowseEvent(id, r.Status, WsdTargetResponse.From(r.Target)),
                DiscoveryBridgeJsonContext.Default.WsdBrowseEvent
            )
        );
    }

    static async ValueTask WsdResolveAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        var endpointReference = context.Request.Query["endpointReference"].ToString();

        if (endpointReference.Length == 0)
        {
            await WebAppBridgeResults.BadRequest(context, "Pass ?endpointReference=….");
            return;
        }

        var target = await manager.Resolve(endpointReference, Milliseconds(context.Request.Query["timeoutMs"].ToString(), 5_000), context.RequestAborted);

        await (target is null
            ? WebAppBridgeResults.NotFound(context, $"'{endpointReference}' did not answer.")
            : Json(context, WsdTargetResponse.From(target), DiscoveryBridgeJsonContext.Default.WsdTargetResponse));
    }

    async ValueTask WsdPublishAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.WsdPublishRequest);

        if (body is not { EndpointReference.Length: > 0 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"endpointReference\": \"urn:uuid:…\" }.");
            return;
        }

        List<Uri> addresses = [];
        foreach (var address in body.Addresses ?? [])
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                await WebAppBridgeResults.BadRequest(context, $"'{address}' is not an absolute address.");
                return;
            }

            addresses.Add(uri);
        }

        await this.PublishAsync(context, "wsd", async () =>
        {
            var publication = await manager.Publish(
                new WsDiscoveryRegistration(body.EndpointReference)
                {
                    Types = WsdQualifiedName.ToXml(body.Types),
                    Scopes = body.Scopes ?? [],
                    Addresses = addresses,
                    MetadataVersion = body.MetadataVersion,
                    Profiles = body.Profiles
                },
                context.RequestAborted
            );

            return (publication, publication.EndpointReference);
        });
    }

    static async ValueTask<(WsdProbeConfig Config, TimeSpan Window)?> ReadProbeAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, DiscoveryBridgeJsonContext.Default.WsdSearchRequest) ?? new WsdSearchRequest();

        var config = new WsdProbeConfig
        {
            Types = WsdQualifiedName.ToXml(body.Types),
            Scopes = body.Scopes ?? [],
            ScopeMatchBy = body.ScopeMatchBy,
            Profiles = body.Profiles
        };

        return (config, Window(body.ScanMs));
    }

    // ---- sessions

    async ValueTask StartBrowseAsync<TResult>(
        HttpContext context,
        string protocol,
        string target,
        Func<CancellationToken, IAsyncEnumerable<TResult>> stream,
        Action<string, TResult> publish
    )
    {
        if (!this.events.HasSubscribers)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Open /_bridge/events first: browse results arrive as events.");
            return;
        }

        if (this.browses.Count >= MaxBrowses)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status429TooManyRequests, "too_many_browses", $"At most {MaxBrowses} browses run at once. Stop one first.");
            return;
        }

        var id = Guid.NewGuid().ToString("n");
        var browse = new Browse(protocol, target, new CancellationTokenSource());
        this.browses[id] = browse;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in stream(browse.Cancellation.Token).ConfigureAwait(false))
                    publish(id, result);
            }
            catch (OperationCanceledException) when (browse.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                this.events.Publish("discovery.error", new DiscoveryErrorEvent(id, protocol, ex.Message), DiscoveryBridgeJsonContext.Default.DiscoveryErrorEvent);
            }
            finally
            {
                this.browses.TryRemove(id, out _);
                this.events.Publish("discovery.stopped", new BrowseResponse(id, protocol, target), DiscoveryBridgeJsonContext.Default.BrowseResponse);
            }
        });

        await Json(context, new BrowseResponse(id, protocol, target), DiscoveryBridgeJsonContext.Default.BrowseResponse);
    }

    async ValueTask PublishAsync(HttpContext context, string protocol, Func<Task<(IAsyncDisposable Publication, string Name)>> publish)
    {
        if (this.publications.Count >= MaxPublications)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status429TooManyRequests, "too_many_publications", $"At most {MaxPublications} publications at once. Remove one first.");
            return;
        }

        var (publication, name) = await publish();
        var id = Guid.NewGuid().ToString("n");
        this.publications[id] = new Publication(protocol, name, publication);

        await Json(context, new PublicationResponse(id, protocol, name), DiscoveryBridgeJsonContext.Default.PublicationResponse);
    }

    ValueTask ListBrowsesAsync(HttpContext context)
    {
        List<BrowseResponse> list = [.. this.browses.Select(x => new BrowseResponse(x.Key, x.Value.Protocol, x.Value.Target))];
        return Json(context, list, DiscoveryBridgeJsonContext.Default.ListBrowseResponse);
    }

    ValueTask StopBrowseAsync(HttpContext context)
    {
        if (!this.browses.TryGetValue(context.Request.RouteValues["id"] ?? String.Empty, out var browse))
            return WebAppBridgeResults.NotFound(context, "No such browse.");

        browse.Cancellation.Cancel();
        return WebAppBridgeResults.NoContent(context);
    }

    ValueTask ListPublicationsAsync(HttpContext context)
    {
        List<PublicationResponse> list = [.. this.publications.Select(x => new PublicationResponse(x.Key, x.Value.Protocol, x.Value.Name))];
        return Json(context, list, DiscoveryBridgeJsonContext.Default.ListPublicationResponse);
    }

    async ValueTask UnpublishAsync(HttpContext context)
    {
        if (!this.publications.TryRemove(context.Request.RouteValues["id"] ?? String.Empty, out var publication))
        {
            await WebAppBridgeResults.NotFound(context, "No such publication.");
            return;
        }

        // Disposing sends the goodbye, so the service vanishes from other devices now rather than at its TTL.
        await publication.Registration.DisposeAsync();
        await WebAppBridgeResults.NoContent(context);
    }

    void OnSubscribersChanged()
    {
        if (this.events.HasSubscribers)
            return;

        foreach (var browse in this.browses.Values)
            browse.Cancellation.Cancel();
    }

    // ---- helpers

    /// <summary>Everything a stream reported in a window, with anything reported lost taken back out.</summary>
    static async Task<List<TOut>> CollectAsync<TResult, TOut>(
        Func<CancellationToken, IAsyncEnumerable<TResult>> stream,
        Func<TResult, (bool Found, string Key, TOut Value)> map,
        TimeSpan window,
        CancellationToken cancellationToken
    )
    {
        var found = new Dictionary<string, TOut>(StringComparer.OrdinalIgnoreCase);

        using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scan.CancelAfter(window);

        try
        {
            await foreach (var result in stream(scan.Token).ConfigureAwait(false))
            {
                var (isFound, key, value) = map(result);

                if (isFound)
                    found[key] = value;
                else
                    found.Remove(key);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The window closed: the expected way a search ends.
        }

        return [.. found.Values];
    }

    static async ValueTask With<TManager>(HttpContext context, TManager? manager, string protocol, Func<TManager, ValueTask> action)
        where TManager : class
    {
        if (manager is null)
        {
            await WebAppBridgeResults.NotSupported(context, protocol);
            return;
        }

        try
        {
            await action(manager);
        }
        catch (DiscoveryPermissionException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "permission_denied", ex.Message);
        }
        catch (DiscoveryException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "discovery_failed", ex.Message);
        }
    }

    static TimeSpan Window(int? milliseconds) => TimeSpan.FromMilliseconds(Math.Clamp(milliseconds ?? 5_000, 250, 30_000));

    static TimeSpan Milliseconds(string value, int fallback)
        => TimeSpan.FromMilliseconds(Int32.TryParse(value, out var parsed) ? Math.Clamp(parsed, 250, 30_000) : fallback);

    static ValueTask Json<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo) => WebAppBridgeResults.Json(context, value, typeInfo);

    public void Dispose()
    {
        this.events.SubscribersChanged -= this.OnSubscribersChanged;

        foreach (var browse in this.browses.Values)
            browse.Cancellation.Cancel();

        // Goodbyes are sent on the way out without holding up a synchronous shutdown.
        foreach (var id in this.publications.Keys)
        {
            if (this.publications.TryRemove(id, out var publication))
                _ = publication.Registration.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.events.SubscribersChanged -= this.OnSubscribersChanged;

        foreach (var browse in this.browses.Values)
            browse.Cancellation.Cancel();

        foreach (var id in this.publications.Keys)
        {
            if (this.publications.TryRemove(id, out var publication))
                await publication.Registration.DisposeAsync();
        }
    }

    sealed record Browse(string Protocol, string Target, CancellationTokenSource Cancellation);
    sealed record Publication(string Protocol, string Name, IAsyncDisposable Registration);
}

public sealed record BrowseResponse(string Id, string Protocol, string Target);
public sealed record PublicationResponse(string Id, string Protocol, string Name);
public sealed record DiscoveryErrorEvent(string BrowseId, string Protocol, string Message);

public sealed record MdnsSearchRequest(string ServiceType, string? Domain = null, bool ResolveServices = true, int? ScanMs = null);
public sealed record MdnsPublishRequest(string InstanceName, string ServiceType, int Port, string? Domain = null, Dictionary<string, string>? TxtRecords = null);

public sealed record MdnsServiceResponse(
    string InstanceName,
    string ServiceType,
    string Domain,
    string FullName,
    string? HostName,
    int Port,
    IReadOnlyList<string> Addresses,
    IReadOnlyDictionary<string, string> TxtRecords,
    bool IsResolved
)
{
    internal static MdnsServiceResponse From(MdnsService service) => new(
        service.InstanceName,
        service.ServiceType,
        service.Domain,
        service.FullName,
        service.HostName,
        service.Port,
        [.. service.Addresses.Select(x => x.ToString())],
        service.TxtRecords,
        service.IsResolved
    );
}

public sealed record MdnsBrowseEvent(string BrowseId, MdnsBrowseStatus Status, MdnsServiceResponse Service);

public sealed record SsdpSearchRequest(string? SearchTarget = null, int? ScanMs = null, int? MaxWaitMs = null)
{
    internal SsdpBrowseConfig ToConfig() => new(String.IsNullOrWhiteSpace(this.SearchTarget) ? SsdpConstants.SearchAll : this.SearchTarget)
    {
        MaxWait = TimeSpan.FromMilliseconds(Math.Clamp(this.MaxWaitMs ?? 3_000, 1_000, 5_000))
    };
}

public sealed record SsdpPublishRequest(
    string Udn,
    string Location,
    string? DeviceType = null,
    List<string>? ServiceTypes = null,
    bool IsRootDevice = true,
    int? MaxAgeSeconds = null,
    string? Server = null
);

public sealed record SsdpDeviceResponse(
    string Udn,
    string? Location,
    string? Server,
    IReadOnlyList<string> NotificationTypes,
    string? SourceAddress,
    bool IsRootDevice,
    string? DeviceType,
    IReadOnlyList<string> ServiceTypes,
    IReadOnlyDictionary<string, string> Headers
)
{
    internal static SsdpDeviceResponse From(SsdpDevice device) => new(
        device.Udn,
        device.Location?.AbsoluteUri,
        device.Server,
        device.NotificationTypes,
        device.SourceAddress?.ToString(),
        device.IsRootDevice,
        device.DeviceType,
        device.ServiceTypes,
        device.Headers
    );
}

public sealed record SsdpBrowseEvent(string BrowseId, SsdpBrowseStatus Status, SsdpDeviceResponse Device);

/// <summary>An XML qualified name as JSON, since the WS-Discovery types are namespaced XML names.</summary>
public sealed record WsdQualifiedName(string Name, string Namespace)
{
    internal static IReadOnlyList<XmlQualifiedName> ToXml(List<WsdQualifiedName>? names)
        => names is null ? [] : [.. names.Where(x => x.Name.Length > 0).Select(x => new XmlQualifiedName(x.Name, x.Namespace))];
}

public sealed record WsdSearchRequest(
    List<WsdQualifiedName>? Types = null,
    List<string>? Scopes = null,
    string? ScopeMatchBy = null,
    WsDiscoveryProfile Profiles = WsDiscoveryProfile.All,
    int? ScanMs = null
);

public sealed record WsdPublishRequest(
    string EndpointReference,
    List<WsdQualifiedName>? Types = null,
    List<string>? Scopes = null,
    List<string>? Addresses = null,
    uint MetadataVersion = 1,
    WsDiscoveryProfile Profiles = WsDiscoveryProfile.All
);

public sealed record WsdTargetResponse(
    string EndpointReference,
    IReadOnlyList<WsdQualifiedName> Types,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Addresses,
    string? PreferredAddress,
    uint MetadataVersion,
    string? SourceAddress,
    WsDiscoveryProfile Profile,
    bool IsOnvifCamera
)
{
    internal static WsdTargetResponse From(WsdTarget target) => new(
        target.EndpointReference,
        [.. target.Types.Select(x => new WsdQualifiedName(x.Name, x.Namespace))],
        target.Scopes,
        [.. target.Addresses.Select(x => x.AbsoluteUri)],
        target.PreferredAddress?.AbsoluteUri,
        target.MetadataVersion,
        target.SourceAddress?.ToString(),
        target.Profile,
        target.IsOnvifCamera
    );
}

public sealed record WsdBrowseEvent(string BrowseId, WsDiscoveryStatus Status, WsdTargetResponse Target);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowseResponse))]
[JsonSerializable(typeof(List<BrowseResponse>))]
[JsonSerializable(typeof(PublicationResponse))]
[JsonSerializable(typeof(List<PublicationResponse>))]
[JsonSerializable(typeof(DiscoveryErrorEvent))]
[JsonSerializable(typeof(MdnsSearchRequest))]
[JsonSerializable(typeof(MdnsPublishRequest))]
[JsonSerializable(typeof(MdnsServiceResponse))]
[JsonSerializable(typeof(List<MdnsServiceResponse>))]
[JsonSerializable(typeof(MdnsBrowseEvent))]
[JsonSerializable(typeof(SsdpSearchRequest))]
[JsonSerializable(typeof(SsdpPublishRequest))]
[JsonSerializable(typeof(List<SsdpDeviceResponse>))]
[JsonSerializable(typeof(SsdpBrowseEvent))]
[JsonSerializable(typeof(UpnpDeviceDescription))]
[JsonSerializable(typeof(WsdSearchRequest))]
[JsonSerializable(typeof(WsdPublishRequest))]
[JsonSerializable(typeof(WsdTargetResponse))]
[JsonSerializable(typeof(List<WsdTargetResponse>))]
[JsonSerializable(typeof(WsdBrowseEvent))]
partial class DiscoveryBridgeJsonContext : JsonSerializerContext;
