#if WINDOWS
using Microsoft.Web.WebView2.Core;

namespace Shiny.AppDeviceBridge.WebView;

static partial class WebAppWebViewPermissions
{
    static partial void AttachPlatform(Microsoft.Maui.Controls.WebView view, WebAppWebPermissionPolicy policy)
    {
        if (policy.Allowed == WebAppWebPermissions.None || view.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 platformView)
            return;

        if (platformView.CoreWebView2 is { } core)
            Subscribe(core, policy);
        else
            platformView.CoreWebView2Initialized += (sender, _) =>
            {
                if (sender.CoreWebView2 is { } initialized)
                    Subscribe(initialized, policy);
            };
    }

    static void Subscribe(CoreWebView2 core, WebAppWebPermissionPolicy policy) => core.PermissionRequested += async (_, args) =>
    {
        // Deciding here, allow or deny, replaces WebView2's own prompt.
        using var deferral = args.GetDeferral();

        try
        {
            var permission = args.PermissionKind switch
            {
                CoreWebView2PermissionKind.Camera => WebAppWebPermissions.Camera,
                CoreWebView2PermissionKind.Microphone => WebAppWebPermissions.Microphone,
                CoreWebView2PermissionKind.Geolocation => WebAppWebPermissions.Geolocation,
                _ => WebAppWebPermissions.None
            };

            if (permission == WebAppWebPermissions.None)
                return;

            var allow = policy.Allows(permission) && policy.IsHostOrigin(args.Uri);

            if (allow && permission == WebAppWebPermissions.Geolocation)
                allow = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.LocationWhenInUse>()) == PermissionStatus.Granted;

            args.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        catch (Exception)
        {
            args.State = CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    };
}
#endif
