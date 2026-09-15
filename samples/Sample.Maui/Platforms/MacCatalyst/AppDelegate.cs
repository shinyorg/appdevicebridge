using Foundation;
using UIKit;

namespace Sample.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // HTTP transfers bridge: iOS relaunches the app to deliver a background NSURLSession's finished transfers, and
    // neither MAUI nor Shiny.Hosting.Maui forwards this. MauiUIApplicationDelegate has no virtual for it, so it is
    // exported under the delegate selector instead of overridden.
    [Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
    public void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
        => Shiny.Hosting.Host.Lifecycle.OnHandleEventsForBackgroundUrl(sessionIdentifier, completionHandler);
}
