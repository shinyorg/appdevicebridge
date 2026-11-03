using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// The words the splash screen uses, for an app that shows them in another language or its own voice. The default splash
/// uses all of them; a <see cref="WebAppHostView.SplashTemplate"/> of the app's own reads whichever it likes from
/// <see cref="WebAppSplashContext.Message"/>.
/// </summary>
public sealed class WebAppSplashText
{
    public string Starting { get; set; } = String.Empty;
    public string CheckingForUpdates { get; set; } = "Checking for updates…";

    /// <summary>A required web app update is installing. <c>{0}</c> is its version.</summary>
    public string InstallingRequiredUpdate { get; set; } = "Installing required update {0}…";

    /// <summary>The server no longer accepts this native app. <c>{0}</c> is the minimum version.</summary>
    public string HostUpdateRequired { get; set; } = "This version of the app is no longer supported. Update to version {0} or later to continue.";

    public string HostUpdateRequiredNoVersion { get; set; } = "This version of the app is no longer supported. Update it from the store to continue.";
    public string OpenStore { get; set; } = "Update";
    public string Retry { get; set; } = "Try again";
}

/// <summary>
/// What the splash screen shows, bound to by <see cref="WebAppHostView.SplashTemplate"/>. It follows the host's startup
/// from first check to first render, and stops — <see cref="IsStopped"/> — when the app cannot go on: a required update
/// of the native app from the store, or a required web app update or first install that failed.
/// <code>
/// &lt;DataTemplate&gt;
///   &lt;Grid&gt;
///     &lt;Image Source="logo.png" /&gt;
///     &lt;Label Text="{Binding Message}" /&gt;
///     &lt;ProgressBar Progress="{Binding Progress}" IsVisible="{Binding IsProgressVisible}" /&gt;
///     &lt;Label Text="{Binding ReleaseNotes}" IsVisible="{Binding HasReleaseNotes}" /&gt;
///     &lt;Button Text="Update" Command="{Binding OpenStoreCommand}" IsVisible="{Binding CanOpenStore}" /&gt;
///     &lt;Button Text="Try again" Command="{Binding RetryCommand}" IsVisible="{Binding CanRetry}" /&gt;
///   &lt;/Grid&gt;
/// &lt;/DataTemplate&gt;
/// </code>
/// </summary>
public sealed class WebAppSplashContext : INotifyPropertyChanged
{
    WebAppHostState state = WebAppHostState.NotStarted;
    string message = String.Empty;
    string? releaseNotes;
    string? updateVersion;
    double progress;
    bool isProgressVisible;
    bool isBusy = true;
    bool isStopped;
    bool canRetry;
    bool canOpenStore;
    HostUpdateInfo? hostUpdate;

    internal WebAppSplashContext(WebAppSplashText text, ICommand retry, ICommand openStore)
    {
        this.Text = text;
        this.RetryCommand = retry;
        this.OpenStoreCommand = openStore;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The words in use, for labels of a template's own.</summary>
    public WebAppSplashText Text { get; }

    public WebAppHostState State { get => this.state; private set => this.Set(ref this.state, value); }

    /// <summary>What is happening, or why the app stopped, ready to show.</summary>
    public string Message { get => this.message; private set => this.Set(ref this.message, value); }

    /// <summary>
    /// What the update changes: the required web app release while it installs, or the store build when the native app must
    /// update. In the user's language when the server has it.
    /// </summary>
    public string? ReleaseNotes
    {
        get => this.releaseNotes;
        private set
        {
            if (this.Set(ref this.releaseNotes, value))
                this.OnPropertyChanged(nameof(this.HasReleaseNotes));
        }
    }

    public bool HasReleaseNotes => !String.IsNullOrWhiteSpace(this.releaseNotes);

    /// <summary>The web app version being installed, or the native app version the store must deliver.</summary>
    public string? UpdateVersion { get => this.updateVersion; private set => this.Set(ref this.updateVersion, value); }

    /// <summary>A required download's progress, 0 to 1.</summary>
    public double Progress { get => this.progress; private set => this.Set(ref this.progress, value); }

    public bool IsProgressVisible { get => this.isProgressVisible; private set => this.Set(ref this.isProgressVisible, value); }

    /// <summary>Startup is working on something: show a spinner.</summary>
    public bool IsBusy { get => this.isBusy; private set => this.Set(ref this.isBusy, value); }

    /// <summary>The app cannot go on until the user acts: the splash screen stays, with <see cref="Message"/>.</summary>
    public bool IsStopped { get => this.isStopped; private set => this.Set(ref this.isStopped, value); }

    public bool CanRetry { get => this.canRetry; private set => this.Set(ref this.canRetry, value); }

    /// <summary>The native app must update from the store: show <see cref="OpenStoreCommand"/>.</summary>
    public bool CanOpenStore { get => this.canOpenStore; private set => this.Set(ref this.canOpenStore, value); }

    /// <summary>The native app update, when the server sent one.</summary>
    public HostUpdateInfo? HostUpdate { get => this.hostUpdate; private set => this.Set(ref this.hostUpdate, value); }

    /// <summary>Starts the host again — after a failure, or after returning from the store.</summary>
    public ICommand RetryCommand { get; }

    /// <summary>Opens the store for the native app update.</summary>
    public ICommand OpenStoreCommand { get; }

    internal void Apply(WebAppHostStatus status)
    {
        this.State = status.State;
        this.HostUpdate = status.HostUpdate;

        switch (status.State)
        {
            case WebAppHostState.Downloading when status.Update is { } update:
                this.Show(String.Format(this.Text.InstallingRequiredUpdate, update.Version), busy: true);
                this.UpdateVersion = update.Version.ToString();
                this.ReleaseNotes = update.WhatsNew;
                this.IsProgressVisible = status.Progress is { TotalBytes: > 0 };
                this.Progress = status.Progress?.Fraction ?? 0;
                break;

            case WebAppHostState.HostUpdateRequired when status.HostUpdate is { } host:
                this.ShowHostUpdateRequired(host);
                break;

            case WebAppHostState.CheckingForUpdate:
                this.Show(this.Text.CheckingForUpdates, busy: true);
                break;

            case WebAppHostState.Failed:
                this.Stop(status.Error ?? String.Empty);
                break;

            case WebAppHostState.Starting or WebAppHostState.NotStarted:
                this.Show(this.Text.Starting, busy: true);
                break;
        }
    }

    internal void ShowHostUpdateRequired(HostUpdateInfo host)
    {
        this.Stop(host.MinimumVersion is { } minimum
            ? String.Format(this.Text.HostUpdateRequired, minimum)
            : this.Text.HostUpdateRequiredNoVersion);

        this.State = WebAppHostState.HostUpdateRequired;
        this.HostUpdate = host;
        this.UpdateVersion = host.LatestVersion ?? host.MinimumVersion;
        this.ReleaseNotes = host.WhatsNew;
        this.CanOpenStore = true;
    }

    internal void Stop(string message)
    {
        this.Message = message;
        this.IsBusy = false;
        this.IsStopped = true;
        this.IsProgressVisible = false;
        this.CanRetry = true;
        this.CanOpenStore = false;
    }

    internal void Restart() => this.Show(this.Text.Starting, busy: true);

    void Show(string message, bool busy)
    {
        this.Message = message;
        this.IsBusy = busy;
        this.IsStopped = false;
        this.CanRetry = false;
        this.CanOpenStore = false;
        this.IsProgressVisible = false;
        this.ReleaseNotes = null;
        this.UpdateVersion = null;
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        this.OnPropertyChanged(name);
        return true;
    }

    void OnPropertyChanged(string? name) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
