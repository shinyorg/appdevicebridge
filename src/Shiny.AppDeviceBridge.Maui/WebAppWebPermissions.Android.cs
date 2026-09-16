#if ANDROID
using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AWebView = Android.Webkit.WebView;

namespace Shiny.AppDeviceBridge.Maui;

static partial class WebAppWebViewPermissions
{
    static partial void AttachPlatform(Microsoft.Maui.Controls.WebView view, WebAppWebPermissionPolicy policy)
    {
        if (policy.Allowed == WebAppWebPermissions.None || view.Handler is not WebViewHandler handler || handler.PlatformView is not { } platformView)
            return;

        if (policy.Allows(WebAppWebPermissions.Geolocation))
            platformView.Settings.SetGeolocationEnabled(true);

        // A camera preview is a <video> fed by getUserMedia, usually started after an await, by which time the tap
        // that allowed playback has expired.
        if (policy.Allows(WebAppWebPermissions.Camera) || policy.Allows(WebAppWebPermissions.Microphone))
            platformView.Settings.MediaPlaybackRequiresUserGesture = false;

        // Replaces the client MAUI mapped a moment ago, keeping everything it does: the file chooser, full-screen video.
        platformView.SetWebChromeClient(new WebAppWebChromeClient(handler, policy));
    }
}

/// <summary>
/// MAUI's chrome client plus the decisions it leaves out: an Android WebView denies every media and geolocation
/// request unless a client grants it, and it cannot ask for runtime permissions itself.
/// </summary>
sealed class WebAppWebChromeClient(WebViewHandler handler, WebAppWebPermissionPolicy policy) : MauiWebChromeClient(handler)
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is not null)
            _ = this.DecideAsync(request);
    }

    public override void OnGeolocationPermissionsShowPrompt(string? origin, GeolocationPermissions.ICallback? callback)
    {
        if (callback is not null)
            _ = DecideGeolocationAsync(origin, callback);
    }

    public override bool OnShowFileChooser(AWebView webView, IValueCallback filePathCallback, FileChooserParams fileChooserParams)
    {
        // The stock chooser ignores `capture` and opens the file picker; the page asked for the camera.
        if (fileChooserParams.IsCaptureEnabled
            && policy.Allows(WebAppWebPermissions.Camera)
            && policy.IsHostOrigin(webView.Url)
            && MediaPicker.Default.IsCaptureSupported)
        {
            _ = CaptureAsync(filePathCallback, fileChooserParams);
            return true;
        }

        return base.OnShowFileChooser(webView, filePathCallback, fileChooserParams);
    }

    async Task DecideAsync(PermissionRequest request)
    {
        var granted = new List<string>();

        try
        {
            if (policy.IsHostOrigin(request.Origin?.ToString()))
            {
                foreach (var resource in request.GetResources() ?? [])
                {
                    if (resource == PermissionRequest.ResourceVideoCapture
                        && policy.Allows(WebAppWebPermissions.Camera)
                        && await RequestAsync<Permissions.Camera>())
                    {
                        granted.Add(resource);
                    }
                    else if (resource == PermissionRequest.ResourceAudioCapture
                             && policy.Allows(WebAppWebPermissions.Microphone)
                             && await RequestAsync<Permissions.Microphone>())
                    {
                        granted.Add(resource);
                    }
                }
            }
        }
        catch (Exception)
        {
            // PermissionException when the manifest does not declare the permission: deny rather than crash.
            granted.Clear();
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (granted.Count > 0)
                request.Grant([.. granted]);
            else
                request.Deny();
        });
    }

    async Task DecideGeolocationAsync(string? origin, GeolocationPermissions.ICallback callback)
    {
        var allow = false;

        try
        {
            allow = policy.Allows(WebAppWebPermissions.Geolocation)
                    && policy.IsHostOrigin(origin)
                    && await RequestAsync<Permissions.LocationWhenInUse>();
        }
        catch (Exception)
        {
            allow = false;
        }

        // retain: false, so a permission revoked in settings is asked about again.
        MainThread.BeginInvokeOnMainThread(() => callback.Invoke(origin, allow, false));
    }

    static async Task CaptureAsync(IValueCallback callback, FileChooserParams parameters)
    {
        Android.Net.Uri[]? result = null;

        try
        {
            var accept = parameters.GetAcceptTypes() ?? [];
            var video = accept.Any(x => x.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        && !accept.Any(x => x.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

            var file = await MainThread.InvokeOnMainThreadAsync(() => video
                ? MediaPicker.Default.CaptureVideoAsync()
                : MediaPicker.Default.CapturePhotoAsync()
            );

            if (file is not null)
                result = [ToContentUri(file.FullPath)];
        }
        catch (Exception)
        {
            // Cancelled, or the camera permission was refused: the input simply gets no file.
        }

        // The callback must be answered exactly once, or the input never opens again.
        MainThread.BeginInvokeOnMainThread(() => callback.OnReceiveValue(result));
    }

    static Android.Net.Uri ToContentUri(string path)
    {
        var file = new Java.IO.File(path);
        var context = Android.App.Application.Context;

        try
        {
            // Essentials declares this provider, and captures land in the cache directories it shares.
            return AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileProvider", file)!;
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            return Android.Net.Uri.FromFile(file)!;
        }
    }

    static Task<bool> RequestAsync<TPermission>() where TPermission : Permissions.BasePermission, new()
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var status = await Permissions.CheckStatusAsync<TPermission>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<TPermission>();

            return status == PermissionStatus.Granted;
        });
}
#endif
