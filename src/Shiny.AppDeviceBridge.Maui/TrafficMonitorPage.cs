using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AppDeviceBridge.Maui;

/// <summary>
/// Every request the server answered while recording, newest first — status, path, size, and when, from where and how
/// long. Tap one for the whole exchange. Needs <see cref="AppDeviceBridgeMauiExtensions.UseTrafficMonitor"/>.
/// <code>
/// await TrafficMonitorPage.ShowAsync(this.Navigation);
/// </code>
/// <para>
/// A window onto the recorder, not a copy of it: the list is rebuilt from <see cref="TrafficRecorder.Snapshot"/> whenever
/// it changes, and only while the page is on screen.
/// </para>
/// </summary>
public class TrafficMonitorPage : ContentPage
{
    readonly TrafficRecorder recorder;
    readonly CollectionView list;
    readonly Label summary = new() { FontAttributes = FontAttributes.Bold, FontSize = 15 };
    readonly Label totals = new() { FontSize = 12 };
    readonly Label empty = new() { Margin = new Thickness(32), HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
    readonly Switch recordSwitch = new() { VerticalOptions = LayoutOptions.Center };
    readonly Entry filter = new() { Placeholder = "Filter by path, method or status", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    readonly Button clearButton = new() { Text = "Clear", Padding = new Thickness(16, 6) };
    readonly Button closeButton = new() { Text = "Close", Padding = new Thickness(16, 6) };
    bool attached;
    bool refreshQueued;

    public TrafficMonitorPage() : this(ResolveRecorder()) { }

    public TrafficMonitorPage(TrafficRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        this.recorder = recorder;
        this.Title = "Traffic";

        Secondary(this.totals);
        Secondary(this.empty);

        this.list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(CreateRow)
        };
        this.list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not TrafficExchange exchange)
                return;

            this.list.SelectedItem = null;
            await this.Navigation.PushModalAsync(new TrafficExchangePage(this.recorder, exchange.Id));
        };

        this.recordSwitch.Toggled += (_, e) => this.recorder.IsRecording = e.Value;
        this.filter.TextChanged += (_, _) => this.Refresh();
        this.clearButton.Clicked += (_, _) => this.recorder.Clear();
        this.closeButton.Clicked += async (_, _) => await this.CloseAsync();

        var recordLabel = new Label { Text = "Record", VerticalOptions = LayoutOptions.Center };

        var header = new Grid
        {
            Padding = new Thickness(16, 12),
            ColumnSpacing = 12,
            RowSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
        };
        header.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F4F6"), Color.FromArgb("#1F2330"));

