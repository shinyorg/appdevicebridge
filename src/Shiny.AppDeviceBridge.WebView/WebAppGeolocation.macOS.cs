#if MACOS
using System.Globalization;
using System.Runtime.CompilerServices;
using CoreLocation;
using Foundation;
using WebKit;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// <c>navigator.geolocation</c> for the AppKit head. WebKit on macOS gives an app's WKWebView no location provider of
/// its own, so every request is denied or never answered. This replaces the page's <c>navigator.geolocation</c> with one
/// that asks CoreLocation through the host, for the host's own origin only.
/// </summary>
sealed class WebAppGeolocation : WKScriptMessageHandler
{
    const string HandlerName = "shinyGeolocation";

    // One per WKWebView: a handler reconnect attaches again, and WebKit refuses a second handler of the same name.
    static readonly ConditionalWeakTable<WKWebView, WebAppGeolocation> Attached = new();

    // Errors use the GeolocationPositionError codes: 1 PERMISSION_DENIED, 2 POSITION_UNAVAILABLE.
    const string Script = """
        (() => {
            const handler = window.webkit?.messageHandlers?.shinyGeolocation;
            if (!handler || !navigator.geolocation) return;

            const pending = new Set();
            const watches = new Map();
            let nextWatch = 1;
            let last = null;
            let running = false;

            const error = (code, message) => ({ code, message, PERMISSION_DENIED: 1, POSITION_UNAVAILABLE: 2, TIMEOUT: 3 });
            const update = () => {
                const wanted = pending.size > 0 || watches.size > 0;
                if (wanted !== running) {
                    running = wanted;
                    handler.postMessage(wanted ? "start" : "stop");
                }
            };

            window.__shinyGeolocation = message => {
                if (message.position) last = message.position;
                for (const request of [...pending]) {
                    pending.delete(request);
                    clearTimeout(request.timer);
                    message.position ? request.success(message.position) : request.failure?.(error(message.code, message.message));
                }
                for (const watch of watches.values())
                    message.position ? watch.success(message.position) : watch.failure?.(error(message.code, message.message));
                update();
            };

            const getCurrentPosition = (success, failure, options) => {
                const maximumAge = options?.maximumAge ?? 0;
                if (last && Date.now() - last.timestamp <= maximumAge) {
                    setTimeout(() => success(last));
                    return;
                }
                const request = { success, failure };
                const timeout = options?.timeout ?? Infinity;
                if (Number.isFinite(timeout))
                    request.timer = setTimeout(() => {
                        pending.delete(request);
                        failure?.(error(3, "Timeout expired"));
                        update();
                    }, Math.max(0, timeout));
                pending.add(request);
                update();
            };

            const watchPosition = (success, failure) => {
                const id = nextWatch++;
                watches.set(id, { success, failure });
                update();
                return id;
            };

            const clearWatch = id => {
                watches.delete(id);
                update();
            };

            Object.defineProperty(navigator, "geolocation", {
                configurable: true,
                value: { getCurrentPosition, watchPosition, clearWatch }
            });
        })();
        """;

    readonly WeakReference<WKWebView> webView;
    readonly WebAppWebPermissionPolicy policy;
    readonly CLLocationManager manager = new();
    readonly Delegate locationDelegate;
    bool wanted;

    WebAppGeolocation(WKWebView webView, WebAppWebPermissionPolicy policy)
    {
        this.webView = new(webView);
        this.policy = policy;
        this.locationDelegate = new Delegate(this);
        this.manager.Delegate = this.locationDelegate;
    }

    public static void Attach(WKWebView webView, WebAppWebPermissionPolicy policy)
    {
        if (!policy.Allows(WebAppWebPermissions.Geolocation) || Attached.TryGetValue(webView, out _))
            return;

        var geolocation = new WebAppGeolocation(webView, policy);
        Attached.Add(webView, geolocation);

        var content = webView.Configuration.UserContentController;
        content.AddScriptMessageHandler(geolocation, HandlerName);
        content.AddUserScript(new WKUserScript(new NSString(Script), WKUserScriptInjectionTime.AtDocumentStart, true));
    }

    public override void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        // Only the web app's own page: a site the user navigated to, or a frame inside the page, gets nothing.
        var origin = message.FrameInfo.SecurityOrigin;
        if (!message.FrameInfo.MainFrame || !this.policy.IsHostOrigin(origin.Protocol, origin.Host, (int)origin.Port))
            return;

        switch ((message.Body as NSString)?.ToString())
        {
            case "start":
                this.wanted = true;
                this.Start();
                break;

            case "stop":
                this.wanted = false;
                this.manager.StopUpdatingLocation();
                break;
        }
    }

    void Start()
    {
        switch (this.manager.AuthorizationStatus)
        {
            case CLAuthorizationStatus.NotDetermined:
                // The answer arrives in DidChangeAuthorization, which starts updates.
                this.manager.RequestWhenInUseAuthorization();
                break;

            case CLAuthorizationStatus.Denied:
            case CLAuthorizationStatus.Restricted:
                this.SendError(1, "User denied Geolocation");
                break;

            default:
                this.manager.StartUpdatingLocation();
                break;
        }
    }

    void Send(string message)
    {
        if (this.webView.TryGetTarget(out var webView))
            webView.EvaluateJavaScript($"window.__shinyGeolocation?.({message})", null);
    }

    void SendError(int code, string message) => this.Send(
        $"{{\"code\":{code},\"message\":\"{message}\"}}"
    );

    void SendPosition(CLLocation location)
    {
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Optional(bool known, double value) => known ? Number(value) : "null";

        var c = location.Coordinate;
        var timestamp = (long)(location.Timestamp.SecondsSince1970 * 1000);

        this.Send(
            "{\"position\":{\"coords\":{"
            + $"\"latitude\":{Number(c.Latitude)},\"longitude\":{Number(c.Longitude)},"
            + $"\"accuracy\":{Number(location.HorizontalAccuracy)},"
            + $"\"altitude\":{Optional(location.VerticalAccuracy >= 0, location.Altitude)},"
            + $"\"altitudeAccuracy\":{Optional(location.VerticalAccuracy >= 0, location.VerticalAccuracy)},"
            + $"\"heading\":{Optional(location.Course >= 0, location.Course)},"
            + $"\"speed\":{Optional(location.Speed >= 0, location.Speed)}"
            + $"}},\"timestamp\":{timestamp}}}}}"
        );
    }

    sealed class Delegate(WebAppGeolocation owner) : CLLocationManagerDelegate
    {
        public override void DidChangeAuthorization(CLLocationManager manager)
        {
            if (owner.wanted && manager.AuthorizationStatus != CLAuthorizationStatus.NotDetermined)
                owner.Start();
        }

        public override void LocationsUpdated(CLLocationManager manager, CLLocation[] locations)
        {
            if (locations.LastOrDefault() is { } location)
                owner.SendPosition(location);
        }

        public override void Failed(CLLocationManager manager, NSError error)
        {
            if (error.Code == (nint)(long)CLError.Denied)
                owner.SendError(1, "User denied Geolocation");
            else if (error.Code != (nint)(long)CLError.LocationUnknown)
                owner.SendError(2, "Position unavailable");
        }
    }
}
#endif
