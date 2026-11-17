using Shiny.AppDeviceBridge.Obd;
using Shiny.Obd;
using Shiny.Obd.Emulator;

namespace Shiny.AppDeviceBridge.Simulator.Simulation;

/// <summary>
/// An emulated vehicle for the <c>obd</c> bridge, loaded from a Shiny.Obd emulator profile
/// (<c>obd-emulator.schema.json</c>): its vehicle, driving scenarios and fault state. While one is loaded it is an adapter
/// in range — the page scans, finds it, connects and reads through the real <see cref="ObdBridge"/> and Shiny.Obd, over an
/// ELM327 emulated in-process — and a scenario played drives its live values: speed, rpm, load, fuel, temperatures, all
/// consistent with each other and with the vehicle.
/// </summary>
public sealed class ObdEmulation : IObdVirtualAdapter, IDisposable
{
    public const string AdapterId = "shiny-obd-emulator";

    readonly SimulatorState state;
    readonly Lock gate = new();
    ObdEmulatorState vehicle = new();
    DrivingScenarioPlayer player;
    IReadOnlyList<DrivingScenario> profileScenarios = [];
    int generation;

    public ObdEmulation(SimulatorState state)
    {
        this.state = state;
        this.player = this.NewPlayer(this.vehicle);
    }

    public string Id => AdapterId;

    public string Name => $"Shiny OBD emulator ({this.Vehicle.Vehicle.Name})";

    public bool IsAvailable => this.Profile is not null;

    /// <summary>The loaded profile's name — its file's, without the extension. Null when none is loaded.</summary>
    public string? Profile { get; private set; }

    /// <summary>The emulated vehicle: its identity, fault memory and every parameter.</summary>
    public ObdEmulatorState Vehicle
    {
        get
        {
            lock (this.gate)
                return this.vehicle;
        }
    }

    /// <summary>Plays a scenario into <see cref="Vehicle"/>.</summary>
    public DrivingScenarioPlayer Player
    {
        get
        {
            lock (this.gate)
                return this.player;
        }
    }

    /// <summary>The loaded profile's own scenarios, ahead of the emulator's built-in ones in <see cref="DrivingScenarioPlayer.Scenarios"/>.</summary>
    public IReadOnlyList<DrivingScenario> ProfileScenarios
    {
        get
        {
            lock (this.gate)
                return this.profileScenarios;
        }
    }

    /// <summary>Loaded, unloaded, or a scenario started or stopped. From any thread.</summary>
    public event Action? Changed;

    /// <summary>The drive moved the vehicle's live values — five times a second while driving. From the drive's thread.</summary>
    public event Action? Driven;

