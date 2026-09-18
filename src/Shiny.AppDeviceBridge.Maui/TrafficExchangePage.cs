using System.Text;

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
        this.text = exchange is null ? "" : Describe(exchange);

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
        var (light, dark) = TrafficFormat.StatusColors(exchange.StatusCode, exchange.Error is not null);
        var status = new Label { FontSize = 14, Text = $"{exchange.Method}  →  {Status(exchange)}" };
        status.SetAppThemeColor(Label.TextColorProperty, light, dark);

        var overview = new Label { Text = Overview(exchange), FontSize = 13 };
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

        Section(stack, "REQUEST HEADERS", Headers(exchange.RequestHeaders));
        Section(stack, "REQUEST BODY", Body(exchange.RequestBody));
        Section(stack, "RESPONSE HEADERS", Headers(exchange.ResponseHeaders));
        Section(stack, "RESPONSE BODY", Body(exchange.ResponseBody));

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

    static string Status(TrafficExchange exchange) => $"{exchange.StatusCode} {TrafficFormat.Reason(exchange.StatusCode)}".TrimEnd();

    static string Overview(TrafficExchange exchange)
    {
        var at = exchange.StartedOn.ToLocalTime();
        return String.Join(
            "\n",
            $"From {TrafficFormat.Origin(exchange.Origin)} ({exchange.RemoteAddress})",
            $"At {at:HH:mm:ss.fff} on {at:d}",
            $"Took {TrafficFormat.Duration(exchange.Elapsed)}",
            $"Sent {TrafficFormat.Size(exchange.RequestBody.ByteCount)}, received {TrafficFormat.Size(exchange.ResponseBody.ByteCount)}"
        );
    }

    internal static string Headers(IReadOnlyList<TrafficHeader> headers)
    {
        if (headers.Count == 0)
            return "(none)";

        var text = new StringBuilder();
        foreach (var header in headers)
            text.Append(header.Name).Append(": ").AppendLine(header.Value);

        return text.ToString().TrimEnd();
    }

    /// <summary>A body that was not kept still says why — "nothing here" and "247 KB of PNG" are different answers.</summary>
    internal static string Body(TrafficBody body) => body.State switch
    {
        TrafficBodyState.Empty => "(no body)",
        TrafficBodyState.Binary => $"({TrafficFormat.Size(body.ByteCount)} of {body.ContentType ?? "unknown type"}, not shown)",
        TrafficBodyState.Redacted => $"({TrafficFormat.Size(body.ByteCount)}, redacted)",
        TrafficBodyState.Truncated => $"{body.Text}\n\n(the first {TrafficFormat.Size(Encoding.UTF8.GetByteCount(body.Text ?? ""))} of {TrafficFormat.Size(body.ByteCount)})",
        _ => String.IsNullOrEmpty(body.Text) ? "(no body)" : body.Text
    };

    /// <summary>The exchange as text, for a bug report.</summary>
    internal static string Describe(TrafficExchange exchange) => new StringBuilder()
        .AppendLine($"{exchange.Method} {exchange.Target}")
        .AppendLine(Overview(exchange))
        .AppendLine()
        .AppendLine("--- request headers ---")
        .AppendLine(Headers(exchange.RequestHeaders))
        .AppendLine()
        .AppendLine("--- request body ---")
        .AppendLine(Body(exchange.RequestBody))
        .AppendLine()
        .AppendLine($"--- response {Status(exchange)} ---")
        .AppendLine(Headers(exchange.ResponseHeaders))
        .AppendLine()
        .AppendLine("--- response body ---")
        .AppendLine(Body(exchange.ResponseBody))
        .ToString();
}
