using Microsoft.Extensions.DependencyInjection;

namespace Shiny.WebAppHost.Maui;

/// <summary>
/// The web app, full size, with a progress screen in front of it until it can load.
/// <code>
/// public App() => MainPage = new ContentPage { Content = new WebAppHostView() };
/// </code>
/// <para>
/// Starts the host when it first appears, shows download progress while a required update installs,
/// offers a retry when there is nothing to show, and restarts the loopback server when the window
/// resumes.
/// </para>
/// </summary>
public class WebAppHostView : ContentView
{
    readonly ActivityIndicator spinner = new() { IsRunning = true, HorizontalOptions = LayoutOptions.Center };
    readonly ProgressBar progressBar = new() { IsVisible = false, WidthRequest = 240, HorizontalOptions = LayoutOptions.Center };
    readonly Label statusLabel = new() { HorizontalTextAlignment = TextAlignment.Center, HorizontalOptions = LayoutOptions.Center };
    readonly Button retryButton = new() { Text = "Try again", IsVisible = false, HorizontalOptions = LayoutOptions.Center };
    readonly VerticalStackLayout loading;

    WebAppHost? host;
    WebAppHostOptions? options;
    Window? window;
    bool attached;
    bool started;
    bool starting;

    public WebAppHostView()
    {
        this.WebView = new WebView { IsVisible = false };

        // The WebView's handler can arrive before this view's, and a new one arrives with every reconnect.
        this.WebView.HandlerChanged += (_, _) => WebAppWebViewPermissions.Attach(this.WebView);

        this.loading = new VerticalStackLayout
        {
            Spacing = 12,
            Padding = new Thickness(24),
            VerticalOptions = LayoutOptions.Center,
            Children = { this.spinner, this.progressBar, this.statusLabel, this.retryButton }
        };

        this.retryButton.Clicked += async (_, _) => await this.StartAsync();

        this.Content = new Grid { Children = { this.WebView, this.loading } };

        this.Loaded += this.OnLoaded;
        this.Unloaded += this.OnUnloaded;
    }

    /// <summary>The WebView the app loads into, for handler customisation.</summary>
    public WebView WebView { get; }

    /// <summary>Raised on the UI thread once the web app has been handed to the WebView.</summary>
    public event EventHandler<Uri>? Ready;

    // Not every backend raises Loaded — the maui-labs AppKit and GTK4 backends never do — so a handler being
    // attached is also taken as the moment to start. Both paths go through AttachAsync, which runs once.
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (this.Handler is not null)
            _ = this.AttachAsync();
        else
            this.Detach();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        // The window can arrive after the handler does; follow it for resume.
        if (propertyName == nameof(this.Window) && this.attached)
            this.WatchWindow(this.Window);
    }

    async void OnLoaded(object? sender, EventArgs e) => await this.AttachAsync();

    void OnUnloaded(object? sender, EventArgs e) => this.Detach();

    async Task AttachAsync()
    {
        if (this.attached)
            return;

        var services = this.Handler?.MauiContext?.Services ?? IPlatformApplication.Current?.Services;

        try
        {
            this.host ??= services?.GetService<WebAppHost>();
            this.options ??= services?.GetService<WebAppHostOptions>();
        }
        catch (Exception ex)
        {
            // Resolving the host constructs every registered bridge. One that throws would otherwise leave the
            // spinner turning forever, because nothing observes a task started from OnHandlerChanged.
            var root = ex.GetBaseException();
            this.ShowError($"The web app host could not be created: {root.GetType().Name}: {root.Message}");
            return;
        }

        if (this.host is null)
        {
            if (services is not null)
                this.ShowError("WebAppHost is not registered. Call UseWebAppHost in MauiProgram.");

            return;
        }

        this.attached = true;
        this.host.StatusChanged += this.OnStatusChanged;
        this.host.UpdateInstalled += this.OnUpdateInstalled;
        this.WatchWindow(this.Window);

        if (!this.started)
            await this.StartAsync();
    }

    void Detach()
    {
        if (!this.attached)
            return;

        this.attached = false;

        if (this.host is not null)
        {
            this.host.StatusChanged -= this.OnStatusChanged;
            this.host.UpdateInstalled -= this.OnUpdateInstalled;
        }

        this.WatchWindow(null);
    }

    void WatchWindow(Window? next)
    {
        if (ReferenceEquals(this.window, next))
            return;

        if (this.window is not null)
            this.window.Resumed -= this.OnResumed;

        this.window = next;

        if (this.window is not null)
            this.window.Resumed += this.OnResumed;
    }

    async Task StartAsync()
    {
        if (this.host is null || this.starting)
            return;

        this.starting = true;
        this.retryButton.IsVisible = false;
        this.spinner.IsRunning = true;
        this.spinner.IsVisible = true;

        try
        {
            var uri = await this.host.StartAsync();

            this.WebView.Source = new UrlWebViewSource { Url = uri.AbsoluteUri };
            this.WebView.IsVisible = true;
            this.loading.IsVisible = false;
            this.started = true;

            this.Ready?.Invoke(this, uri);
        }
        catch (Exception ex)
        {
            this.ShowError(ex.Message);
        }
        finally
        {
            this.starting = false;
        }
    }

    async void OnResumed(object? sender, EventArgs e)
    {
        if (this.host is null || !this.started)
            return;

        try
        {
            if (await this.host.EnsureServerRunningAsync())
                this.WebView.Reload();
        }
        catch (Exception ex)
        {
            this.ShowError(ex.Message);
        }
    }

    void OnUpdateInstalled(object? sender, WebAppPackage package)
    {
        if (this.options?.ApplyOptionalUpdatesImmediately != true)
            return;

        this.Dispatcher.Dispatch(() =>
        {
            if (this.host?.ApplyPendingUpdate() == true && this.started)
                this.WebView.Reload();
        });
    }

    void OnStatusChanged(object? sender, WebAppHostStatus status) => this.Dispatcher.Dispatch(() =>
    {
        // Once the page is up, background activity is the page's business, not an overlay's.
        if (this.started)
            return;

        this.progressBar.IsVisible = status.Progress is not null;
        this.progressBar.Progress = status.Progress?.Fraction ?? 0;

        this.statusLabel.Text = status.State switch
        {
            WebAppHostState.CheckingForUpdate => "Checking for updates…",
            WebAppHostState.Downloading => status.Progress is { } p ? $"Downloading update… {p.Fraction:P0}" : "Downloading update…",
            WebAppHostState.Failed => status.Error,
            _ => String.Empty
        };
    });

    void ShowError(string? message)
    {
        this.loading.IsVisible = true;
        this.WebView.IsVisible = false;
        this.spinner.IsRunning = false;
        this.spinner.IsVisible = false;
        this.progressBar.IsVisible = false;
        this.statusLabel.Text = message;
        this.retryButton.IsVisible = this.host is not null;
    }
}

/// <summary>A page holding only a <see cref="WebAppHostView"/>.</summary>
public class WebAppHostPage : ContentPage
{
    public WebAppHostPage()
    {
        this.HostView = new WebAppHostView();
        this.Content = this.HostView;
    }

    public WebAppHostView HostView { get; }
}
