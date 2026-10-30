using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maui;
using Shiny.AppDeviceBridge.ShareTarget;
using Shiny.AppDeviceBridge.ShareTarget.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The share target's queue and routes, fed the way the platform hooks feed it. The hooks themselves — Android send
/// intents, iOS "Open in", Windows share activation — need a device and are checked in the sample app.
/// </summary>
public class ShareTargetBridgeTests
{
    [Fact]
    public async Task A_link_shared_as_text_arrives_as_the_url()
    {
        using var scratch = new Scratch();

        var share = await scratch.Inbox().ReceiveAsync(new IncomingShare("  https://example.com/a?b=1  ", "A page"));

        Assert.NotNull(share);
        Assert.Null(share.Text);
        Assert.Equal("https://example.com/a?b=1", share.Url);
        Assert.Equal("A page", share.Subject);
        Assert.Empty(share.Files);
    }

    [Fact]
    public async Task Only_web_links_are_urls_and_anything_else_is_kept_as_text()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox();

        var script = await inbox.ReceiveAsync(new IncomingShare(Url: "javascript:alert(1)"));
        Assert.Null(script!.Url);
        Assert.Equal("javascript:alert(1)", script.Text);

        var file = await inbox.ReceiveAsync(new IncomingShare("Notes", Url: "file:///etc/passwd"));
        Assert.Null(file!.Url);
        Assert.Equal("Notes", file.Text);

