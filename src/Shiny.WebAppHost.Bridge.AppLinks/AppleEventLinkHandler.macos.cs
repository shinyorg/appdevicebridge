#if MACOS
using AppKit;
using Foundation;
using ObjCRuntime;

namespace Shiny.WebAppHost.Bridge.AppLinks;

/// <summary>
/// The maui-labs AppKit backend raises no lifecycle event for opened URLs, so the bridge takes the Get URL Apple
/// event itself. Links that are not the web app's go on to the app delegate's <c>application:openURLs:</c>, which
/// is where AppKit would have sent them.
/// </summary>
sealed class AppleEventLinkHandler : NSObject
{
    const string Selector = "handleGetURLEvent:withReplyEvent:";
    const uint DirectObjectKeyword = 0x2D2D2D2D;    // '----'

    // The event manager does not retain its handler.
    static AppleEventLinkHandler? registered;

    readonly WebAppLinks links;

    AppleEventLinkHandler(WebAppLinks links) => this.links = links;

    public static void Register(WebAppLinks links)
    {
        if (registered is not null)
            return;

        registered = new AppleEventLinkHandler(links);
        NSAppleEventManager.SharedAppleEventManager.SetEventHandler(
            registered,
            new Selector(Selector),
            AEEventClass.Internet,
            AEEventID.GetUrl
        );
    }

    [Export(Selector)]
    public void HandleGetUrl(NSAppleEventDescriptor appleEvent, NSAppleEventDescriptor reply)
    {
        if (appleEvent.ParamDescriptorForKeyword(DirectObjectKeyword)?.StringValue is not { } value)
            return;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && this.links.Receive(uri))
            return;

        var application = NSApplication.SharedApplication;
        if (application.Delegate is NSObject appDelegate
            && appDelegate.RespondsToSelector(new Selector("application:openURLs:"))
            && NSUrl.FromString(value) is { } url)
            ((INSApplicationDelegate)appDelegate).OpenUrls(application, [url]);
    }
}
#endif
