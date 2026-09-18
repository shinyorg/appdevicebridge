using System.Net.Http.Json;
using System.Text.Json;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The page's side of <c>/_bridge/events</c>, as a test drives it. Opening returns once <c>bridge.stream</c> has arrived,
/// which the host sends only after the named topics' sources are listening — so a test can raise an event straight away.
/// </summary>
sealed class TestEventStream : IAsyncDisposable
{
    readonly HttpClient client;
    readonly HttpResponseMessage response;
    readonly StreamReader reader;

    TestEventStream(HttpClient client, HttpResponseMessage response, StreamReader reader, string id)
    {
        this.client = client;
        this.response = response;
        this.reader = reader;
        this.Id = id;
    }

    public string Id { get; }

    public HttpResponseMessage Response => this.response;

    /// <param name="path">Where the stream is, when the bridges are not at <c>/_bridge</c>.</param>
    public static async Task<TestEventStream> OpenAsync(HttpClient client, string topics, CancellationToken cancellationToken, string path = "/_bridge/events")
    {
        var response = await client.GetAsync($"{path}?topics={Uri.EscapeDataString(topics)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        var stream = new TestEventStream(client, response, reader, "");

        var (name, data) = await stream.NextAsync(cancellationToken);
        Assert.Equal(EventStreamProtocol.OpenedEvent, name);

        return new TestEventStream(client, response, reader, JsonSerializer.Deserialize(data, AppDeviceBridgeJsonContext.Default.EventStreamOpened)!.Id);
    }

    /// <summary>The next event, whatever it is.</summary>
    public async Task<(string Name, string Data)> NextAsync(CancellationToken cancellationToken)
    {
        string? name = null;

        while (await this.reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
                name = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal) && name is not null)
                return (name, line["data:".Length..].Trim());
        }

        throw new EndOfStreamException("The event stream ended.");
    }

    /// <summary>The data of the next <paramref name="eventName"/>, skipping anything else.</summary>
    public async Task<string> NextAsync(string eventName, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (name, data) = await this.NextAsync(cancellationToken);
            if (name == eventName)
                return data;
        }
    }

    /// <summary>Replaces the stream's topics; returns once the new sources are listening.</summary>
    public async Task SetTopicsAsync(CancellationToken cancellationToken, params string[] topics)
    {
        using var response = await this.client.PutAsJsonAsync($"/_bridge/events/{this.Id}", new EventStreamTopics(topics), AppDeviceBridgeJsonContext.Default.EventStreamTopics, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        this.reader.Dispose();
        this.response.Dispose();
        return ValueTask.CompletedTask;
    }
}
