using System.Text.Json;
using Shiny.AppDeviceBridge.Simulator.Scenarios;
using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Simulator.Trails;

/// <summary>The simulator's trails, each with its own player, so several can play at once.</summary>
public sealed class TrailLibrary(SimulatorState state) : IDisposable
{
    readonly Lock gate = new();
    readonly List<TrailPlayer> players = [];

    public IReadOnlyList<TrailPlayer> Players
    {
        get
        {
            lock (this.gate)
                return [.. this.players];
        }
    }

    public IEnumerable<Trail> Trails => this.Players.Select(x => x.Trail);

    /// <summary>A player changed, or the library did. From any thread.</summary>
    public event Action? Changed;

    /// <summary>Adds a trail, replacing one of the same name — stopping it first.</summary>
    public TrailPlayer Add(Trail trail)
    {
        var player = new TrailPlayer(state, trail);
        player.Changed += _ => this.Changed?.Invoke();

        TrailPlayer? replaced;
        lock (this.gate)
        {
            var index = this.players.FindIndex(x => String.Equals(x.Trail.Name, trail.Name, StringComparison.OrdinalIgnoreCase));
            replaced = index >= 0 ? this.players[index] : null;

            if (index >= 0)
                this.players[index] = player;
            else
                this.players.Add(player);
        }

        replaced?.Dispose();
        this.Changed?.Invoke();
        return player;
    }

    public TrailPlayer? Find(string name) => this.Players.FirstOrDefault(x => String.Equals(x.Trail.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Remove(TrailPlayer player)
    {
        lock (this.gate)
            this.players.Remove(player);

        player.Dispose();
        this.Changed?.Invoke();
    }

    /// <summary>Loads a <c>.gpx</c>, <c>.kml</c> or <c>.kmz</c> as a GPS walk, or a trail's JSON.</summary>
    /// <param name="interval">For a walk: the gap between points that carry no time.</param>
    public TrailPlayer Load(string path, TimeSpan? interval = null) => this.Add(ReadFile(path, interval));

    public static Trail ReadFile(string path, TimeSpan? interval = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".gpx")
            return GpxImporter.Import(path, interval);

        if (extension is ".kml" or ".kmz")
            return KmlImporter.Import(path, interval);

        using var stream = File.OpenRead(path);
        var trail = JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.Trail) ?? throw new InvalidDataException($"{path} is not a trail.");
        if (String.IsNullOrWhiteSpace(trail.Name) || trail.Name == "Trail")
            trail.Name = Path.GetFileNameWithoutExtension(path).Replace(".trail", "", StringComparison.OrdinalIgnoreCase);

        return trail;
    }

    public static void WriteFile(Trail trail, string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, trail, ScenarioJsonContext.Default.Trail);
    }

    public void StopAll()
    {
        foreach (var player in this.Players)
            player.Stop();
    }

    public void Dispose()
    {
        foreach (var player in this.Players)
            player.Dispose();
    }
}
