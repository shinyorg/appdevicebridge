using Shiny.AppDeviceBridge.TypeScript;

namespace Shiny.AppDeviceBridge.Tests;

public class TypeScriptClientTests
{
    /// <summary>
    /// The committed TypeScript is what the C# declarations produce today. Change a contract or a [BridgeClient] interface
    /// without regenerating, and this fails — run the tools/Shiny.AppDeviceBridge.TypeScript project to fix it.
    /// </summary>
    [Fact]
    public void Committed_clients_match_the_declarations()
    {
        var source = Path.Combine(RepositoryRoot(), "clients", "typescript", "src");

        foreach (var (file, expected) in TypeScriptGenerator.Generate())
        {
            var path = Path.Combine(source, file);
            Assert.True(File.Exists(path), $"clients/typescript/src/{file} is missing. Run the TypeScript tool.");
            Assert.True(File.ReadAllText(path) == expected, $"clients/typescript/src/{file} is out of date. Run the TypeScript tool.");
        }
    }

    [Fact]
    public void Generates_a_client_class_per_bridge_with_its_contracts()
    {
        var calendar = TypeScriptGenerator.Generate()["calendar.ts"];

        Assert.Contains("export class CalendarBridge {", calendar);
        Assert.Contains("createEvent(calendarEvent: NewCalendarEvent, options?: { signal?: AbortSignal }): Promise<CalendarEvent>", calendar);
        Assert.Contains("deleteEvent(id: string, options?: { series?: boolean; signal?: AbortSignal }): Promise<void>", calendar);
        Assert.Contains("export type EventAvailability = \"Busy\" | \"Free\" | \"Tentative\" | \"Unavailable\";", calendar);

        // A required member is required; an optional one on a request is optional.
        Assert.Contains("    title: string;", calendar);
        Assert.Contains("    calendarId?: string | null;", calendar);
    }

    /// <summary>A type reached only as a dictionary's value is declared too, or the Record&lt;string, T&gt; naming it does not compile.</summary>
    [Fact]
    public void Declares_types_used_only_as_dictionary_values()
    {
        var maps = TypeScriptGenerator.Generate()["maps.ts"];

        Assert.Contains("    kinds: Record<string, TrafficIncidentKind>;", maps);
        Assert.Contains("export type TrafficIncidentKind = \"Other\" | \"Accident\"", maps);
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shiny.AppDeviceBridge.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Not inside the repository.");
    }
}
