using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shiny.AppDeviceBridge.TypeScript;

/// <summary>An assembly's XML documentation, reduced to the plain summaries JSDoc can carry.</summary>
sealed partial class XmlDocs
{
    readonly Dictionary<string, string> summaries = new(StringComparer.Ordinal);

    XmlDocs(string? path)
    {
        if (path is null || !File.Exists(path))
            return;

        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            if (member.Attribute("name")?.Value is { } name && member.Element("summary") is { } summary)
                this.summaries[name] = Plain(summary);
        }
    }

    public static XmlDocs For(Assembly assembly)
        => new(Path.ChangeExtension(assembly.Location, ".xml"));

    public string? Type(Type type) => this.summaries.GetValueOrDefault("T:" + type.FullName);

    public string? Property(PropertyInfo property) => this.summaries.GetValueOrDefault($"P:{property.DeclaringType!.FullName}.{property.Name}");

    /// <summary>By name: interface methods here are not overloaded, and computing full documentation ids buys nothing.</summary>
    public string? Method(MethodInfo method)
    {
        var prefix = $"M:{method.DeclaringType!.FullName}.{method.Name}";
        return this.summaries.FirstOrDefault(x => x.Key == prefix || x.Key.StartsWith(prefix + "(", StringComparison.Ordinal)).Value;
    }

    /// <summary>The summary as prose: references by their short name, code in backticks, C# examples dropped.</summary>
    static string Plain(XElement summary)
    {
        var text = new StringBuilder();

        void Visit(XNode node)
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;

                case XElement { Name.LocalName: "code" }:
                    break;

                case XElement { Name.LocalName: "see" or "seealso" } e:
                    var target = e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? "";
                    text.Append('`').Append(ShortName(target)).Append('`');
                    break;

                // A record's parameters are its properties, which are camelCase on the wire and in TypeScript.
                case XElement { Name.LocalName: "paramref" } e:
                    text.Append('`').Append(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(e.Attribute("name")?.Value ?? "")).Append('`');
                    break;

                case XElement { Name.LocalName: "typeparamref" } e:
                    text.Append('`').Append(e.Attribute("name")?.Value).Append('`');
                    break;

                case XElement { Name.LocalName: "c" } e:
                    text.Append('`').Append(e.Value).Append('`');
                    break;

                case XElement { Name.LocalName: "para" } e:
                    text.Append("\n\n");
                    foreach (var child in e.Nodes())
                        Visit(child);
                    text.Append("\n\n");
                    break;

                case XElement e:
                    foreach (var child in e.Nodes())
                        Visit(child);
                    break;
            }
        }

        foreach (var node in summary.Nodes())
            Visit(node);

        var paragraphs = Paragraphs().Split(text.ToString())
            .Select(x => Whitespace().Replace(x, " ").Trim())
            .Where(x => x.Length > 0);

        return String.Join("\n\n", paragraphs);
    }

    /// <summary>A reference by the name a TypeScript reader sees: types as declared, members camelCased, methods without Async.</summary>
    static string ShortName(string cref)
    {
        var kind = cref.Length > 1 && cref[1] == ':' ? cref[0] : 'T';
        var name = cref.Contains(':') ? cref[(cref.IndexOf(':') + 1)..] : cref;
        name = name.Split('(')[0];
        var dot = name.LastIndexOf('.');
        var member = dot >= 0 ? name[(dot + 1)..] : name;

        if (kind == 'M' && member.EndsWith("Async", StringComparison.Ordinal))
            member = member[..^"Async".Length];

        return kind is 'M' or 'P' ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(member) : member;
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
