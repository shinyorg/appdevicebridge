using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.AppDeviceBridge.Simulator.Hosting;

namespace Shiny.AppDeviceBridge.Simulator.Control;

/// <summary>
/// The simulator speaking MCP on stdin and stdout, for an agent that starts it itself rather than attaching to one a
/// person is running.
/// <para>
/// The page is still served over HTTP as always — only the control surface moves. stdout belongs to the protocol, so
/// everything the simulator would have printed goes to stderr; a stray <c>Console.WriteLine</c> here corrupts the
/// framing and the agent sees a dead server.
/// </para>
/// </summary>
static class McpStdio
{
    public static async Task<int> RunAsync(SimulatorHost host)
    {
        var log = Console.Error;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        host.State.Logged += record => log.WriteLine($"{record.At:HH:mm:ss.fff}  {record.Text}");
        foreach (var record in host.State.Activity.Reverse())
            log.WriteLine($"{record.At:HH:mm:ss.fff}  {record.Text}");

        log.WriteLine($"Shiny.AppDeviceBridge simulator on {host.Origin} as {host.State.Platform} — MCP on stdin/stdout.");

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var loggers = host.Services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

        await using var transport = new StdioServerTransport(options, loggers);
        await using var server = McpServer.Create(transport, options, loggers, host.Services);

        try
        {
            await server.RunAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}
