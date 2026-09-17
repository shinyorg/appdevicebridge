using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AppDeviceBridge.Camera;

/// <summary>
/// The bridge's own camera screen: a <see cref="CameraBridgeView"/> filling the page, with a close button, a shutter, and
/// the switches that make sense on this device. Shown when a page asks the device to open its camera — see
/// <see cref="CameraBridgeOptions.PresentWhenOpened"/> — and usable as a page of your own.
/// <para>
/// Dark whatever the theme: the preview is the content, and light chrome around a viewfinder is glare on the thing being
/// framed.
/// </para>
/// </summary>
public class CameraBridgePage : ContentPage
{
    readonly Button close = Chip("Close");
    readonly Button flip = Chip("Flip");
    readonly Button torch = Chip("Torch");
    readonly Button mode = Chip("Video");
    readonly Button shutter = new() { WidthRequest = 160, HeightRequest = 56, CornerRadius = 28, BackgroundColor = Colors.White, TextColor = Colors.Black };
    readonly Label status = new() { TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };

    public CameraBridgePage()
    {
        this.BackgroundColor = Colors.Black;
        this.Title = "Camera";

        this.View = new CameraBridgeView();
        this.View.PropertyChanged += (_, _) => this.Refresh();

        this.close.Clicked += async (_, _) => await this.CloseAsync();
        this.flip.Clicked += (_, _) => this.Run(this.View.Flip);
        this.torch.Clicked += (_, _) => this.Run(() => this.View.Apply(new(TorchOn: !this.View.IsTorchOn)));
        this.mode.Clicked += (_, _) => this.Run(this.View.ToggleMode);
        this.shutter.Clicked += async (_, _) =>
        {
            try
            {
                await this.View.ShutterAsync();
            }
            catch (Exception ex)
            {
                this.status.Text = ex.Message;
            }
        };

        var top = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(12), Children = { this.close, this.flip, this.torch } };
        var bottom = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(12, 12, 12, 24),
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.45),
            VerticalOptions = LayoutOptions.End,
            Children =
            {
                this.status,
                new HorizontalStackLayout { Spacing = 16, HorizontalOptions = LayoutOptions.Center, Children = { this.mode, this.shutter } }
            }
        };

        this.Content = new Grid
        {
            Children = { this.View, new VerticalStackLayout { VerticalOptions = LayoutOptions.Start, Children = { top } }, bottom }
        };

        this.Refresh();
    }

    /// <summary>The camera on this page.</summary>
    public CameraBridgeView View { get; }

    /// <summary>Raised once the page has left the screen for good — popped, not merely covered.</summary>
    public event EventHandler? Closed;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.View.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (this.Navigation.ModalStack.Contains(this) || this.Navigation.NavigationStack.Contains(this))
            return;

        this.View.Stop();
        this.Closed?.Invoke(this, EventArgs.Empty);
    }

    async Task CloseAsync()
    {
        // Through the session, so the app hears about it as it would about a close from a page elsewhere.
        if (this.Handler?.MauiContext?.Services.GetService<CameraBridgeSession>() is { } session)
            await session.RequestCloseAsync();

        if (this.Navigation.ModalStack.Contains(this))
            await this.Navigation.PopModalAsync(true);
        else if (this.Navigation.NavigationStack.Contains(this))
            await this.Navigation.PopAsync(true);
    }

    void Run(Action action)
    {
        try
        {
            action();
        }
        catch (CameraBridgeException ex)
        {
            this.status.Text = ex.Message;
        }
    }

    void Refresh()
    {
        var view = this.View;

        this.shutter.Text = !view.IsVideoMode ? "Take photo" : view.IsRecording ? "Stop" : "Record";
        this.shutter.IsEnabled = view.IsCameraActive && !view.IsBusy;
        this.mode.Text = view.IsVideoMode ? "Photo" : "Video";
        this.mode.IsEnabled = !view.IsRecording;
        this.flip.IsVisible = !view.ChoosesCamera;
        this.flip.IsEnabled = !view.IsRecording;
        this.torch.IsVisible = view.TorchAvailable;
        this.status.Text = view.Message;
    }

    static Button Chip(string text) => new()
    {
        Text = text,
        HeightRequest = 36,
        Padding = new Thickness(14, 0),
        CornerRadius = 18,
        BackgroundColor = Color.FromRgba(0, 0, 0, 0.55),
        TextColor = Colors.White
    };
}
