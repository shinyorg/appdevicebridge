using Shiny.AppDeviceBridge.Simulator.Control;
using Shiny.AppDeviceBridge.Simulator.Hosting;
using Shiny.AppDeviceBridge.Simulator.Tui;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

SimulatorOptions options;
try
{
    options = SimulatorOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(SimulatorOptions.Usage);
    return 0;
}

SimulatorHost host;
try
{
    host = SimulatorHost.Create(options);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Xml.XmlException or InvalidDataException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

await using (host)
{
    try
    {
        await host.StartAsync();
    }
    catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException or IOException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (options.McpStdio)
        return await McpStdio.RunAsync(host);

    return options.Headless ? await Headless.RunAsync(host) : await RunTuiAsync(host);
}

static async Task<int> RunTuiAsync(SimulatorHost host)
{
    var shell = new SimulatorShell(host);
    var root = shell.Build();
    var attached = false;

    await Terminal.RunAsync(
        root,
        context =>
        {
            if (!attached)
            {
                attached = true;
                shell.Attach(context.App);
            }

            shell.Tick();

            return shell.ExitRequested.Value ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
        },
        new TerminalRunOptions
        {
            LoopMode = TerminalLoopMode.Auto,
            UpdateWaitDuration = TimeSpan.FromMilliseconds(250)
        }
    );

    return 0;
}
