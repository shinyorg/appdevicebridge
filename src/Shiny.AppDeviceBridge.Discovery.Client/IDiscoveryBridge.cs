using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Discovery.Client;

/// <summary>
/// Local network discovery over mDNS/Bonjour, SSDP/UPnP and WS-Discovery: search for a window, browse with results as
/// events, resolve one, and advertise this app. A protocol the app did not register fails with 501. A browse needs a
/// listener for its protocol's results first — it fails with 409 without one — and stops once that has none left;
/// publications keep advertising until removed or the app exits.
/// </summary>
[BridgeClient("discovery", typeof(DiscoveryJsonContext))]
public interface IDiscoveryBridge
{
    /// <summary>The mDNS services seen during the scan window, less any that went away.</summary>
    [BridgePost("mdns/search")]
    Task<IReadOnlyList<MdnsService>> SearchMdnsAsync(MdnsSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Browses for mDNS services; results arrive through <see cref="OnMdnsAsync"/>.</summary>
    [BridgePost("mdns/browse")]
    Task<DiscoveryBrowse> BrowseMdnsAsync(MdnsSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves one mDNS service. Fails with 404 when it does not answer.</summary>
    /// <param name="timeoutMs">250–30,000 ms; 5 seconds when null.</param>
    [BridgeGet("mdns/resolve")]
    Task<MdnsService> ResolveMdnsAsync(string instanceName, string serviceType, int? timeoutMs = null, CancellationToken cancellationToken = default);

    /// <summary>Advertises a service over mDNS. On iOS the type must be listed in <c>NSBonjourServices</c>.</summary>
    [BridgePost("mdns/publications")]
    Task<DiscoveryPublication> PublishMdnsAsync(MdnsPublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>The UPnP devices seen during the scan window, less any that went away.</summary>
    [BridgePost("ssdp/search")]
    Task<IReadOnlyList<SsdpDevice>> SearchSsdpAsync(SsdpSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Browses for UPnP devices; results arrive through <see cref="OnSsdpAsync"/>.</summary>
    [BridgePost("ssdp/browse")]
    Task<DiscoveryBrowse> BrowseSsdpAsync(SsdpSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>A device's UPnP description. The device must have been seen by a search or browse first; otherwise 404.</summary>
    [BridgeGet("ssdp/description")]
    Task<UpnpDeviceDescription> GetSsdpDescriptionAsync(string udn, CancellationToken cancellationToken = default);

    /// <summary>Advertises a device over SSDP.</summary>
    [BridgePost("ssdp/publications")]
    Task<DiscoveryPublication> PublishSsdpAsync(SsdpPublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>The WS-Discovery targets seen during the scan window, less any that went away.</summary>
    [BridgePost("wsd/search")]
    Task<IReadOnlyList<WsdTarget>> SearchWsdAsync(WsdSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Browses for WS-Discovery targets; results arrive through <see cref="OnWsdAsync"/>.</summary>
    [BridgePost("wsd/browse")]
    Task<DiscoveryBrowse> BrowseWsdAsync(WsdSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves one WS-Discovery target. Fails with 404 when it does not answer.</summary>
    /// <param name="timeoutMs">250–30,000 ms; 5 seconds when null.</param>
    [BridgeGet("wsd/resolve")]
    Task<WsdTarget> ResolveWsdAsync(string endpointReference, int? timeoutMs = null, CancellationToken cancellationToken = default);

    /// <summary>Advertises a target over WS-Discovery.</summary>
    [BridgePost("wsd/publications")]
    Task<DiscoveryPublication> PublishWsdAsync(WsdPublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>The running browses.</summary>
    [BridgeGet("browses")]
    Task<IReadOnlyList<DiscoveryBrowse>> GetBrowsesAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops a browse.</summary>
    [BridgeDelete("browses/{id}")]
    Task StopBrowseAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The active publications.</summary>
    [BridgeGet("publications")]
    Task<IReadOnlyList<DiscoveryPublication>> GetPublicationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops advertising, sending a goodbye so it vanishes from other devices straight away.</summary>
    [BridgeDelete("publications/{id}")]
    Task UnpublishAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>An mDNS service was found or lost by a browse.</summary>
    [BridgeEvent("discovery.mdns")]
    Task<IAsyncDisposable> OnMdnsAsync(Func<MdnsBrowseResult, Task> handler);

    /// <summary>A UPnP device was found or lost by a browse.</summary>
    [BridgeEvent("discovery.ssdp")]
    Task<IAsyncDisposable> OnSsdpAsync(Func<SsdpBrowseResult, Task> handler);

    /// <summary>A WS-Discovery target was found or lost by a browse.</summary>
    [BridgeEvent("discovery.wsd")]
    Task<IAsyncDisposable> OnWsdAsync(Func<WsdBrowseResult, Task> handler);

    /// <summary>A browse failed; <see cref="OnStoppedAsync"/> follows.</summary>
    [BridgeEvent("discovery.error")]
    Task<IAsyncDisposable> OnErrorAsync(Func<DiscoveryError, Task> handler);

    /// <summary>A browse ended.</summary>
    [BridgeEvent("discovery.stopped")]
    Task<IAsyncDisposable> OnStoppedAsync(Func<DiscoveryBrowse, Task> handler);
}
