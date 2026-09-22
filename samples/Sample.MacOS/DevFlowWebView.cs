#if DEBUG
using System.Text.Json;
using System.Text.Json.Nodes;
using Foundation;
using Microsoft.Maui.DevFlow.Agent.Core;
using WebKit;
using MauiWebView = Microsoft.Maui.Controls.WebView;

namespace Sample.MacOS;

/// <summary>
/// DevFlow only speaks CDP to a BlazorWebView; the web app here is a plain WebView. This registers it with the DevFlow
/// agent and answers <c>Runtime.evaluate</c>, which is enough to drive the page from the CLI
/// (<c>maui devflow webview Runtime evaluate "…"</c>) without taking over the real mouse and keyboard.
/// </summary>
static class DevFlowWebView
{
    public static MauiAppBuilder AddDevFlowWebView(this MauiAppBuilder builder)
    {
        Microsoft.Maui.Platforms.MacOS.Handlers.WebViewHandler.Mapper.AppendToMapping("DevFlowCdp", (handler, view) =>
        {
            if (view is not MauiWebView webView || handler.PlatformView is not WKWebView platform)
                return;

            if (handler.MauiContext?.Services.GetService<DevFlowAgentService>() is not { } agent)
                return;

            var id = agent.RegisterCdpWebView(json => EvaluateAsync(platform, json), () => !platform.IsLoading, webView.AutomationId, null, platform.Url?.AbsoluteString);
            webView.HandlerChanging += (_, _) => agent.UnregisterCdpWebView(id);
        });

        return builder;
    }

    static async Task<string> EvaluateAsync(WKWebView webView, string json)
    {
        var command = JsonNode.Parse(json)!;
        var id = command["id"]?.GetValue<int>() ?? 0;
        var method = command["method"]?.GetValue<string>();

        if (method != "Runtime.evaluate")
            return new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} is not supported; only Runtime.evaluate." } }.ToJsonString();

        var expression = command["params"]?["expression"]?.GetValue<string>() ?? "undefined";

        try
        {
            // Indirect eval runs the expression in the page's global scope; a promise is awaited.
            var result = await MainThread.InvokeOnMainThreadAsync(() => webView.CallAsyncJavaScriptAsync(
                "const v = await (0, eval)(expression); return v === undefined ? 'null' : JSON.stringify(v);",
                new NSDictionary<NSString, NSObject>(new NSString("expression"), new NSString(expression)),
                null,
                WKContentWorld.Page));

            var value = JsonNode.Parse(result?.ToString() ?? "null");
            return new JsonObject
            {
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["result"] = new JsonObject { ["type"] = TypeOf(value), ["value"] = value }
                }
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["result"] = new JsonObject { ["type"] = "object", ["subtype"] = "error", ["description"] = ex.Message },
                    ["exceptionDetails"] = new JsonObject { ["text"] = ex.Message }
                }
            }.ToJsonString();
        }
    }

    static string TypeOf(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        null or JsonValueKind.Null => "object",
        _ => "object"
    };
}
#endif
