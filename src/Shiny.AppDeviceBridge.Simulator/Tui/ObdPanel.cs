using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.Obd;
using Shiny.Obd.Emulator;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>OBD: load a Shiny.Obd emulator profile, drive its scenarios, and watch the vehicle the page is reading.</summary>
sealed class ObdPanel
{
    readonly SimulatorShell shell;
    readonly ListBox<Visual> list = new();
    readonly State<int> listVersion = new(0);
    int listVersions;
    IReadOnlyList<DrivingScenario> scenarios = [];

    public ObdPanel(SimulatorShell shell)
    {
        this.shell = shell;

        var toolbar = Ui.Toolbar(
            Ui.Action("Load profile…", this.Load),
            Ui.Danger("Unload", () => this.Obd.Unload())
        );

        var rows = new ComputedVisual(() =>
        {
            _ = this.listVersion.Value;
            return this.scenarios.Count > 0
                ? this.list
                : Ui.Empty("No vehicle. Load a Shiny.Obd emulator profile (obd-emulator.schema.json), or start with --obd file.");
        });

        var buttons = Ui.Toolbar(
            Ui.Primary("▶ Drive  F5", this.DriveSelected),
            Ui.Action("■ Stop", () => this.Obd.StopDriving())
        );

        var detail = new DockLayout(
            new VStack(Ui.Live(this.VehicleMarkup), buttons).Spacing(1),
            Ui.Panel("Live", new ScrollViewer(Ui.Live(this.LiveMarkup))),
            new VStack()
        );

        this.View = new HSplitter()
            .First(Ui.Panel("Scenarios", new DockLayout(toolbar, rows, new VStack())))
            .Second(new Padder(detail).Padding(new Thickness(1, 0, 1, 0)))
            .Ratio(0.36)
            .MinFirst(34)
            .MinSecond(50);

        // Enter on the list, or F5 anywhere in the tab, drives the selected scenario — or stops the drive.
        this.list.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.None), this.ToggleDrive);
        this.View.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), this.ToggleDrive);

        this.Sync();
    }

    public Visual View { get; }

    ObdEmulation Obd => this.shell.Host.Obd;

    DrivingScenario? Selected => this.list.SelectedIndex is >= 0 and var i && i < this.scenarios.Count ? this.scenarios[i] : null;

    /// <summary>Rebuilds the list when a profile was loaded or unloaded. On the render thread.</summary>
    public void Sync()
    {
        IReadOnlyList<DrivingScenario> current = this.Obd.IsAvailable ? [.. this.Obd.Player.Scenarios] : [];
        if (current.SequenceEqual(this.scenarios))
            return;

        this.scenarios = current;
        var own = this.Obd.ProfileScenarios.Select(x => x.Name).ToHashSet();

        this.list.Items.Clear();
        foreach (var scenario in current)
            this.list.Items.Add(Ui.Live(() => this.Row(scenario, own.Contains(scenario.Name))));

        var chosen = this.Obd.Player.Scenario;
        this.list.SelectedIndex = chosen is null ? 0 : Math.Max(0, current.ToList().IndexOf(chosen));
        this.listVersion.Value = ++this.listVersions;
    }

    void ToggleDrive()
    {
        if (this.Obd.Player.IsRunning)
            this.Obd.StopDriving();
        else
            this.DriveSelected();
    }

    void DriveSelected()
    {
        if (this.Selected is not { } scenario)
            return;

        try
        {
            this.Obd.Drive(scenario.Name);
        }
        catch (ArgumentException ex)
        {
            this.shell.Error(ex.Message);
        }
    }

    void Load()
        => this.shell.Prompt(
            "Load an OBD profile",
            "A Shiny.Obd emulator profile (.json): a vehicle, driving scenarios and fault state. The page's obd bridge finds it as an adapter.",
            Environment.CurrentDirectory + Path.DirectorySeparatorChar,
            path =>
            {
                // Read off the render thread; the toast comes back on it.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await this.Obd.LoadAsync(path);
                        this.shell.Post(() => this.shell.Success($"Loaded {this.Obd.Vehicle.Vehicle.Name}"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObdException or ArgumentException)
                    {
                        this.shell.Post(() => this.shell.Error(ex.Message));
                    }
                });
            });

    string Row(DrivingScenario scenario, bool fromProfile)
    {
        _ = this.shell.Revision;
        var player = this.Obd.Player;
        var icon = player.IsRunning && player.Scenario == scenario ? $"[{Ui.Accent}]▶[/]" : $"[{Ui.Muted}]■[/]";
        var source = fromProfile ? "" : $" [{Ui.Muted}]built-in[/]";
        var loop = scenario.Loops ? $" [{Ui.Muted}]↻[/]" : "";
        return $"{icon} {Ui.Escape(scenario.Name)} [{Ui.Muted}]{scenario.Duration:m\\:ss}[/]{loop}{source}";
    }

    string VehicleMarkup()
    {
        _ = this.shell.Revision;
        if (!this.Obd.IsAvailable)
            return $"[{Ui.Muted}]No OBD profile loaded — the obd bridge answers the values set on its routes in the Bridges tab.[/]";

        var vehicle = this.Obd.Vehicle;
        var codes = vehicle.StoredDtcs.Count + vehicle.PendingDtcs.Count == 0
            ? $"[{Ui.Green}]no trouble codes[/]"
            : $"[{Ui.Amber}]stored {Ui.Escape(String.Join(" ", vehicle.StoredDtcs))}  pending {Ui.Escape(String.Join(" ", vehicle.PendingDtcs))}[/]";

        return String.Join("\n",
            $"[bold]{Ui.Escape(vehicle.Vehicle.Name)}[/]  [{Ui.Muted}]{Ui.Escape(this.Obd.Profile)}[/]",
            $"[{Ui.Muted}]VIN[/] {Ui.Escape(vehicle.Vehicle.Vin)}  [{Ui.Muted}]adapter[/] {Ui.Escape(this.Obd.Id)}",
            $"{codes}{(vehicle.MilOn ? $"  [{Ui.Amber}]MIL on[/]" : "")}{(vehicle.EngineOff ? $"  [{Ui.Red}]engine off — NO DATA[/]" : "")}");
    }

    string LiveMarkup()
    {
        _ = this.shell.Revision;
        var player = this.Obd.Player;
        if (!this.Obd.IsAvailable || !player.IsRunning)
            return $"[{Ui.Muted}]Not driving. Pick a scenario and press F5.[/]";

        return String.Join("\n",
            $"[{Ui.Accent}]▶[/] [bold]{Ui.Escape(player.Scenario?.Name)}[/]  {Ui.Escape(player.StepLabel)}  [{Ui.Muted}]{Ui.Escape(player.ElapsedLabel)}{(player.Laps > 0 ? $", lap {player.Laps + 1}" : "")}[/]",
            "",
            $"[{Ui.Muted}]speed[/]     {player.SpeedKph,6:0} km/h    [{Ui.Muted}]gear[/] {Ui.Escape(player.GearLabel)}",
            $"[{Ui.Muted}]rpm[/]       {player.Rpm,6:0}",
            $"[{Ui.Muted}]throttle[/]  {player.ThrottlePercent,6:0} %       [{Ui.Muted}]load[/] {player.LoadPercent:0} %",
            $"[{Ui.Muted}]coolant[/]   {player.CoolantTemperature,6:0} °C",
            $"[{Ui.Muted}]MAF[/]       {player.MassAirFlow,6:0.0} g/s     [{Ui.Muted}]fuel[/] {player.FuelRate:0.0} L/h");
    }
}