        var titles = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { this.summary, this.totals } };
        header.Add(titles, 0, 0);
        header.Add(recordLabel, 1, 0);
        header.Add(this.recordSwitch, 2, 0);
        header.Add(this.clearButton, 3, 0);
        header.Add(this.closeButton, 4, 0);
        header.Add(this.filter, 0, 1);
        Grid.SetColumnSpan(this.filter, 5);

        var body = new Grid { this.empty, this.list };

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        root.Add(header, 0, 0);
        root.Add(body, 0, 1);
        this.Content = root;

        this.Refresh();
    }

    /// <summary>
    /// Shows the monitor over whatever <paramref name="navigation"/> belongs to, with a Close button. Uses the recorder in the
    /// app's container.
    /// </summary>
    public static Task ShowAsync(INavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return navigation.PushModalAsync(new TrafficMonitorPage());
    }

    // Following the handler rather than Appearing or Loaded: the maui-labs AppKit and GTK4 backends do not raise Loaded, and
    // a page the app hosts in a tab of its own appears and disappears without being torn down.
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (this.Handler is not null && !this.attached)
        {
            this.attached = true;
            this.recorder.Changed += this.OnChanged;
            this.Refresh();
        }
        else if (this.Handler is null && this.attached)
        {
            this.attached = false;
            this.recorder.Changed -= this.OnChanged;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Close only means something when this page was pushed over another.
        this.closeButton.IsVisible = this.Navigation.ModalStack.Contains(this);
        this.Refresh();
    }

    async Task CloseAsync()
    {
        var modals = this.Navigation.ModalStack;
        if (modals.Count > 0 && ReferenceEquals(modals[^1], this))
            await this.Navigation.PopModalAsync();
    }

    void OnChanged(object? sender, EventArgs e)
    {
        // A busy server raises this far faster than a list can be rebuilt; one pass per turn of the UI thread is plenty.
        if (this.refreshQueued)
            return;

        this.refreshQueued = true;
        this.Dispatcher.Dispatch(() =>
        {
            this.refreshQueued = false;
            this.Refresh();
        });
    }

    void Refresh()
    {
        var all = this.recorder.Snapshot();
        var shown = TrafficText.Filter(all, this.filter.Text);

        this.list.ItemsSource = shown;
        this.recordSwitch.IsToggled = this.recorder.IsRecording;
        this.clearButton.IsEnabled = all.Count > 0;

        this.summary.Text = all.Count switch
        {
            0 => this.recorder.IsRecording ? "Recording" : "Not recording",
            _ when shown.Count != all.Count => $"{shown.Count} of {all.Count} requests",
            1 => "1 request",
            var count => $"{count} requests"
        };

        this.totals.Text = shown.Count == 0
            ? ""
            : $"↑ {TrafficText.Size(shown.Sum(x => x.RequestBody.ByteCount))} · ↓ {TrafficText.Size(shown.Sum(x => x.ResponseBody.ByteCount))}";

        this.empty.Text = all.Count > 0
            ? "No request matches the filter."
            : this.recorder.IsRecording
                ? "Nothing has been asked for yet."
                : "Recording is off. Switch it on to see requests as the server answers them.";

        this.empty.IsVisible = shown.Count == 0;
        this.list.IsVisible = shown.Count > 0;
    }

    static View CreateRow()
    {
        var status = new Label { FontAttributes = FontAttributes.Bold, FontSize = 14, WidthRequest = 36, VerticalOptions = LayoutOptions.Center };
        var target = new Label { FontSize = 14, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center };
        var size = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center };
        var detail = new Label { FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation };
        Secondary(size);
        Secondary(detail);

        var row = new Grid
        {
            Padding = new Thickness(16, 10),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
        };
        row.Add(status, 0, 0);
        row.Add(target, 1, 0);
        row.Add(size, 2, 0);
        row.Add(detail, 1, 1);
        Grid.SetColumnSpan(detail, 2);

        // Set from the item rather than bound: every column is a formatted string, and the status colour is a pair of theme
        // colours that a binding would need a converter per theme for.
        row.BindingContextChanged += (_, _) =>
        {
            if (row.BindingContext is not TrafficExchange exchange)
                return;

            status.Text = exchange.StatusCode.ToString();
            var (light, dark) = TrafficColors.Status(exchange.StatusCode, exchange.Error is not null);
            status.SetAppThemeColor(Label.TextColorProperty, light, dark);

            target.Text = exchange.Target;
            size.Text = TrafficText.Size(exchange.ResponseBody.ByteCount);
            detail.Text = $"{exchange.Method}  {exchange.StartedOn.ToLocalTime():HH:mm:ss} · {TrafficText.Origin(exchange.Origin)} · {TrafficText.Duration(exchange.Elapsed)}";
        };

        return row;
    }

    internal static void Secondary(Label label)
        => label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#6B7280"), Color.FromArgb("#9BA2BC"));

    static TrafficRecorder ResolveRecorder()
        => IPlatformApplication.Current?.Services.GetService<TrafficRecorder>()
           ?? throw new InvalidOperationException("TrafficRecorder is not registered. Call UseTrafficMonitor in MauiProgram.");
}

/// <summary>How the traffic pages colour a status. The words are <see cref="TrafficText"/>'s.</summary>
static class TrafficColors
{
    /// <summary>Ink for a status code, light theme then dark: what is being scanned for is the one request that went wrong.</summary>
    public static (Color Light, Color Dark) Status(int statusCode, bool failed)
    {
        var (light, dark) = failed ? ("#DC2626", "#F87171") : statusCode switch
        {
            >= 500 => ("#DC2626", "#F87171"),
            >= 400 => ("#D97706", "#FBBF24"),
            >= 300 => ("#2563EB", "#93B4FD"),
            >= 200 => ("#059669", "#34D399"),
            _ => ("#6B7280", "#9BA2BC")
        };

        return (Color.FromArgb(light), Color.FromArgb(dark));
    }
}
