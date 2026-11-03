using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Maui;
using MauiWebView = Microsoft.Maui.Controls.WebView;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// The web app, full size, with a splash screen in front of it until the page has rendered.
/// <code>
/// public App() => MainPage = new ContentPage { Content = new WebAppHostView() };
/// </code>
/// <para>
/// Starts the host when it first appears. The splash checks for updates, shows a required web app update installing with
/// its release notes and progress, and stops with a message when the app cannot go on — the native app must update from
/// the store (with an <b>Update</b> button), or a required update failed (with <b>Try again</b>). Once the page has loaded it
/// waits for the page's <c>ready</c> signal, up to <see cref="WebAppHostOptions.PageReadyTimeout"/>, so the user never sees a
/// half-drawn page. Restarts the loopback server when the window resumes.
/// </para>
/// <para>
/// Replace the look with <see cref="SplashTemplate"/>, bound to a <see cref="WebAppSplashContext"/>; change the words with
/// <see cref="SplashText"/>.
/// </para>
/// </summary>
public class WebAppHostView : ContentView
{
    /// <summary>The splash screen, bound to a <see cref="WebAppSplashContext"/>. Null shows the built-in one.</summary>
    public static readonly BindableProperty SplashTemplateProperty = BindableProperty.Create(
        nameof(SplashTemplate),
        typeof(DataTemplate),
        typeof(WebAppHostView),
        propertyChanged: (b, _, _) => ((WebAppHostView)b).BuildSplash()
    );

    readonly ContentView splash = new();
    readonly Grid root;

    WebAppHost? host;
    WebAppHostOptions? options;
    Window? window;
    bool attached;
    bool started;
    bool starting;
    TaskCompletionSource? navigated;

    public WebAppHostView()
    {
        // Never hidden: the splash screen covers it until the page is ready. Visibility changes are not applied to the native
        // view by every backend — the maui-labs AppKit one left the WKWebView hidden after IsVisible went back to true.
        this.WebView = new MauiWebView();

        // The WebView's handler can arrive before this view's, and a new one arrives with every reconnect.
        this.WebView.HandlerChanged += (_, _) => WebAppWebViewPermissions.Attach(this.WebView);
        this.WebView.Navigated += (_, _) => this.navigated?.TrySetResult();

        this.Splash = new WebAppSplashContext(
            this.SplashText,
            new Command(async () => await this.StartAsync()),
            new Command(async () => await this.OpenStoreAsync())
        );

        this.splash.BindingContext = this.Splash;
        this.BuildSplash();

        // The splash sits over the WebView, which loads underneath it: some platforms will not load a hidden WebView.
        this.root = new Grid { Children = { this.WebView, this.splash } };
        this.Content = this.root;

        this.Loaded += this.OnLoaded;
        this.Unloaded += this.OnUnloaded;
    }

    public DataTemplate? SplashTemplate
    {
        get => (DataTemplate?)this.GetValue(SplashTemplateProperty);
        set => this.SetValue(SplashTemplateProperty, value);
    }

    /// <summary>The words the splash screen uses. Change them before the view appears.</summary>
    public WebAppSplashText SplashText { get; } = new();

    /// <summary>What the splash screen shows. A <see cref="SplashTemplate"/> binds to it.</summary>
    public WebAppSplashContext Splash { get; }

    /// <summary>Raised on the UI thread when the splash screen comes down and the page shows.</summary>
    public event EventHandler? SplashDismissed;

    /// <summary>The WebView the app loads into, for handler customisation.</summary>
    public MauiWebView WebView { get; }

    /// <summary>Raised on the UI thread once the web app has been handed to the WebView.</summary>
    public event EventHandler<Uri>? Ready;

    /// <summary>
    /// Shows <see cref="TrafficMonitorPage"/> over the web app: every request the server answered, and each one in full. Needs
    /// <see cref="AppDeviceBridgeMauiExtensions.UseTrafficMonitor"/>; wire it to a gesture, a menu item or a button of your
    /// own in a debug build.
    /// </summary>
    public Task ShowTrafficMonitorAsync()
    {
        var services = this.Handler?.MauiContext?.Services ?? IPlatformApplication.Current?.Services;
        var recorder = services?.GetService<TrafficRecorder>()
            ?? throw new InvalidOperationException("TrafficRecorder is not registered. Call UseTrafficMonitor in MauiProgram.");

        return this.Navigation.PushModalAsync(new TrafficMonitorPage(recorder));
    }

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
                this.ShowError("WebAppHost is not registered. Call UseAppDeviceBridge with the web app's options in MauiProgram.");

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
        this.Splash.Restart();
        this.ShowSplash();

