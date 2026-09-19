using System.Text;
using System.Text.Json;
using Shiny.AppDeviceBridge.Wearables.Client;

namespace Shiny.AppDeviceBridge.Wearables;

/// <summary>
/// The bridge's side of the wire: the page speaks JSON, the wearable speaks bytes. What the page sends goes out as its
/// UTF-8 JSON text; what comes back is read as JSON, and bytes that are not JSON reach the page as a base64 string with
/// <c>binary</c> set rather than being dropped.
/// </summary>
static class WearablesPayload
{
    static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>A JSON value as the bytes a companion app receives. Absent is empty, not the text <c>null</c>.</summary>
    public static byte[] ToBytes(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Undefined } v ? Encoding.UTF8.GetBytes(v.GetRawText()) : [];

    /// <summary>Bytes from a companion app as a JSON value: JSON null for nothing, the value itself for JSON, base64 otherwise.</summary>
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
            return (JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes), WearablesJsonContext.Default.String), true);
        }
    }

    /// <summary>What a handler returned (as JSON text) as the reply's bytes.</summary>
    public static byte[] FromResultJson(string? json)
        => String.IsNullOrEmpty(json) ? [] : Encoding.UTF8.GetBytes(json);
}
