namespace Shiny.AppDeviceBridge.Client;

/// <summary>
/// The protocol of the host's one event stream, <c>GET {bridge}/events</c>. A page opens it once and names the events it
/// wants, in the query (<c>?topics=gps.reading,app.battery</c>) or later with <c>PUT {bridge}/events/{id}</c>. The host
/// only listens to the native source behind an event while some stream asks for it.
/// </summary>
public static class EventStreamProtocol
{
    /// <summary>The first event on every stream: its <see cref="EventStreamOpened"/> id, which is also how a client learns it reconnected.</summary>
    public const string OpenedEvent = "bridge.stream";

    /// <summary>A source failed; its topic is dropped from the stream. Always delivered, whatever the topics.</summary>
    public const string ErrorEvent = "bridge.error";

    /// <summary>The query parameter that names a new stream's first topics, comma separated.</summary>
    public const string TopicsQuery = "topics";

    /// <summary>The most topics one stream carries.</summary>
    public const int MaxTopics = 64;

    /// <summary>What an event or topic name may be: ASCII letters, digits and <c>. : _ -</c>, at most 128 of them.</summary>
    public static bool IsValidEventName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 128
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '-');
}

/// <summary>Sent first on every stream, as <see cref="EventStreamProtocol.OpenedEvent"/>.</summary>
/// <param name="Id">What <c>PUT {bridge}/events/{id}</c> takes to change this stream's topics.</param>
public sealed record EventStreamOpened(string Id);

/// <summary>The events a stream carries, replacing the ones it had. Names the host has no source for are ignored.</summary>
public sealed record EventStreamTopics(IReadOnlyList<string> Topics);

/// <summary>Sent as <see cref="EventStreamProtocol.ErrorEvent"/> when the source behind a topic fails; the topic is dropped.</summary>
/// <param name="Event">The topic whose source failed.</param>
public sealed record EventStreamError(string Event, string Message);
