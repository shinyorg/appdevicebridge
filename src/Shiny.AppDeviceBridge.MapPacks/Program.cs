using Shiny.AppDeviceBridge.MapPacks;

try
{
    return await MapPackTool.RunAsync(args, Console.Out, CancellationToken.None);
}
catch (MapPackToolException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex.ExitCode;
}
