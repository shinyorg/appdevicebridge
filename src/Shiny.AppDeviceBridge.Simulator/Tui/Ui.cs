using System.Text.Json;
using System.Text.Json.Nodes;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Shiny.AppDeviceBridge.Simulator.Tui;

/// <summary>The simulator's small vocabulary of widgets and colours.</summary>
static class Ui
{
    public const string Accent = "#22d3ee";
    public const string Violet = "#a78bfa";
    public const string Green = "#34d399";
    public const string Amber = "#fbbf24";
    public const string Red = "#f87171";
    public const string Blue = "#93b4fd";
    public const string Muted = "#9ba2bc";

    // "Café", not "Caf\u00E9": shown as text in a terminal, never put into markup or a script.
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Markup from a finished string. <c>new Markup($"…")</c> binds to the control's interpolation handler, which escapes
    /// every hole — so a hole holding markup of its own, such as <see cref="Method"/>, would show its tags as text.
    /// </summary>
    public static Markup Markup(string markup) => new(markup);

    /// <summary>Markup rebuilt whenever a state it reads changes. Typed as a string for the reason <see cref="Markup(string)"/> gives.</summary>
    public static Markup Live(Func<string> markup) => new(markup);

    /// <summary>Markup treats brackets as tags; text shown as-is doubles them.</summary>
    public static string Escape(string? text) => (text ?? "").Replace("[", "[[").Replace("]", "]]");

    public static Visual Title(string text) => Ui.Markup($"[bold]{Escape(text)}[/]");

    public static Visual Dim(string text) => Ui.Markup($"[{Muted}]{Escape(text)}[/]").Wrap(true);

    public static Group Panel(string title, Visual content)
        => new Group(Ui.Markup($"[bold {Accent}]{Escape(title)}[/]"), content).Padding(new Thickness(1, 0, 1, 0)).Stretch();

    public static Visual Empty(string message) => new Center(Ui.Markup($"[{Muted}]{Escape(message)}[/]"));

    public static Button Action(string text, Action onClick) => new Button(new TextBlock(text)).Click(onClick);

    public static Button Primary(string text, Action onClick) => new Button(new TextBlock(text)).Tone(ControlTone.Primary).Click(onClick);

    public static Button Danger(string text, Action onClick) => new Button(new TextBlock(text)).Tone(ControlTone.Error).Click(onClick);

    public static WrapHStack Toolbar(params Visual[] items) => new WrapHStack().Children(items).Spacing(1);

    public static Visual Field(string label, Visual input)
        => new HStack(Ui.Markup($"[{Muted}]{Escape(label)}[/]").MinWidth(14), input).Spacing(1);

    public static string MethodColor(string method) => method switch
    {
        "GET" => Blue,
        "POST" => Green,
        "PUT" => Amber,
        "DELETE" => Red,
        _ => Muted
    };

    public static string Method(string method) => $"[bold {MethodColor(method)}]{method,-6}[/] ";

    public static string StatusColor(int status, bool failed = false) => failed ? Red : status switch
    {
        >= 500 => Red,
        >= 400 => Amber,
        >= 300 => Blue,
        >= 200 => Green,
        _ => Muted
    };

    public static string Status(int status, bool failed = false) => $"[bold {StatusColor(status, failed)}]{status}[/]";

    /// <summary>Indented, when it parses; as typed when it does not, so a half-finished edit is never lost.</summary>
    public static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(Indented) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
