using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Obd;
using Shiny.AppDeviceBridge.Obd.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Simulator.Simulation;

/// <summary>
/// The simulator's <c>obd</c> bridge: the real <see cref="ObdBridge"/> while an emulated vehicle is loaded, so the page
/// scans, connects and reads through Shiny.Obd against the emulator exactly as it would against an adapter — and the
/// simulated bridge, answering the values set on its routes, while none is.
/// <para>
/// Both are mounted once, behind the same routes and the same bridge policy; each request goes to whichever is live. The
/// page's <c>obd.reading</c> and <c>obd.disconnected</c> streams carry both: events fired by hand, and the real bridge's.
/// </para>
/// </summary>
public sealed class ObdBridgeSwitch(SimulatorState state, BridgeState bridge, ObdBridge device, IObdVirtualAdapter vehicle) : IWebAppBridge
{
    readonly SimulatedBridge simulated = new(state, bridge);

    public string Name => bridge.Name;

    /// <summary>A loaded vehicle is an adapter in range, whatever the simulated bridge is set to.</summary>
    public bool IsSupported => vehicle.IsAvailable || bridge.IsSupported;

    public void Map(WebAppBridgeRoutes routes)
    {
        var handlers = device.Routes().ToDictionary(
            x => $"{x.Method} {x.Pattern.TrimStart('/')}".TrimEnd(),
            x => x.Handler,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var route in bridge.Routes)
        {
            // The catalog is read from the client interface the real bridge implements; a route one has and the other
            // doesn't is drift between them, and better found here than by a page getting the wrong side.
            var real = handlers.GetValueOrDefault(route.Route.Key)
                ?? throw new InvalidOperationException($"ObdBridge has no handler for the client's '{route.Route.Key}'.");

            RequestDelegate handler = ctx => this.HandleAsync(ctx, route, real);
            var pattern = route.Route.Pattern;

            _ = route.Route.Method switch
            {
                "GET" => routes.MapGet(pattern, handler),
                "POST" => routes.MapPost(pattern, handler),
                "PUT" => routes.MapPut(pattern, handler),
                _ => routes.MapDelete(pattern, handler)
            };
        }

        var streams = new Dictionary<string, Func<CancellationToken, IAsyncEnumerable<JsonElement>>>
        {
            ["obd.reading"] = ct => Elements(device.ListenReadings(ct), ObdJsonContext.Default.ObdMonitorReading),
            ["obd.disconnected"] = ct => Elements(device.ListenDisconnected(ct), ObdJsonContext.Default.ObdDisconnected)
        };

        foreach (var evt in bridge.Events)
        {
            // The simulated side's own source, kept off the hub so the one name on the hub can carry both sides.
            var fired = new WebAppEventSource<JsonElement>();
            evt.Source = fired;

            var real = streams.GetValueOrDefault(evt.Event.Name)
                ?? throw new InvalidOperationException($"ObdBridge raises no '{evt.Event.Name}'.");

            routes.MapEvent(evt.Event.Name, ct => Merge(fired.ListenAsync(cancellationToken: ct), real, ct), AppDeviceBridgeJsonContext.Default.JsonElement);
        }
    }

    async ValueTask HandleAsync(HttpContext context, RouteState route, RequestDelegate real)
    {
        if (!vehicle.IsAvailable)
        {
            await this.simulated.HandleAsync(context, route);
            return;
        }

        route.Hit();
        state.OnChanged();
        await real(context);
    }

    static async IAsyncEnumerable<JsonElement> Elements<T>(IAsyncEnumerable<T> source, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        await foreach (var item in source)
            yield return JsonSerializer.SerializeToElement(item, typeInfo);
    }

    /// <summary>Both streams as one, for as long as the page listens; each is listened to only while it is.</summary>
    static async IAsyncEnumerable<JsonElement> Merge(
        IAsyncEnumerable<JsonElement> fired,
        Func<CancellationToken, IAsyncEnumerable<JsonElement>> real,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pumps = Task.WhenAll(Pump(fired), Pump(real(stop.Token)));
        _ = pumps.ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }
        finally
        {
            stop.Cancel();
        }

        async Task Pump(IAsyncEnumerable<JsonElement> source)
        {
            try
            {
                await foreach (var item in source.WithCancellation(stop.Token))
                    await channel.Writer.WriteAsync(item, stop.Token);
            }
            catch (OperationCanceledException)
            {
                // The page stopped listening.
            }
        }
    }
}
