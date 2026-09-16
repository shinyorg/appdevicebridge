using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Discovery.Client;

public enum DiscoveryProtocol { Mdns, Ssdp, Wsd }

/// <summary>Whether a browse result is new or went away.</summary>
public enum DiscoveryStatus { Found, Lost }

[Flags]
public enum WsDiscoveryProfile { None = 0, Ws2005 = 1, Ws2009 = 2, All = Ws2005 | Ws2009 }

/// <param name="Target">What is being browsed for: a service type, search target or type list.</param>
public sealed record DiscoveryBrowse(string Id, DiscoveryProtocol Protocol, string Target);

/// <param name="Name">The name in use, which mDNS may have changed to settle a conflict on the network.</param>
public sealed record DiscoveryPublication(string Id, DiscoveryProtocol Protocol, string Name);

public sealed record DiscoveryError(string BrowseId, DiscoveryProtocol Protocol, string Message);

/// <param name="ServiceType">Such as <c>_http._tcp</c>.</param>
/// <param name="Domain"><c>local.</c> when null.</param>
/// <param name="ResolveServices">Resolve host, port, addresses and TXT records for each service found.</param>
/// <param name="ScanMs">A search's window, 250–30,000 ms; 5 seconds when null. Ignored by a browse.</param>
public sealed record MdnsSearchRequest(string ServiceType, string? Domain = null, bool ResolveServices = true, int? ScanMs = null);

/// <param name="Port">1–65535.</param>
public sealed record MdnsPublishRequest(
    string InstanceName,
    string ServiceType,
    int Port,
    string? Domain = null,
    IReadOnlyDictionary<string, string>? TxtRecords = null
);

public sealed record MdnsService(
    string InstanceName,
    string ServiceType,
    string Domain,
    string FullName,
    string? HostName,
    int Port,
    IReadOnlyList<string> Addresses,
    IReadOnlyDictionary<string, string> TxtRecords,
    bool IsResolved
);

public sealed record MdnsBrowseResult(string BrowseId, DiscoveryStatus Status, MdnsService Service);

/// <param name="SearchTarget"><c>ssdp:all</c> when null.</param>
/// <param name="ScanMs">A search's window, 250–30,000 ms; 5 seconds when null. Ignored by a browse.</param>
/// <param name="MaxWaitMs">How long devices may wait before answering, 1,000–5,000 ms.</param>
public sealed record SsdpSearchRequest(string? SearchTarget = null, int? ScanMs = null, int? MaxWaitMs = null);

/// <param name="Location">The absolute URL of the device description XML.</param>
public sealed record SsdpPublishRequest(
    string Udn,
    string Location,
    string? DeviceType = null,
    IReadOnlyList<string>? ServiceTypes = null,
    bool IsRootDevice = true,
    int? MaxAgeSeconds = null,
    string? Server = null
);

public sealed record SsdpDevice(
    string Udn,
    string? Location,
    string? Server,
    IReadOnlyList<string> NotificationTypes,
    string? SourceAddress,
    bool IsRootDevice,
    string? DeviceType,
    IReadOnlyList<string> ServiceTypes,
    IReadOnlyDictionary<string, string> Headers
);

public sealed record SsdpBrowseResult(string BrowseId, DiscoveryStatus Status, SsdpDevice Device);

public sealed record UpnpIcon(string? MimeType, int Width, int Height, int Depth, string? Url);

public sealed record UpnpService(string? ServiceType, string? ServiceId, string? ScpdUrl, string? ControlUrl, string? EventSubscriptionUrl);

/// <summary>A device's UPnP description, with its embedded devices.</summary>
public sealed record UpnpDeviceDescription(
    string? Udn,
    string? DeviceType,
    string? FriendlyName,
    string? Manufacturer,
    string? ManufacturerUrl,
    string? ModelName,
    string? ModelNumber,
    string? ModelDescription,
    string? ModelUrl,
    string? SerialNumber,
    string? Upc,
    string? PresentationUrl,
    IReadOnlyList<UpnpIcon> Icons,
    IReadOnlyList<UpnpService> Services,
    IReadOnlyList<UpnpDeviceDescription> Devices
);

/// <summary>An XML qualified name, since WS-Discovery types are namespaced XML names.</summary>
public sealed record WsdQualifiedName(string Name, string Namespace);

/// <param name="ScanMs">A search's window, 250–30,000 ms; 5 seconds when null. Ignored by a browse.</param>
public sealed record WsdSearchRequest(
    IReadOnlyList<WsdQualifiedName>? Types = null,
    IReadOnlyList<string>? Scopes = null,
    string? ScopeMatchBy = null,
    WsDiscoveryProfile Profiles = WsDiscoveryProfile.All,
    int? ScanMs = null
);

/// <param name="Addresses">Absolute URLs the target answers at.</param>
public sealed record WsdPublishRequest(
    string EndpointReference,
    IReadOnlyList<WsdQualifiedName>? Types = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? Addresses = null,
    uint MetadataVersion = 1,
    WsDiscoveryProfile Profiles = WsDiscoveryProfile.All
);

public sealed record WsdTarget(
    string EndpointReference,
    IReadOnlyList<WsdQualifiedName> Types,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Addresses,
    string? PreferredAddress,
    uint MetadataVersion,
    string? SourceAddress,
    WsDiscoveryProfile Profile,
    bool IsOnvifCamera
);

public sealed record WsdBrowseResult(string BrowseId, DiscoveryStatus Status, WsdTarget Target);

/// <summary>Serialization for every discovery contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscoveryBrowse))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveryBrowse>))]
[JsonSerializable(typeof(DiscoveryPublication))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveryPublication>))]
[JsonSerializable(typeof(DiscoveryError))]
[JsonSerializable(typeof(MdnsSearchRequest))]
[JsonSerializable(typeof(MdnsPublishRequest))]
[JsonSerializable(typeof(MdnsService))]
[JsonSerializable(typeof(IReadOnlyList<MdnsService>))]
[JsonSerializable(typeof(MdnsBrowseResult))]
[JsonSerializable(typeof(SsdpSearchRequest))]
[JsonSerializable(typeof(SsdpPublishRequest))]
[JsonSerializable(typeof(IReadOnlyList<SsdpDevice>))]
[JsonSerializable(typeof(SsdpBrowseResult))]
[JsonSerializable(typeof(UpnpDeviceDescription))]
[JsonSerializable(typeof(WsdSearchRequest))]
[JsonSerializable(typeof(WsdPublishRequest))]
[JsonSerializable(typeof(WsdTarget))]
[JsonSerializable(typeof(IReadOnlyList<WsdTarget>))]
[JsonSerializable(typeof(WsdBrowseResult))]
public partial class DiscoveryJsonContext : JsonSerializerContext;