        var sentence = await inbox.ReceiveAsync(new IncomingShare("look at https://example.com"));
        Assert.Null(sentence!.Url);
        Assert.Equal("look at https://example.com", sentence.Text);
    }

    [Fact]
    public async Task Nothing_usable_is_not_queued()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox();

        Assert.Null(await inbox.ReceiveAsync(new IncomingShare("   ", "Subject only")));
        Assert.Null(await inbox.ReceiveAsync(new IncomingShare(Files: [Failing("broken.png")])));
        Assert.Empty(await inbox.GetPendingAsync());
    }

    [Fact]
    public async Task Long_text_is_cut_to_the_limit()
    {
        using var scratch = new Scratch();

        var share = await scratch.Inbox(o => o.MaxTextLength = 10).ReceiveAsync(new IncomingShare(new String('x', 50)));

        Assert.Equal(new String('x', 10), share!.Text);
    }

    [Fact]
    public async Task Files_are_copied_into_the_root_under_the_share()
    {
        using var scratch = new Scratch();

        var share = await scratch.Inbox().ReceiveAsync(new IncomingShare(
            "Two photos",
            Files: [Text("a.txt", "first", "text/plain"), Text("a.txt", "second"), Text(null, "third")]
        ));

        Assert.NotNull(share);
        Assert.Equal(["a.txt", "a-2.txt", "file3"], share.Files.Select(x => WebAppFilePath.NameOf(x.File.Path)));
        Assert.All(share.Files, x =>
        {
            Assert.Equal("data", x.File.Root);
            Assert.StartsWith($"shares/{share.Id}/", x.File.Path);
        });

        Assert.Equal("text/plain", share.Files[0].ContentType);
        Assert.Equal("a.txt", share.Files[1].FileName);    // the sender's name, not the one made unique
        Assert.Equal(5, share.Files[0].Size);
        Assert.Equal("second", File.ReadAllText(scratch.PathOf(share.Files[1].File)));
        Assert.Equal("third", File.ReadAllText(scratch.PathOf(share.Files[2].File)));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\me\report.pdf", "report.pdf")]
    [InlineData("a:b?c*.png", "a_b_c_.png")]
    [InlineData(".hidden", "hidden")]
    [InlineData("..", "file1")]
    [InlineData("   ", "file1")]
    [InlineData(null, "file1")]
    [InlineData("line\nbreak.txt", "line_break.txt")]
    public void File_names_become_one_safe_segment(string? name, string expected)
        => Assert.Equal(expected, ShareInbox.SafeName(name, 1));

    [Fact]
    public void A_long_file_name_keeps_its_extension()
    {
        var name = ShareInbox.SafeName(new String('n', 300) + ".jpeg", 1);

        Assert.Equal(120, name.Length);
        Assert.EndsWith(".jpeg", name);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_left_out_and_the_rest_kept()
    {
        using var scratch = new Scratch();

        var share = await scratch.Inbox().ReceiveAsync(new IncomingShare(Files: [Failing("gone.png"), Text("kept.txt", "ok")]));

        Assert.Equal(["kept.txt"], share!.Files.Select(x => x.FileName));
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_left_out()
    {
        using var scratch = new Scratch(o => o.MaxFileWriteBytes = 4);

        var share = await scratch.Inbox().ReceiveAsync(new IncomingShare("too big", Files: [Text("big.txt", "12345"), Text("small.txt", "123")]));

        Assert.Equal(["small.txt"], share!.Files.Select(x => x.FileName));
    }

    [Fact]
    public async Task Takes_at_most_the_configured_number_of_files()
    {
        using var scratch = new Scratch();

        var share = await scratch.Inbox(o => o.MaxFiles = 2).ReceiveAsync(new IncomingShare(Files: [.. Enumerable.Range(1, 5).Select(i => Text($"{i}.txt", "x"))]));

        Assert.Equal(["1.txt", "2.txt"], share!.Files.Select(x => x.FileName));
    }

    [Fact]
    public async Task Past_the_limit_the_oldest_share_is_dropped_with_its_files()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox(o => o.MaxPending = 2);

        var first = await inbox.ReceiveAsync(new IncomingShare(Files: [Text("one.txt", "1")]));
        var second = await inbox.ReceiveAsync(new IncomingShare("two"));
        var third = await inbox.ReceiveAsync(new IncomingShare("three"));

        Assert.Equal([second!.Id, third!.Id], (await inbox.GetPendingAsync()).Select(x => x.Id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scratch.PathOf(first!.Files[0].File))));
    }

    [Fact]
    public async Task Waiting_shares_survive_a_restart()
    {
        using var scratch = new Scratch();
        var share = await scratch.Inbox().ReceiveAsync(new IncomingShare("kept", Files: [Text("a.txt", "a")]));

        var restarted = scratch.Inbox();

        var pending = Assert.Single(await restarted.GetPendingAsync());
        Assert.Equal(share!.Id, pending.Id);
        Assert.Equal("kept", pending.Text);
        Assert.Equal(share.Files[0].File, pending.Files[0].File);
    }

    [Fact]
    public async Task A_damaged_queue_starts_empty_rather_than_failing()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Directory);
        File.WriteAllText(Path.Combine(scratch.Directory, "sharetarget.json"), "{ not json");

        var inbox = scratch.Inbox();

        Assert.Empty(await inbox.GetPendingAsync());
        Assert.NotNull(await inbox.ReceiveAsync(new IncomingShare("after")));
    }

    [Fact]
    public async Task Removing_a_share_deletes_its_files_and_an_unknown_id_touches_nothing()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox();
        var share = await inbox.ReceiveAsync(new IncomingShare(Files: [Text("a.txt", "a")]));
        var folder = Path.GetDirectoryName(scratch.PathOf(share!.Files[0].File))!;

        // Ids are looked up in the queue, never used as a path.
        Assert.False(await inbox.RemoveAsync("../" + share.Id));
        Assert.True(Directory.Exists(folder));

        Assert.True(await inbox.RemoveAsync(share.Id));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(await inbox.GetPendingAsync());
        Assert.False(await inbox.RemoveAsync(share.Id));
    }

    [Fact]
    public async Task A_share_removed_after_the_page_moved_its_files_still_leaves_the_queue()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox();
        var share = await inbox.ReceiveAsync(new IncomingShare(Files: [Text("a.txt", "a")]));
        Directory.Delete(Path.GetDirectoryName(scratch.PathOf(share!.Files[0].File))!, recursive: true);

        Assert.True(await inbox.RemoveAsync(share.Id));
        Assert.Empty(await inbox.GetPendingAsync());
    }

    [Fact]
    public async Task Without_its_file_root_the_bridge_answers_501_and_drops_files_but_keeps_text()
    {
        using var scratch = new Scratch();
        var inbox = scratch.Inbox(o => o.Root = "missing");

        Assert.False(inbox.IsSupported);
        var share = await inbox.ReceiveAsync(new IncomingShare("text", Files: [Text("a.txt", "a")]));
        Assert.Equal("text", share!.Text);
        Assert.Empty(share.Files);

        await using var fixture = await ShareFixture.StartAsync(app => new ShareInbox(new ShareTargetOptions { Root = "missing" }, new WebAppFileRoots(app.BridgeOptions()), app.BridgeOptions()));

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetPendingAsync());
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "sharetarget").IsSupported);
    }

    [Fact]
    public async Task The_page_reads_a_share_and_its_files_then_removes_it()
    {
        await using var fixture = await ShareFixture.StartAsync();
        var share = await fixture.Inbox.ReceiveAsync(new IncomingShare("hello", "Greeting", Files: [Text("note.txt", "from another app")]));

        var pending = Assert.Single(await fixture.Client.GetPendingAsync());
        Assert.Equal(share, pending, SharedContentComparer.Instance);
        Assert.Equal(share!.Id, (await fixture.Client.GetAsync(share.Id)).Id);

        // The files are ordinary files in a root: the files bridge reads them.
        var file = pending.Files[0].File;
        Assert.Equal("from another app", await fixture.WebView.GetStringAsync($"/_bridge/files/{file.Root}/content?path={Uri.EscapeDataString(file.Path)}"));

        await fixture.Client.RemoveAsync(share.Id);
        Assert.Empty(await fixture.Client.GetPendingAsync());

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetAsync(share.Id));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RemoveAsync(share.Id));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_share_arriving_while_the_page_is_open_is_an_event_and_stays_queued()
    {
        await using var fixture = await ShareFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "sharetarget.received", timeout.Token);

        var share = await fixture.Inbox.ReceiveAsync(new IncomingShare("https://example.com"));

        var received = JsonSerializer.Deserialize(await stream.NextAsync("sharetarget.received", timeout.Token), ShareTargetJsonContext.Default.SharedContent);
        Assert.Equal(share!.Id, received!.Id);
        Assert.Equal("https://example.com", received.Url);
        Assert.Single(await fixture.Client.GetPendingAsync());
    }

    [Fact]
    public async Task Shares_that_arrive_before_the_inbox_are_held_and_delivered_in_order()
    {
        using var scratch = new Scratch();

        var first = ShareTarget.ShareTarget.ReceiveAsync(new IncomingShare("first"));
        var second = ShareTarget.ShareTarget.ReceiveAsync(new IncomingShare("second"));
        Assert.False(first.IsCompleted);

        var inbox = scratch.Inbox();
        ShareTarget.ShareTarget.Attach(inbox);

        Assert.Equal("first", (await first.WaitAsync(TimeSpan.FromSeconds(10)))!.Text);
        Assert.Equal("second", (await second.WaitAsync(TimeSpan.FromSeconds(10)))!.Text);
        Assert.Equal(["first", "second"], (await inbox.GetPendingAsync()).Select(x => x.Text));

        // Once attached, a share goes straight in.
        Assert.Equal("third", (await ShareTarget.ShareTarget.ReceiveAsync(new IncomingShare("third")))!.Text);
    }

    [Fact]
    public void Registers_once_with_its_options()
    {
        var builder = MauiApp.CreateBuilder(useDefaults: false);
        builder.UseAppDeviceBridge(bridge => bridge
            .Configure(o => o.AppId = TestApp.AppId)
            .AddShareTargetBridge(o => o.Root = "cache")
            .AddShareTargetBridge(o => o.Root = "ignored")
        );

        Assert.Single(builder.Services, x => x.ServiceType == typeof(ShareInbox));
        Assert.Equal("cache", Assert.Single(builder.Services, x => x.ServiceType == typeof(ShareTargetOptions)).ImplementationInstance is ShareTargetOptions o ? o.Root : null);
        Assert.Single(builder.Services, x => x.ImplementationType == typeof(ShareTargetStartup));
    }

    static IncomingShareFile Text(string? name, string content, string? contentType = null)
        => new(name, contentType, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))));

    static IncomingShareFile Failing(string name)
        => new(name, null, _ => throw new IOException("The provider revoked access."));

    sealed class Scratch : IDisposable
    {
        readonly Action<AppDeviceBridgeOptions>? configure;

        public Scratch(Action<AppDeviceBridgeOptions>? configure = null) => this.configure = configure;

        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));

        public ShareInbox Inbox(Action<ShareTargetOptions>? configure = null)
        {
            var bridgeOptions = new AppDeviceBridgeOptions { AppId = TestApp.AppId, DataDirectory = this.Directory };
            this.configure?.Invoke(bridgeOptions);

            var options = new ShareTargetOptions();
            configure?.Invoke(options);
            return new ShareInbox(options, new WebAppFileRoots(bridgeOptions), bridgeOptions);
        }

        public string PathOf(BridgeFile file) => Path.Combine(this.Directory, "files", file.Path);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(this.Directory))
                System.IO.Directory.Delete(this.Directory, recursive: true);
        }
    }

    sealed class ShareFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public ShareInbox Inbox { get; private set; } = null!;
        public ShareTargetBridgeClient Client { get; private set; } = null!;

        public static async Task<ShareFixture> StartAsync(Func<TestApp, ShareInbox>? inbox = null)
        {
            var fixture = new ShareFixture();
            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    var options = app.BridgeOptions();
                    var roots = new WebAppFileRoots(options);
                    fixture.Inbox = inbox?.Invoke(app) ?? new ShareInbox(new ShareTargetOptions(), roots, options);

                    // The page reads shared files through the files bridge, as it would in an app.
                    return [new ShareTargetBridge(fixture.Inbox), new WebAppFilesBridge(roots, options)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new ShareTargetBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    /// <summary>Records compare their lists by reference; a share read back over HTTP has new ones.</summary>
    sealed class SharedContentComparer : IEqualityComparer<SharedContent?>
    {
        public static readonly SharedContentComparer Instance = new();

        public bool Equals(SharedContent? x, SharedContent? y)
            => x is not null && y is not null
               && x with { Files = [] } == y with { Files = [] }
               && x.Files.SequenceEqual(y.Files);

        public int GetHashCode(SharedContent? obj) => obj?.Id.GetHashCode() ?? 0;
    }
}
