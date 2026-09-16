using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Calendar.Client;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Client.SourceGenerators;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>The calendar bridge's generated client, against a transport that records what it was asked to send.</summary>
public class TypedClientTests
{
    [Fact]
    public async Task Sends_simple_parameters_as_an_invariant_query()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.OK, """{"items":[],"offset":10,"limit":5,"hasMore":false}""");
        var calendar = new CalendarBridgeClient(transport);

        var page = await calendar.GetEventsAsync(
            new DateTimeOffset(2026, 9, 16, 9, 30, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 9, 17, 9, 30, 0, TimeSpan.FromHours(-4)),
            calendarId: "work",
            search: "stand up & review",
            offset: 10,
            limit: 5
        );

        var request = Assert.Single(transport.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            "calendar/events?start=2026-09-16T09%3A30%3A00.0000000-04%3A00&end=2026-09-17T09%3A30%3A00.0000000-04%3A00"
            + "&calendarId=work&search=stand%20up%20%26%20review&offset=10&limit=5",
            request.Uri
        );
        Assert.Equal((10, 5, false), (page.Offset, page.Limit, page.HasMore));
    }

    [Fact]
    public async Task Leaves_null_parameters_out_of_the_query()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.OK, """{"items":[],"offset":0,"limit":50,"hasMore":false}""");
        await new CalendarBridgeClient(transport).GetEventsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("calendarId", transport.Requests[0].Uri);
        Assert.DoesNotContain("search", transport.Requests[0].Uri);
    }

    [Fact]
    public async Task Escapes_a_route_value_as_one_segment()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.OK, Event);
        await new CalendarBridgeClient(transport).GetEventAsync("a/b c?d");

        Assert.Equal("calendar/events/a%2Fb%20c%3Fd", transport.Requests[0].Uri);
    }

    [Fact]
    public async Task Sends_a_complex_parameter_as_json_with_enums_by_name()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.Created, Event);
        var created = await new CalendarBridgeClient(transport).CreateEventAsync(new NewCalendarEvent
        {
            Title = "Standup",
            Start = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 9, 16, 9, 15, 0, TimeSpan.Zero),
            Availability = EventAvailability.Tentative,
            ReminderMinutes = [5, 15]
        });

        var request = transport.Requests[0];
        Assert.Equal(("POST", "calendar/events", "application/json"), (request.Method, request.Uri, request.ContentType));

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Standup", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Tentative", body.RootElement.GetProperty("availability").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("reminderMinutes").GetArrayLength());

        // And the response came back typed, enums and nested records included.
        Assert.Equal(EventAvailability.Busy, created.Availability);
        Assert.Equal(AttendeeStatus.Accepted, created.Attendees[0].Status);
    }

    [Fact]
    public async Task Completes_a_call_the_host_answers_with_no_content()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.NoContent, null);
        await new CalendarBridgeClient(transport).DeleteEventAsync("42", series: true);

        Assert.Equal(("DELETE", "calendar/events/42?series=true"), (transport.Requests[0].Method, transport.Requests[0].Uri));
    }

    [Fact]
    public async Task Turns_a_bridge_error_into_an_exception_with_its_code()
    {
        var transport = new RecordingTransport()
            .Returns(HttpStatusCode.Forbidden, """{"code":"access_denied","message":"Calendar access has not been granted."}""")
            .Returns(HttpStatusCode.NotImplemented, """{"code":"not_supported","message":"Calendar is not available on this platform."}""");

        var calendar = new CalendarBridgeClient(transport);

        var denied = await Assert.ThrowsAsync<BridgeException>(() => calendar.GetCalendarsAsync());
        Assert.Equal((HttpStatusCode.Forbidden, "access_denied", "Calendar access has not been granted."), (denied.StatusCode, denied.Code, denied.Message));
        Assert.False(denied.IsNotSupported);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => calendar.GetCalendarsAsync())).IsNotSupported);
    }

    [Fact]
    public async Task Reports_a_failure_that_is_not_a_bridge_error_by_its_status()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.BadGateway, "<html>proxy</html>", "text/html");
        var failure = await Assert.ThrowsAsync<BridgeException>(() => new CalendarBridgeClient(transport).GetCalendarsAsync());

        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Null(failure.Code);
    }

    /// <summary>The native bridge reads the same types, so a request missing a required member is refused on the device too.</summary>
    [Fact]
    public void Refuses_a_new_event_without_its_required_members()
        => Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"start":"2026-09-16T09:00:00Z","end":"2026-09-16T10:00:00Z"}""", CalendarJsonContext.Default.NewCalendarEvent));

    const string Event = """
        {
          "id": "42", "calendarId": "work", "title": "Standup", "description": null, "location": null,
          "start": "2026-09-16T09:00:00+00:00", "end": "2026-09-16T09:15:00+00:00", "isAllDay": false,
          "availability": "Busy", "url": null, "isRecurring": false, "recurrenceRule": null,
          "reminderMinutes": [5],
          "attendees": [ { "name": "Ada", "email": "ada@example.com", "role": "Required", "status": "Accepted", "isOrganizer": true } ],
          "organizer": null
        }
        """;
}

/// <summary>The generator's features beyond what the calendar bridge happens to use, and the declarations it must refuse.</summary>
public class BridgeClientGeneratorTests
{
    [Fact]
    public async Task Subscribes_to_an_event_with_a_typed_payload()
    {
        var transport = new RecordingTransport();
        var client = new TestBridgeClient(transport);
        TestPayload? received = null;

        // The Action overload is generated as an extension, so a plain assignment works as a handler.
        await using var subscription = await client.OnChangedAsync(p => received = p);

        await transport.RaiseAsync("test.changed", """{"name":"hello","count":3}""");
        Assert.Equal(new TestPayload("hello", 3), received);
    }

    [Fact]
    public async Task Reads_a_response_as_a_stream_or_bytes()
    {
        var transport = new RecordingTransport()
            .Returns(HttpStatusCode.OK, "stream-body", "application/octet-stream")
            .Returns(HttpStatusCode.OK, "byte-body", "application/octet-stream");
        var client = new TestBridgeClient(transport);

        await using (var stream = await client.DownloadAsync("a.txt"))
            Assert.Equal("stream-body", await new StreamReader(stream).ReadToEndAsync());

        Assert.Equal("byte-body", Encoding.UTF8.GetString(await client.DownloadBytesAsync("b.txt")));
        Assert.Equal("test/files/a.txt/content", transport.Requests[0].Uri);
    }

    [Fact]
    public async Task Sends_a_raw_body_with_its_content_type_and_a_renamed_query_parameter()
    {
        var transport = new RecordingTransport().Returns(HttpStatusCode.NoContent, null);
        await new TestBridgeClient(transport).UploadAsync("notes/today.txt", "hello", overwrite: false);

        var request = transport.Requests[0];
        Assert.Equal(("PUT", "test/upload?path=notes%2Ftoday.txt&overwrite=false", "text/plain", "hello"),
            (request.Method, request.Uri, request.ContentType, request.Body));
    }

    [Fact]
    public async Task Registers_the_client_against_its_interface()
    {
        var services = new ServiceCollection()
            .AddSingleton<IBridgeTransport>(new RecordingTransport())
            .AddTestBridgeClient()
            .AddTestBridgeClient();

        Assert.Single(services, x => x.ServiceType == typeof(ITestBridge));

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<TestBridgeClient>(provider.GetRequiredService<ITestBridge>());
    }

    [Theory]
    [InlineData("""[BridgeGet("items")] Task<TestPayload> Get(TestPayload filter);""", "complex type on a GET")]
    [InlineData("""[BridgeGet("items/{id}")] Task<TestPayload> Get();""", "no parameter has that name")]
    [InlineData("""[BridgePost("items")] Task<TestPayload> Post(TestPayload a, TestPayload b);""", "only one parameter can be the body")]
    [InlineData("""[BridgeGet("items")] TestPayload Get();""", "must return Task")]
    [InlineData("""Task<TestPayload> Get();""", "has no [BridgeGet]")]
    [InlineData("""[BridgeEvent("x")] Task<IAsyncDisposable> On(Action<TestPayload> handler);""", "must be shaped")]
    public void Refuses_a_declaration_it_cannot_honour(string member, string message)
    {
        var diagnostics = Generate($$"""
            using System;
            using System.Threading.Tasks;
            using System.Text.Json.Serialization;
            using Shiny.AppDeviceBridge.Client;

            public sealed record TestPayload(string Name);

            [JsonSerializable(typeof(TestPayload))]
            public partial class BadJson : JsonSerializerContext;

            [BridgeClient("bad", typeof(BadJson))]
            public interface IBadBridge
            {
                {{member}}
            }
            """);

        var error = Assert.Single(diagnostics, x => x.Id == "ADB001");
        Assert.Contains(message, error.GetMessage());
    }

    static IReadOnlyList<Diagnostic> Generate(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && x.Location.Length > 0)
            .Select(x => MetadataReference.CreateFromFile(x.Location))
            .Append(MetadataReference.CreateFromFile(typeof(BridgeClientAttribute).Assembly.Location));

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "Bad",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        CSharpGeneratorDriver.Create([new BridgeClientGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        return diagnostics;
    }
}

public sealed record TestPayload(string Name, int Count);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestPayload))]
public partial class TestBridgeJsonContext : JsonSerializerContext;

[BridgeClient("test", typeof(TestBridgeJsonContext))]
public interface ITestBridge
{
    [BridgeEvent("test.changed")]
    Task<IAsyncDisposable> OnChangedAsync(Func<TestPayload, Task> handler);

    [BridgeGet("files/{path}/content")]
    Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken = default);

    [BridgeGet("files/{path}/content")]
    Task<byte[]> DownloadBytesAsync(string path, CancellationToken cancellationToken = default);

    [BridgePut("upload")]
    Task UploadAsync([BridgeQuery("path")] string target, [BridgeBody("text/plain")] string text, bool overwrite = true, CancellationToken cancellationToken = default);
}

/// <summary>Records each request and answers from a queue; raises events on demand.</summary>
sealed class RecordingTransport : IBridgeTransport
{
    readonly Queue<(HttpStatusCode Status, string? Body, string ContentType)> responses = new();
    readonly Dictionary<string, List<Func<string, Task>>> subscribers = [];

    public List<Recorded> Requests { get; } = [];

    public RecordingTransport Returns(HttpStatusCode status, string? body, string contentType = "application/json")
    {
        this.responses.Enqueue((status, body, contentType));
        return this;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Requests.Add(new(
            request.Method.Method,
            request.RequestUri!.OriginalString,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)
        ));

        var (status, body, contentType) = this.responses.Dequeue();
        var response = new HttpResponseMessage(status);

        if (body is not null)
            response.Content = new StringContent(body, Encoding.UTF8, contentType);

        return response;
    }

    public Task<IAsyncDisposable> SubscribeAsync(string eventName, Func<string, Task> handler)
    {
        if (!this.subscribers.TryGetValue(eventName, out var list))
            this.subscribers[eventName] = list = [];

        list.Add(handler);
        return Task.FromResult<IAsyncDisposable>(new Unsubscribe(() => list.Remove(handler)));
    }

    public async Task RaiseAsync(string eventName, string json)
    {
        foreach (var handler in this.subscribers.GetValueOrDefault(eventName) ?? [])
            await handler(json);
    }

    public sealed record Recorded(string Method, string Uri, string? ContentType, string? Body);

    sealed class Unsubscribe(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
