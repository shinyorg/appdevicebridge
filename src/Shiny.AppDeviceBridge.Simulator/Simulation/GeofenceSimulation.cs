using System.Text.Json;
using Shiny.AppDeviceBridge.Geofencing.Client;
using Shiny.AppDeviceBridge.Gps.Client;
using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Trails;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Simulator.Simulation;

/// <summary>A region the page is monitoring, and where the simulated device is relative to it.</summary>
public sealed record SimulatedGeofence(GeofenceRegion Region, GeofenceState State);

/// <summary>
/// The geofences bridge, played out against the simulated GPS: the regions the page monitors through
/// <c>POST geofences/regions</c> are kept, and every <c>gps.reading</c> — fired by hand, by an agent or by a GPX/KML trail —
/// is checked against them. Crossing a boundary fires <c>geofence.status</c>, as the OS would.
/// <para>
/// A region starts <see cref="GeofenceState.Unknown"/>. The first position inside it fires an entry — as Android does for a
/// region the device is already in — and the first outside settles it as exited silently. After that each crossing fires,
/// unless the region turned that direction off; a single-use region stops being monitored after its first.
/// </para>
/// <para>
/// Off, or while the geofences bridge is unsupported, nothing is kept or fired and the bridge answers only the values set on
/// its routes, like any other.
/// </para>
/// </summary>
public sealed class GeofenceSimulation(SimulatorState state)
{
    public const string Bridge = "geofences";
    public const string StatusEvent = "geofence.status";
    public const string GpsEvent = "gps.reading";

    readonly Lock gate = new();
    readonly List<SimulatedGeofence> regions = [];
    TrackPoint? position;
    volatile bool enabled = true;

    /// <summary>On by default.</summary>
    public bool Enabled
    {
        get => this.enabled;
        set
        {
            this.enabled = value;
            state.Log($"geofence simulation {(value ? "on" : "off")}");
        }
    }

    /// <summary>The regions being monitored, in the order the page added them.</summary>
    public IReadOnlyList<SimulatedGeofence> Regions
    {
        get
        {
            lock (this.gate)
                return [.. this.regions];
        }
    }

    /// <summary>The last <c>gps.reading</c>'s position; null before the first.</summary>
    public TrackPoint? Position
    {
        get
        {
            lock (this.gate)
                return this.position;
        }
    }

    bool IsLive => this.enabled && state.Find(Bridge) is { IsSupported: true };

    /// <summary>Starts monitoring a region, replacing one with the same identifier, and checks it against the last position.</summary>
    /// <exception cref="ArgumentException">The region is not one the real bridge would accept.</exception>
    public void Monitor(GeofenceRegion region)
    {
        if (Validate(region) is { } error)
            throw new ArgumentException(error, nameof(region));

        GeofenceStatus? fired;
        lock (this.gate)
        {
            this.regions.RemoveAll(x => x.Region.Identifier == region.Identifier);
            this.regions.Add(new SimulatedGeofence(region, GeofenceState.Unknown));
            fired = this.position is { } at ? this.Evaluate(this.regions.Count - 1, at) : null;
        }

        state.Log($"geofence '{region.Identifier}' monitored ({region.RadiusMeters:0.#} m at {region.Latitude:F5}, {region.Longitude:F5})");
        this.Publish(fired is null ? [] : [fired]);
    }

    /// <summary>False when no region has that identifier.</summary>
    public bool StopMonitoring(string identifier)
    {
        int removed;
        lock (this.gate)
            removed = this.regions.RemoveAll(x => x.Region.Identifier == identifier);

        if (removed > 0)
        {
            state.Log($"geofence '{identifier}' no longer monitored");
            this.SyncRoute();
        }

        return removed > 0;
    }

    public void StopAll()
    {
        lock (this.gate)
            this.regions.Clear();

        state.Log("no geofences monitored");
        this.SyncRoute();
    }

    /// <summary>Where the device is relative to a monitored region; null for one not monitored.</summary>
    public GeofenceStatus? GetStatus(string identifier)
    {
        lock (this.gate)
            return this.regions.Where(x => x.Region.Identifier == identifier).Select(x => new GeofenceStatus(identifier, x.State)).FirstOrDefault();
    }

