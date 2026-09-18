using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Discovery.Client;
using static Shiny.AppDeviceBridge.Discovery.DiscoveryContractMapping;

namespace Shiny.AppDeviceBridge.Discovery;

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
    public static TBuilder AddDiscoveryBridge<TBuilder>(this TBuilder bridge, DiscoveryProtocols protocols = DiscoveryProtocols.All)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        if (protocols.HasFlag(DiscoveryProtocols.Mdns))
            bridge.Services.AddMdns();

        if (protocols.HasFlag(DiscoveryProtocols.Ssdp))
            bridge.Services.AddSsdp();

        if (protocols.HasFlag(DiscoveryProtocols.WsDiscovery))
            bridge.Services.AddWsDiscovery();

        bridge.AddBridge<DiscoveryBridge>();
        return bridge;
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
/// A browse needs a listener on its protocol's result topic (<c>discovery.mdns</c>, <c>discovery.ssdp</c> or
/// <c>discovery.wsd</c>) to start, and every browse of that protocol stops when the topic's last listener leaves, since
/// its results would have nowhere to go. Publications keep advertising until deleted, or until the app exits.
/// </para>
/// </summary>
public sealed class DiscoveryBridge : IWebAppBridge, IDisposable, IAsyncDisposable
{
    const int MaxBrowses = 8;
    const int MaxPublications = 16;

    readonly IMdnsManager? mdns;
    readonly ISsdpManager? ssdp;
    readonly IWsDiscoveryManager? wsd;
    readonly WebAppEventSource<Contracts.MdnsBrowseResult> mdnsResults = new();
    readonly WebAppEventSource<Contracts.SsdpBrowseResult> ssdpResults = new();
    readonly WebAppEventSource<Contracts.WsdBrowseResult> wsdResults = new();
    readonly WebAppEventSource<Contracts.DiscoveryError> errors = new();
    readonly WebAppEventSource<Contracts.DiscoveryBrowse> stopped = new();
    readonly ConcurrentDictionary<string, Browse> browses = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Publication> publications = new(StringComparer.Ordinal);

    // GetDescription needs the device as it was advertised, so every device the page has seen is kept by UDN.
    readonly ConcurrentDictionary<string, SsdpDevice> seenDevices = new(StringComparer.OrdinalIgnoreCase);

    public DiscoveryBridge(IServiceProvider services)
    {
        this.mdns = services.GetOptionalService<IMdnsManager>();
        this.ssdp = services.GetOptionalService<ISsdpManager>();
        this.wsd = services.GetOptionalService<IWsDiscoveryManager>();
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
        .MapDelete("/publications/{id}", this.UnpublishAsync)
        .MapEvent("discovery.mdns", ct => this.mdnsResults.ListenAsync(r => this.OnResultListenerStopped(Contracts.DiscoveryProtocol.Mdns, r), ct), Contracts.DiscoveryJsonContext.Default.MdnsBrowseResult)
        .MapEvent("discovery.ssdp", ct => this.ssdpResults.ListenAsync(r => this.OnResultListenerStopped(Contracts.DiscoveryProtocol.Ssdp, r), ct), Contracts.DiscoveryJsonContext.Default.SsdpBrowseResult)
        .MapEvent("discovery.wsd", ct => this.wsdResults.ListenAsync(r => this.OnResultListenerStopped(Contracts.DiscoveryProtocol.Wsd, r), ct), Contracts.DiscoveryJsonContext.Default.WsdBrowseResult)
        .MapEvent("discovery.error", this.errors.ListenAsync, Contracts.DiscoveryJsonContext.Default.DiscoveryError)
        .MapEvent("discovery.stopped", this.stopped.ListenAsync, Contracts.DiscoveryJsonContext.Default.DiscoveryBrowse);

    // ---- mDNS

    async ValueTask MdnsSearchAsync(HttpContext context, IMdnsManager manager)
    {
        if (await ReadMdnsConfigAsync(context) is not { } request)
            return;

        var services = await CollectAsync(
            ct => manager.Browse(request.Config, ct),
            r => (r.Status == MdnsBrowseStatus.Found, r.Service.FullName, ToContract(r.Service)),
            request.Window,
            context.RequestAborted
        );

        await Json(context, services, Contracts.DiscoveryJsonContext.Default.IReadOnlyListMdnsService);
    }

    async ValueTask MdnsBrowseAsync(HttpContext context, IMdnsManager manager)
    {
        if (await ReadMdnsConfigAsync(context) is not { } request)
            return;

        await this.StartBrowseAsync(
            context,
            Contracts.DiscoveryProtocol.Mdns,
            request.Config.ServiceType,
            ct => manager.Browse(request.Config, ct),
            this.mdnsResults,
            (id, r) => new Contracts.MdnsBrowseResult(id, Convert(r.Status), ToContract(r.Service))
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
            : Json(context, ToContract(service), Contracts.DiscoveryJsonContext.Default.MdnsService));
    }

    async ValueTask MdnsPublishAsync(HttpContext context, IMdnsManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.MdnsPublishRequest);

        if (body is not { InstanceName.Length: > 0, ServiceType.Length: > 0, Port: > 0 and <= 65535 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"instanceName\": \"…\", \"serviceType\": \"_name._tcp\", \"port\": 1-65535 }.");
            return;
        }

        await this.PublishAsync(context, Contracts.DiscoveryProtocol.Mdns, async () =>
        {
            var publication = await manager.Publish(
                new MdnsServiceRegistration(body.InstanceName, body.ServiceType, body.Port)
                {
                    Domain = body.Domain ?? MdnsConstants.LocalDomain,
                    TxtRecords = body.TxtRecords is null ? [] : new Dictionary<string, string>(body.TxtRecords)
                },
                context.RequestAborted
            );

            // The instance name may have been changed to settle a conflict on the network; report the one in use.
            return (publication, $"{publication.InstanceName}.{publication.ServiceType}.{publication.Domain}");
        });
    }

