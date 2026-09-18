using System.Text;
using Shiny.AppDeviceBridge.Simulator.Catalog;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Simulation;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>
/// The bridges as a tree — the host, then each bridge with its routes and events — and, beside it, an editor for whatever
/// is selected: what a route answers, what an event carries, whether a bridge exists at all.
/// </summary>
sealed class BridgesPanel
{
    /// <summary>The tree's first node: the host itself — the platform the page is told it is on.</summary>
    sealed record HostTarget;

    readonly SimulatorShell shell;
    readonly TreeView tree = new();
    readonly State<int> editorVersion = new(0);
    readonly ComputedVisual detail;

    // A tree's selection is not tracked the way a State is, so Tick copies it here, where the editor follows it.
    readonly State<object?> selection = new(null);
    int editorVersions;
    Action? sync;

    public BridgesPanel(SimulatorShell shell)
    {
        this.shell = shell;

        var host = new TreeNode(Ui.Live(() => $"[bold {Ui.Violet}]host[/] [{Ui.Muted}]{Ui.Escape(this.shell.State.Platform)}[/]")) { Data = new HostTarget(), Icon = new Rune('⌂') };
        this.tree.Roots.Add(host);

        foreach (var bridge in shell.State.Bridges)
        {
            var node = new TreeNode(Ui.Live(() => BridgeHeader(bridge, this.shell.Revision))) { Data = bridge, Icon = new Rune('◇') };

            foreach (var route in bridge.Routes)
                node.Children.Add(new TreeNode(Ui.Live(() => RouteHeader(route, this.shell.Revision))) { Data = route, Icon = new Rune('·') });

            foreach (var evt in bridge.Events)
                node.Children.Add(new TreeNode(Ui.Live(() => EventHeader(evt, this.shell.Revision))) { Data = evt, Icon = new Rune('⚡') });

            this.tree.Roots.Add(node);
        }


        this.detail = new ComputedVisual(() =>
        {
            _ = this.editorVersion.Value;
            return this.BuildEditor(this.selection.Value);
        }).Stretch();

        // Enter from the tree goes to the editor's input; Escape from anywhere in the editor comes back.
        this.tree.AddKeyBinding(new KeyGesture(TerminalKey.Enter, TerminalModifiers.None), this.FocusEditor);
        var editorPane = new Padder(this.detail).Padding(new Thickness(1, 0, 1, 0)).Stretch();
        editorPane.AddKeyBinding(new KeyGesture(TerminalKey.Escape, TerminalModifiers.None), this.Focus);

        this.View = new HSplitter()
            .First(Ui.Panel("Bridges", new ScrollViewer(this.tree).Stretch()))
            .Second(editorPane)
            .Ratio(0.3)
            .MinFirst(28)
            .MinSecond(50);

        this.tree.TrySelectNode(host);
        this.selection.Value = host.Data;
    }

    public Visual View { get; }

    public TreeView Tree => this.tree;

    public void Focus() => this.shell.Focus(this.tree);

    /// <summary>Focuses the open editor's main input: a route's value, an event's payload, or its first control.</summary>
    void FocusEditor()
    {
        var visuals = this.detail.EnumerateVisualsDepthFirst().ToList();
        var target = visuals.FirstOrDefault(x => x is CodeEditor { IsVisible: true })
            ?? visuals.FirstOrDefault(x => x is { Focusable: true, IsTabStop: true, IsVisible: true } && !ReferenceEquals(x, this.detail));

        if (target is not null)
            this.shell.Focus(target);
    }

    /// <summary>Rebuilds the editor from the simulator — after a scenario replaced what it was showing.</summary>
    public void Reload() => this.editorVersion.Value = ++this.editorVersions;

    /// <summary>Called on the render thread when the simulator changes: lets the open editor follow switches made elsewhere.</summary>
    public void Sync() => this.sync?.Invoke();

