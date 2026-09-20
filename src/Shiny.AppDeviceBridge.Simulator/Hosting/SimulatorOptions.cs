using System.Globalization;

namespace Shiny.AppDeviceBridge.Simulator.Hosting;

/// <summary>How the simulator is started. Parsed from the command line by <see cref="Parse"/>.</summary>
public sealed class SimulatorOptions
{
    public const int DefaultPort = 5299;

    public static readonly IReadOnlyList<string> Platforms = ["android", "ios", "maccatalyst", "macos", "windows", "linux"];

    /// <summary>The loopback port the page and the bridges are served on; 0 for any free port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>A built web app to serve — a published Blazor <c>wwwroot</c>, or any folder with an <c>index.html</c>.</summary>
    public string? AppDirectory { get; set; }

    /// <summary>A dev server — <c>dotnet watch</c>, Vite — whose pages the simulator serves as its own, so the page and the bridges share an origin.</summary>
    public Uri? DevServer { get; set; }

    public string AppId { get; set; } = "simulator";

    /// <summary>What <c>GET /_bridge/host</c> reports: <see cref="Platforms"/>.</summary>
    public string Platform { get; set; } = "ios";

    /// <summary>A scenario to apply on start.</summary>
    public string? Scenario { get; set; }

    /// <summary>Trails to load: <c>.gpx</c> or <c>.trail.json</c>.</summary>
    public List<string> Trails { get; } = [];

    /// <summary>Trails to play as soon as the server is up, by name — the loaded file's name without its extension.</summary>
    public List<string> Play { get; } = [];

    public double Speed { get; set; } = 1;

    /// <summary>No TUI: serve, apply, play, and print the activity log — for a test run or CI.</summary>
    public bool Headless { get; set; }

    /// <summary>
    /// Serve the MCP control endpoint, so an agent can drive this simulator while it runs. Off unless asked for: it
    /// rewrites what the device answers, and the page is served from the same origin.
    /// </summary>
    public bool Mcp { get; set; }

    /// <summary>
    /// Speak MCP on stdin and stdout, for an agent that starts the simulator itself. Implies <see cref="Headless"/>,
    /// and everything that would have gone to stdout goes to stderr instead — stdout is the protocol.
    /// </summary>
    public bool McpStdio { get; set; }

    /// <summary>The token the MCP endpoint requires. Generated and printed when <see cref="Mcp"/> is on and this is null.</summary>
    public string? McpToken { get; set; }

    /// <summary>Where the settings and files bridges keep their data. A fresh temporary folder when null.</summary>
    public string? DataDirectory { get; set; }

    public bool ShowHelp { get; set; }

    public const string Usage = """
        shiny-bridge-sim — every Shiny.AppDeviceBridge bridge, simulated, with values you set and trails you play.

        usage: shiny-bridge-sim [options]

          --app <dir>            serve a built web app (a published Blazor wwwroot, or any folder with index.html)
          --dev-server <url>     serve pages from a dev server (dotnet watch, Vite), on the bridges' origin
          --port <n>             loopback port (default 5299; 0 for any free port)
          --platform <name>      android | ios | maccatalyst | macos | windows | linux (default ios)
          --app-id <id>          the app id GET /_bridge/host reports (default simulator)
          --scenario <file>      apply a saved scenario on start
          --trail <file>         load a trail: .gpx (a GPS walk) or .trail.json; repeatable
          --play <name>          play a loaded trail on start; repeatable
          --speed <n>            playback speed for --play (default 1)
          --data <dir>           where the settings and files bridges keep data (default: a temp folder)
          --headless             no TUI: serve, apply, play and log to the console
          --mcp                  serve the MCP control endpoint so an AI agent can drive this simulator
          --mcp-token <t>        the token the MCP endpoint requires (default: generated and printed)
          --mcp-stdio            speak MCP on stdin/stdout for an agent that starts the simulator itself (implies --headless)
          -h, --help             this help

        Open the printed URL in a browser. The page and the bridges share that origin, so a page built with
        Shiny.AppDeviceBridge.Blazor or the TypeScript client finds the bridges on its own.
        """;

    /// <exception cref="ArgumentException">An unknown option or a bad value.</exception>
    public static SimulatorOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SimulatorOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "--app":
                    options.AppDirectory = Path.GetFullPath(Next());
                    if (!Directory.Exists(options.AppDirectory))
                        throw new ArgumentException($"--app: {options.AppDirectory} does not exist.");
                    break;

                case "--dev-server":
                    var url = Next();
                    options.DevServer = Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var dev) && dev.Scheme is "http" or "https"
                        ? dev
                        : throw new ArgumentException($"--dev-server: '{url}' is not an http(s) URL.");
                    break;

                case "--port":
                    options.Port = Int32.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= 65535
                        ? port
                        : throw new ArgumentException("--port: expected 0–65535.");
                    break;

                case "--platform":
                    var platform = Next().ToLowerInvariant();
                    options.Platform = Platforms.Contains(platform) ? platform : throw new ArgumentException($"--platform: expected one of {String.Join(", ", Platforms)}.");
                    break;

                case "--app-id":
                    options.AppId = Next();
                    break;

                case "--scenario":
                    options.Scenario = ExistingFile(arg, Next());
                    break;

                case "--trail":
                    options.Trails.Add(ExistingFile(arg, Next()));
                    break;

                case "--play":
                    options.Play.Add(Next());
                    break;

                case "--speed":
                    options.Speed = Double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0
                        ? speed
                        : throw new ArgumentException("--speed: expected a positive number.");
                    break;

                case "--data":
                    options.DataDirectory = Path.GetFullPath(Next());
                    break;

                case "--headless":
                    options.Headless = true;
                    break;

                case "--mcp":
                    options.Mcp = true;
                    break;

                case "--mcp-token":
                    options.McpToken = Next() is { Length: > 0 } token
                        ? token
                        : throw new ArgumentException("--mcp-token: expected a token.");
                    break;

                case "--mcp-stdio":
                    options.McpStdio = true;
                    options.Headless = true;
                    break;

                case "-h" or "--help" or "-?":
                    options.ShowHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Try --help.");
            }
        }

        if (options.AppDirectory is not null && options.DevServer is not null)
            throw new ArgumentException("Pass --app or --dev-server, not both.");

        if (options.McpToken is not null && !options.Mcp)
            throw new ArgumentException("--mcp-token: there is nothing to guard without --mcp. Talking MCP over stdio needs no token.");

        return options;
    }

    static string ExistingFile(string option, string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) ? full : throw new ArgumentException($"{option}: {full} does not exist.");
    }
}
