using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Maps;
using Shiny.AppDeviceBridge.Maps.Client;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>The maps and directions bridges on the app's real server, with their own storage and a scriptable network.</summary>
sealed class MapsFixture : IAsyncDisposable
{
    BuiltInClientTests.HostFixture host = null!;
    readonly string root = Path.Combine(Path.GetTempPath(), "appdevicebridge-maps", Guid.NewGuid().ToString("n"));

    public MapsService Service { get; private set; } = null!;
    public WebAppEventHub Events { get; } = new();
    public HttpClient WebView { get; private set; } = null!;
    public IBridgeTransport Transport => this.host.Transport;
    public MapsBridgeClient Maps { get; private set; } = null!;
    public DirectionsBridgeClient Directions { get; private set; } = null!;
    public Network Network { get; } = new();

    public static async Task<MapsFixture> StartAsync(Action<MapsOptions>? configure = null, IOnDeviceRouterFactory? onDevice = null)
    {
        var fixture = new MapsFixture();
        var options = new MapsOptions
        {
            OnlineAssets = null,
            Directory = Path.Combine(fixture.root, "data"),
            CacheDirectory = Path.Combine(fixture.root, "cache"),
            HttpMessageHandlerFactory = () => fixture.Network
        };
        configure?.Invoke(options);

        var services = new ServiceCollection();
        if (onDevice is not null)
            services.AddSingleton(onDevice);
        var provider = services.BuildServiceProvider();

        fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
            app =>
            {
                fixture.Service = new MapsService(options, app.BridgeOptions(), fixture.Events, provider);
                return [new MapsBridge(fixture.Service), new DirectionsBridge(fixture.Service)];
            },
            fixture.Events,
            client => fixture.WebView = client
        );

        fixture.Maps = new MapsBridgeClient(fixture.host.Transport);
        fixture.Directions = new DirectionsBridgeClient(fixture.host.Transport);
        return fixture;
    }

    /// <summary>Marks a region installed without downloading it: the files copied into place and its record written.</summary>
    public void InstallDirectly(string id, double[] bounds, string? mapFile = null, bool directions = false)
    {
        var storage = this.Service.Storage;
        Directory.CreateDirectory(storage.RegionDirectory(id));

        if (mapFile is not null)
            File.Copy(mapFile, storage.MapFile(id), true);
        if (directions)
            File.WriteAllText(storage.DirectionsFile(id), "valhalla tiles");

        storage.WriteRegion(new InstalledRegion
        {
            Id = id,
            Name = id,
            Bounds = bounds,
            MaxZoom = 14,
            MapVersion = mapFile is null ? null : "v1",
            MapSize = mapFile is null ? 0 : new FileInfo(mapFile).Length,
            DirectionsVersion = directions ? "v1" : null,
            DirectionsSize = directions ? 14 : 0
        });

        this.Service.ReloadInstalled();
    }

    /// <summary>Waits for a region's download to end, and returns every <c>maps.download</c> event it raised.</summary>
    public async Task<List<MapPackDownload>> WaitForDownloadAsync(string regionId, Func<Task> start)
    {
        var events = new List<MapPackDownload>();
        var source = this.Events.Source(MapsService.DownloadEvent, MapsJsonContext.Default.MapPackDownload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listening = new TaskCompletionSource();
        var listen = Task.Run(async () =>
        {
            var enumerator = source.ListenAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            var first = enumerator.MoveNextAsync();
            listening.SetResult();

            try
            {
                while (await first)
                {
                    events.Add(enumerator.Current);
                    if (enumerator.Current.RegionId == regionId && enumerator.Current.State is MapPackDownloadState.Installed or MapPackDownloadState.Failed or MapPackDownloadState.Cancelled)
                        return;

                    first = enumerator.MoveNextAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        });

        await listening.Task;
        await Task.Delay(50);   // the listener registers when enumeration starts
        await start();
        await listen;
        return events;
    }

    public async ValueTask DisposeAsync()
    {
        await this.host.DisposeAsync();
        this.Service.Dispose();

        try
        {
            Directory.Delete(this.root, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Everything the bridge sends out: routed by host to TestServers and stubs, recorded, and switchable off.</summary>
sealed class Network : HttpMessageHandler
{
    readonly Dictionary<string, HttpMessageInvoker> hosts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> stubs = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = [];
    public bool Offline { get; set; }

    public void Route(string host, HttpMessageHandler handler) => this.hosts[host] = new HttpMessageInvoker(handler);

    public void Stub(string host, Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) => this.stubs[host] = answer;

    public void Stub(string host, Func<HttpRequestMessage, HttpResponseMessage> answer) => this.stubs[host] = r => Task.FromResult(answer(r));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (this.Requests)
            this.Requests.Add(request);

        if (this.Offline)
            throw new HttpRequestException("The network is off.");

        var host = request.RequestUri!.Host;
        if (this.hosts.TryGetValue(host, out var invoker))
            return await invoker.SendAsync(request, cancellationToken);

        if (this.stubs.TryGetValue(host, out var stub))
            return await stub(request);

        throw new HttpRequestException($"No route to {host}.");
    }

    public static HttpResponseMessage Bytes(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
