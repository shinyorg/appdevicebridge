using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>What one route answers: a value, null, or an error — after an optional delay.</summary>
sealed class RouteEditor
{
    static readonly string[] Modes = ["Value (200)", "Null (204)", "Error"];

    readonly SimulatorShell shell;
    readonly RouteState route;
    RouteBehavior loaded;
    readonly State<int> mode;
    readonly State<string> json;
    readonly State<int> delay;
    readonly State<int> status;
    readonly State<string> code;
    readonly State<string> message;
    readonly State<string> file;
    readonly State<string> feedback = new("");

    public RouteEditor(SimulatorShell shell, RouteState route)
    {
        this.shell = shell;
        this.route = route;
        this.loaded = route.Behavior;

        this.mode = new State<int>((int)this.loaded.Mode);
        this.json = new State<string>(Ui.Pretty(this.loaded.Json));
        this.delay = new State<int>(this.loaded.DelayMs);
        this.status = new State<int>(this.loaded.StatusCode);
        this.code = new State<string>(this.loaded.ErrorCode);
        this.message = new State<string>(this.loaded.ErrorMessage);
        this.file = new State<string>(this.loaded.FilePath ?? "");

        this.View = this.Build();
    }

    public Visual View { get; }

    SimRoute Route => this.route.Route;

    Visual Build()
    {
        var modes = new Select<string>(this.Route.Kind == ResponseKind.Empty ? ["Success (204)", "Null (204)", "Error"] : Modes);
        modes.BindSelectedIndex(this.mode.Bind.Value);

        var presets = new Select<string>([.. RouteBehavior.ErrorPresets.Select(x => $"{x.Status} {x.Code}")], -1);
        presets.SelectionChanged(() =>
        {
            if (presets.SelectedIndex is >= 0 and var i && i < RouteBehavior.ErrorPresets.Count)
            {
                var (s, c, m) = RouteBehavior.ErrorPresets[i];
                this.status.Value = s;
                this.code.Value = c;
                this.message.Value = m;
            }
        });

        var editor = new CodeEditor(this.json.Bind.Value!).ShowLineNumbers(true).MinHeight(8).Stretch();
        editor.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), this.Apply);

        var error = new VStack(
            Ui.Field("Preset", presets.MinWidth(24)),
            Ui.Field("Status", new NumberBox<int>(this.status.Bind.Value).MinWidth(8)),
            Ui.Field("Code", new TextBox(this.code.Bind.Value!).MinWidth(32)),
            Ui.Field("Message", new TextBox(this.message.Bind.Value!).MinWidth(48))
        ).Spacing(0).IsVisible(() => this.mode.Value == (int)ResponseMode.Error);

        var value = this.Route.Kind switch
        {
            ResponseKind.Json => (Visual)editor,
            ResponseKind.Binary => new VStack(
                Ui.Field("File", new TextBox(this.file.Bind.Value!).MinWidth(56)),
                Ui.Dim("A path to the bytes to send — a photo, a snapshot. Empty sends a small placeholder PNG.")
            ).Spacing(0),
            _ => Ui.Dim("This route returns nothing: success is a 204.")
        };

        var header = new VStack(
            Ui.Markup($"{Ui.Method(this.Route.Method)}[bold]/_bridge/{Ui.Escape(this.Route.Path)}[/]"),
            new Markup(this.Signature()).Wrap(true),
            Ui.Live(() => this.LiveLine())
        ).Spacing(0);

        var buttons = Ui.Toolbar(
            Ui.Primary("Apply  F5", this.Apply),
            Ui.Action("Format", () => this.json.Value = Ui.Pretty(this.json.Value)),
            Ui.Action("Reset to sample", this.Reset),
            Ui.Action("Load current", this.Load).IsVisible(() => this.IsStale())
        );

        var view = new DockLayout(
            new VStack(
                header,
                new HStack(Ui.Field("Answer", modes.MinWidth(16)), Ui.Field("Delay (ms)", new NumberBox<int>(this.delay.Bind.Value).MinWidth(8))).Spacing(3),
                error
            ).Spacing(1),
            value.IsVisible(() => this.mode.Value == (int)ResponseMode.Value),
            new VStack(buttons, Ui.Live(() => this.feedback.Value).Wrap(true)).Spacing(0)
        ).Stretch();

        // F5 anywhere in the editor. Ctrl+Enter too, where the terminal tells it apart from Enter — most do not.
        view.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), this.Apply);
        return view;
    }

    string Signature()
    {
        var parts = new List<string> { $"[{Ui.Muted}]{Ui.Escape(this.Route.Operation)}[/]" };

        if (this.Route.ResultType is { } result)
            parts.Add($"[{Ui.Muted}]→[/] {Ui.Escape(result.Name)}");
        else if (this.Route.Kind == ResponseKind.Binary)
            parts.Add($"[{Ui.Muted}]→[/] bytes");

        if (this.Route.BodyType is { } body)
            parts.Add($"[{Ui.Muted}]body[/] {Ui.Escape(body.Name)}");

        if (this.Route.QueryParameters.Count > 0)
            parts.Add($"[{Ui.Muted}]query[/] {Ui.Escape(String.Join(", ", this.Route.QueryParameters))}");

        return String.Join("  ", parts);
    }

    string LiveLine()
    {
        _ = this.shell.Revision;
        var line = $"[{Ui.Muted}]called ×{this.route.Hits}[/]";

        if (this.IsStale())
            line += $"  [{Ui.Amber}]changed elsewhere since this was loaded — Load current shows it[/]";

        return line;
    }

    /// <summary>A trail, a scenario or the page's own write changed the route while this editor was open.</summary>
    bool IsStale()
    {
        _ = this.shell.Revision;
        return !ReferenceEquals(this.route.Behavior, this.loaded);
    }

    void Load()
    {
        var current = this.route.Behavior;
        this.loaded = current;
        this.mode.Value = (int)current.Mode;
        this.json.Value = Ui.Pretty(current.Json);
        this.delay.Value = current.DelayMs;
        this.status.Value = current.StatusCode;
        this.code.Value = current.ErrorCode;
        this.message.Value = current.ErrorMessage;
        this.file.Value = current.FilePath ?? "";
    }

    void Apply()
    {
        var mode = (ResponseMode)Math.Clamp(this.mode.Value, 0, 2);
        var behavior = new RouteBehavior(
            mode,
            this.Route.Kind == ResponseKind.Json ? this.json.Value : "",
            this.status.Value,
            this.code.Value.Trim(),
            this.message.Value,
            Math.Max(0, this.delay.Value),
            this.file.Value.Trim() is { Length: > 0 } path ? Path.GetFullPath(path) : null
        );

        try
        {
            this.shell.State.SetBehavior(this.Route.Bridge, this.Route.Key, behavior);
            this.loaded = this.route.Behavior;
            this.feedback.Value = $"[{Ui.Green}]✓ applied[/] [{Ui.Muted}]{DateTime.Now:HH:mm:ss}[/]";
        }
        catch (ArgumentException ex)
        {
            this.feedback.Value = $"[{Ui.Red}]✗ {Ui.Escape(ex.Message)}[/]";
        }
    }

    void Reset()
    {
        this.shell.State.ResetRoute(this.Route.Bridge, this.Route.Key);
        this.Load();
        this.feedback.Value = $"[{Ui.Green}]✓ reset to the sample[/]";
    }
}