    static async ValueTask<(MdnsBrowseConfig Config, TimeSpan Window)?> ReadMdnsConfigAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.MdnsSearchRequest);

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
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.SsdpSearchRequest) ?? new Contracts.SsdpSearchRequest();

        var devices = await CollectAsync(
            ct => manager.Browse(ToConfig(body), ct),
            r =>
            {
                this.seenDevices[r.Device.Udn] = r.Device;
                return (r.Status == SsdpBrowseStatus.Found, r.Device.Udn, ToContract(r.Device));
            },
            Window(body.ScanMs),
            context.RequestAborted
        );

        await Json(context, devices, Contracts.DiscoveryJsonContext.Default.IReadOnlyListSsdpDevice);
    }

    async ValueTask SsdpBrowseAsync(HttpContext context, ISsdpManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.SsdpSearchRequest) ?? new Contracts.SsdpSearchRequest();
        var config = ToConfig(body);

        await this.StartBrowseAsync(
            context,
            Contracts.DiscoveryProtocol.Ssdp,
            config.SearchTarget,
            ct => manager.Browse(config, ct),
            this.ssdpResults,
            (id, r) =>
            {
                this.seenDevices[r.Device.Udn] = r.Device;
                return new Contracts.SsdpBrowseResult(id, Convert(r.Status), ToContract(r.Device));
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
        await Json(context, ToContract(description), Contracts.DiscoveryJsonContext.Default.UpnpDeviceDescription);
    }

    async ValueTask SsdpPublishAsync(HttpContext context, ISsdpManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.SsdpPublishRequest);

        if (body is not { Udn.Length: > 0 } || !Uri.TryCreate(body.Location, UriKind.Absolute, out var location))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"udn\": \"uuid:…\", \"location\": \"http://…/description.xml\" }.");
            return;
        }

        await this.PublishAsync(context, Contracts.DiscoveryProtocol.Ssdp, async () =>
        {
            var publication = await manager.Publish(
                new SsdpDeviceRegistration(body.Udn, location)
                {
                    DeviceType = body.DeviceType,
                    ServiceTypes = body.ServiceTypes is null ? [] : [.. body.ServiceTypes],
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
            r => (r.Status == WsDiscoveryStatus.Found, r.Target.EndpointReference, ToContract(r.Target)),
            request.Window,
            context.RequestAborted
        );

        await Json(context, targets, Contracts.DiscoveryJsonContext.Default.IReadOnlyListWsdTarget);
    }

    async ValueTask WsdBrowseAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        if (await ReadProbeAsync(context) is not { } request)
            return;

        await this.StartBrowseAsync(
            context,
            Contracts.DiscoveryProtocol.Wsd,
            String.Join(", ", request.Config.Types),
            ct => manager.Browse(request.Config, ct),
            this.wsdResults,
            (id, r) => new Contracts.WsdBrowseResult(id, Convert(r.Status), ToContract(r.Target))
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
            : Json(context, ToContract(target), Contracts.DiscoveryJsonContext.Default.WsdTarget));
    }

    async ValueTask WsdPublishAsync(HttpContext context, IWsDiscoveryManager manager)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.WsdPublishRequest);

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

        await this.PublishAsync(context, Contracts.DiscoveryProtocol.Wsd, async () =>
        {
            var publication = await manager.Publish(
                new WsDiscoveryRegistration(body.EndpointReference)
                {
                    Types = ToXml(body.Types),
                    Scopes = body.Scopes is null ? [] : [.. body.Scopes],
                    Addresses = addresses,
                    MetadataVersion = body.MetadataVersion,
                    Profiles = Convert(body.Profiles)
                },
                context.RequestAborted
            );

            return (publication, publication.EndpointReference);
        });
    }

    static async ValueTask<(WsdProbeConfig Config, TimeSpan Window)?> ReadProbeAsync(HttpContext context)
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.DiscoveryJsonContext.Default.WsdSearchRequest) ?? new Contracts.WsdSearchRequest();

        var config = new WsdProbeConfig
        {
            Types = ToXml(body.Types),
            Scopes = body.Scopes is null ? [] : [.. body.Scopes],
            ScopeMatchBy = body.ScopeMatchBy,
            Profiles = Convert(body.Profiles)
        };

        return (config, Window(body.ScanMs));
    }

    // ---- sessions

    async ValueTask StartBrowseAsync<TResult, TEvent>(
        HttpContext context,
        Contracts.DiscoveryProtocol protocol,
        string target,
        Func<CancellationToken, IAsyncEnumerable<TResult>> stream,
        WebAppEventSource<TEvent> results,
        Func<string, TResult, TEvent> toEvent
    )
    {
        if (!results.HasListeners)
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status409Conflict,
                "not_listening",
                $"Listen for {ResultEventName(protocol)} first: browse results arrive as events."
            );
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

        // The last listener may have left between the check and the add; its stop would have missed this browse.
        if (!results.HasListeners)
            browse.Cancellation.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in stream(browse.Cancellation.Token).ConfigureAwait(false))
                    results.Publish(toEvent(id, result));
            }
            catch (OperationCanceledException) when (browse.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                this.errors.Publish(new Contracts.DiscoveryError(id, protocol, ex.Message));
            }
            finally
            {
                this.browses.TryRemove(id, out _);
                this.stopped.Publish(new Contracts.DiscoveryBrowse(id, protocol, target));
            }
        });

        await Json(context, new Contracts.DiscoveryBrowse(id, protocol, target), Contracts.DiscoveryJsonContext.Default.DiscoveryBrowse);
    }

    async ValueTask PublishAsync(HttpContext context, Contracts.DiscoveryProtocol protocol, Func<Task<(IAsyncDisposable Publication, string Name)>> publish)
    {
        if (this.publications.Count >= MaxPublications)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status429TooManyRequests, "too_many_publications", $"At most {MaxPublications} publications at once. Remove one first.");
            return;
        }

        var (publication, name) = await publish();
        var id = Guid.NewGuid().ToString("n");
        this.publications[id] = new Publication(protocol, name, publication);

        await Json(context, new Contracts.DiscoveryPublication(id, protocol, name), Contracts.DiscoveryJsonContext.Default.DiscoveryPublication);
    }

    ValueTask ListBrowsesAsync(HttpContext context)
    {
        IReadOnlyList<Contracts.DiscoveryBrowse> list = [.. this.browses.Select(x => new Contracts.DiscoveryBrowse(x.Key, x.Value.Protocol, x.Value.Target))];
        return Json(context, list, Contracts.DiscoveryJsonContext.Default.IReadOnlyListDiscoveryBrowse);
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
        IReadOnlyList<Contracts.DiscoveryPublication> list = [.. this.publications.Select(x => new Contracts.DiscoveryPublication(x.Key, x.Value.Protocol, x.Value.Name))];
        return Json(context, list, Contracts.DiscoveryJsonContext.Default.IReadOnlyListDiscoveryPublication);
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

    /// <summary>With no one left listening for a protocol's results, its browses have nowhere to send them.</summary>
    void OnResultListenerStopped(Contracts.DiscoveryProtocol protocol, int remaining)
    {
        if (remaining > 0)
            return;

        foreach (var browse in this.browses.Values)
        {
            if (browse.Protocol == protocol)
                browse.Cancellation.Cancel();
        }
    }

    static string ResultEventName(Contracts.DiscoveryProtocol protocol) => protocol switch
    {
        Contracts.DiscoveryProtocol.Mdns => "discovery.mdns",
        Contracts.DiscoveryProtocol.Ssdp => "discovery.ssdp",
        _ => "discovery.wsd"
    };

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
        foreach (var browse in this.browses.Values)
            browse.Cancellation.Cancel();

        foreach (var id in this.publications.Keys)
        {
            if (this.publications.TryRemove(id, out var publication))
                await publication.Registration.DisposeAsync();
        }
    }

    sealed record Browse(Contracts.DiscoveryProtocol Protocol, string Target, CancellationTokenSource Cancellation);
    sealed record Publication(Contracts.DiscoveryProtocol Protocol, string Name, IAsyncDisposable Registration);
}

