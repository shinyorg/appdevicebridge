using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.DocumentGeofencing;
using Shiny.AppDeviceBridge.DocumentGeofencing.Client;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer;
using Native = Shiny.DocumentDb.Geofencing;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The document geofencing bridge over a fake Shiny.DocumentDb.Geofencing manager: 501 without one, the registered
/// region sets, start/stop and their refusals, where the device is, and changes reaching the page and background.js
/// with the region document written through the store's own JSON metadata. The GPS-driven spatial evaluation itself is
/// Shiny.DocumentDb.Geofencing's and is tested there.
/// </summary>
public class DocumentGeofenceBridgeTests
{
    [Fact]
    public async Task Answers_501_without_document_geofencing()
    {
        await using var fixture = await DocumentGeofenceFixture.StartAsync(null);

        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetStatusAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StopAsync())).IsNotSupported);
        Assert.True((await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.GetCurrentAsync())).IsNotSupported);
        Assert.False(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "documentgeofences").IsSupported);
    }

    [Fact]
    public async Task Status_reports_monitoring_and_the_registered_region_sets()
    {
        var manager = new FakeManager { IsStarted = true };
        var config = new Native.DocumentGeofenceConfig()
            .AddRegionSet<Zone>("zones", z => z.Id, z => z.Name)
            .AddRegionSet<Zone>("stores", z => z.Id, withinMeters: 250);
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager, services => services.AddSingleton(config));

        var status = await fixture.Client.GetStatusAsync();

        Assert.True(status.IsStarted);
        Assert.Equal([new DocumentRegionSetInfo("zones"), new DocumentRegionSetInfo("stores", 250)], status.RegionSets);
        Assert.True(Assert.Single((await new HostBridgeClient(fixture.Transport).GetInfoAsync()).Bridges, x => x.Name == "documentgeofences").IsSupported);
    }

    [Fact]
    public async Task Requests_access()
    {
        var manager = new FakeManager { Access = Shiny.AccessState.Restricted };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager);

        var result = await fixture.Client.RequestAccessAsync();

        Assert.Equal(Client.AccessState.Restricted, result.Access);
    }

    [Fact]
    public async Task Starts_and_stops_monitoring()
    {
        var manager = new FakeManager();
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager);

        await fixture.Client.StartAsync();
        Assert.True((await fixture.Client.GetStatusAsync()).IsStarted);

        await fixture.Client.StopAsync();
        Assert.False((await fixture.Client.GetStatusAsync()).IsStarted);
        Assert.Equal(["start", "stop"], manager.Calls);
    }

    [Fact]
    public async Task A_store_that_cannot_run_spatial_queries_is_a_501()
    {
        var manager = new FakeManager { StartFailure = new NotSupportedException("The document store's provider does not support spatial queries.") };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync());

        Assert.Equal(HttpStatusCode.NotImplemented, refused.StatusCode);
        Assert.Equal("spatial_not_supported", refused.Code);
    }

    [Fact]
    public async Task Starting_without_permission_is_a_409()
    {
        var manager = new FakeManager { StartFailure = new InvalidOperationException("Insufficient GPS permissions") };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager);

        var refused = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.StartAsync());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("geofence_refused", refused.Code);
        Assert.False(manager.IsStarted);
    }

    [Fact]
    public async Task Current_writes_region_documents_through_the_stores_json_metadata()
    {
        var manager = new FakeManager
        {
            Current =
            [
                new Native.DocumentCurrentRegion("zones", "z1", "Downtown", new Zone("z1", "Downtown", true), 0),
                new Native.DocumentCurrentRegion("cities", "toronto", "Toronto", new Unmapped("toronto"), 1234.5),
                new Native.DocumentCurrentRegion("states", null, null, null, 0)
            ]
        };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager, StoreJson);

        var current = await fixture.Client.GetCurrentAsync();

        Assert.Equal(3, current.Count);
        Assert.Equal(("zones", "z1", "Downtown", 0d), (current[0].RegionSet, current[0].RegionId, current[0].RegionName, current[0].DistanceMeters));
        Assert.Equal("""{"id":"z1","name":"Downtown","active":true}""", current[0].Region?.GetRawText());

        // The store's options have no metadata for this type: left out, never reflected over.
        Assert.Equal(("cities", "toronto", 1234.5), (current[1].RegionSet, current[1].RegionId, current[1].DistanceMeters));
        Assert.Null(current[1].Region);

        // Outside every region of the set.
        Assert.Equal(new DocumentCurrentRegion("states", null, null, 0), current[2]);
    }

    [Fact]
    public async Task Uses_the_stores_json_options_once_the_store_fills_them_in()
    {
        // A store left on its defaults sets JsonSerializerOptions only when it is first built — after the bridge.
        var storeOptions = new DocumentStoreOptions { DatabaseProvider = null! };
        var manager = new FakeManager { Current = [new Native.DocumentCurrentRegion("zones", "z1", null, new Zone("z1", "Downtown", true), 0)] };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager, services => services.AddSingleton(storeOptions));

        storeOptions.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = RegionJsonContext.Default };
        var current = Assert.Single(await fixture.Client.GetCurrentAsync());

        Assert.Equal("""{"id":"z1","name":"Downtown","active":true}""", current.Region?.GetRawText());
    }

    [Fact]
    public async Task Current_is_empty_without_a_gps_reading()
    {
        await using var fixture = await DocumentGeofenceFixture.StartAsync(new FakeManager());

        Assert.Empty(await fixture.Client.GetCurrentAsync());
    }

    [Fact]
    public async Task Region_serializer_options_take_over_from_the_stores()
    {
        var manager = new FakeManager { Current = [new Native.DocumentCurrentRegion("cities", "toronto", null, new Unmapped("toronto"), 0)] };
        await using var fixture = await DocumentGeofenceFixture.StartAsync(manager, services =>
        {
            StoreJson(services);
            services.AddSingleton(new WebAppDocumentGeofenceOptions
            {
                RegionSerializerOptions = new JsonSerializerOptions { TypeInfoResolver = OtherRegionJsonContext.Default }
            });
        });

        var current = Assert.Single(await fixture.Client.GetCurrentAsync());

        Assert.Equal("""{"Code":"toronto"}""", current.Region?.GetRawText());
    }

    [Fact]
    public async Task The_delegate_publishes_changes_to_the_page()
    {
        await using var fixture = await DocumentGeofenceFixture.StartAsync(new FakeManager(), StoreJson);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var stream = await TestEventStream.OpenAsync(fixture.WebView, WebAppDocumentGeofenceDelegate.EventName, timeout.Token);

        await fixture.CreateDelegate().OnRegionChanged(new Native.DocumentRegionChange(
            "zones", "z1", "Downtown", new Zone("z1", "Downtown", true), true, new GeoPoint(43.6532, -79.3832)
        ));

        var change = JsonSerializer.Deserialize(await stream.NextAsync(WebAppDocumentGeofenceDelegate.EventName, timeout.Token), DocumentGeofencingJsonContext.Default.DocumentRegionChange)!;
        Assert.Equal(("zones", "z1", "Downtown", true, 43.6532, -79.3832), (change.RegionSet, change.RegionId, change.RegionName, change.Entered, change.Latitude, change.Longitude));
        Assert.Equal("""{"id":"z1","name":"Downtown","active":true}""", change.Region?.GetRawText());
    }

    [Fact]
    public async Task The_delegate_hands_changes_to_background_js()
    {
        await using var app = new TestApp();
        var background = new RecordingBackground();
        var invoker = new WebAppInvoker(app.BridgeOptions(), () => background);
        var provider = new ServiceCollection().AddSingleton(new WebAppDocumentGeofenceOptions()).BuildServiceProvider();
        var d = new WebAppDocumentGeofenceDelegate(provider, new WebAppEventHub(), invoker);

        // An exit from a region deleted since: no document to describe it.
        await d.OnRegionChanged(new Native.DocumentRegionChange("zones", "gone", null, null, false, new GeoPoint(1, 2)));

        var (handler, payload) = Assert.Single(background.Calls);
        Assert.Equal("documentgeofence", handler);
        Assert.Equal(
            new DocumentRegionChange("zones", "gone", null, false, 1, 2),
            JsonSerializer.Deserialize(payload, DocumentGeofencingJsonContext.Default.DocumentRegionChange)
        );
    }

    [Fact]
    public async Task AddDocumentGeofenceBridge_registers_the_bridge_and_its_options()
    {
        var serializerOptions = new JsonSerializerOptions();
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge => bridge
                .Configure(o => o.AppId = TestApp.AppId)
                .AddDocumentGeofenceBridge(
                    cfg => cfg.AddRegionSet<Zone>("zones", z => z.Id),
                    o => o.RegionSerializerOptions = serializerOptions
                )),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        // Plain net10.0 has no GPS to drive monitoring, so the bridge is there and answers 501.
        var bridge = Assert.Single(server.Bridges.OfType<DocumentGeofenceBridge>());
        Assert.False(bridge.IsSupported);
        Assert.Same(serializerOptions, provider.GetRequiredService<WebAppDocumentGeofenceOptions>().RegionSerializerOptions);
    }

    static void StoreJson(IServiceCollection services) => services.AddSingleton(new DocumentStoreOptions
    {
        // Only the serializer options are read; the provider is never touched.
        DatabaseProvider = null!,
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = RegionJsonContext.Default }
    });

    sealed class DocumentGeofenceFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;
        ServiceProvider provider = null!;

        public HttpClient WebView { get; private set; } = null!;
        public IBridgeTransport Transport => this.host.Transport;
        public DocumentGeofencesBridgeClient Client { get; private set; } = null!;
        public WebAppEventHub Events { get; } = new();

        public WebAppDocumentGeofenceDelegate CreateDelegate()
            => new(this.provider, this.Events, new WebAppInvoker(new AppDeviceBridgeOptions { AppId = TestApp.AppId }));

        public static async Task<DocumentGeofenceFixture> StartAsync(Native.IDocumentGeofenceManager? manager, Action<IServiceCollection>? configure = null)
        {
            var fixture = new DocumentGeofenceFixture();
            var services = new ServiceCollection();
            if (manager is not null)
                services.AddSingleton(manager);
            configure?.Invoke(services);
            fixture.provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(_ => [new DocumentGeofenceBridge(fixture.provider)], fixture.Events, client => fixture.WebView = client);
            fixture.Client = new DocumentGeofencesBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await this.host.DisposeAsync();
            await this.provider.DisposeAsync();
        }
    }

    sealed class FakeManager : Native.IDocumentGeofenceManager
    {
        public bool IsStarted { get; set; }
        public Shiny.AccessState Access { get; init; } = Shiny.AccessState.Available;
        public Exception? StartFailure { get; init; }
        public IReadOnlyList<Native.DocumentCurrentRegion> Current { get; init; } = [];
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

        public Task<IReadOnlyList<Native.DocumentCurrentRegion>> GetCurrent(CancellationToken cancellationToken = default)
            => Task.FromResult(this.Current);
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
partial class RegionJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(Unmapped))]
partial class OtherRegionJsonContext : JsonSerializerContext;
