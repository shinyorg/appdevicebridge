using Shiny.AppDeviceBridge.WebView;

namespace Sample;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new WebAppHostPage();

#if DEBUG
        // Every request the server answers — the page's files, its bridge calls, the refusals. UseTrafficMonitor in
        // SampleConfiguration records them; this opens the monitor over the web app.
        var traffic = new Button
        {
            Text = "Traffic",
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Margin = new Thickness(12),
            Opacity = 0.85,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End
        };
        traffic.Clicked += async (_, _) => await page.HostView.ShowTrafficMonitorAsync();

        // Detached first: replacing a page's content un-parents the old content, which would take the host view out of the
        // grid it had just been added to.
        var host = page.HostView;
        page.Content = null;
        page.Content = new Grid { host, traffic };
#endif

        return new(page) { Title = "AppDeviceBridge Sample" };
    }
}