    /// <summary>A <c>gps.reading</c> went out: moves the device and fires a <c>geofence.status</c> for each boundary it crossed.</summary>
    internal void OnReading(JsonElement payload)
    {
        if (!this.IsLive)
            return;

        var reading = payload.Deserialize(GpsJsonContext.Default.GpsReading);
        if (reading is null)
            return;

        var at = new TrackPoint(reading.Latitude, reading.Longitude);
        var fired = new List<GeofenceStatus>();

        lock (this.gate)
        {
            this.position = at;

            // Backwards, so a single-use region removed on its transition does not shift the ones still to check.
            for (var i = this.regions.Count - 1; i >= 0; i--)
            {
                if (this.Evaluate(i, at) is { } status)
                    fired.Insert(0, status);
            }
        }

        this.Publish(fired);
    }

    /// <summary>
    /// Answers the routes whose answer is the monitored regions themselves. False for the rest, which answer from their
    /// behavior as usual — <c>GET regions</c> among them, whose value is kept in step with the regions.
    /// </summary>
    internal async ValueTask<bool> TryAnswerAsync(HttpContext context, SimRoute route)
    {
        if (!this.IsLive)
            return false;

        switch (route.Key)
        {
            case "POST regions":
                var region = await WebAppBridgeResults.ReadBodyAsync(context, GeofencingJsonContext.Default.GeofenceRegion);
                var error = region is null ? "Expected a region." : Validate(region);
                if (error is not null)
                {
                    await WebAppBridgeResults.BadRequest(context, error);
                    return true;
                }

                this.Monitor(region!);
                await WebAppBridgeResults.NoContent(context);
                return true;

            case "DELETE regions":
                this.StopAll();
                await WebAppBridgeResults.NoContent(context);
                return true;

            case "DELETE regions/{identifier}":
                this.StopMonitoring(context.Request.RouteValues["identifier"] ?? String.Empty);
                await WebAppBridgeResults.NoContent(context);
                return true;

            case "GET regions/{identifier}/state":
                var identifier = context.Request.RouteValues["identifier"] ?? String.Empty;
                if (this.GetStatus(identifier) is { } status)
                    await WebAppBridgeResults.Json(context, status, GeofencingJsonContext.Default.GeofenceStatus);
                else
                    await WebAppBridgeResults.NotFound(context, $"No monitored region '{identifier}'.");

                return true;

            default:
                return false;
        }
    }

    /// <summary>Checks one region against <paramref name="at"/>, updating it; the status to fire, if the change is one to tell.</summary>
    GeofenceStatus? Evaluate(int index, TrackPoint at)
    {
        var current = this.regions[index];
        var region = current.Region;
        var inside = TrackPoint.Distance(region.Latitude, region.Longitude, at.Latitude, at.Longitude) <= region.RadiusMeters;
        var next = inside ? GeofenceState.Entered : GeofenceState.Exited;

        if (next == current.State)
            return null;

        this.regions[index] = current with { State = next };

        // Starting outside is where a region begins, not a crossing.
        if (current.State == GeofenceState.Unknown && next == GeofenceState.Exited)
            return null;

        var notify = inside ? region.NotifyOnEntry : region.NotifyOnExit;
        if (!notify)
            return null;

        if (region.SingleUse)
            this.regions.RemoveAt(index);

        return new GeofenceStatus(region.Identifier, next);
    }

    void Publish(IReadOnlyList<GeofenceStatus> fired)
    {
        foreach (var status in fired)
        {
            state.Raise(StatusEvent, JsonSerializer.Serialize(status, GeofencingJsonContext.Default.GeofenceStatus));
            state.Log($"geofence '{status.Identifier}' {status.State.ToString().ToLowerInvariant()}");
        }

        this.SyncRoute();
    }

    /// <summary>Keeps what <c>GET regions</c> answers in step with the regions monitored, leaving how it answers alone.</summary>
    void SyncRoute()
    {
        if (state.FindRoute(Bridge, "GET regions") is not { } route)
            return;

        IReadOnlyList<GeofenceRegion> monitored = [.. this.Regions.Select(x => x.Region)];
        route.Behavior = route.Behavior with
        {
            Json = JsonSerializer.Serialize(monitored, GeofencingJsonContext.Default.IReadOnlyListGeofenceRegion),
            Sequence = null
        };
        state.OnChanged();
    }

    /// <summary>The real bridge's checks; null when the region passes them.</summary>
    static string? Validate(GeofenceRegion region)
    {
        if (String.IsNullOrWhiteSpace(region.Identifier))
            return "identifier is required.";

        if (region.Latitude is < -90 or > 90 || region.Longitude is < -180 or > 180)
            return "latitude or longitude is out of range.";

        if (region.RadiusMeters <= 0)
            return "radiusMeters must be positive.";

        return null;
    }
}
