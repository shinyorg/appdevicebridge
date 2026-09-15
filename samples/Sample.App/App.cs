using Shiny.WebAppHost.Maui;

namespace Sample;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new WebAppHostPage()) { Title = "WebAppHost Sample" };
}
