using Shiny.AppDeviceBridge.Camera.Client;
using ContractAccess = Shiny.AppDeviceBridge.Client.AccessState;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// Whether a camera screen is open on the device, and the way to it from the bridge. The HTTP routes run on the server's
/// threads and know nothing about pages; a camera lives on a view, on the UI thread, only while that view is on screen.
/// This singleton is what holds "is there a camera right now" between the two.
/// <para>
/// <b>Nothing attached is a normal state.</b> A phone in a pocket, an app showing something else: there is no camera, and
/// iOS will not run one in the background at all. Commands then fail with <c>camera_not_open</c>, and the status says so,
/// which lets a page say "open the camera on the device" — or ask for it with <see cref="RequestOpenAsync"/>.
/// </para>
/// </summary>
public sealed class CameraBridgeSession
{
    readonly CameraBridgeOptions options;
    readonly ICameraBridgePresenter presenter;
    readonly Lock gate = new();
    ICameraBridgeController? controller;
    int publishing;
    int dirty;

    internal CameraBridgeSession(CameraBridgeOptions options, ICameraBridgePresenter presenter)
    {
        this.options = options;
        this.presenter = presenter;

        // The preview outlives every camera screen, so it asks the session, which forwards to whichever is attached.
        this.Preview.StreamingChanged += streaming => _ = this.OnUi(() =>
        {
            this.Current?.SetPreviewStreaming(streaming);
            return Task.CompletedTask;
        });
    }

    /// <summary><c>camera.status</c>, mapped by <see cref="CameraBridge"/>.</summary>
    internal WebAppEventSource<CameraStatus> Statuses { get; } = new();

    /// <summary>The viewfinder's frames. A camera screen publishes to it while <see cref="CameraPreview.HasViewers"/>.</summary>
    public CameraPreview Preview { get; } = new();

    /// <summary>The options the bridge was added with.</summary>
    public CameraBridgeOptions Options => this.options;

    /// <summary>Whether a camera screen is attached right now.</summary>
    public bool IsLive => this.Current is not null;

    /// <summary>
    /// A page asked the device to show its camera, and none is open. Set <see cref="CameraOpenRequestedEventArgs.Handled"/>
    /// after navigating to a camera screen of your own; otherwise, with <see cref="CameraBridgeOptions.PresentWhenOpened"/>,
    /// the bridge shows its own. Raised on the UI thread.
    /// </summary>
    public event EventHandler<CameraOpenRequestedEventArgs>? OpenRequested;

    /// <summary>A page asked the device to close its camera. Raised on the UI thread, before the bridge closes its own screen.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Makes a camera screen the live camera. Dispose the result when it leaves the screen. A screen attached while another
    /// is still attached replaces it.
    /// </summary>
    public IDisposable Attach(ICameraBridgeController camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        lock (this.gate)
            this.controller = camera;

        // A viewer can already be watching: somebody left the viewfinder up, and the camera has just come back. Delivery is
        // off on a fresh control, so the standing viewers have to be re-applied or the stream stays black.
        if (this.Preview.HasViewers)
            camera.SetPreviewStreaming(true);

        this.NotifyChanged();
        return new Attachment(this, camera);
    }

    void Detach(ICameraBridgeController leaving)
    {
        lock (this.gate)
        {
            // Only if it is still the live one: a fast switch can attach the next screen before the last one's teardown.
            if (!ReferenceEquals(this.controller, leaving))
                return;

            this.controller = null;
        }

        this.NotifyChanged();
    }

