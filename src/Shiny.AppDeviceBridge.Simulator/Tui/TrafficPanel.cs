using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>
/// Every request the page made and what it got back, newest first — the same window onto the bridge server's
/// <see cref="TrafficRecorder"/> the MAUI app's traffic monitor gives, with the whole exchange beside the list.
/// </summary>
sealed class TrafficPanel
{
    readonly SimulatorShell shell;
    readonly ListBox<Visual> list = new();
    readonly State<string> filter = new("");
    readonly State<bool> recording;
    readonly State<int> shownRevision = new(0);
    readonly State<int> selectedIndex = new(-1);
    int shownRevisions;
    IReadOnlyList<TrafficExchange> shown = [];
    IReadOnlyList<TrafficExchange> all = [];

    public TrafficPanel(SimulatorShell shell)
    {
        this.shell = shell;
        this.recording = new State<bool>(shell.Host.Traffic.IsRecording);

        var search = new TextBox(this.filter.Bind.Value!).MinWidth(28);
        // After the keystroke has reached the text, not before it.
        search.TextInput(() => this.shell.Post(this.Refresh));
        search.KeyDown(() => this.shell.Post(this.Refresh));

        var record = new CheckBox(new TextBlock("Record"), this.recording.Bind.Value)
            .ValueChanged(() => this.shell.Host.Traffic.IsRecording = this.recording.Value);

        var toolbar = new VStack(
            Ui.Live(() => this.Summary()),
            new HStack(Ui.Field("Filter", search), record, Ui.Action("Clear", () => this.shell.Host.Traffic.Clear())).Spacing(2)
        ).Spacing(0);

        var rows = new ComputedVisual(() =>
        {
            _ = this.shownRevision.Value;
            return this.shown.Count > 0 ? this.list : Ui.Empty(this.EmptyMessage());
        });

        var detail = new ComputedVisual(() =>
        {
            _ = this.shownRevision.Value;
            var index = this.selectedIndex.Value;
            return index >= 0 && index < this.shown.Count ? Detail(this.shown[index]) : Ui.Empty("Pick a request to see all of it.");
        });

        this.View = new HSplitter()
            .First(Ui.Panel("Traffic", new DockLayout(toolbar, rows, new VStack())))
            .Second(new Padder(new ScrollViewer(detail)).Padding(new Thickness(1, 0, 1, 0)))
            .Ratio(0.5)
            .MinFirst(40)
            .MinSecond(40);

        this.Refresh();
    }

    public Visual View { get; }

    public int Count => this.shown.Count;

    /// <summary>Rebuilds the list from the recorder. On the render thread.</summary>
    public void Refresh()
    {
        var selectedId = this.list.SelectedIndex is >= 0 and var i && i < this.shown.Count ? this.shown[i].Id : null;

        this.all = this.shell.Host.Traffic.Snapshot();
        this.shown = TrafficText.Filter(this.all, this.filter.Value);
        this.recording.Value = this.shell.Host.Traffic.IsRecording;

        this.list.Items.Clear();
        foreach (var exchange in this.shown)
            this.list.Items.Add(new Markup(Row(exchange)));

        // The list is newest first, so a new request moves everything down; the selection follows its request.
        var index = selectedId is null ? -1 : this.shown.ToList().FindIndex(x => x.Id == selectedId);
        this.list.SelectedIndex = index >= 0 ? index : (this.shown.Count > 0 && selectedId is null ? 0 : -1);

        this.selectedIndex.Value = this.list.SelectedIndex;
        this.shownRevision.Value = ++this.shownRevisions;
    }

    /// <summary>Called on the render thread each turn of the loop: the detail follows the list's selection.</summary>
    public void Tick()
    {
        if (this.list.SelectedIndex != this.selectedIndex.Value)
            this.selectedIndex.Value = this.list.SelectedIndex;
    }

    string Summary()
    {
        _ = this.shownRevision.Value;
        var head = this.all.Count switch
        {
            0 => this.recording.Value ? "Recording" : "Not recording",
            _ when this.shown.Count != this.all.Count => $"{this.shown.Count} of {this.all.Count} requests",
            1 => "1 request",
            var count => $"{count} requests"
        };

        var totals = this.shown.Count == 0
            ? ""
            : $"  [{Ui.Muted}]↑ {TrafficText.Size(this.shown.Sum(x => x.RequestBody.ByteCount))} · ↓ {TrafficText.Size(this.shown.Sum(x => x.ResponseBody.ByteCount))}[/]";

        return $"[bold]{head}[/]{totals}";
    }

    string EmptyMessage() => this.all.Count > 0
        ? "No request matches the filter."
        : this.recording.Value
            ? "Nothing has been asked for yet. Open the page and use it."
            : "Recording is off. Switch it on to see requests as the server answers them.";

    static string Row(TrafficExchange exchange)
    {
        var failed = exchange.Error is not null;
        return $"{Ui.Status(exchange.StatusCode, failed)} {Ui.Method(exchange.Method)}{Ui.Escape(exchange.Target)}  "
               + $"[{Ui.Muted}]{TrafficText.Size(exchange.ResponseBody.ByteCount)} · {exchange.StartedOn.ToLocalTime():HH:mm:ss} · {TrafficText.Duration(exchange.Elapsed)}[/]";
    }

    static Visual Detail(TrafficExchange exchange)
    {
        var failed = exchange.Error is not null;
        var items = new List<Visual>
        {
            Ui.Markup($"[bold]{Ui.Escape(exchange.Target)}[/]").Wrap(true),
            Ui.Markup($"{Ui.Method(exchange.Method)} → [bold {Ui.StatusColor(exchange.StatusCode, failed)}]{Ui.Escape(TrafficText.Status(exchange))}[/]"),
            Ui.Markup($"[{Ui.Muted}]{Ui.Escape(TrafficText.Overview(exchange))}[/]")
        };

        if (exchange.Error is { } error)
            items.Add(Ui.Markup($"[{Ui.Red}]{Ui.Escape(error)}[/]").Wrap(true));

        items.Add(Section("Request headers", TrafficText.Headers(exchange.RequestHeaders)));
        items.Add(Section("Request body", Ui.Pretty(TrafficText.Body(exchange.RequestBody))));
        items.Add(Section("Response headers", TrafficText.Headers(exchange.ResponseHeaders)));
        items.Add(Section("Response body", Ui.Pretty(TrafficText.Body(exchange.ResponseBody))));

        return new VStack([.. items]).Spacing(1);
    }

    /// <summary>A titled block of text, a line to a line: a text block wraps, but reads a newline as a space.</summary>
    static Visual Section(string title, string content)
        => new Group(
                Ui.Markup($"[{Ui.Muted}]{Ui.Escape(title.ToUpperInvariant())}[/]"),
                new VStack([.. content.ReplaceLineEndings("\n").Split('\n').Select(line => (Visual)new TextBlock(line).Wrap(true).IsSelectable(true))]).Spacing(0)
            )
            .Padding(new Thickness(1, 0, 1, 0))
            .Stretch();
}
