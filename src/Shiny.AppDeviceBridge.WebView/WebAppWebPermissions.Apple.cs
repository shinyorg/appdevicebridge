#if IOS || MACCATALYST || MACOS
using System.Runtime.CompilerServices;
using AVFoundation;
using CoreFoundation;
using WebKit;
#if IOS || MACCATALYST
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
#else
using AppKit;
using Foundation;
#endif

namespace Shiny.AppDeviceBridge.WebView;

static partial class WebAppWebViewPermissions
{
    // WKWebView holds its UI delegate weakly.
    static readonly ConditionalWeakTable<WKWebView, WKUIDelegate> Delegates = new();

    static partial void AttachPlatform(Microsoft.Maui.Controls.WebView view, WebAppWebPermissionPolicy policy)
    {
#if IOS || MACCATALYST
        // Media capture is the only decision WebKit hands out here: inline playback is already on in MAUI's
        // configuration, and file inputs and geolocation work from the Info.plist usage descriptions alone.
        if (!policy.Allows(WebAppWebPermissions.Camera) && !policy.Allows(WebAppWebPermissions.Microphone))
            return;

        if (view.Handler is not IWebViewHandler handler || handler.PlatformView is not { } platformView)
            return;

        var uiDelegate = new WebAppWebViewUIDelegate(handler, policy);
#else
        // The maui-labs AppKit handler sets no UI delegate at all, so without this one file inputs do nothing
        // either. It is installed whatever the permissions.
        if (view.Handler?.PlatformView is not WKWebView platformView)
            return;

        var uiDelegate = new WebAppWebViewUIDelegate(policy);
#endif
        Delegates.AddOrUpdate(platformView, uiDelegate);
        platformView.UIDelegate = uiDelegate;
    }

    internal static async void DecideMediaCapture(
        WebAppWebPermissionPolicy policy,
        WKSecurityOrigin origin,
        WKMediaCaptureType type,
        Action<WKPermissionDecision> decisionHandler
    )
    {
        var decision = WKPermissionDecision.Deny;

        try
        {
            if (policy.IsHostOrigin(origin.Protocol, origin.Host, (int)origin.Port))
            {
                var camera = type is WKMediaCaptureType.Camera or WKMediaCaptureType.CameraAndMicrophone;
                var microphone = type is WKMediaCaptureType.Microphone or WKMediaCaptureType.CameraAndMicrophone;

                var allowed = (!camera || (policy.Allows(WebAppWebPermissions.Camera) && await RequestAccessAsync(AVAuthorizationMediaType.Video)))
                              && (!microphone || (policy.Allows(WebAppWebPermissions.Microphone) && await RequestAccessAsync(AVAuthorizationMediaType.Audio)));

                // Grant, not Prompt: the app already decided, and WebKit would otherwise ask again every launch.
                if (allowed)
                    decision = WKPermissionDecision.Grant;
            }
        }
        catch (Exception)
        {
            decision = WKPermissionDecision.Deny;
        }

        DispatchQueue.MainQueue.DispatchAsync(() => decisionHandler(decision));
    }

    static async Task<bool> RequestAccessAsync(AVAuthorizationMediaType media) => AVCaptureDevice.GetAuthorizationStatus(media) switch
    {
        AVAuthorizationStatus.Authorized => true,
        AVAuthorizationStatus.NotDetermined => await AVCaptureDevice.RequestAccessForMediaTypeAsync(media),
        _ => false
    };
}

#if IOS || MACCATALYST
/// <summary>MAUI's UI delegate — alerts, confirms, prompts, context menus — plus the media capture decision.</summary>
sealed class WebAppWebViewUIDelegate(IWebViewHandler handler, WebAppWebPermissionPolicy policy) : MauiWebViewUIDelegate(handler)
{
    public override void RequestMediaCapturePermission(
        WKWebView webView,
        WKSecurityOrigin origin,
        WKFrameInfo frame,
        WKMediaCaptureType type,
        Action<WKPermissionDecision> decisionHandler
    ) => WebAppWebViewPermissions.DecideMediaCapture(policy, origin, type, decisionHandler);
}
#else
sealed class WebAppWebViewUIDelegate(WebAppWebPermissionPolicy policy) : WKUIDelegate
{
    public override void RequestMediaCapturePermission(
        WKWebView webView,
        WKSecurityOrigin origin,
        WKFrameInfo frame,
        WKMediaCaptureType type,
        Action<WKPermissionDecision> decisionHandler
    ) => WebAppWebViewPermissions.DecideMediaCapture(policy, origin, type, decisionHandler);

    public override void RunOpenPanel(WKWebView webView, WKOpenPanelParameters parameters, WKFrameInfo frame, Action<NSUrl[]> completionHandler)
    {
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = parameters.AllowsDirectories;
        panel.AllowsMultipleSelection = parameters.AllowsMultipleSelection;

        void Complete(nint result) => completionHandler(result == (nint)(long)NSModalResponse.OK ? panel.Urls : null!);

        if (webView.Window is { } window)
            panel.BeginSheet(window, Complete);
        else
            Complete(panel.RunModal());
    }
}
#endif
#endif
