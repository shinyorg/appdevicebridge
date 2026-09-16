using Shiny.AppDeviceBridge.Maui;

namespace Sample;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new WebAppHostPage()) { Title = "AppDeviceBridge Sample" };
}