static class DiscoveryContractMapping
{
    public static Contracts.DiscoveryStatus Convert(MdnsBrowseStatus status) => BridgeEnum.Convert<MdnsBrowseStatus, Contracts.DiscoveryStatus>(status);

    public static Contracts.DiscoveryStatus Convert(SsdpBrowseStatus status) => BridgeEnum.Convert<SsdpBrowseStatus, Contracts.DiscoveryStatus>(status);

    public static Contracts.DiscoveryStatus Convert(WsDiscoveryStatus status) => BridgeEnum.Convert<WsDiscoveryStatus, Contracts.DiscoveryStatus>(status);

    // Flags: by value, since both declare the same bits and a combination has no single name.
    public static WsDiscoveryProfile Convert(Contracts.WsDiscoveryProfile profile) => (WsDiscoveryProfile)(int)profile;

    public static Contracts.WsDiscoveryProfile Convert(WsDiscoveryProfile profile) => (Contracts.WsDiscoveryProfile)(int)profile;

    public static Contracts.MdnsService ToContract(MdnsService service) => new(
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

    public static SsdpBrowseConfig ToConfig(Contracts.SsdpSearchRequest request)
        => new(String.IsNullOrWhiteSpace(request.SearchTarget) ? SsdpConstants.SearchAll : request.SearchTarget)
        {
            MaxWait = TimeSpan.FromMilliseconds(Math.Clamp(request.MaxWaitMs ?? 3_000, 1_000, 5_000))
        };

    public static Contracts.SsdpDevice ToContract(SsdpDevice device) => new(
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

    public static Contracts.UpnpDeviceDescription ToContract(UpnpDeviceDescription d) => new(
        d.Udn,
        d.DeviceType,
        d.FriendlyName,
        d.Manufacturer,
        d.ManufacturerUrl?.AbsoluteUri,
        d.ModelName,
        d.ModelNumber,
        d.ModelDescription,
        d.ModelUrl?.AbsoluteUri,
        d.SerialNumber,
        d.Upc,
        d.PresentationUrl?.AbsoluteUri,
        [.. d.Icons.Select(x => new Contracts.UpnpIcon(x.MimeType, x.Width, x.Height, x.Depth, x.Url?.AbsoluteUri))],
        [.. d.Services.Select(x => new Contracts.UpnpService(x.ServiceType, x.ServiceId, x.ScpdUrl?.AbsoluteUri, x.ControlUrl?.AbsoluteUri, x.EventSubscriptionUrl?.AbsoluteUri))],
        [.. d.Devices.Select(ToContract)]
    );

    public static IReadOnlyList<XmlQualifiedName> ToXml(IReadOnlyList<Contracts.WsdQualifiedName>? names)
        => names is null ? [] : [.. names.Where(x => x.Name.Length > 0).Select(x => new XmlQualifiedName(x.Name, x.Namespace))];

    public static Contracts.WsdTarget ToContract(WsdTarget target) => new(
        target.EndpointReference,
        [.. target.Types.Select(x => new Contracts.WsdQualifiedName(x.Name, x.Namespace))],
        target.Scopes,
        [.. target.Addresses.Select(x => x.AbsoluteUri)],
        target.PreferredAddress?.AbsoluteUri,
        target.MetadataVersion,
        target.SourceAddress?.ToString(),
        Convert(target.Profile),
        target.IsOnvifCamera
    );
}