    /// <summary>Loads a profile file, replacing any vehicle already loaded.</summary>
    /// <exception cref="ObdException">The file is not a profile this emulator reads; nothing is changed.</exception>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var profile = await ObdEmulatorProfile.Load(stream, cancellationToken);
        this.Load(profile, Path.GetFileNameWithoutExtension(path).Replace(".obd", "", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads a profile on a fresh emulator: its vehicle (or the emulator's default one), its state on top, and its scenarios
    /// — the first of them chosen to play. A page still connected to the previous vehicle loses it and has to connect again,
    /// as it would on swapping adapters.
    /// </summary>
    /// <exception cref="ObdException">A parameter is not one the emulator has, or a value does not fit its kind; nothing is changed.</exception>
    public void Load(ObdEmulatorProfile profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var fresh = new ObdEmulatorState();
        var freshPlayer = this.NewPlayer(fresh);
        profile.ApplyTo(fresh, freshPlayer);

        var own = profile.Scenarios ?? [];
        if (own.Count > 0)
            freshPlayer.Scenario = freshPlayer.Scenarios.First(x => x.Name == own[0].Name);

        DrivingScenarioPlayer previous;
        lock (this.gate)
        {
            previous = this.player;
            this.vehicle = fresh;
            this.player = freshPlayer;
            this.profileScenarios = [.. own];
            this.Profile = name;
            this.generation++;
        }

        previous.Stop();
        this.state.Log($"obd: loaded {name} — {fresh.Vehicle.Name} ({fresh.Vehicle.Vin}), {own.Count} scenario{(own.Count == 1 ? "" : "s")}");
        this.Changed?.Invoke();
        this.state.OnChanged();
    }

    /// <summary>Forgets the vehicle: the <c>obd</c> bridge answers the values set on its routes again.</summary>
    public void Unload()
    {
        DrivingScenarioPlayer stopping;
        string? was;
        lock (this.gate)
        {
            stopping = this.player;
            was = this.Profile;
            this.Profile = null;
            this.profileScenarios = [];
            this.generation++;
        }

        stopping.Stop();
        if (was is not null)
            this.state.Log($"obd: unloaded {was}");

        this.Changed?.Invoke();
        this.state.OnChanged();
    }

    /// <summary>Starts driving a scenario — by name, or the one chosen — from its first step.</summary>
    /// <exception cref="ArgumentException">No vehicle is loaded, or no scenario has that name.</exception>
    public DrivingScenario Drive(string? scenario = null)
    {
        if (!this.IsAvailable)
            throw new ArgumentException("No OBD profile is loaded.");

        var playing = this.Player;
        var chosen = scenario is null
            ? playing.Scenario ?? playing.Scenarios[0]
            : playing.Scenarios.FirstOrDefault(x => String.Equals(x.Name, scenario, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"No OBD scenario is named '{scenario}'. There are: {String.Join(", ", playing.Scenarios.Select(x => x.Name))}.");

        playing.Stop();
        playing.Scenario = chosen;
        playing.Start();

        this.state.Log($"obd: driving {chosen.Name} ({chosen.Duration.TotalSeconds:0}s{(chosen.Loops ? ", looping" : "")})");
        this.Changed?.Invoke();
        return chosen;
    }

    /// <summary>Stops the drive. The vehicle keeps the values it had reached.</summary>
    public void StopDriving()
    {
        var playing = this.Player;
        if (!playing.IsRunning)
            return;

        playing.Stop();
        this.state.Log("obd: stopped driving");
        this.Changed?.Invoke();
    }

    public IObdTransport CreateTransport()
    {
        lock (this.gate)
            return new EmulatedTransport(this, new Elm327Responder(this.vehicle), this.generation);
    }

    public void Dispose() => this.Player.Stop();

    DrivingScenarioPlayer NewPlayer(ObdEmulatorState vehicle)
    {
        // Inline: the simulator has no UI thread for the emulator's bindings to be marshalled to.
        var created = new DrivingScenarioPlayer(vehicle, new SynchronizationContextDispatcher(null));
        created.PropertyChanged += (_, _) => this.Driven?.Invoke();
        return created;
    }

    bool IsCurrent(int of)
    {
        lock (this.gate)
            return this.Profile is not null && this.generation == of;
    }

    /// <summary>
    /// An ELM327 on the end of a function call rather than a socket: each command goes to the emulator's responder and comes
    /// back rendered as the wire would carry it, less the prompt — exactly what <c>WifiObdTransport</c> hands Shiny.Obd. It
    /// belongs to the vehicle loaded when it was made; loading another, or unloading, takes the adapter away.
    /// </summary>
    sealed class EmulatedTransport(ObdEmulation owner, Elm327Responder responder, int generation) : IObdTransport
    {
        readonly Elm327Session session = new("simulator");
        readonly SemaphoreSlim exchange = new(1, 1);
        volatile bool connected;

        public bool IsConnected => this.connected && owner.IsCurrent(generation);

        public Task Connect(CancellationToken ct = default)
        {
            if (!owner.IsCurrent(generation))
                throw new ObdException("The emulated vehicle is no longer loaded.");

            this.connected = true;
            return Task.CompletedTask;
        }

        public Task Disconnect()
        {
            this.connected = false;
            return Task.CompletedTask;
        }

        public async Task<string> Send(string command, CancellationToken ct = default)
        {
            if (!this.IsConnected)
                throw new ObdException("Not connected to OBD adapter");

            await this.exchange.WaitAsync(ct);
            try
            {
                // Framed as the emulator's TCP server frames a socket: a command ends at a carriage return.
                var replies = command
                    .Split('\r', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => this.session.Render(responder.Respond(this.session, x)));

                return String.Concat(replies).Replace(">", "").Trim();
            }
            finally
            {
                this.exchange.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            this.connected = false;
            return ValueTask.CompletedTask;
        }
    }
}
