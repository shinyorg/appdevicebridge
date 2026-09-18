using System.Net;
using System.Text;
using Shiny.AppDeviceBridge.Simulator.Simulation;

namespace Shiny.AppDeviceBridge.Simulator.Hosting;

/// <summary>What the root answers when the simulator serves no page: how to point one at it, and what it simulates.</summary>
static class LandingPage
{
    public static string Html(SimulatorState state)
    {
        var rows = new StringBuilder();
        foreach (var bridge in state.Bridges)
        {
            rows.Append("<tr><td><code>/_bridge/").Append(WebUtility.HtmlEncode(bridge.Name)).Append("</code></td><td>")
                .Append(bridge.Routes.Count).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(String.Join(", ", bridge.Events.Select(x => x.Event.Name)))).Append("</td><td>")
                .Append(bridge.IsSupported ? "yes" : "501").Append("</td></tr>");
        }

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Shiny.AppDeviceBridge simulator</title>
            <style>
              :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
              body { max-width: 56rem; margin: 3rem auto; padding: 0 1.25rem; line-height: 1.5; }
              code, pre { font-family: ui-monospace, monospace; font-size: .9em; }
              pre { background: color-mix(in srgb, currentColor 8%, transparent); padding: .75rem 1rem; border-radius: .5rem; overflow-x: auto; }
              table { border-collapse: collapse; width: 100%; }
              td, th { text-align: left; padding: .3rem .6rem; border-bottom: 1px solid color-mix(in srgb, currentColor 15%, transparent); }
            </style>
            </head>
            <body>
            <h1>Shiny.AppDeviceBridge simulator</h1>
            <p>The bridges are running, but no page is being served. Restart with one, so the page and the bridges share this origin:</p>
            <pre>shiny-bridge-sim --dev-server http://localhost:5080     # dotnet watch, Vite…
            shiny-bridge-sim --app ./bin/Release/net10.0/publish/wwwroot</pre>
            <p>Platform: <code>{{WebUtility.HtmlEncode(state.Platform)}}</code> · app id: <code>{{WebUtility.HtmlEncode(state.AppId)}}</code></p>
            <table>
            <thead><tr><th>Bridge</th><th>Routes</th><th>Events</th><th>Supported</th></tr></thead>
            <tbody>{{rows}}</tbody>
            </table>
            </body>
            </html>
            """;
    }
}
