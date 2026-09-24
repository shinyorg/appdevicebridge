using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;
using AppSupportClient = Shiny.AppDeviceBridge.AppSupport.Client;
using BleClient = Shiny.AppDeviceBridge.BluetoothLE.Client;
using CalendarClient = Shiny.AppDeviceBridge.Calendar.Client;
using CameraClient = Shiny.AppDeviceBridge.Camera.Client;
using ContactsClient = Shiny.AppDeviceBridge.Contacts.Client;
using DesktopClient = Shiny.AppDeviceBridge.Desktop.Client;
using DiscoveryClient = Shiny.AppDeviceBridge.Discovery.Client;
using FoldersClient = Shiny.AppDeviceBridge.Folders.Client;
using HealthClient = Shiny.AppDeviceBridge.Health.Client;
using LocationsClient = Shiny.AppDeviceBridge.Locations.Client;
using MapsClient = Shiny.AppDeviceBridge.Maps.Client;
using NotificationsClient = Shiny.AppDeviceBridge.Notifications.Client;
using ObdClient = Shiny.AppDeviceBridge.Obd.Client;
using PhotosClient = Shiny.AppDeviceBridge.Photos.Client;
using PushClient = Shiny.AppDeviceBridge.Push.Client;
using RpiCameraClient = Shiny.AppDeviceBridge.RpiCamera.Client;
using ScreenRecorderClient = Shiny.AppDeviceBridge.ScreenRecorder.Client;
using SpeechClient = Shiny.AppDeviceBridge.Speech.Client;
using TransfersClient = Shiny.AppDeviceBridge.HttpTransfers.Client;
using WearablesClient = Shiny.AppDeviceBridge.Wearables.Client;
using WifiClient = Shiny.AppDeviceBridge.Wifi.Client;

namespace Shiny.AppDeviceBridge.Simulator.Catalog;

/// <summary>What a route answers with when it succeeds.</summary>
public enum ResponseKind
{
    /// <summary><c>Task</c>: 204.</summary>
    Empty,

    /// <summary>A contract, serialized through the bridge's own JSON context.</summary>
    Json,

    /// <summary><c>Task&lt;Stream&gt;</c> or <c>Task&lt;byte[]&gt;</c>: raw bytes, such as a photo.</summary>
    Binary
}

/// <summary>One endpoint of a bridge, as its <c>[BridgeClient]</c> interface declares it.</summary>
/// <param name="Operation">The interface method's name, without <c>Async</c>.</param>
/// <param name="Pattern">Relative to the bridge; empty for the bridge's root.</param>
/// <param name="QueryParameters">The names the page sends in the query string.</param>
public sealed record SimRoute(
    string Bridge,
    string Method,
    string Pattern,
    string Operation,
    ResponseKind Kind,
    Type? ResultType,
    Type? BodyType,
    IReadOnlyList<string> QueryParameters,
    JsonSerializerContext Json
)
{
    /// <summary>Unique within a bridge: <c>GET networks</c>, <c>DELETE known</c>, <c>GET</c> for the root.</summary>
    public string Key => this.Pattern.Length == 0 ? this.Method : $"{this.Method} {this.Pattern}";

    /// <summary>The path under the bridge prefix: <c>wifi/networks</c>.</summary>
    public string Path => this.Pattern.Length == 0 ? this.Bridge : $"{this.Bridge}/{this.Pattern}";
}

/// <summary>A native event a bridge raises on the page's event stream.</summary>
public sealed record SimEvent(string Bridge, string Name, string Operation, Type PayloadType, JsonSerializerContext Json);

