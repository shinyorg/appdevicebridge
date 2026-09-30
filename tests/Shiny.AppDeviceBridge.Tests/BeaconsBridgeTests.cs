using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Beacons;
using Shiny.AppDeviceBridge.Beacons.Client;
using Shiny.AppDeviceBridge.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;
using Native = Shiny.Beacons;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The beacons bridge over fake Shiny.Beacons managers: each feature answering 501 unless registered, ranging and
/// Eddystone scans that need a listener and stop with the last one, monitoring, the delegate's events, and broadcasting.
/// CoreLocation and the radio themselves are Shiny.Beacons' and need a device.
/// </summary>
public class BeaconsBridgeTests
{
    static readonly Guid Uuid = Guid.Parse("f7826da6-4fa2-4e98-8024-bc5b71e0893e");

    [Fact]
    public async Task Answers_501_for_every_feature_without_beacon_services()
    {
        await using var fixture = await BeaconsFixture.StartAsync();

        var status = await fixture.Client.GetStatusAsync();
        Assert.Equal(ContractAccess.NotSupported, status.Ranging);
        Assert.Equal(ContractAccess.NotSupported, status.Monitoring);
        Assert.Equal(ContractAccess.NotSupported, status.Eddystone);
        Assert.False(status.BroadcastingSupported);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetRangedRegionsAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetMonitoredRegionsAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartEddystoneScanAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetBroadcastAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync(new BeaconAccessRequest(BeaconFeature.Ranging)))).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "beacons").IsSupported);
    }

    [Fact]
    public async Task Reports_each_registered_feature_and_leaves_the_rest_unsupported()
    {
        var ranging = new FakeRanging { Status = Shiny.AccessState.Available };
        var broadcaster = new FakeBroadcaster { IsBroadcasting = true };
        await using var fixture = await BeaconsFixture.StartAsync(ranging: ranging, broadcaster: broadcaster);

        var status = await fixture.Client.GetStatusAsync();

        Assert.Equal(new BeaconsStatus(ContractAccess.Available, ContractAccess.NotSupported, ContractAccess.NotSupported, true, true), status);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "beacons").IsSupported);

        ranging.Status = Shiny.AccessState.Denied;
        Assert.Equal(ContractAccess.Denied, (await fixture.Client.RequestAccessAsync(new BeaconAccessRequest(BeaconFeature.Ranging))).Access);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync(new BeaconAccessRequest(BeaconFeature.Monitoring)))).IsNotSupported);
    }

    [Fact]
    public async Task Ranging_needs_a_listener_and_permission()
    {
        var ranging = new FakeRanging { Status = Shiny.AccessState.Available };
        await using var fixture = await BeaconsFixture.StartAsync(ranging: ranging);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_listening", refused.Code);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.ranged", timeout.Token);

        ranging.Status = Shiny.AccessState.Denied;
        var denied = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid)));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("beacons_refused", denied.Code);
        Assert.Empty(ranging.Regions);
    }

    [Fact]
    public async Task A_feature_the_platform_reports_unsupported_answers_501()
    {
        // Shiny.Beacons on macOS: the managers are registered, but CoreLocation cannot range or monitor iBeacons there.
        var ranging = new FakeRanging { Status = Shiny.AccessState.NotSupported };
        var monitoring = new FakeMonitoring { Status = Shiny.AccessState.NotSupported };
        var scanner = new FakeEddystone { Status = Shiny.AccessState.NotSupported };
        await using var fixture = await BeaconsFixture.StartAsync(ranging, monitoring, scanner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.ranged,beacons.eddystone", timeout.Token);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid)))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitoringAsync(new BeaconRegion("lobby", Uuid)))).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartEddystoneScanAsync())).IsNotSupported);
        Assert.Empty(ranging.Regions);
        Assert.Empty(monitoring.Regions);
    }

    [Fact]
    public async Task Ranges_regions_into_events_tagged_with_the_region()
    {
        var ranging = new FakeRanging { Status = Shiny.AccessState.Available };
        await using var fixture = await BeaconsFixture.StartAsync(ranging: ranging);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.ranged", timeout.Token);

        await fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid, 1));
        await fixture.Client.StartRangingAsync(new BeaconRegion("dock", Uuid, 2, 7));

        Assert.Equal(["dock", "lobby"], (await fixture.Client.GetRangedRegionsAsync()).Select(x => x.Identifier).Order());
        var lobby = Assert.Single(ranging.Regions, x => x.Identifier == "lobby");
        Assert.Equal((ushort)1, lobby.Major);
        Assert.Null(lobby.Minor);

        var seen = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        ranging.Emit("dock", new Native.Beacon(Uuid, 2, 7, -71, Native.Proximity.Near, 1.8, -59, seen));

        var reading = JsonSerializer.Deserialize(await stream.NextAsync("beacons.ranged", timeout.Token), BeaconsJsonContext.Default.BeaconReading)!;
        Assert.Equal(new BeaconReading("dock", Uuid, 2, 7, -71, BeaconProximity.Near, 1.8, -59, seen), reading);

        await fixture.Client.StopRangingAsync("dock");
        Assert.True(ranging.IsStopped("dock"));
        Assert.False(ranging.IsStopped("lobby"));
        Assert.Equal("lobby", Assert.Single(await fixture.Client.GetRangedRegionsAsync()).Identifier);

        await fixture.Client.StopAllRangingAsync();
        Assert.True(ranging.IsStopped("lobby"));
        Assert.Empty(await fixture.Client.GetRangedRegionsAsync());
    }

    [Fact]
    public async Task Ranging_stops_when_the_last_listener_leaves()
    {
        var ranging = new FakeRanging { Status = Shiny.AccessState.Available };
        await using var fixture = await BeaconsFixture.StartAsync(ranging: ranging);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.ranged", timeout.Token);
        await fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid));
        await stream.DisposeAsync();

        while (!ranging.IsStopped("lobby"))
            await Task.Delay(20, timeout.Token);

        Assert.Empty(await fixture.Client.GetRangedRegionsAsync());
    }

    [Fact]
    public async Task A_failed_ranging_drops_only_its_own_region()
    {
        var ranging = new FakeRanging { Status = Shiny.AccessState.Available };
        await using var fixture = await BeaconsFixture.StartAsync(ranging: ranging);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.ranged", timeout.Token);

        await fixture.Client.StartRangingAsync(new BeaconRegion("lobby", Uuid));
        await fixture.Client.StartRangingAsync(new BeaconRegion("dock", Uuid));

        ranging.Fail("lobby", new InvalidOperationException("Bluetooth is off"));

        Assert.Equal("dock", Assert.Single(await fixture.Client.GetRangedRegionsAsync()).Identifier);
    }

    [Theory]
    [InlineData("""{ "identifier": "", "uuid": "f7826da6-4fa2-4e98-8024-bc5b71e0893e" }""", "identifier")]
    [InlineData("""{ "identifier": "lobby", "uuid": "00000000-0000-0000-0000-000000000000" }""", "uuid")]
    [InlineData("""{ "identifier": "lobby", "uuid": "f7826da6-4fa2-4e98-8024-bc5b71e0893e", "minor": 3 }""", "minor")]
    public async Task Refuses_an_invalid_region_with_400(string region, string problem)
    {
        var monitoring = new FakeMonitoring();
        await using var fixture = await BeaconsFixture.StartAsync(monitoring: monitoring);

        using var response = await fixture.WebView.PostAsync("/_bridge/beacons/regions", new StringContent(region, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(problem, await response.Content.ReadAsStringAsync());
        Assert.Empty(monitoring.Regions);
    }

    [Fact]
    public async Task Monitors_regions_and_reports_their_state()
    {
        var monitoring = new FakeMonitoring { State = Native.BeaconRegionState.Entered };
        await using var fixture = await BeaconsFixture.StartAsync(monitoring: monitoring);

        await fixture.Client.StartMonitoringAsync(new BeaconRegion("lobby", Uuid, 1, 2, NotifyOnExit: false));

        var region = Assert.Single(await fixture.Client.GetMonitoredRegionsAsync());
        Assert.Equal(new BeaconRegion("lobby", Uuid, 1, 2, true, false), region);
        Assert.Equal(new BeaconRegionStatus("lobby", BeaconRegionState.Entered), await fixture.Client.GetRegionStateAsync("lobby"));

        var missing = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetRegionStateAsync("dock"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await fixture.Client.StopMonitoringAsync("lobby");
        Assert.Empty(await fixture.Client.GetMonitoredRegionsAsync());
    }

    [Fact]
    public async Task Monitoring_without_permission_answers_409()
    {
        var monitoring = new FakeMonitoring { Refuse = true };
        await using var fixture = await BeaconsFixture.StartAsync(monitoring: monitoring);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartMonitoringAsync(new BeaconRegion("lobby", Uuid)));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("beacons_refused", refused.Code);
    }

    [Fact]
    public async Task The_delegate_publishes_transitions_to_the_page()
    {
        await using var fixture = await BeaconsFixture.StartAsync(monitoring: new FakeMonitoring());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.region", timeout.Token);

        WebAppBeaconMonitorDelegate.Publish(fixture.Events, Native.BeaconRegionState.Exited, new Native.BeaconRegion("lobby", Uuid));

        var status = JsonSerializer.Deserialize(await stream.NextAsync("beacons.region", timeout.Token), BeaconsJsonContext.Default.BeaconRegionStatus);
        Assert.Equal(new BeaconRegionStatus("lobby", BeaconRegionState.Exited), status);
    }

    [Fact]
    public async Task Flattens_eddystone_frames_and_stops_scanning_with_the_last_listener()
    {
        var scanner = new FakeEddystone { Status = Shiny.AccessState.Available };
        await using var fixture = await BeaconsFixture.StartAsync(eddystone: scanner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var notListening = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartEddystoneScanAsync());
        Assert.Equal("not_listening", notListening.Code);

        var stream = await TestEventStream.OpenAsync(fixture.WebView, "beacons.eddystone", timeout.Token);
        await fixture.Client.StartEddystoneScanAsync();
        Assert.True(scanner.Scanning);

        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        scanner.Frames.OnNext(new Native.EddystoneUidFrame("AA:BB", -60, at, Native.EddystoneUid.Parse("00112233445566778899", "AABBCCDDEEFF"), -18, 0.9, Native.Proximity.Near));
        scanner.Frames.OnNext(new Native.EddystoneUrlFrame("AA:BB", -61, at, "https://shinylib.net", -18, 4.2, Native.Proximity.Far));
        scanner.Frames.OnNext(new Native.EddystoneTlmFrame("AA:BB", -62, at, 0, 3.1, 21.5, 1200, TimeSpan.FromHours(2), null));

        var uid = await NextAsync();
        Assert.Equal(EddystoneFrameType.Uid, uid.FrameType);
        Assert.Equal("00112233445566778899", uid.Namespace);
        Assert.Equal("AABBCCDDEEFF", uid.Instance);
        Assert.Equal(BeaconProximity.Near, uid.Proximity);

        var url = await NextAsync();
        Assert.Equal(EddystoneFrameType.Url, url.FrameType);
        Assert.Equal("https://shinylib.net", url.Url);
        Assert.Null(url.Namespace);

        var tlm = await NextAsync();
        Assert.Equal(EddystoneFrameType.Tlm, tlm.FrameType);
        Assert.Equal(3.1, tlm.BatteryVolts);
        Assert.Equal(TimeSpan.FromHours(2), tlm.Uptime);
        Assert.False(tlm.Encrypted);
        Assert.Null(tlm.Proximity);

        await stream.DisposeAsync();
        while (scanner.Scanning)
            await Task.Delay(20, timeout.Token);

        async Task<EddystoneFrame> NextAsync()
            => JsonSerializer.Deserialize(await stream.NextAsync("beacons.eddystone", timeout.Token), BeaconsJsonContext.Default.EddystoneFrame)!;
    }

    [Fact]
    public async Task Broadcasts_as_an_ibeacon_or_eddystone_beacon()
    {
        var broadcaster = new FakeBroadcaster();
        await using var fixture = await BeaconsFixture.StartAsync(broadcaster: broadcaster);

        await fixture.Client.BroadcastIBeaconAsync(new BeaconBroadcast(Uuid, 1, 2, -55));
        Assert.Equal("ibeacon f7826da6-4fa2-4e98-8024-bc5b71e0893e 1 2 -55", broadcaster.Last);
        Assert.True((await fixture.Client.GetBroadcastAsync()).Broadcasting);

        await fixture.Client.BroadcastEddystoneUidAsync(new EddystoneUidBroadcast("00112233445566778899", "aabbccddeeff"));
        Assert.Equal("uid 00112233445566778899:AABBCCDDEEFF ", broadcaster.Last);

        await fixture.Client.BroadcastEddystoneUrlAsync(new EddystoneUrlBroadcast("https://shinylib.net", -20));
        Assert.Equal("url https://shinylib.net -20", broadcaster.Last);

        await fixture.Client.StopBroadcastAsync();
        Assert.False((await fixture.Client.GetBroadcastAsync()).Broadcasting);
    }

    [Fact]
    public async Task Refuses_a_broadcast_it_cannot_encode_with_400()
    {
        var broadcaster = new FakeBroadcaster();
        await using var fixture = await BeaconsFixture.StartAsync(broadcaster: broadcaster);

        var badUid = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.BroadcastEddystoneUidAsync(new EddystoneUidBroadcast("0011", "zz")));
        Assert.Equal(HttpStatusCode.BadRequest, badUid.StatusCode);

        var longUrl = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.BroadcastEddystoneUrlAsync(new EddystoneUrlBroadcast("https://example.com/a/very/long/path/indeed")));
        Assert.Equal(HttpStatusCode.BadRequest, longUrl.StatusCode);

        var noUuid = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.BroadcastIBeaconAsync(new BeaconBroadcast(Guid.Empty, 1, 2)));
        Assert.Equal(HttpStatusCode.BadRequest, noUuid.StatusCode);

        Assert.Null(broadcaster.Last);
    }

    // ---------------------------------------------------------------------------------------------------------------

    sealed class BeaconsFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public BeaconsBridgeClient Client { get; private set; } = null!;
        public WebAppEventHub Events { get; } = new();

        public static async Task<BeaconsFixture> StartAsync(
            Native.IBeaconRangingManager? ranging = null,
            Native.IBeaconMonitoringManager? monitoring = null,
            Native.IEddystoneScanner? eddystone = null,
            Native.IBeaconBroadcaster? broadcaster = null)
        {
            var fixture = new BeaconsFixture();
            var services = new ServiceCollection();
            if (ranging is not null)
                services.AddSingleton(ranging);
            if (monitoring is not null)
                services.AddSingleton(monitoring);
            if (eddystone is not null)
                services.AddSingleton(eddystone);
            if (broadcaster is not null)
                services.AddSingleton(broadcaster);
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                _ => [new BeaconsBridge(provider)],
                fixture.Events,
                client => fixture.WebView = client
            );

            fixture.Client = new BeaconsBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class FakeRanging : Native.IBeaconRangingManager
    {
        readonly Dictionary<string, Subject<Native.Beacon>> ranging = [];
        readonly HashSet<string> stopped = [];

        public Shiny.AccessState Status { get; set; } = Shiny.AccessState.Unknown;
        public List<Native.BeaconRegion> Regions { get; } = [];

        public Shiny.AccessState CurrentStatus => this.Status;

        public Task<Shiny.AccessState> RequestAccess() => Task.FromResult(this.Status);

        public IObservable<Native.Beacon> WhenBeaconRanged(Native.BeaconRegion region)
        {
            var subject = new Subject<Native.Beacon>();
            lock (this.ranging)
            {
                this.Regions.Add(region);
                this.ranging[region.Identifier] = subject;
                this.stopped.Remove(region.Identifier);
            }

            return subject.Finally(() =>
            {
                lock (this.ranging)
                    this.stopped.Add(region.Identifier);
            });
        }

        public void Emit(string region, Native.Beacon beacon) => this.Subject(region).OnNext(beacon);

        public void Fail(string region, Exception ex) => this.Subject(region).OnError(ex);

        public bool IsStopped(string region)
        {
            lock (this.ranging)
                return this.stopped.Contains(region);
        }

        Subject<Native.Beacon> Subject(string region)
        {
            lock (this.ranging)
                return this.ranging[region];
        }
    }

    sealed class FakeMonitoring : Native.IBeaconMonitoringManager
    {
        public List<Native.BeaconRegion> Regions { get; } = [];
        public Native.BeaconRegionState State { get; init; } = Native.BeaconRegionState.Unknown;
        public bool Refuse { get; init; }
        public Shiny.AccessState Status { get; init; } = Shiny.AccessState.Available;

        public Shiny.AccessState CurrentStatus => this.Status;

        public Task<Shiny.AccessState> RequestAccess() => Task.FromResult(Shiny.AccessState.Available);

        public IList<Native.BeaconRegion> GetMonitoredRegions() => [.. this.Regions];

        public Task StartMonitoring(Native.BeaconRegion region)
        {
            if (this.Refuse)
                throw new InvalidOperationException("Invalid State Denied");

            this.Regions.RemoveAll(x => x.Identifier == region.Identifier);
            this.Regions.Add(region);
            return Task.CompletedTask;
        }

        public Task StopMonitoring(string identifier)
        {
            this.Regions.RemoveAll(x => x.Identifier == identifier);
            return Task.CompletedTask;
        }

        public Task StopAllMonitoring()
        {
            this.Regions.Clear();
            return Task.CompletedTask;
        }

        public Task<Native.BeaconRegionState> RequestState(Native.BeaconRegion region, CancellationToken cancelToken = default)
            => Task.FromResult(this.State);
    }

    sealed class FakeEddystone : Native.IEddystoneScanner
    {
        public Subject<Native.EddystoneFrame> Frames { get; } = new();
        public Shiny.AccessState Status { get; init; } = Shiny.AccessState.Unknown;
        public bool Scanning => Volatile.Read(ref this.scanning) > 0;
        int scanning;

        public Shiny.AccessState CurrentStatus => this.Status;

        public Task<Shiny.AccessState> RequestAccess() => Task.FromResult(this.Status);

        public IObservable<Native.EddystoneFrame> WhenFrameReceived()
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref this.scanning);
                return this.Frames.Finally(() => Interlocked.Decrement(ref this.scanning));
            });
    }

    sealed class FakeBroadcaster : Native.IBeaconBroadcaster
    {
        public bool IsBroadcasting { get; set; }
        public string? Last { get; private set; }

        public Task<Shiny.AccessState> RequestAccess() => Task.FromResult(Shiny.AccessState.Available);

        public Task StartIBeacon(Guid uuid, ushort major, ushort minor, sbyte? txPower = null)
            => this.Start($"ibeacon {uuid} {major} {minor} {txPower}");

        public Task StartEddystoneUid(Native.EddystoneUid uid, sbyte? txPower = null)
            => this.Start($"uid {uid} {txPower}");

        public Task StartEddystoneUrl(string url, sbyte? txPower = null)
        {
            // What Shiny.Beacons' encoder does with a URL that will not fit an Eddystone-URL frame.
            if (url.Length > 24)
                throw new ArgumentException("The URL does not compress to 17 bytes", nameof(url));

            return this.Start($"url {url} {txPower}");
        }

        public void Stop() => this.IsBroadcasting = false;

        Task Start(string what)
        {
            this.Last = what;
            this.IsBroadcasting = true;
            return Task.CompletedTask;
        }
    }
}
