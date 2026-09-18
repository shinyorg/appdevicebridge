using Shiny.AppDeviceBridge.Simulator.Simulation;
using Shiny.AppDeviceBridge.Simulator.Trails;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>Trails: load a GPX walk or a trail file, record one, and play any number of them at once.</summary>
sealed class TrailsPanel
{
    readonly SimulatorShell shell;
    readonly ListBox<Visual> list = new();
    readonly State<int> libraryVersion = new(0);
    readonly State<int> selectedIndex = new(-1);
    int libraryVersions;
    IReadOnlyList<TrailPlayer> players = [];
    TrailRecording? recording;

    public TrailsPanel(SimulatorShell shell)
    {
        this.shell = shell;

        var toolbar = Ui.Toolbar(
            Ui.Action("Load .gpx / .trail.json  Ctrl+L", this.Load),
            new Button(Ui.Live(() =>
            {
                _ = this.shell.Revision;
                return this.recording is null ? "● Record  Ctrl+R" : $"[bold {Ui.Red}]■ Stop recording ({this.recording.Count})[/]";
            })).Click(this.ToggleRecording)
        );

        var rows = new ComputedVisual(() =>
        {
            _ = this.libraryVersion.Value;
            return this.players.Count > 0 ? this.list : Ui.Empty("No trails yet. Load a .gpx walk or a .trail.json, or record one.");
        });

        var detail = new ComputedVisual(() =>
        {
            _ = this.libraryVersion.Value;
            var index = this.selectedIndex.Value;
            return index >= 0 && index < this.players.Count ? this.Detail(this.players[index]) : Ui.Empty("Pick a trail.");
        });

        this.View = new HSplitter()
            .First(Ui.Panel("Trails", new DockLayout(toolbar, rows, new VStack())))
            .Second(new Padder(detail).Padding(new Thickness(1, 0, 1, 0)))
            .Ratio(0.36)
            .MinFirst(34)
            .MinSecond(50);

        // Enter on the list, or F5 anywhere in the tab, plays the selected trail — or stops it if it is playing.
        this.list.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.None), this.TogglePlay);
        this.View.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), this.TogglePlay);

        this.Sync();
    }

    public Visual View { get; }

    public bool IsRecording => this.recording is not null;

    TrailPlayer? Selected => this.list.SelectedIndex is >= 0 and var i && i < this.players.Count ? this.players[i] : null;

    void TogglePlay()
    {
        if (this.Selected is not { } player)
            return;

        if (player.Playback is TrailPlayback.Playing or TrailPlayback.Paused)
            player.Stop();
        else
            _ = player.Play();
    }

    /// <summary>Rebuilds the list when trails were added or removed. On the render thread.</summary>
    public void Sync()
    {
        var current = this.shell.Host.Trails.Players;
        if (current.SequenceEqual(this.players))
            return;

        var selected = this.list.SelectedIndex is >= 0 and var i && i < this.players.Count ? this.players[i] : null;
        this.players = current;

        this.list.Items.Clear();
        foreach (var player in current)
            this.list.Items.Add(Ui.Live(() => Row(player, this.shell.Revision)));

        var index = selected is null ? -1 : current.ToList().IndexOf(selected);
        this.list.SelectedIndex = index >= 0 ? index : current.Count - 1;
        this.selectedIndex.Value = this.list.SelectedIndex;
        this.libraryVersion.Value = ++this.libraryVersions;
    }

    /// <summary>Called on the render thread each turn of the loop: the detail follows the list's selection.</summary>
    public void Tick()
    {
        if (this.list.SelectedIndex != this.selectedIndex.Value)
            this.selectedIndex.Value = this.list.SelectedIndex;
    }

    public void ToggleRecording()
    {
        if (this.recording is { } active)
        {
            this.recording = null;
            var trail = active.Stop();

            if (trail.Steps.Count == 0)
            {
                this.shell.Info("Nothing was recorded.");
                return;
            }

            this.shell.Host.Trails.Add(trail);
            this.shell.Success($"Recorded '{trail.Name}': {trail.Steps.Count} steps");
            return;
        }

        this.shell.Prompt(
            "Record a trail",
            "Set values, fire events and switch bridges in the Bridges tab; each is recorded with the time since the last. Ctrl+R again stops.",
            $"recording {DateTime.Now:HH.mm.ss}",
            name =>
            {
                this.recording = new TrailRecording(this.shell.State, name.Length == 0 ? "recording" : name);
                this.shell.Info("Recording — Ctrl+R stops.");
            });
    }

    public void Load()
        => this.shell.Prompt(
            "Load a trail",
            "A .gpx file (a track, route or waypoints — played as gps.reading events at its recorded pace) or a .trail.json.",
            Environment.CurrentDirectory + Path.DirectorySeparatorChar,
            path =>
            {
                try
                {
                    var player = this.shell.Host.Trails.Load(path);
                    this.shell.Success($"Loaded '{player.Trail.Name}': {player.Trail.Steps.Count} steps");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Xml.XmlException or InvalidDataException or FormatException or ArgumentException)
                {
                    this.shell.Error(ex.Message);
                }
            });

    Visual Detail(TrailPlayer player)
    {
        var trail = player.Trail;
        var speed = new State<double>(player.Speed);
        var loop = new State<bool>(player.Loop);

        var speedBox = new NumberBox<double>(speed.Bind.Value).MinWidth(8);
        var loopBox = new CheckBox(new TextBlock("Loop"), loop.Bind.Value).ValueChanged(() => player.Loop = loop.Value);

        void ApplySpeed()
        {
            if (speed.Value > 0)
                player.Speed = speed.Value;
        }

        var buttons = Ui.Toolbar(
            Ui.Primary("▶ Play  F5", () =>
            {
                ApplySpeed();
                _ = player.Play();
            }),
            Ui.Action("⏸ Pause", player.Pause),
            Ui.Action("⏵ Resume", () =>
            {
                ApplySpeed();
                player.Resume();
            }),
            Ui.Action("■ Stop", player.Stop),
            Ui.Action("Save…", () => this.Save(trail)),
            Ui.Danger("Remove", () => this.shell.Host.Trails.Remove(player))
        );

        var steps = Ui.Live(() =>
        {
            _ = this.shell.Revision;
            var lines = new List<string>();
            var elapsed = 0L;

            for (var i = 0; i < trail.Steps.Count; i++)
            {
                var step = trail.Steps[i];
                elapsed += Math.Max(0, step.DelayMs);
                var current = i == player.Position && player.Playback is TrailPlayback.Playing or TrailPlayback.Paused;
                var marker = current ? $"[bold {Ui.Accent}]▶[/]" : " ";
                var text = current ? $"[bold]{Ui.Escape(step.Describe())}[/]" : Ui.Escape(step.Describe());
                lines.Add($"{marker} [{Ui.Muted}]{TimeSpan.FromMilliseconds(elapsed):mm\\:ss\\.f}[/]  {text}");
            }

            return lines.Count == 0 ? $"[{Ui.Muted}]This trail has no steps.[/]" : String.Join("\n", lines);
        });

        return new DockLayout(
            new VStack(
                Ui.Markup($"[bold]{Ui.Escape(trail.Name)}[/]  [{Ui.Muted}]{trail.Steps.Count} steps · {FormatDuration(trail.Duration)} at 1×[/]"),
                Ui.Live(() => Status(player, this.shell.Revision)),
                new HStack(Ui.Field("Speed (×)", speedBox), loopBox).Spacing(3),
                buttons
            ).Spacing(1),
            Ui.Panel("Steps", new ScrollViewer(steps)),
            new VStack()
        );
    }

    void Save(Trail trail)
        => this.shell.Prompt(
            "Save trail",
            "As a .trail.json, to load again or start with: shiny-bridge-sim --trail file --play name",
            Path.Combine(Environment.CurrentDirectory, $"{String.Join("-", trail.Name.Split(Path.GetInvalidFileNameChars()))}.trail.json"),
            path =>
            {
                try
                {
                    TrailLibrary.WriteFile(trail, path);
                    this.shell.Success($"Saved {Path.GetFileName(path)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    this.shell.Error(ex.Message);
                }
            });

    static string Row(TrailPlayer player, int revision)
    {
        _ = revision;
        var icon = player.Playback switch
        {
            TrailPlayback.Playing => $"[{Ui.Accent}]▶[/]",
            TrailPlayback.Paused => $"[{Ui.Amber}]⏸[/]",
            TrailPlayback.Finished => $"[{Ui.Green}]✓[/]",
            _ => $"[{Ui.Muted}]■[/]"
        };

        var progress = player.Playback is TrailPlayback.Playing or TrailPlayback.Paused
            ? $" [{Ui.Muted}]{player.Position + 1}/{player.Trail.Steps.Count}[/]"
            : $" [{Ui.Muted}]{player.Trail.Steps.Count} steps[/]";

        var loop = player.Loop ? $" [{Ui.Muted}]↻[/]" : "";
        return $"{icon} {Ui.Escape(player.Trail.Name)}{progress}{loop}";
    }

    static string Status(TrailPlayer player, int revision)
    {
        _ = revision;
        return player.Playback switch
        {
            TrailPlayback.Playing => $"[{Ui.Accent}]▶ playing[/] step {player.Position + 1} of {player.Trail.Steps.Count} at {player.Speed:0.##}×{(player.Passes > 0 ? $", pass {player.Passes + 1}" : "")}",
            TrailPlayback.Paused => $"[{Ui.Amber}]⏸ paused[/] at step {player.Position + 1} of {player.Trail.Steps.Count}",
            TrailPlayback.Finished => $"[{Ui.Green}]✓ finished[/]",
            _ => $"[{Ui.Muted}]stopped[/]"
        };
    }

    static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}

/// <summary>What happened, newest last: events fired, values changed, trails played, the page's calls that changed state.</summary>
sealed class ActivityPanel
{
    readonly LogControl log = new();

    public ActivityPanel()
    {
        this.log.MaxCapacity(2000).FollowTail(true).WrapText(true);
        this.View = Ui.Panel("Activity", this.log);
    }

    public Visual View { get; }

    public void Append(ActivityRecord record)
    {
        // A string, not an interpolation, for the reason Ui.Markup gives.
        string line = $"[{Ui.Muted}]{record.At:HH:mm:ss.fff}[/]  {Ui.Escape(record.Text)}";
        this.log.AppendMarkupLine(line);
    }
}