/// <summary>A bridge: its routes and events, read from its <c>[BridgeClient]</c> interface.</summary>
public sealed record SimBridge(string Name, string Package, IReadOnlyList<SimRoute> Routes, IReadOnlyList<SimEvent> Events)
{
    public SimRoute? FindRoute(string key) => this.Routes.FirstOrDefault(x => String.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Every bridge the library ships, described from the same <c>[BridgeClient]</c> interfaces the C# and TypeScript clients
/// are generated from — so the simulator answers exactly the requests those clients send. Adding a bridge means adding its
/// interface here; a test fails until it is.
/// <para>
/// <c>host</c>, <c>settings</c> and <c>files</c> are not simulated: the simulator runs the real bridge server, which
/// answers them itself.
/// </para>
/// </summary>
public static class BridgeCatalog
{
    /// <summary>Bridges the bridge server provides for real, which the simulator leaves to it.</summary>
    public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal) { "host", "settings", "files" };

    static IReadOnlyList<SimBridge>? all;

    /// <summary>Every simulated bridge, ordered by name.</summary>
    public static IReadOnlyList<SimBridge> All => all ??= Build();

    public static SimBridge? Find(string name) => All.FirstOrDefault(x => x.Name == name);

    static IReadOnlyList<SimBridge> Build() =>
    [
        .. new[]
        {
            Describe(typeof(ILinksBridge), AppDeviceBridgeJsonContext.Default),
            Describe(typeof(AppSupportClient.IAppBridge), AppSupportClient.AppJsonContext.Default),
            Describe(typeof(AppSupportClient.ISensorsBridge), AppSupportClient.SensorsJsonContext.Default),
            Describe(typeof(BleClient.IBluetoothLEBridge), BleClient.BleJsonContext.Default),
            Describe(typeof(CalendarClient.ICalendarBridge), CalendarClient.CalendarJsonContext.Default),
            Describe(typeof(CameraClient.ICameraBridge), CameraClient.CameraJsonContext.Default),
            Describe(typeof(ContactsClient.IContactsBridge), ContactsClient.ContactsJsonContext.Default),
            Describe(typeof(DesktopClient.IQuickEntryBridge), DesktopClient.QuickEntryJsonContext.Default),
            Describe(typeof(DesktopClient.ITrayBridge), DesktopClient.TrayJsonContext.Default),
            Describe(typeof(DiscoveryClient.IDiscoveryBridge), DiscoveryClient.DiscoveryJsonContext.Default),
            Describe(typeof(FoldersClient.IFoldersBridge), FoldersClient.FoldersJsonContext.Default),
            Describe(typeof(HealthClient.IHealthBridge), HealthClient.HealthJsonContext.Default),
            Describe(typeof(TransfersClient.ITransfersBridge), TransfersClient.TransfersJsonContext.Default),
            Describe(typeof(LocationsClient.IGpsBridge), LocationsClient.LocationsJsonContext.Default),
            Describe(typeof(LocationsClient.IGeofencesBridge), LocationsClient.LocationsJsonContext.Default),
            Describe(typeof(LocationsClient.IMotionBridge), LocationsClient.LocationsJsonContext.Default),
            Describe(typeof(MapsClient.IMapsBridge), MapsClient.MapsJsonContext.Default),
            Describe(typeof(MapsClient.IDirectionsBridge), MapsClient.DirectionsJsonContext.Default),
            Describe(typeof(NotificationsClient.INotificationsBridge), NotificationsClient.NotificationsJsonContext.Default),
            Describe(typeof(ObdClient.IObdBridge), ObdClient.ObdJsonContext.Default),
            Describe(typeof(PhotosClient.IPhotosBridge), PhotosClient.PhotosJsonContext.Default),
            Describe(typeof(PushClient.IPushBridge), PushClient.PushJsonContext.Default),
            Describe(typeof(RpiCameraClient.IRpiCameraBridge), RpiCameraClient.RpiCameraJsonContext.Default),
            Describe(typeof(ScreenRecorderClient.IScreenRecorderBridge), ScreenRecorderClient.ScreenRecorderJsonContext.Default),
            Describe(typeof(SpeechClient.ISpeechBridge), SpeechClient.SpeechJsonContext.Default),
            Describe(typeof(WearablesClient.IWearablesBridge), WearablesClient.WearablesJsonContext.Default),
            Describe(typeof(WifiClient.IWifiBridge), WifiClient.WifiJsonContext.Default)
        }.OrderBy(x => x.Name, StringComparer.Ordinal)
    ];

    /// <summary>
    /// Reads one <c>[BridgeClient]</c> interface. Parameters are classified exactly as the generators classify them — route
    /// token, then body, then query — so a request the typed client builds lands on the route described here.
    /// </summary>
    public static SimBridge Describe(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type bridge,
        JsonSerializerContext json
    )
    {
        var client = bridge.GetCustomAttribute<BridgeClientAttribute>()
            ?? throw new ArgumentException($"{bridge.Name} has no [BridgeClient] attribute.", nameof(bridge));

        var routes = new List<SimRoute>();
        var events = new List<SimEvent>();

        foreach (var method in bridge.GetMethods().OrderBy(x => x.MetadataToken))
        {
            var operation = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^"Async".Length] : method.Name;

            if (method.GetCustomAttribute<BridgeEventAttribute>() is { } evt)
            {
                // Task<IAsyncDisposable> OnXAsync(Func<TPayload, Task> handler)
                var payload = method.GetParameters()[0].ParameterType.GetGenericArguments()[0];
                events.Add(new SimEvent(client.Name, evt.EventName, operation, payload, json));
                continue;
            }

            if (method.GetCustomAttribute<BridgeRouteAttribute>() is not { } route)
                continue;

            var result = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? method.ReturnType.GetGenericArguments()[0]
                : null;

            var kind = result switch
            {
                null => ResponseKind.Empty,
                _ when result == typeof(Stream) || result == typeof(byte[]) || result == typeof(HttpResponseMessage) => ResponseKind.Binary,
                _ => ResponseKind.Json
            };

            Type? body = null;
            var query = new List<string>();

            foreach (var parameter in method.GetParameters())
            {
                switch (Classify(parameter, route))
                {
                    case ParameterKind.Body:
                        body = parameter.ParameterType;
                        break;

                    case ParameterKind.Query:
                        query.Add(parameter.GetCustomAttribute<BridgeQueryAttribute>()?.Name ?? parameter.Name!);
                        break;
                }
            }

            routes.Add(new SimRoute(
                client.Name,
                route.Method,
                route.Pattern.Trim('/'),
                operation,
                kind,
                kind == ResponseKind.Json ? result : null,
                body,
                query,
                json
            ));
        }

        return new SimBridge(client.Name, bridge.Assembly.GetName().Name!, routes, events);
    }

    enum ParameterKind { Cancellation, Route, Body, Query }

    static ParameterKind Classify(ParameterInfo parameter, BridgeRouteAttribute route)
    {
        if (parameter.ParameterType == typeof(CancellationToken))
            return ParameterKind.Cancellation;

        if (route.Pattern.Contains("{" + parameter.Name + "}", StringComparison.Ordinal))
            return ParameterKind.Route;

        if (parameter.GetCustomAttribute<BridgeBodyAttribute>() is not null || (!IsSimple(parameter.ParameterType) && route.Method is "POST" or "PUT"))
            return ParameterKind.Body;

        return ParameterKind.Query;
    }

    static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
               || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan);
    }
}