    /// <summary>Called on the render thread each turn of the loop: the editor follows the tree's selection.</summary>
    public void Tick()
    {
        // Null until the tree is first laid out; a tree with nodes always has one selected after that.
        if (this.tree.SelectedNode?.Data is { } data && !ReferenceEquals(data, this.selection.Value))
            this.selection.Value = data;
    }

    /// <summary>The editor for a tree node's data: the host, a bridge, a route or an event.</summary>
    internal Visual BuildEditor(object? target)
    {
        this.sync = null;

        return target switch
        {
            HostTarget => this.BuildHost(),
            BridgeState bridge => this.BuildBridge(bridge),
            RouteState route => new RouteEditor(this.shell, route).View,
            EventState evt => new EventEditor(this.shell, evt).View,
            _ => Ui.Empty("Pick a bridge, a route or an event.")
        };
    }

    Visual BuildHost()
    {
        var state = this.shell.State;
        var platforms = SimulatorOptions.Platforms;

        var platform = new State<int>(Math.Max(0, platforms.ToList().IndexOf(state.Platform)));
        var select = new Select<string>(platforms);
        select.BindSelectedIndex(platform.Bind.Value);
        select.SelectionChanged(() =>
        {
            var chosen = platforms[Math.Clamp(select.SelectedIndex, 0, platforms.Count - 1)];
            if (chosen != state.Platform)
                state.Platform = chosen;
        });

        var sticky = new State<bool>(state.StickyWrites);
        var stickyBox = new CheckBox(new TextBlock("Sticky writes — a PUT or POST of what a GET returns becomes that GET's answer"), sticky.Bind.Value)
            .ValueChanged(() => state.StickyWrites = sticky.Value);

        this.sync = () =>
        {
            platform.Value = Math.Max(0, platforms.ToList().IndexOf(state.Platform));
            sticky.Value = state.StickyWrites;
        };

        var host = this.shell.Host;
        var info = Ui.Live(() =>
        {
            _ = this.shell.Revision;
            var origin = host.Origin?.AbsoluteUri ?? "not listening";
            var page = host.Options.DevServer is { } dev ? $"proxied from {dev}" : host.Options.AppDirectory ?? "none (a landing page explains how to serve one)";
            return String.Join("\n",
                $"[{Ui.Muted}]origin[/]        [underline {Ui.Accent}]{Ui.Escape(origin)}[/]",
                $"[{Ui.Muted}]bridges[/]       {Ui.Escape(origin)}_bridge/…",
                $"[{Ui.Muted}]page[/]          {Ui.Escape(page)}",
                $"[{Ui.Muted}]event streams[/] {host.Server.Events.StreamCount}",
                $"[{Ui.Muted}]data[/]          {Ui.Escape(host.Server.Options.ResolveDataDirectory())} [{Ui.Muted}](the real settings and files bridges)[/]");
        });

        return new ScrollViewer(new VStack(
            Ui.Title("The host"),
            Ui.Dim("What GET /_bridge/host reports, and how every simulated bridge behaves."),
            Ui.Field("Platform", select.MinWidth(16)),
            stickyBox,
            new Rule(),
            info,
            new Rule(),
            Ui.Dim("Open the origin in a browser. The page and the bridges share it, so a page built with Shiny.AppDeviceBridge.Blazor or the TypeScript client finds them on its own. Every bridge starts answering with a sample of its contract; pick a route to change it.")
        ).Spacing(1));
    }

