using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Hubs.Client;

namespace Sample.Blazor;

/// <summary>
/// The Hubs page's chat: a Universal Hub whose logic is this page's, hosted by the app over BLE, Wi-Fi and Multipeer.
/// Another device running the sample joins it from its own Hubs page; a native app joins it with this contract:
/// <code>
/// [BleHubClient]
/// public interface IChatHub
/// {
///     Task Send(string text);
///     Task&lt;string[]&gt; Who();
///     IAsyncEnumerable&lt;int&gt; Countdown(int from, CancellationToken cancellationToken);
///     event Action&lt;string, string&gt; Message;      // from, text
/// }
/// </code>
/// </summary>
public sealed class ChatHub : WebAppHub
{
    public const string HubName = "chat";
    public const string Characteristic = "6e400050-b5a3-f393-e0a9-e50e24dcca9e";

    /// <summary>Listed in the Apple heads' NSBonjourServices, and the only type the app lets the page's hubs use.</summary>
    public const string ServiceType = "_adbsample._tcp";

    // one instance serves every call; calls can overlap
    readonly Dictionary<string, string> people = new();

    /// <summary>What the host's own page shows: everything said in the chat.</summary>
    public event Action<string, string>? Said;

    protected override void Map(WebAppHubMethods methods) => methods
        .Invoke("Send", ChatJson.Default.String, this.Send)
        .Invoke("Who", ChatJson.Default.StringArray, this.Who)
        .Stream("Countdown", ChatJson.Default.Int32, ChatJson.Default.Int32, Countdown);

    public override async Task OnConnectedAsync(HubConnectionInfo connection)
    {
        var name = String.IsNullOrWhiteSpace(connection.Name) ? "someone" : connection.Name;
        lock (this.people)
            this.people[connection.Id] = name;

        await this.SayAsync("hub", $"{name} joined over {connection.Transport}");
    }

    public override async Task OnDisconnectedAsync(HubConnectionInfo connection, HubDisconnectReason reason, string? message)
    {
        string? name;
        lock (this.people)
        {
            this.people.TryGetValue(connection.Id, out name);
            this.people.Remove(connection.Id);
        }

        await this.SayAsync("hub", $"{name ?? "someone"} left ({reason})");
    }

    /// <summary>The host's own messages, from its page.</summary>
    public Task SayAsHostAsync(string text) => this.SayAsync("host", text);

    Task Send(string text)
    {
        if (String.IsNullOrWhiteSpace(text))
            throw new HubException("EmptyMessage", "Say something.");

        string? name;
        lock (this.people)
            this.people.TryGetValue(this.Connection.Id, out name);

        return this.SayAsync(name ?? this.Connection.Name ?? "someone", text);
    }

    Task<string[]> Who()
    {
        lock (this.people)
            return Task.FromResult(this.people.Values.Order().ToArray());
    }

    static async IAsyncEnumerable<int> Countdown(int from, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = Math.Clamp(from, 0, 30); i >= 0; i--)
        {
            yield return i;
            if (i > 0)
                await Task.Delay(1000, cancellationToken);
        }
    }

    async Task SayAsync(string from, string text)
    {
        this.Said?.Invoke(from, text);
        await this.PushAsync("Message", [
            JsonSerializer.SerializeToElement(from, ChatJson.Default.String),
            JsonSerializer.SerializeToElement(text, ChatJson.Default.String)
        ]);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
public partial class ChatJson : JsonSerializerContext;
