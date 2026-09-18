using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Scenarios;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>
/// The simulator's terminal UI: bridges to set up, traffic to watch, trails to play, and what happened.
/// <para>
/// Everything the UI shows lives on the render thread. The simulator changes from the server's threads and the trails', so
/// those only ever bump a revision through <see cref="Post"/>; views read the simulator when that revision moves.
/// </para>
/// </summary>
sealed class SimulatorShell
{
    readonly State<int> revision = new(0);
    readonly State<int> trafficRevision = new(0);
    readonly State<int> tab = new(0);
    int revisions;
    int trafficRevisions;
    int queued;
    int trafficQueued;
    TerminalApp? app;
    ToastHost toasts = null!;
    WindowLayer windows = null!;

    public SimulatorShell(SimulatorHost host)
    {
        this.Host = host;
        this.Bridges = new BridgesPanel(this);
        this.Traffic = new TrafficPanel(this);
        this.Trails = new TrailsPanel(this);
        this.Activity = new ActivityPanel();
    }

    public SimulatorHost Host { get; }

    public SimulatorState State => this.Host.State;

    /// <summary>Moves whenever the simulator changes. Read it in a computed visual to follow the simulator.</summary>
    public int Revision => this.revision.Value;

    /// <summary>Moves whenever the traffic recorder changes.</summary>
    public int TrafficRevision => this.trafficRevision.Value;

    public BridgesPanel Bridges { get; }

    public TrafficPanel Traffic { get; }

    public TrailsPanel Trails { get; }

    public ActivityPanel Activity { get; }

    public State<bool> ExitRequested { get; } = new(false);

    /// <summary>Bridges, Traffic, Trails, Activity: 0 to 3.</summary>
    public void SelectTab(int index) => this.tab.Value = Math.Clamp(index, 0, 3);

    public Visual Build()
    {
        var tabs = new TabControl(
            new TabPage(new TextBlock(" Bridges "), this.Bridges.View),
            new TabPage(new TextBlock(" Traffic "), this.Traffic.View),
            new TabPage(new TextBlock(" Trails "), this.Trails.View),
            new TabPage(new TextBlock(" Activity "), this.Activity.View)
        );
        tabs.SelectedIndex(this.tab.Bind.Value);

        var header = new HStack(
            Ui.Markup($"[bold {Ui.Accent}]◆ Shiny.AppDeviceBridge[/] [bold]Simulator[/]"),
            Ui.Live(() => this.HeaderMarkup())
        ).Spacing(2);

        var status = new StatusBar(Ui.Live(() => this.StatusMarkup()), Ui.Live(() => this.HintMarkup()));

        this.toasts = new ToastHost(new DockLayout(new Padder(header).Padding(new Thickness(1, 0, 1, 0)), tabs, status)).Position(ToastPosition.BottomRight);
        this.windows = new WindowLayer(this.toasts);
        this.AddCommands(this.windows);

        this.State.Changed += this.Invalidate;
        this.Host.Traffic.Changed += (_, _) => this.InvalidateTraffic();
        this.Host.Trails.Changed += this.Invalidate;
        this.State.Logged += record => this.Post(() => this.Activity.Append(record));

        foreach (var record in this.State.Activity.Reverse())
            this.Activity.Append(record);

        return this.windows;
    }

    /// <summary>Called on the first tick of the loop, once there is an app to post to.</summary>
    public void Attach(TerminalApp terminal)
    {
        this.app = terminal;
        this.Bridges.Focus();
    }

    /// <summary>
    /// Each turn of the loop, on the render thread. Trees and lists do not notify when their selection moves, so the panels
    /// look for themselves.
    /// </summary>
    public void Tick()
    {
        this.Bridges.Tick();
        this.Traffic.Tick();
        this.Trails.Tick();
    }

    /// <summary>Runs <paramref name="action"/> on the render thread; inline before the loop starts, as in tests.</summary>
    public void Post(Action action)
    {
        if (this.app is null)
            action();
        else
            this.app.Post(action);
    }

    public void Focus(Visual visual) => this.app?.Focus(visual);

    public void Info(string message) => this.toasts.Show(new Markup(Ui.Escape(message)), ToastSeverity.Info);

    public void Success(string message) => this.toasts.Show(new Markup(Ui.Escape(message)), ToastSeverity.Success);

    public void Error(string message) => this.toasts.Show(new Markup(Ui.Escape(message)), ToastSeverity.Error);

    public void OpenDialog(Visual dialog) => this.windows.AddWindow(dialog);

    public void CloseDialog(Visual dialog) => this.windows.RemoveWindow(dialog);

