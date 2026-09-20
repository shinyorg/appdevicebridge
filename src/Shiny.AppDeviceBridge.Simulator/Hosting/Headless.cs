namespace Shiny.AppDeviceBridge.Simulator.Hosting;

/// <summary>
/// The simulator without its TUI, for a test run or CI: serve, apply the scenario, play the trails, and write what happens
/// — activity and every request — to the console until Ctrl+C.
/// </summary>
static class Headless
{
    public static async Task<int> RunAsync(SimulatorHost host)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var printed = new HashSet<string>();
        host.State.Logged += record => Console.WriteLine($"{record.At:HH:mm:ss.fff}  {record.Text}");
        host.Traffic.Changed += (_, _) =>
        {
            // The recorder raises Changed as an exchange starts and again as it completes; print each once, finished.
            foreach (var exchange in host.Traffic.Snapshot().Reverse())
            {
                if (exchange.StatusCode == 0 || !printed.Add(exchange.Id))
                    continue;

                Console.WriteLine($"{exchange.StartedOn.ToLocalTime():HH:mm:ss.fff}  {exchange.StatusCode} {exchange.Method,-6} {exchange.Target}  {TrafficText.Duration(exchange.Elapsed)}");
            }
        };

        foreach (var record in host.State.Activity.Reverse())
            Console.WriteLine($"{record.At:HH:mm:ss.fff}  {record.Text}");

        Console.WriteLine($"Shiny.AppDeviceBridge simulator on {host.Origin} as {host.State.Platform} — Ctrl+C stops.");

        if (host.McpToken is { } token && host.McpEndpoint is not null)
        {
            Console.WriteLine();
            Console.WriteLine("An agent drives this simulator through MCP. Point one at it with:");
            Console.WriteLine();
            Console.WriteLine(Control.McpSetup.ClientConfig(host.Origin!, token));
            Console.WriteLine();
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}