/// <summary>An event's payload, and firing it at the page.</summary>
sealed class EventEditor
{
    readonly SimulatorShell shell;
    readonly EventState evt;
    readonly State<string> json;
    readonly State<string> feedback = new("");

    public EventEditor(SimulatorShell shell, EventState evt)
    {
        this.shell = shell;
        this.evt = evt;
        this.json = new State<string>(Ui.Pretty(evt.Json));
        this.View = this.Build();
    }

    public Visual View { get; }

    Visual Build()
    {
        var editor = new CodeEditor(this.json.Bind.Value!).ShowLineNumbers(true).MinHeight(8).Stretch();
        editor.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), this.Fire);

        var header = new VStack(
            Ui.Markup($"[bold {Ui.Violet}]⚡ {Ui.Escape(this.evt.Event.Name)}[/]  [{Ui.Muted}]{Ui.Escape(this.evt.Event.Operation)} →[/] {Ui.Escape(this.evt.Event.PayloadType.Name)}"),
            Ui.Live(() =>
            {
                _ = this.shell.Revision;
                var listeners = this.evt.Listeners;
                var who = listeners == 0
                    ? $"[{Ui.Amber}]no page stream is listening — a fired event goes nowhere until the page subscribes[/]"
                    : $"[{Ui.Green}]{listeners} page stream{(listeners == 1 ? " is" : "s are")} listening[/]";
                return $"{who}  [{Ui.Muted}]fired ×{this.evt.Fired}[/]";
            }).Wrap(true)
        ).Spacing(0);

        var buttons = Ui.Toolbar(
            Ui.Primary("Fire  F5", this.Fire),
            Ui.Action("Save as default", this.Save),
            Ui.Action("Format", () => this.json.Value = Ui.Pretty(this.json.Value)),
            Ui.Action("Reset to sample", () => this.json.Value = Ui.Pretty(this.evt.DefaultJson))
        );

        var view = new DockLayout(
            header,
            editor,
            new VStack(
                buttons,
                Ui.Live(() => this.feedback.Value).Wrap(true),
                Ui.Dim("\"$now\" is sent as the current time (\"$now-5m\", \"$now+1h\" offset it); \"$uuid\" as a new GUID.")
            ).Spacing(0)
        ).Stretch();

        view.AddKeyBinding(new KeyGesture(TerminalKey.F5, TerminalModifiers.None), this.Fire);
        return view;
    }

    void Fire()
    {
        try
        {
            var reached = this.shell.State.Fire(this.evt.Event.Name, this.json.Value);
            this.feedback.Value = reached == 0
                ? $"[{Ui.Amber}]fired, but no page stream was listening[/]"
                : $"[{Ui.Green}]✓ fired to {reached} stream{(reached == 1 ? "" : "s")}[/] [{Ui.Muted}]{DateTime.Now:HH:mm:ss}[/]";
        }
        catch (ArgumentException ex)
        {
            this.feedback.Value = $"[{Ui.Red}]✗ {Ui.Escape(ex.Message)}[/]";
        }
    }

    void Save()
    {
        try
        {
            this.shell.State.SetEventPayload(this.evt.Event.Name, this.json.Value);
            this.feedback.Value = $"[{Ui.Green}]✓ saved — trails and Fire without a payload send this[/]";
        }
        catch (ArgumentException ex)
        {
            this.feedback.Value = $"[{Ui.Red}]✗ {Ui.Escape(ex.Message)}[/]";
        }
    }
}
