using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Nfc;
using Shiny.AppDeviceBridge.Nfc.Client;
using Native = Shiny.Nfc;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The NFC bridge over a fake <see cref="Native.INfcManager"/> whose scan the test feeds tag by tag: the one-tap routes,
/// record mapping and validation, the scan session's events, holding a tag for writes, and the errors. Reader mode,
/// CoreNFC and PC/SC themselves are Shiny.Nfc's.
/// </summary>
public class NfcBridgeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);


    [Fact]
    public async Task Answers_501_without_nfc()
    {
        await using var fixture = await NfcFixture.StartAsync(new FakeNfc { Capabilities = Native.NfcCapabilities.None });

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ReadAsync(new NfcReadRequest()))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "nfc").IsSupported);
    }


    [Fact]
    public async Task Answers_501_without_nfc_registered()
    {
        await using var fixture = await NfcFixture.StartAsync(null);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync())).IsNotSupported);
    }


    [Fact]
    public async Task Reports_capabilities_and_access()
    {
        var nfc = new FakeNfc { Capabilities = Native.NfcCapabilities.ReadNdef | Native.NfcCapabilities.WriteNdef, Access = global::Shiny.AccessState.Disabled };
        await using var fixture = await NfcFixture.StartAsync(nfc);

        var status = await fixture.Client.GetStatusAsync();
        Assert.Equal([NfcCapability.ReadNdef, NfcCapability.WriteNdef], status.Capabilities);
        Assert.False(status.Scanning);
        Assert.Equal(Shiny.AppDeviceBridge.Client.AccessState.Disabled, (await fixture.Client.RequestAccessAsync()).Access);
    }


    [Fact]
    public async Task Reads_one_tag_with_its_records_decoded()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);

        var tag = new FakeTag(new Native.NdefMessage(
            Native.NdefRecord.CreateUri("https://shinylib.net"),
            Native.NdefRecord.CreateText("bonjour", "fr"),
            Native.NdefRecord.CreateMime("application/json", "{}"u8.ToArray())
        ));
        var read = fixture.Client.ReadAsync(new NfcReadRequest("Hold near the tag"));
        await nfc.Tap(tag);

        var result = await read.WaitAsync(Wait);
        Assert.NotNull(result);
        Assert.Equal("04A1B2C3", result.Identifier);
        Assert.Equal("Hold near the tag", nfc.LastAlert);

        var records = result.Records!;
        Assert.Equal((NfcTypeNameFormat.WellKnown, "U", "https://shinylib.net"), (records[0].Format, records[0].Type, records[0].Uri));
        Assert.Equal(("bonjour", "fr"), (records[1].Text, records[1].Language));
        Assert.Equal((NfcTypeNameFormat.Media, "application/json"), (records[2].Format, records[2].MimeType));
        Assert.Equal("{}"u8.ToArray(), records[2].Payload);
    }


    [Fact]
    public async Task A_read_that_ends_before_a_tap_is_no_content()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);

        var read = fixture.Client.ReadAsync(new NfcReadRequest());
        await nfc.Dismiss();

        Assert.Null(await read.WaitAsync(Wait));
    }


    [Fact]
    public async Task Writes_one_tag_from_each_kind_of_record()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        var tag = new FakeTag(null);

        var write = fixture.Client.WriteAsync(new NfcWriteRequest([
            new(Uri: "https://shinylib.net"),
            new(Text: "hello"),
            new(MimeType: "text/plain", Data: "hi"u8.ToArray()),
            new(AndroidPackage: "org.shiny.app"),
            new(Format: NfcTypeNameFormat.External, Type: "example.com:thing", Data: [1, 2], Id: "x")
        ]));
        await nfc.Tap(tag);

        Assert.True((await write.WaitAsync(Wait)).Written);
        var written = tag.Written!.Records;
        Assert.Equal(Native.NdefRecord.CreateUri("https://shinylib.net"), written[0]);
        Assert.Equal(Native.NdefRecord.CreateText("hello", "en"), written[1]);
        Assert.Equal("text/plain", written[2].MimeType);
        Assert.Equal(Native.NdefRecord.CreateAndroidApplication("org.shiny.app"), written[3]);
        Assert.Equal((Native.NdefTypeNameFormat.External, "example.com:thing"), (written[4].TypeNameFormat, written[4].TypeString));
        Assert.Equal("x"u8.ToArray(), written[4].Id.ToArray());
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refuses_records_that_are_not_exactly_one_kind(bool none)
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);

        NfcWriteRecord record = none ? new() : new(Uri: "https://a", Text: "b");
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.WriteAsync(new NfcWriteRequest([record])));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, nfc.Scans);
    }


    [Fact]
    public async Task Refuses_an_empty_message()
    {
        await using var fixture = await NfcFixture.StartAsync(new FakeNfc());
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.WriteAsync(new NfcWriteRequest([])));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }


    [Theory]
    [InlineData("lost", HttpStatusCode.Gone, "tag_lost")]
    [InlineData("readonly", HttpStatusCode.Conflict, "tag_read_only")]
    [InlineData("toolarge", HttpStatusCode.RequestEntityTooLarge, "message_too_large")]
    [InlineData("unsupported", HttpStatusCode.Conflict, "tag_not_supported")]
    [InlineData("other", HttpStatusCode.BadGateway, "nfc_failed")]
    public async Task Tag_failures_become_errors_the_page_can_switch_on(string failure, HttpStatusCode status, string code)
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        var tag = new FakeTag(null)
        {
            WriteFailure = failure switch
            {
                "lost" => new Native.NfcTagLostException(),
                "readonly" => new Native.NfcTagReadOnlyException(),
                "toolarge" => new Native.NdefMessageTooLargeException(200, 137),
                "unsupported" => new Native.NfcNotSupportedException("no", Native.NfcCapabilities.WriteNdef),
                _ => new Native.NfcException("radio fault")
            }
        };

        var write = fixture.Client.WriteAsync(new NfcWriteRequest([new(Uri: "https://shinylib.net")]));
        await nfc.Tap(tag);

        var failed = await Assert.ThrowsAsync<BridgeException>(() => write.WaitAsync(Wait));
        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, failed.Code);
    }


    [Fact]
    public async Task A_scan_needs_a_listener()
    {
        await using var fixture = await NfcFixture.StartAsync(new FakeNfc());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartScanAsync(new NfcScanRequest()));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_listening", refused.Code);
    }


    [Fact]
    public async Task A_scan_refuses_when_nfc_is_switched_off()
    {
        await using var fixture = await NfcFixture.StartAsync(new FakeNfc { Access = global::Shiny.AccessState.Disabled });
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartScanAsync(new NfcScanRequest()));
        Assert.Equal("nfc_unavailable", refused.Code);
    }


    [Fact]
    public async Task A_scan_fires_each_tag_and_ends_when_stopped()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        await fixture.Client.StartScanAsync(new NfcScanRequest("Tap tags"));
        Assert.True((await fixture.Client.GetStatusAsync()).Scanning);

        await nfc.Tap(new FakeTag(new Native.NdefMessage(Native.NdefRecord.CreateText("one"))));
        await nfc.Tap(new FakeTag(null, readFailure: new Native.NdefFormatException("garbage")));

        var first = await NextTag(stream, timeout.Token);
        Assert.Equal("one", first.Records![0].Text);
        var second = await NextTag(stream, timeout.Token);
        Assert.Null(second.Records);
        Assert.Equal("garbage", second.ReadError);

        // while scanning, a one-tap call is refused
        Assert.Equal("nfc_busy", (await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.ReadAsync(new NfcReadRequest()))).Code);

        await fixture.Client.StopScanAsync();
        Assert.Equal(NfcScanEndReason.Stopped, (await NextEnd(stream, timeout.Token)).Reason);
        Assert.False((await fixture.Client.GetStatusAsync()).Scanning);

        // and is allowed again afterwards
        var read = fixture.Client.ReadAsync(new NfcReadRequest());
        await nfc.Tap(new FakeTag(null));
        Assert.NotNull(await read.WaitAsync(Wait));
    }


    [Fact]
    public async Task The_platform_ending_the_scan_is_reported()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        await fixture.Client.StartScanAsync(new NfcScanRequest());
        await nfc.Fail(new Native.NfcException("The NFC session ended: system busy"));

        var ended = await NextEnd(stream, timeout.Token);
        Assert.Equal(NfcScanEndReason.Failed, ended.Reason);
        Assert.Contains("system busy", ended.Message);
    }


    [Fact]
    public async Task A_held_tag_can_be_written_and_locked_until_released()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        await fixture.Client.StartScanAsync(new NfcScanRequest(HoldSeconds: 30));
        var tag = new FakeTag(null);
        await nfc.Tap(tag);
        var fired = await NextTag(stream, timeout.Token);

        await fixture.Client.WriteTagAsync(fired.TagId, new NfcTagWriteRequest([new(Uri: "https://shinylib.net")]));
        await fixture.Client.LockTagAsync(fired.TagId);
        Assert.Equal(Native.NdefRecord.CreateUri("https://shinylib.net"), tag.Written!.Records[0]);
        Assert.True(tag.Locked);

        // the next tag waits for the release
        var next = new FakeTag(null);
        var tapped = nfc.Tap(next);
        await Task.Delay(200, timeout.Token);
        Assert.False(tapped.IsCompleted);

        await fixture.Client.ReleaseTagAsync(fired.TagId);
        await tapped.WaitAsync(Wait);
        Assert.NotEqual(fired.TagId, (await NextTag(stream, timeout.Token)).TagId);

        var gone = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.WriteTagAsync(fired.TagId, new NfcTagWriteRequest([new(Text: "late")])));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(Native.NdefRecord.CreateUri("https://shinylib.net"), tag.Written!.Records[0]);
    }


    [Fact]
    public async Task Without_a_hold_tags_are_released_straight_away()
    {
        var nfc = new FakeNfc();
        await using var fixture = await NfcFixture.StartAsync(nfc);
        using var timeout = new CancellationTokenSource(Wait);
        await using var stream = await fixture.ListenAsync(timeout.Token);

        await fixture.Client.StartScanAsync(new NfcScanRequest());
        await nfc.Tap(new FakeTag(null));
        var fired = await NextTag(stream, timeout.Token);

        var gone = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.WriteTagAsync(fired.TagId, new NfcTagWriteRequest([new(Text: "x")])));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }


    [Fact]
    public async Task Refuses_a_hold_over_a_minute()
    {
        await using var fixture = await NfcFixture.StartAsync(new FakeNfc());
        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartScanAsync(new NfcScanRequest(HoldSeconds: 61)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }


    static async Task<NfcTag> NextTag(TestEventStream stream, CancellationToken ct)
        => JsonSerializer.Deserialize(await stream.NextAsync(NfcBridge.TagEvent, ct), NfcJsonContext.Default.NfcTag)!;


    static async Task<NfcScanEnded> NextEnd(TestEventStream stream, CancellationToken ct)
        => JsonSerializer.Deserialize(await stream.NextAsync(NfcBridge.ScanEvent, ct), NfcJsonContext.Default.NfcScanEnded)!;


    static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();

            await Task.Delay(20);
        }
    }


    sealed class NfcFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public NfcBridgeClient Client { get; private set; } = null!;
        public NfcBridge Bridge { get; private set; } = null!;
        public WebAppEventHub Events { get; } = new();

        /// <summary>Opens the page's event stream on the NFC topics and waits until the bridge sees the listener.</summary>
        public async Task<TestEventStream> ListenAsync(CancellationToken ct)
        {
            var stream = await TestEventStream.OpenAsync(this.WebView, $"{NfcBridge.TagEvent},{NfcBridge.ScanEvent}", ct);
            await WaitUntil(() => this.Bridge.TagListeners > 0);
            return stream;
        }

        public static async Task<NfcFixture> StartAsync(Native.INfcManager? nfc)
        {
            var fixture = new NfcFixture();
            var services = new ServiceCollection();
            if (nfc is not null)
                services.AddSingleton(nfc);
            var provider = services.BuildServiceProvider();

            fixture.Bridge = new NfcBridge(provider);
            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(_ => [fixture.Bridge], fixture.Events, client => fixture.WebView = client);
            fixture.Client = new NfcBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }


    /// <summary>A scan the test drives: each <see cref="Tap"/> goes to the scan running now, and completes once the bridge's loop has taken the tag.</summary>
    sealed class FakeNfc : Native.INfcManager
    {
        volatile Channel<object>? active;

        public Native.NfcCapabilities Capabilities { get; init; } = Native.NfcCapabilities.ReadNdef | Native.NfcCapabilities.WriteNdef | Native.NfcCapabilities.MakeReadOnly | Native.NfcCapabilities.TagIdentifier;
        public global::Shiny.AccessState Access { get; init; } = global::Shiny.AccessState.Available;
        public int Scans { get; private set; }
        public string? LastAlert { get; private set; }

        public Task<global::Shiny.AccessState> RequestAccess(CancellationToken ct = default) => Task.FromResult(this.Access);

        public async IAsyncEnumerable<Native.INfcTag> Scan(Native.NfcScanOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            this.Scans++;
            this.LastAlert = options?.AlertMessage;
            var feed = Channel.CreateUnbounded<object>();
            this.active = feed;
            try
            {
                await foreach (var item in feed.Reader.ReadAllAsync(ct))
                {
                    if (item is Exception ex)
                        throw ex;

                    var (tag, taken) = ((Native.INfcTag, TaskCompletionSource))item;
                    taken.TrySetResult();
                    yield return tag;
                }
            }
            finally
            {
                if (ReferenceEquals(this.active, feed))
                    this.active = null;
            }
        }

        async Task<Channel<object>> Running()
        {
            await WaitUntil(() => this.active is not null);
            return this.active!;
        }

        public async Task Tap(Native.INfcTag tag)
        {
            var feed = await this.Running();
            var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await feed.Writer.WriteAsync((tag, taken));
            await taken.Task.WaitAsync(Wait);
        }

        public async Task Dismiss() => (await this.Running()).Writer.TryComplete();

        public async Task Fail(Exception ex) => await (await this.Running()).Writer.WriteAsync(ex);
    }


    sealed class FakeTag(Native.NdefMessage? message, Exception? readFailure = null) : Native.INfcTag
    {
        public byte[]? Identifier => [0x04, 0xA1, 0xB2, 0xC3];
        public bool IsWritable => !this.Locked;
        public int? MaxSize => 137;
        public Exception? WriteFailure { get; init; }
        public Native.NdefMessage? Written { get; private set; }
        public bool Locked { get; private set; }

        public Task<Native.NdefMessage?> ReadNdef(CancellationToken ct = default)
            => readFailure is null ? Task.FromResult(message) : Task.FromException<Native.NdefMessage?>(readFailure);

        public Task WriteNdef(Native.NdefMessage msg, CancellationToken ct = default)
        {
            if (this.WriteFailure is not null)
                return Task.FromException(this.WriteFailure);

            this.Written = msg;
            return Task.CompletedTask;
        }

        public Task MakeReadOnly(CancellationToken ct = default)
        {
            this.Locked = true;
            return Task.CompletedTask;
        }
    }
}