    /// <summary>A one-line question; <paramref name="accept"/> gets the answer.</summary>
    public void Prompt(string title, string label, string initial, Action<string> accept)
    {
        var value = new State<string>(initial);
        Dialog? dialog = null;

        void Accept()
        {
            this.CloseDialog(dialog!);
            accept(value.Value.Trim());
        }

        var input = new TextBox(value.Bind.Value!).AutoFocus(true).MinWidth(64);
        input.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.None), Accept);

        var body = new VStack(
            Ui.Dim(label),
            input,
            new HStack(Ui.Action("Cancel", () => this.CloseDialog(dialog!)), Ui.Primary("OK", Accept)).Spacing(1).HorizontalAlignment(Align.End)
        ).Spacing(1);

        dialog = new Dialog(Ui.Markup($"[bold]{Ui.Escape(title)}[/]"), body).Padding(new Thickness(1)).IsModal(true).IsDraggable(true);
        dialog.AddKeyBinding(new KeyGesture(TerminalKey.Escape, TerminalModifiers.None), () => this.CloseDialog(dialog));
        this.OpenDialog(dialog);
    }

    /// <summary>Saves everything that differs from a fresh simulator, and every trail, to a scenario file.</summary>
    public void SaveScenario()
        => this.Prompt(
            "Save scenario",
            "Everything set here, and every trail, to a file you can start the simulator with (--scenario).",
            this.Host.Options.Scenario ?? Path.Combine(Environment.CurrentDirectory, "simulator.scenario.json"),
            path =>
            {
                try
                {
                    Scenario.Capture(this.State, this.Host.Trails.Trails).Save(path);
                    this.State.Log($"saved scenario {path}");
                    this.Success($"Saved {Path.GetFileName(path)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    this.Error(ex.Message);
                }
            });

    public void LoadScenario()
        => this.Prompt(
            "Apply scenario",
            "A scenario file saved from here or written by hand. Its trails are added to the Trails tab.",
            this.Host.Options.Scenario ?? Path.Combine(Environment.CurrentDirectory, "simulator.scenario.json"),
            path =>
            {
                try
                {
                    var scenario = Scenario.Load(path);
                    var problems = scenario.Apply(this.State);
                    foreach (var trail in scenario.Trails)
                        this.Host.Trails.Add(trail);

                    foreach (var problem in problems)
                        this.State.Log($"scenario: {problem}");

                    this.Bridges.Reload();

                    if (problems.Count == 0)
                        this.Success($"Applied {Path.GetFileName(path)}");
                    else
                        this.Error($"Applied {Path.GetFileName(path)} with {problems.Count} problem(s) — see Activity.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    this.Error(ex.Message);
                }
            });

    void Invalidate()
    {
        // A busy page or a fast trail changes the simulator far faster than a screen is drawn; one bump per turn is plenty.
        if (Interlocked.Exchange(ref this.queued, 1) == 1)
            return;

        this.Post(() =>
        {
            Volatile.Write(ref this.queued, 0);
            this.revision.Value = ++this.revisions;
            this.Bridges.Sync();
            this.Trails.Sync();
        });
    }

    void InvalidateTraffic()
    {
        if (Interlocked.Exchange(ref this.trafficQueued, 1) == 1)
            return;

        this.Post(() =>
        {
            Volatile.Write(ref this.trafficQueued, 0);
            this.trafficRevision.Value = ++this.trafficRevisions;
            this.Traffic.Refresh();
        });
    }

    string HeaderMarkup()
    {
        _ = this.revision.Value;
        var origin = this.Host.Origin?.AbsoluteUri ?? "not listening";
        return $"[{Ui.Muted}]serving[/] [underline {Ui.Accent}]{Ui.Escape(origin)}[/]  [{Ui.Muted}]as[/] [bold {Ui.Violet}]{Ui.Escape(this.State.Platform)}[/]  [{Ui.Muted}]app[/] {Ui.Escape(this.State.AppId)}";
    }

    string StatusMarkup()
    {
        _ = this.revision.Value;
        _ = this.trafficRevision.Value;

        var streams = this.Host.Server.Events.StreamCount;
        var playing = this.Host.Trails.Players.Count(x => x.Playback == Simulator.Trails.TrailPlayback.Playing);
        var requests = this.Host.Traffic.Snapshot().Count;
        var unsupported = this.State.Bridges.Count(x => !x.IsSupported);

        var parts = new List<string>
        {
            streams == 0 ? $"[{Ui.Muted}]○ no page listening[/]" : $"[{Ui.Green}]● {streams} event stream{(streams == 1 ? "" : "s")}[/]",
            $"{requests} request{(requests == 1 ? "" : "s")}"
        };

        if (playing > 0)
            parts.Add($"[{Ui.Accent}]▶ {playing} trail{(playing == 1 ? "" : "s")} playing[/]");

        if (this.Trails.IsRecording)
            parts.Add($"[bold {Ui.Red}]● recording[/]");

        if (unsupported > 0)
            parts.Add($"[{Ui.Amber}]{unsupported} bridge{(unsupported == 1 ? "" : "s")} off[/]");

        return String.Join($"  [{Ui.Muted}]·[/]  ", parts);
    }

    // Short, so the status on the left always fits; F1 lists every key.
    string HintMarkup() => $"[{Ui.Muted}]F5 apply · Alt+1-4 tabs · F1 keys · Ctrl+Q quit[/]";

    void AddCommands(Visual root)
    {
        root.AddCommand(new Command
        {
            Id = "sim.quit",
            Name = "Quit",
            LabelMarkup = "Quit",
            Gesture = new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl),
            Execute = _ => this.ExitRequested.Value = true
        });

        root.AddCommand(new Command
        {
            Id = "sim.save",
            Name = "Save scenario",
            LabelMarkup = "Save scenario",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Execute = _ => this.SaveScenario()
        });

        root.AddCommand(new Command
        {
            Id = "sim.open",
            Name = "Apply scenario",
            LabelMarkup = "Apply scenario",
            Gesture = new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl),
            Execute = _ => this.LoadScenario()
        });

        root.AddCommand(new Command
        {
            Id = "sim.trail",
            Name = "Load a trail",
            LabelMarkup = "Load a trail",
            Gesture = new KeyGesture(TerminalChar.CtrlL, TerminalModifiers.Ctrl),
            Execute = _ =>
            {
                this.SelectTab(2);
                this.Trails.Load();
            }
        });

        root.AddCommand(new Command
        {
            Id = "sim.record",
            Name = "Record a trail",
            LabelMarkup = "Record a trail",
            Gesture = new KeyGesture(TerminalChar.CtrlR, TerminalModifiers.Ctrl),
            Execute = _ => this.Trails.ToggleRecording()
        });

        root.AddCommand(new Command
        {
            Id = "sim.help",
            Name = "Help",
            LabelMarkup = "Help",
            Gesture = new KeyGesture(TerminalKey.F1, TerminalModifiers.None),
            Execute = _ => this.ShowHelp()
        });

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            root.AddCommand(new Command
            {
                Id = $"sim.tab{index + 1}",
                Name = $"Tab {index + 1}",
                LabelMarkup = $"Tab {index + 1}",
                Gesture = new KeyGesture((char)('1' + index), TerminalModifiers.Alt),
                Execute = _ => this.SelectTab(index)
            });
        }
    }

    void ShowHelp()
    {
        Dialog? dialog = null;
        var text = $"""
            [bold {Ui.Accent}]Bridges[/]  Pick a route and set what it answers: a value (200), null (204) or an error — with an
                     optional delay. Values are checked against the route's contract before they are used.
                     Pick an event and fire it at the page. Switch a whole bridge off to answer 501 everywhere.
                     Enter on the tree jumps into the editor, Esc comes back; F5 applies a route or fires an event.

            [bold {Ui.Accent}]Traffic[/]  Every request the page made and what it got back. Filter by path, method or status.

            [bold {Ui.Accent}]Trails[/]   Timed scripts: Ctrl+L loads a .gpx walk or a .trail.json. Play several at once, change
                     the speed, loop. Ctrl+R records what you do in the Bridges tab as a new trail.

            [bold {Ui.Accent}]Values[/]   "$now" becomes the current time when sent; "$now-5m", "$now+2h", "$now+1d" offset it.
                     "$uuid" becomes a new GUID.

            [bold {Ui.Accent}]Files[/]    Ctrl+S saves a scenario — everything set here plus the trails — and Ctrl+O applies one.
                     Start with it again: shiny-bridge-sim --scenario file.json

            [bold {Ui.Accent}]Keys[/]     Enter/Esc into and out of an editor · F5 apply, fire, play · Alt+1-4 tabs · Ctrl+L load trail
                     Ctrl+R record · Ctrl+S save scenario · Ctrl+O apply scenario · F1 help · Ctrl+Q quit
            """;

        var body = new VStack(new Markup(text).Wrap(true), new HStack(Ui.Primary("Close", () => this.CloseDialog(dialog!))).HorizontalAlignment(Align.End)).Spacing(1);
        dialog = new Dialog(new Markup("[bold]Shiny.AppDeviceBridge simulator[/]"), body).Padding(new Thickness(1)).IsModal(true).IsDraggable(true);
        dialog.AddKeyBinding(new KeyGesture(TerminalKey.Escape, TerminalModifiers.None), () => this.CloseDialog(dialog));
        this.OpenDialog(dialog);
    }
}
