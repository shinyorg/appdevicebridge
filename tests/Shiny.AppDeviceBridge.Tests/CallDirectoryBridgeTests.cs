using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.CallDirectory;
using Shiny.AppDeviceBridge.CallDirectory.Client;
using Shiny.AppDeviceBridge.Client;
using Native = Shiny.CallDirectory;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The call directory bridge over a fake <see cref="Native.ICallDirectoryManager"/>: the routes, entry validation, the
/// mapping both ways and number normalization. The extension and call screening themselves are
/// Shiny.Mobile.CallDirectory's and need a phone.
/// </summary>
public class CallDirectoryBridgeTests
{
    [Fact]
    public async Task Answers_501_where_there_is_no_call_directory()
    {
        await using var fixture = await CallDirectoryFixture.StartAsync(new Native.NoOpCallDirectoryManager());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync());
        Assert.True(refused.IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "calldirectory").IsSupported);
    }

    [Fact]
    public async Task Reports_access_and_how_many_numbers_it_holds()
    {
        var manager = new FakeCallDirectory { Access = Shiny.AccessState.Disabled };
        manager.Entries[18005550100] = new Native.CallDirectoryEntry(18005550100, "Acme");
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        Assert.Equal(new CallDirectoryStatus(Client.AccessState.Disabled, 1), await fixture.Client.GetStatusAsync());
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "calldirectory").IsSupported);
    }

    [Fact]
    public async Task Requests_access_on_the_main_thread()
    {
        var manager = new FakeCallDirectory { Access = Shiny.AccessState.Available };
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        Assert.Equal(Client.AccessState.Available, (await fixture.Client.RequestAccessAsync()).Access);
        Assert.Equal(1, fixture.MainThread.Calls);
    }

    [Fact]
    public async Task Sets_reads_removes_and_clears_entries()
    {
        var manager = new FakeCallDirectory();
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        await fixture.Client.SetAsync(new CallDirectoryEntriesRequest(
        [
            new CallDirectoryEntry(18005550100, "Acme Support"),
            new CallDirectoryEntry(18005550199, Type: CallDirectoryEntryType.Block),
            new CallDirectoryEntry(442079460000, "Office", CallDirectoryEntryType.Allow)
        ]));

        Assert.Equal(new Native.CallDirectoryEntry(18005550199, null, Native.CallDirectoryEntryType.Block), manager.Entries[18005550199]);
        Assert.Equal(new CallDirectoryEntry(442079460000, "Office", CallDirectoryEntryType.Allow), await fixture.Client.GetEntryAsync(442079460000));

        await fixture.Client.RemoveAsync(new CallDirectoryRemoveRequest([18005550199, 15555555555]));
        Assert.Equal([18005550100, 442079460000], manager.Entries.Keys.Order());

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetEntryAsync(18005550199));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await fixture.Client.ClearAsync();
        Assert.Empty(manager.Entries);
    }

    [Fact]
    public async Task Replaces_the_whole_list()
    {
        var manager = new FakeCallDirectory();
        manager.Entries[18005550100] = new Native.CallDirectoryEntry(18005550100, "Old");
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        await fixture.Client.ReplaceAllAsync(new CallDirectoryEntriesRequest([new CallDirectoryEntry(16135550123, "New")]));

        Assert.Equal(16135550123, Assert.Single(manager.Entries.Keys));
        Assert.Equal(1, manager.Replaces);
    }

    [Theory]
    [InlineData("""{ "entries": [ { "phoneNumber": 0 } ] }""", "entries[0]: phoneNumber")]
    [InlineData("""{ "entries": [ { "phoneNumber": 1000000000000000 } ] }""", "entries[0]: phoneNumber")]
    [InlineData("""{ "entries": [ { "phoneNumber": 18005550100, "type": 9 } ] }""", "entries[0]: type")]
    [InlineData("""{ "entries": [ null ] }""", "entries[0]: is missing")]
    [InlineData("""{}""", "Expected")]
    public async Task Refuses_entries_it_cannot_store(string body, string message)
    {
        var manager = new FakeCallDirectory();
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        var response = await fixture.WebView.PutAsync("/_bridge/calldirectory/entries", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Empty(manager.Entries);
    }

    [Fact]
    public async Task Refuses_a_label_that_is_too_long()
    {
        await using var fixture = await CallDirectoryFixture.StartAsync(new FakeCallDirectory());

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.SetAsync(new CallDirectoryEntriesRequest([new CallDirectoryEntry(18005550100, new string('x', 257))])));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Reload_answers_how_it_went()
    {
        var manager = new FakeCallDirectory { ReloadResult = Native.CallDirectoryReloadResult.ExtensionDisabled };
        await using var fixture = await CallDirectoryFixture.StartAsync(manager);

        Assert.Equal(CallDirectoryReloadResult.ExtensionDisabled, (await fixture.Client.ReloadAsync()).Result);
    }

    [Theory]
    [InlineData("+1 (800) 555-0100", null, 18005550100)]
    [InlineData("800-555-0100", null, 18005550100)]
    [InlineData("020 7946 0000", 44, 442079460000)]
    [InlineData("0044 20 7946 0000", null, 442079460000)]
    public async Task Normalizes_numbers_as_people_write_them(string number, int? countryCode, long expected)
    {
        await using var fixture = await CallDirectoryFixture.StartAsync(new FakeCallDirectory());

        Assert.Equal(expected, (await fixture.Client.NormalizeAsync(new NormalizeNumberRequest(number, countryCode))).PhoneNumber);
    }

    [Fact]
    public async Task Normalizing_uses_the_apps_default_country_code_and_refuses_what_is_not_a_number()
    {
        await using var fixture = await CallDirectoryFixture.StartAsync(new FakeCallDirectory(), new Native.CallDirectoryOptions { DefaultCountryCode = 44 });

        Assert.Equal(442079460000, (await fixture.Client.NormalizeAsync(new NormalizeNumberRequest("020 7946 0000"))).PhoneNumber);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.NormalizeAsync(new NormalizeNumberRequest("call me")));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    sealed class CallDirectoryFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public CallDirectoryBridgeClient Client { get; private set; } = null!;
        public CountingMainThread MainThread { get; } = new();

        public static async Task<CallDirectoryFixture> StartAsync(Native.ICallDirectoryManager manager, Native.CallDirectoryOptions? options = null)
        {
            var fixture = new CallDirectoryFixture();
            var services = new ServiceCollection();
            services.AddSingleton(manager);
            if (options is not null)
                services.AddSingleton(options);
            services.AddSingleton<IWebAppMainThread>(fixture.MainThread);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new CallDirectoryBridge(provider)],
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new CallDirectoryBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class CountingMainThread : IWebAppMainThread
    {
        public int Calls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            this.Calls++;
            return action();
        }
    }

    sealed class FakeCallDirectory : Native.ICallDirectoryManager
    {
        public Shiny.AccessState Access { get; init; } = Shiny.AccessState.Available;
        public Native.CallDirectoryReloadResult ReloadResult { get; init; } = Native.CallDirectoryReloadResult.Success;
        public Dictionary<long, Native.CallDirectoryEntry> Entries { get; } = [];
        public int Replaces { get; private set; }

        public bool IsSupported => true;

        public Task<Shiny.AccessState> GetCurrentAccess() => Task.FromResult(this.Access);

        public Task<Shiny.AccessState> RequestAccess(CancellationToken cancelToken = default) => Task.FromResult(this.Access);

        public Task Set(IEnumerable<Native.CallDirectoryEntry> entries, CancellationToken cancelToken = default)
        {
            foreach (var entry in entries)
                this.Entries[entry.PhoneNumber] = entry;
            return Task.CompletedTask;
        }

        public Task Remove(IEnumerable<long> phoneNumbers, CancellationToken cancelToken = default)
        {
            foreach (var number in phoneNumbers)
                this.Entries.Remove(number);
            return Task.CompletedTask;
        }

        public Task ReplaceAll(IEnumerable<Native.CallDirectoryEntry> entries, CancellationToken cancelToken = default)
        {
            this.Replaces++;
            this.Entries.Clear();
            return this.Set(entries, cancelToken);
        }

        public Task Clear(CancellationToken cancelToken = default)
        {
            this.Entries.Clear();
            return Task.CompletedTask;
        }

        public Task<Native.CallDirectoryEntry?> Get(long phoneNumber, CancellationToken cancelToken = default)
            => Task.FromResult(this.Entries.GetValueOrDefault(phoneNumber));

        public Task<int> Count(CancellationToken cancelToken = default) => Task.FromResult(this.Entries.Count);

        public Task<Native.CallDirectoryReloadResult> Reload(CancellationToken cancelToken = default) => Task.FromResult(this.ReloadResult);
    }
}
