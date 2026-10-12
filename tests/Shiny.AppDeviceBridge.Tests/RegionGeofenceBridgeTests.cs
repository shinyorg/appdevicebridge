using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.RegionGeofencing;
using Shiny.AppDeviceBridge.RegionGeofencing.Client;
using Shiny.Net.HttpServer;
using Native = Shiny.Locations;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The region geofencing bridge over a fake Shiny.Geofencing.Regions manager: 501 without one, the registered region
/// sets, start/stop and their refusals, where the device is, and changes reaching the page and background.js with the
/// region written through the configured JSON metadata — or Shiny's placemark for a geocoder set. The GPS-driven region
/// evaluation itself is Shiny.Geofencing.Regions' and is tested there.
/// </summary>
public class RegionGeofenceBridgeTests
{
    [Fact]
    public async Task Answers_501_without_region_geofencing()
    {
        await using var fixture = await RegionGeofenceFixture.StartAsync(null);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetCurrentAsync())).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "regiongeofences").IsSupported);
    }

    [Fact]
    public async Task Status_reports_monitoring_and_the_registered_region_sets()
    {
        var manager = new FakeManager { IsStarted = true };
        var config = new Native.RegionGeofenceConfig()
            .AddRegionSet("zones", new FakeSource())
            .AddRegionSet("stores", new FakeSource());
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager, services => services.AddSingleton(config));

        var status = await fixture.Client.GetStatusAsync();

        Assert.True(status.IsStarted);
        Assert.Equal([new RegionSetInfo("zones"), new RegionSetInfo("stores")], status.RegionSets);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "regiongeofences").IsSupported);
    }

    [Fact]
    public async Task Requests_access()
    {
        var manager = new FakeManager { Access = Shiny.AccessState.Restricted };
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager);

        var result = await fixture.Client.RequestAccessAsync();

        Assert.Equal(Client.AccessState.Restricted, result.Access);
    }

    [Fact]
    public async Task Starts_and_stops_monitoring()
    {
        var manager = new FakeManager();
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager);

        await fixture.Client.StartAsync();
        Assert.True((await fixture.Client.GetStatusAsync()).IsStarted);

        await fixture.Client.StopAsync();
        Assert.False((await fixture.Client.GetStatusAsync()).IsStarted);
        Assert.Equal(["start", "stop"], manager.Calls);
    }

    [Fact]
    public async Task Starting_without_permission_is_a_409()
    {
        var manager = new FakeManager { StartFailure = new InvalidOperationException("Insufficient GPS permissions") };
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("geofence_refused", refused.Code);
        Assert.False(manager.IsStarted);
    }

    [Fact]
    public async Task Current_writes_regions_through_the_configured_json_metadata()
    {
        var manager = new FakeManager
        {
            Current =
            [
                new Native.CurrentRegion("zones", "z1", "Downtown", new Zone("z1", "Downtown", true), 0),
                new Native.CurrentRegion("cities", "toronto", "Toronto", new Unmapped("toronto"), 1234.5),
                new Native.CurrentRegion("states", null, null, null, 0)
            ]
        };
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager, RegionJson);

        var current = await fixture.Client.GetCurrentAsync();

        Assert.Equal(3, current.Count);
        Assert.Equal(("zones", "z1", "Downtown", 0d), (current[0].RegionSet, current[0].RegionId, current[0].RegionName, current[0].DistanceMeters));
        Assert.Equal("""{"id":"z1","name":"Downtown","active":true}""", current[0].Region?.GetRawText());

        // The options have no metadata for this type: left out, never reflected over.
        Assert.Equal(("cities", "toronto", 1234.5), (current[1].RegionSet, current[1].RegionId, current[1].DistanceMeters));
        Assert.Null(current[1].Region);

        // Outside every region of the set.
        Assert.Equal(new CurrentRegion("states", null, null, 0), current[2]);
    }

    [Fact]
    public async Task Without_serializer_options_a_document_region_is_left_out()
    {
        var manager = new FakeManager { Current = [new Native.CurrentRegion("zones", "z1", "Downtown", new Zone("z1", "Downtown", true), 0)] };
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager);

        var current = Assert.Single(await fixture.Client.GetCurrentAsync());

        Assert.Equal("z1", current.RegionId);
        Assert.Null(current.Region);
    }

    [Fact]
    public async Task A_geocoder_sets_placemark_is_written_without_serializer_options()
    {
        var placemark = new Native.Placemark(
            new Native.Position(39.7392, -104.9903), "Civic Center", null, null, null, "Denver", "Denver County", "Colorado", "80202", "US", "United States", null
        );
        var manager = new FakeManager { Current = [new Native.CurrentRegion("states", "US-Colorado", "Colorado", placemark, 0)] };
        await using var fixture = await RegionGeofenceFixture.StartAsync(manager);

        var current = Assert.Single(await fixture.Client.GetCurrentAsync());
        var region = current.Region!.Value;

        Assert.Equal("Colorado", region.GetProperty("administrativeArea").GetString());
        Assert.Equal("US", region.GetProperty("countryCode").GetString());
        Assert.Equal(39.7392, region.GetProperty("position").GetProperty("latitude").GetDouble());
    }

    [Fact]
    public async Task Current_is_empty_without_a_gps_reading()
    {
        await using var fixture = await RegionGeofenceFixture.StartAsync(new FakeManager());

        Assert.Empty(await fixture.Client.GetCurrentAsync());
    }

    [Fact]
    public async Task The_delegate_publishes_changes_to_the_page()
    {
        await using var fixture = await RegionGeofenceFixture.StartAsync(new FakeManager(), RegionJson);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, WebAppRegionGeofenceDelegate.EventName, timeout.Token);

        await fixture.CreateDelegate().OnRegionChanged(new Native.RegionChange(
            "zones", "z1", "Downtown", new Zone("z1", "Downtown", true), true, new Native.Position(43.6532, -79.3832)
        ));

        var change = JsonSerializer.Deserialize(await stream.NextAsync(WebAppRegionGeofenceDelegate.EventName, timeout.Token), RegionGeofencingJsonContext.Default.RegionChange)!;
        Assert.Equal(("zones", "z1", "Downtown", true, 43.6532, -79.3832), (change.RegionSet, change.RegionId, change.RegionName, change.Entered, change.Latitude, change.Longitude));
        Assert.Equal("""{"id":"z1","name":"Downtown","active":true}""", change.Region?.GetRawText());
    }

    [Fact]
    public async Task The_delegate_hands_changes_to_background_js()
    {
        await using var app = new TestApp();
        var background = new RecordingBackground();
        var invoker = new WebAppInvoker(app.BridgeOptions(), () => background);
        var provider = new ServiceCollection().AddSingleton(new WebAppRegionGeofenceOptions()).BuildServiceProvider();
        var d = new WebAppRegionGeofenceDelegate(provider, new WebAppEventHub(), invoker);

        // An exit the source can no longer describe: no region object.
        await d.OnRegionChanged(new Native.RegionChange("zones", "gone", null, null, false, new Native.Position(1, 2)));

        var (handler, payload) = Assert.Single(background.Calls);
        Assert.Equal("regiongeofence", handler);
        Assert.Equal(
            new RegionChange("zones", "gone", null, false, 1, 2),
            JsonSerializer.Deserialize(payload, RegionGeofencingJsonContext.Default.RegionChange)
        );
    }

    [Fact]
    public async Task AddRegionGeofenceBridge_registers_the_bridge_and_its_options()
    {
        var serializerOptions = new JsonSerializerOptions();
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddRegionGeofenceBridge(
                    cfg => cfg.AddRegionSet("zones", new FakeSource()),
                    o => o.RegionSerializerOptions = serializerOptions
                )),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        // Plain net10.0 has no GPS to drive monitoring, so the bridge is there and answers 501.
        var bridge = Assert.Single(server.Bridges.OfType<RegionGeofenceBridge>());
        Assert.False(bridge.IsSupported);
        Assert.Same(serializerOptions, provider.GetRequiredService<WebAppRegionGeofenceOptions>().RegionSerializerOptions);
    }

    static void RegionJson(IServiceCollection services) => services.AddSingleton(new WebAppRegionGeofenceOptions
    {
        RegionSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ZoneJsonContext.Default }
    });

    sealed class RegionGeofenceFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;
        ServiceProvider provider = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public RegionGeofencesBridgeClient Client { get; private set; } = null!;
        public WebAppEventHub Events { get; } = new();

        public WebAppRegionGeofenceDelegate CreateDelegate()
            => new(this.provider, this.Events, new WebAppInvoker(new AppDeviceBridgeOptions { AppId = TestApp.AppId }));

        public static async Task<RegionGeofenceFixture> StartAsync(Native.IRegionGeofenceManager? manager, Action<IServiceCollection>? configure = null)
        {
            var fixture = new RegionGeofenceFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            configure?.Invoke(services);
            fixture.provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new RegionGeofenceBridge(fixture.provider)], fixture.Events, client => fixture.WebView = client);
            fixture.Client = new RegionGeofencesBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await this.host.DisposeAsync();
            await this.provider.DisposeAsync();
        }
    }

    sealed class FakeManager : Native.IRegionGeofenceManager
    {
        public bool IsStarted { get; set; }
        public Shiny.AccessState Access { get; init; } = Shiny.AccessState.Available;
        public Exception? StartFailure { get; init; }
        public IReadOnlyList<Native.CurrentRegion> Current { get; init; } = [];
        public List<string> Calls { get; } = [];

        public Task<Shiny.AccessState> RequestAccess() => Task.FromResult(this.Access);

        public Task Start()
        {
            if (this.StartFailure is { } failure)
                return Task.FromException(failure);

            this.Calls.Add("start");
            this.IsStarted = true;
            return Task.CompletedTask;
        }

        public Task Stop()
        {
            this.Calls.Add("stop");
            this.IsStarted = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Native.CurrentRegion>> GetCurrent(CancellationToken cancellationToken = default)
            => Task.FromResult(this.Current);
    }

    sealed class FakeSource : Native.IRegionSource
    {
        public Task<Native.RegionMatch?> FindRegion(Native.Position position, CancellationToken cancellationToken = default)
            => Task.FromResult<Native.RegionMatch?>(null);

        public Task<Native.RegionMatch?> GetRegion(string regionId, CancellationToken cancellationToken = default)
            => Task.FromResult<Native.RegionMatch?>(null);
    }

    sealed class RecordingBackground : IWebAppBackgroundInvoker
    {
        public List<(string Handler, string Payload)> Calls { get; } = [];

        public Task<WebAppInvocationResult> InvokeAsync(string handler, string payloadJson, CancellationToken cancellationToken)
        {
            this.Calls.Add((handler, payloadJson));
            return Task.FromResult(new WebAppInvocationResult(WebAppInvocationTarget.BackgroundScript, true));
        }
    }
}

public sealed record Zone(string Id, string Name, bool Active);

public sealed record Unmapped(string Code);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Zone))]
partial class ZoneJsonContext : JsonSerializerContext;
