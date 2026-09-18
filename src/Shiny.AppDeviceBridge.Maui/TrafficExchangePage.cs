namespace Shiny.AppDeviceBridge.Maui;

/// <summary>
/// One exchange, whole: the request with every header and whatever body came with it, and the response the same way.
/// Shown modally by <see cref="TrafficMonitorPage"/>; Copy puts all of it on the clipboard as text.
/// </summary>
public class TrafficExchangePage : ContentPage
{
    readonly string text;

    public TrafficExchangePage(TrafficRecorder recorder, string exchangeId)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        this.Title = "Request";

        var exchange = recorder.Find(exchangeId);
        this.text = exchange is null ? "" : TrafficText.Describe(exchange);

        var copy = new Button { Text = "Copy", IsVisible = exchange is not null };
        var close = new Button { Text = "Close" };
        copy.Clicked += async (_, _) => await this.CopyAsync(copy);
        close.Clicked += async (_, _) =>
        {
            var modals = this.Navigation.ModalStack;
            if (modals.Count > 0 && ReferenceEquals(modals[^1], this))
                await this.Navigation.PopModalAsync();
        };

        var buttons = new Grid
        {
            Padding = new Thickness(16, 12, 16, 16),
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }
        };
        buttons.Add(copy, 0, 0);
        buttons.Add(close, 1, 0);

        View body;
        if (exchange is null)
        {
            var missing = new Label
            {
                // The oldest is dropped past the limit, and Clear drops them all, while this page may still be open on one.
                Text = "This request is no longer held. Only the most recent requests are kept, and clearing the list drops them all.",
                Margin = new Thickness(32),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center
            };
            TrafficMonitorPage.Secondary(missing);
            body = missing;
        }
        else
        {
            body = new ScrollView { Content = Build(exchange) };
        }

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) } };
        root.Add(body, 0, 0);
        root.Add(buttons, 0, 1);
        this.Content = root;
    }

    async Task CopyAsync(Button button)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(this.text);
            button.Text = "Copied";
        }
        catch (Exception)
        {
            // Not every backend has a clipboard; the text is still on screen to select.
            button.Text = "Copy unavailable";
        }
    }

    static VerticalStackLayout Build(TrafficExchange exchange)
    {
        var (light, dark) = TrafficColors.Status(exchange.StatusCode, exchange.Error is not null);
        var status = new Label { FontSize = 14, Text = $"{exchange.Method}  →  {TrafficText.Status(exchange)}" };
        status.SetAppThemeColor(Label.TextColorProperty, light, dark);

        var overview = new Label { Text = TrafficText.Overview(exchange), FontSize = 13 };
        TrafficMonitorPage.Secondary(overview);

        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new Label { Text = exchange.Target, FontAttributes = FontAttributes.Bold, FontSize = 16 },
                status,
                overview
            }
        };

        if (exchange.Error is { } error)
        {
            var label = new Label { Text = error, FontSize = 13 };
            label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#DC2626"), Color.FromArgb("#F87171"));
            stack.Add(label);
        }

        Section(stack, "REQUEST HEADERS", TrafficText.Headers(exchange.RequestHeaders));
        Section(stack, "REQUEST BODY", TrafficText.Body(exchange.RequestBody));
        Section(stack, "RESPONSE HEADERS", TrafficText.Headers(exchange.ResponseHeaders));
        Section(stack, "RESPONSE BODY", TrafficText.Body(exchange.ResponseBody));

        return stack;
    }

    static void Section(VerticalStackLayout stack, string title, string content)
    {
        var heading = new Label { Text = title, FontSize = 11, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
        TrafficMonitorPage.Secondary(heading);

        var block = new Border
        {
            Padding = new Thickness(12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Label { Text = content, FontSize = 12, FontFamily = Monospace, LineBreakMode = LineBreakMode.CharacterWrap }
        };
        block.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F4F6"), Color.FromArgb("#1F2330"));

        stack.Add(heading);
        stack.Add(block);
    }

    // From the OS rather than DeviceInfo, which Essentials does not implement on every backend.
    static string Monospace => OperatingSystem.IsWindows() ? "Consolas"
        : OperatingSystem.IsAndroid() || OperatingSystem.IsLinux() ? "monospace"
        : "Menlo";
}