    /// <summary>
    /// Tells the page the camera changed. A camera screen calls it whenever anything a viewer would draw differently moves.
    /// Coalesced: a burst of changes becomes the latest status, once. Nothing is read while nobody listens for
    /// <c>camera.status</c>.
    /// </summary>
    public void NotifyChanged()
    {
        if (!this.Statuses.HasListeners)
            return;

        Interlocked.Exchange(ref this.dirty, 1);
        if (Interlocked.CompareExchange(ref this.publishing, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref this.dirty, 0) == 1)
                    this.Statuses.Publish(await this.GetStatusAsync().ConfigureAwait(false));
            }
            catch (Exception)
            {
                // A camera tearing down mid-read. The next change publishes again.
            }
            finally
            {
                Interlocked.Exchange(ref this.publishing, 0);

                if (Volatile.Read(ref this.dirty) == 1)
                    this.NotifyChanged();
            }
        });
    }

    /// <summary>The camera's state, from anywhere. A not-live status, never an exception, when no camera is attached.</summary>
    public async Task<CameraStatus> GetStatusAsync()
    {
        var access = CameraPermission.IsSupported ? await CameraPermission.GetAccessAsync().ConfigureAwait(false) : ContractAccess.NotSupported;
        var notLive = new CameraStatus(
            CameraPermission.IsSupported,
            access,
            Live: false,
            Message: CameraPermission.IsSupported ? "No camera screen is open on the device." : "This device has no camera."
        );

        if (!this.IsLive)
            return notLive;

        try
        {
            var snapshot = await this.OnUi(() => Task.FromResult(this.Current?.Snapshot())).ConfigureAwait(false);
            return snapshot is null ? notLive : snapshot with { Supported = CameraPermission.IsSupported, Access = access, Live = true };
        }
        catch (Exception)
        {
            // The screen went between the check and the read. Status is asked constantly; a race is not a 500.
            return notLive;
        }
    }

    /// <summary>Runs a command against the live camera on the UI thread.</summary>
    /// <exception cref="CameraBridgeException"><c>camera_not_open</c> when no camera screen is attached.</exception>
    public Task<T> InvokeAsync<T>(Func<ICameraBridgeController, Task<T>> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return this.OnUi(() => command(this.Current ?? throw CameraBridgeException.NotOpen()));
    }

    /// <summary>
    /// Asks the device to show its camera. Nothing happens when one is already open; otherwise
    /// <see cref="OpenRequested"/> is raised, and the bridge shows its own camera screen if nothing handled it.
    /// </summary>
    public Task RequestOpenAsync() => this.OnUi(async () =>
    {
        if (this.IsLive)
            return;

        var args = new CameraOpenRequestedEventArgs();
        this.OpenRequested?.Invoke(this, args);

        if (!args.Handled && this.options.PresentWhenOpened)
            await this.presenter.PresentAsync().ConfigureAwait(false);
    });

    /// <summary>Asks the device to close its camera: raises <see cref="CloseRequested"/>, then closes the bridge's own screen if it is showing.</summary>
    public Task RequestCloseAsync() => this.OnUi(async () =>
    {
        this.CloseRequested?.Invoke(this, EventArgs.Empty);
        await this.presenter.DismissAsync().ConfigureAwait(false);
    });

    ICameraBridgeController? Current
    {
        get
        {
            lock (this.gate)
                return this.controller;
        }
    }

    /// <summary>On the UI thread when there is an app to have one; directly otherwise.</summary>
    Task<T> OnUi<T>(Func<Task<T>> action)
        => Application.Current?.Dispatcher is { IsDispatchRequired: true } dispatcher ? dispatcher.DispatchAsync(action) : action();

    Task OnUi(Func<Task> action)
        => Application.Current?.Dispatcher is { IsDispatchRequired: true } dispatcher ? dispatcher.DispatchAsync(action) : action();

    sealed class Attachment(CameraBridgeSession session, ICameraBridgeController camera) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                session.Detach(camera);
        }
    }
}

public sealed class CameraOpenRequestedEventArgs : EventArgs
{
    /// <summary>Set after showing a camera screen of your own, so the bridge does not show its own as well.</summary>
    public bool Handled { get; set; }
}

/// <summary>Shows and hides the bridge's own camera screen.</summary>
interface ICameraBridgePresenter
{
    Task PresentAsync();

    Task DismissAsync();
}

/// <summary>The bridge's camera screen, pushed modally over whatever the first window shows — the web app, usually.</summary>
sealed class MauiCameraBridgePresenter : ICameraBridgePresenter
{
    CameraBridgePage? shown;

    public async Task PresentAsync()
    {
        if (this.shown is not null || Application.Current?.Windows.FirstOrDefault()?.Page is not { } host)
            return;

        var page = new CameraBridgePage();
        page.Closed += (_, _) =>
        {
            if (ReferenceEquals(this.shown, page))
                this.shown = null;
        };

        this.shown = page;
        await host.Navigation.PushModalAsync(page, true);
    }

    public async Task DismissAsync()
    {
        if (this.shown is not { } page)
            return;

        this.shown = null;
        var navigation = page.Navigation;

        // Only the bridge's own screen, and only when it is on top: anything the app pushed over it is the app's.
        if (navigation.ModalStack.Count > 0 && ReferenceEquals(navigation.ModalStack[^1], page))
            await navigation.PopModalAsync(true);
    }
}
