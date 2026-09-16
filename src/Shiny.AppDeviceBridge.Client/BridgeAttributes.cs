namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// Declares a bridge's API as an interface. The generator in this package writes the implementation — a class named
/// after the interface without its <c>I</c> and with <c>Client</c> on the end — and an <c>Add…Client()</c>
/// registration; the TypeScript tool reads the same declaration, so the C# and TypeScript clients cannot drift.
/// <code>
/// [BridgeClient("calendar", typeof(CalendarJsonContext))]
/// public interface ICalendarBridge
/// {
///     [BridgeGet("events/{id}")]
///     Task&lt;CalendarEvent&gt; GetEventAsync(string id, CancellationToken cancellationToken = default);
/// }
/// </code>
/// </summary>
/// <param name="name">The bridge's route segment — what follows the bridge prefix.</param>
/// <param name="jsonContext">
/// The <c>JsonSerializerContext</c> covering every type the interface sends or receives. Serialization goes through
/// it, never through reflection, so the client survives trimming and AOT.
/// </param>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class BridgeClientAttribute(string name, Type jsonContext) : Attribute
{
    public string Name { get; } = name;

    public Type JsonContext { get; } = jsonContext;
}

/// <summary>Base for the verb attributes. The pattern is relative to the bridge, and may name route parameters: <c>events/{id}</c>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public abstract class BridgeRouteAttribute(string method, string pattern) : Attribute
{
    public string Method { get; } = method;

    public string Pattern { get; } = pattern;
}

public sealed class BridgeGetAttribute(string pattern = "") : BridgeRouteAttribute("GET", pattern);

public sealed class BridgePostAttribute(string pattern = "") : BridgeRouteAttribute("POST", pattern);

public sealed class BridgePutAttribute(string pattern = "") : BridgeRouteAttribute("PUT", pattern);

public sealed class BridgeDeleteAttribute(string pattern = "") : BridgeRouteAttribute("DELETE", pattern);

/// <summary>
/// A native event, subscribed to by a method shaped
/// <c>Task&lt;IAsyncDisposable&gt; OnChangedAsync(Func&lt;WifiChanged, Task&gt; handler)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class BridgeEventAttribute(string eventName) : Attribute
{
    public string EventName { get; } = eventName;
}

/// <summary>
/// Sends a parameter as the request body. Rarely needed: a complex type on a POST or PUT is the body already. Use it
/// for a <c>Stream</c> or <c>byte[]</c> sent as raw content.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class BridgeBodyAttribute(string contentType = "application/json") : Attribute
{
    public string ContentType { get; } = contentType;
}

/// <summary>Renames a query parameter. Simple parameters not in the route are query parameters already, under their own name.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class BridgeQueryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
