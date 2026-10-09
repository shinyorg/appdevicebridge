using System.Text;
using System.Text.Json;
using Shiny.AppDeviceBridge.Wearables.Client;

namespace Shiny.AppDeviceBridge.Wearables;

/// <summary>
/// The bridge's side of the wire: the page speaks JSON, the wearable speaks bytes. What the page sends goes out as its
/// UTF-8 JSON text. What comes back is read as JSON; failing that as UTF-8 text, which is how Shiny.Wearables'
/// <see cref="Shiny.Wearables.WearableData.FromString"/> and a Swift or Kotlin companion send a string; and anything else
/// reaches the page as a base64 string with <c>binary</c> set rather than being dropped.
/// </summary>
static class WearablesPayload
{
    static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A JSON value as the bytes a companion app receives. Absent is empty, not the text <c>null</c>.</summary>
    public static byte[] ToBytes(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Undefined } v ? Encoding.UTF8.GetBytes(v.GetRawText()) : [];

    /// <summary>
    /// Bytes from a companion app as a JSON value: JSON null for nothing, the value itself for JSON, a string for UTF-8
    /// text, base64 otherwise.
    /// </summary>
    public static (JsonElement Data, bool Binary) FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return (Null, false);

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return (doc.RootElement.Clone(), false);
        }
        catch (JsonException)
        {
        }

        if (TryGetText(bytes) is { } text)
            return (JsonSerializer.SerializeToElement(text, WearablesJsonContext.Default.String), false);

        return (JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes), WearablesJsonContext.Default.String), true);
    }

    /// <summary>
    /// The bytes as text when they are valid UTF-8 with no control characters but tab and line breaks — binary that
    /// happens to decode is caught by the control characters it nearly always carries.
    /// </summary>
    static string? TryGetText(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        foreach (var c in text)
        {
            if (Char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                return null;
        }
        return text;
    }

    /// <summary>What a handler returned (as JSON text) as the reply's bytes.</summary>
    public static byte[] FromResultJson(string? json)
        => String.IsNullOrEmpty(json) ? [] : Encoding.UTF8.GetBytes(json);
}
