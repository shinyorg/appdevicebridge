using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.AspNetCore;

/// <summary>
/// Text in several languages — release notes, mostly — resolved against the culture a host sends with its update check.
/// In JSON it is either a plain string, for one language, or an object keyed by culture name:
/// <code>
/// "releaseNotes": "Faster sync."
/// "releaseNotes": { "*": "Faster sync.", "fr": "Synchronisation plus rapide.", "fr-CA": "Synchro plus rapide." }
/// </code>
/// <para>
/// <see cref="Resolve"/> tries the culture itself, then each parent (<c>fr-CA</c>, then <c>fr</c>), then <c>*</c>, then
/// whichever language was given first — so a host always gets something to show.
/// </para>
/// </summary>
[JsonConverter(typeof(WebAppLocalizedTextConverter))]
public sealed class WebAppLocalizedText
{
    /// <summary>The key for text that applies to any culture.</summary>
    public const string Fallback = "*";

    readonly List<KeyValuePair<string, string>> values;

    /// <summary>Text in one language, served to every culture.</summary>
    public WebAppLocalizedText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.values = [new(Fallback, text)];
    }

    /// <summary>Text by culture name; <see cref="Fallback"/> for any culture. Order matters only as the last resort.</summary>
    public WebAppLocalizedText(IEnumerable<KeyValuePair<string, string>> byCulture)
    {
        ArgumentNullException.ThrowIfNull(byCulture);
        this.values = [.. byCulture.Where(x => !String.IsNullOrWhiteSpace(x.Key) && x.Value is not null)];
    }

    /// <summary>Every language given, in the order given.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values => this.values;

    public static implicit operator WebAppLocalizedText(string text) => new(text);

    /// <summary>The best text for <paramref name="culture"/>, such as <c>fr-CA</c>. Null only when there is no text at all.</summary>
    public string? Resolve(string? culture)
    {
        if (this.values.Count == 0)
            return null;

        var name = culture?.Trim();

        while (!String.IsNullOrEmpty(name))
        {
            if (this.Find(name) is { } exact)
                return exact;

            var dash = name.LastIndexOf('-');
            name = dash > 0 ? name[..dash] : null;
        }

        return this.Find(Fallback) ?? this.values[0].Value;
    }

    string? Find(string culture)
    {
        foreach (var (key, value) in this.values)
        {
            if (String.Equals(key, culture, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }
}

/// <summary>Reads and writes <see cref="WebAppLocalizedText"/> as a string or an object of strings. No reflection.</summary>
public sealed class WebAppLocalizedTextConverter : JsonConverter<WebAppLocalizedText>
{
    public override WebAppLocalizedText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new WebAppLocalizedText(reader.GetString()!);

            case JsonTokenType.StartObject:
                var values = new List<KeyValuePair<string, string>>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected a culture name.");

                    var culture = reader.GetString()!;
                    reader.Read();

                    if (reader.TokenType != JsonTokenType.String)
                        throw new JsonException($"The text for '{culture}' must be a string.");

                    values.Add(new(culture, reader.GetString()!));
                }

                return new WebAppLocalizedText(values);

            default:
                throw new JsonException("Localized text is a string, or an object of strings keyed by culture.");
        }
    }

    public override void Write(Utf8JsonWriter writer, WebAppLocalizedText value, JsonSerializerOptions options)
    {
        if (value.Values is [{ Key: WebAppLocalizedText.Fallback } only])
        {
            writer.WriteStringValue(only.Value);
            return;
        }

        writer.WriteStartObject();
        foreach (var (culture, text) in value.Values)
            writer.WriteString(culture, text);

        writer.WriteEndObject();
    }
}