    Visual BuildBridge(BridgeState bridge)
    {
        var state = this.shell.State;
        var supported = new State<bool>(bridge.IsSupported);
        var box = new CheckBox(new TextBlock("Supported — off, every route answers 501 not_supported and the host reports it"), supported.Bind.Value)
            .ValueChanged(() =>
            {
                if (supported.Value != bridge.IsSupported)
                    state.SetSupported(bridge.Name, supported.Value);
            });

        this.sync = () => supported.Value = bridge.IsSupported;

        var summary = Ui.Live(() =>
        {
            _ = this.shell.Revision;
            var lines = new List<string> { $"[bold]Routes[/] [{Ui.Muted}]— what each answers, and how often the page called it[/]" };
            foreach (var route in bridge.Routes)
            {
                var b = route.Behavior;
                var answer = b.Mode switch
                {
                    ResponseMode.Null => $"[{Ui.Amber}]204 null[/]",
                    ResponseMode.Error => $"[{Ui.Red}]{b.StatusCode} {Ui.Escape(b.ErrorCode)}[/]",
                    _ when route.Route.Kind == ResponseKind.Empty => $"[{Ui.Green}]204[/]",
                    _ when route.Route.Kind == ResponseKind.Binary => $"[{Ui.Green}]200 {(b.FilePath is null ? "placeholder" : Ui.Escape(Path.GetFileName(b.FilePath)))}[/]",
                    _ => b.Json == route.DefaultJson ? $"[{Ui.Green}]200[/] [{Ui.Muted}]sample[/]" : $"[{Ui.Green}]200 set[/]"
                };

                var delay = b.DelayMs > 0 ? $" [{Ui.Muted}]+{b.DelayMs}ms[/]" : "";
                lines.Add($"  {Ui.Method(route.Route.Method)} {Ui.Escape(route.Route.Pattern.Length == 0 ? "/" : route.Route.Pattern),-34} {answer}{delay}  [{Ui.Muted}]×{route.Hits}[/]");
            }

            if (bridge.Events.Count > 0)
            {
                lines.Add("");
                lines.Add($"[bold]Events[/] [{Ui.Muted}]— listening page streams, and times fired[/]");
                foreach (var evt in bridge.Events)
                    lines.Add($"  [{Ui.Violet}]⚡ {Ui.Escape(evt.Event.Name),-32}[/] [{(evt.Listeners > 0 ? Ui.Green : Ui.Muted)}]{evt.Listeners} listening[/]  [{Ui.Muted}]fired ×{evt.Fired}[/]");
            }

            return String.Join("\n", lines);
        });

        return new ScrollViewer(new VStack(
            Ui.Markup($"[bold]{Ui.Escape(bridge.Name)}[/]  [{Ui.Muted}]/_bridge/{Ui.Escape(bridge.Name)} · {Ui.Escape(bridge.Bridge.Package)}[/]"),
            box,
            new Rule(),
            summary
        ).Spacing(1));
    }

    static string BridgeHeader(BridgeState bridge, int revision)
    {
        _ = revision;
        var hits = bridge.Routes.Sum(x => x.Hits);
        var off = bridge.IsSupported ? "" : $" [{Ui.Red}]off[/]";
        var calls = hits > 0 ? $" [{Ui.Muted}]×{hits}[/]" : "";
        return $"[bold]{Ui.Escape(bridge.Name)}[/]{off}{calls}";
    }

    static string RouteHeader(RouteState route, int revision)
    {
        _ = revision;
        var b = route.Behavior;
        var flag = b.Mode switch
        {
            ResponseMode.Null => $" [{Ui.Amber}]204[/]",
            ResponseMode.Error => $" [{Ui.Red}]{b.StatusCode}[/]",
            _ when route.Route.Kind == ResponseKind.Json && b.Json != route.DefaultJson => $" [{Ui.Green}]●[/]",
            _ => ""
        };

        var delay = b.DelayMs > 0 ? $" [{Ui.Muted}]⏱[/]" : "";
        var hits = route.Hits > 0 ? $" [{Ui.Muted}]×{route.Hits}[/]" : "";
        return $"{Ui.Method(route.Route.Method)}{Ui.Escape(route.Route.Pattern.Length == 0 ? "/" : route.Route.Pattern)}{flag}{delay}{hits}";
    }

    static string EventHeader(EventState evt, int revision)
    {
        _ = revision;
        var listening = evt.Listeners > 0 ? $" [{Ui.Green}]●[/]" : "";
        return $"[{Ui.Violet}]{Ui.Escape(evt.Event.Name)}[/]{listening}";
    }
}
