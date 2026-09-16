using Shiny.AppDeviceBridge.TypeScript;

// Writes the generated modules into clients/typescript/src, found by walking up to the solution.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Shiny.AppDeviceBridge.slnx")))
    root = root.Parent;

if (root is null)
{
    Console.Error.WriteLine("Run this from inside the Shiny.AppDeviceBridge repository.");
    return 1;
}

var output = Path.Combine(root.FullName, "clients", "typescript", "src");
foreach (var (file, content) in TypeScriptGenerator.Generate())
{
    File.WriteAllText(Path.Combine(output, file), content);
    Console.WriteLine($"wrote clients/typescript/src/{file}");
}

return 0;