        try
        {
            this.navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var uri = await this.host.StartAsync();

            this.WebView.Source = new UrlWebViewSource { Url = uri.AbsoluteUri };
            this.started = true;

            this.Ready?.Invoke(this, uri);

            await this.WaitForPageAsync();
            this.HideSplash();
        }
        catch (WebAppHostUpdateRequiredException ex)
        {
            this.Splash.ShowHostUpdateRequired(ex.Update);
            this.ShowSplash();
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

    /// <summary>
    /// The page's own <c>ready</c> signal when <see cref="WebAppHostOptions.WaitForPageReady"/> is on, the WebView finishing
    /// its navigation when it is off — either way no longer than <see cref="WebAppHostOptions.PageReadyTimeout"/>.
    /// </summary>
    async Task WaitForPageAsync()
    {
        var timeout = this.options?.PageReadyTimeout ?? TimeSpan.FromSeconds(15);
        var signal = this.options?.WaitForPageReady != false ? this.host!.PageReady : this.navigated!.Task;

        await Task.WhenAny(signal, Task.Delay(timeout));
    }

    async Task OpenStoreAsync()
    {
        if (this.host is not null)
            await this.host.OpenStoreAsync();
    }

    // Added and removed rather than hidden: the maui-labs AppKit backend leaves a native view drawn when its IsVisible goes
    // false, which left a white splash over a loaded page.
    void ShowSplash()
    {
        if (!this.root.Children.Contains(this.splash))
            this.root.Children.Add(this.splash);
    }

    void HideSplash()
    {
        if (!this.root.Children.Remove(this.splash))
            return;

        this.SplashDismissed?.Invoke(this, EventArgs.Empty);
    }

    void BuildSplash()
        => this.splash.Content = this.SplashTemplate?.CreateContent() as View ?? CreateDefaultSplash(this.Splash);

    /// <summary>
    /// A spinner, the message, the release notes, progress, and the buttons — over the page's background. Each optional piece
    /// is added to the layout when it applies and removed when it does not, rather than hidden: not every backend applies a
    /// visibility change to the native view.
    /// </summary>
    static View CreateDefaultSplash(WebAppSplashContext context)
    {
        var spinner = new ActivityIndicator { HorizontalOptions = LayoutOptions.Center, IsRunning = true };

        var message = new Label { HorizontalTextAlignment = TextAlignment.Center, HorizontalOptions = LayoutOptions.Center, FontSize = 16 };
        message.SetBinding(Label.TextProperty, static (WebAppSplashContext c) => c.Message);

        var progress = new ProgressBar { WidthRequest = 240, HorizontalOptions = LayoutOptions.Center };
        progress.SetBinding(ProgressBar.ProgressProperty, static (WebAppSplashContext c) => c.Progress);

        // Set by hand: the binding generator emits a setter for a nullable property even one way, and warns about it.
        var notes = new Label { HorizontalTextAlignment = TextAlignment.Start, FontSize = 13, Opacity = 0.8 };
        var notesScroll = new ScrollView { Content = notes, MaximumHeightRequest = 240, MaximumWidthRequest = 480 };

        var store = new Button { HorizontalOptions = LayoutOptions.Center };
        store.SetBinding(Button.TextProperty, static (WebAppSplashContext c) => c.Text.OpenStore);
        store.SetBinding(Button.CommandProperty, static (WebAppSplashContext c) => c.OpenStoreCommand);

        var retry = new Button { HorizontalOptions = LayoutOptions.Center };
        retry.SetBinding(Button.TextProperty, static (WebAppSplashContext c) => c.Text.Retry);
        retry.SetBinding(Button.CommandProperty, static (WebAppSplashContext c) => c.RetryCommand);

        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24), VerticalOptions = LayoutOptions.Center };

        // In display order, each with when it shows.
        (View View, Func<WebAppSplashContext, bool> Shows)[] pieces =
        [
            (spinner, static c => c.IsBusy),
            (message, static _ => true),
            (progress, static c => c.IsProgressVisible),
            (notesScroll, static c => c.HasReleaseNotes),
            (store, static c => c.CanOpenStore),
            (retry, static c => c.CanRetry)
        ];

        void Arrange()
        {
            notes.Text = context.ReleaseNotes;

            var wanted = pieces.Where(x => x.Shows(context)).Select(x => x.View).ToList();
            if (stack.Children.Cast<View>().SequenceEqual(wanted))
                return;

            stack.Children.Clear();
            foreach (var view in wanted)
                stack.Children.Add(view);
        }

        Arrange();
        context.PropertyChanged += (_, _) => Arrange();

        var root = new Grid { Children = { stack } };
        root.SetAppThemeColor(BackgroundColorProperty, Colors.White, Colors.Black);
        return root;
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
        // Once the page is up, background activity is the page's business, not the splash screen's.
        if (this.started)
            return;

        this.Splash.Apply(status);
    });

    void ShowError(string? message)
    {
        this.started = false;
        this.Splash.Stop(message ?? String.Empty);
        this.ShowSplash();
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
