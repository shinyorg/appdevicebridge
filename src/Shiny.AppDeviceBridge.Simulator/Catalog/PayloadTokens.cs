using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiny.AppDeviceBridge.Simulator.Catalog;

/// <summary>
/// Placeholders a value may hold, filled in each time it is sent, so a saved value or a trail stays live:
/// <list type="bullet">
/// <item><c>"$now"</c> — the current time, ISO 8601; <c>"$now-5m"</c>, <c>"$now+30s"</c>, <c>"$now+2h"</c>, <c>"$now-1d"</c> offset it.</item>
/// <item><c>"$uuid"</c> — a new GUID.</item>
/// </list>
/// Only a whole string value is a placeholder; <c>"at $now"</c> is sent as written.
/// </summary>
public static class PayloadTokens
{
    public const string Now = "$now";
    public const string Uuid = "$uuid";

    /// <summary><paramref name="json"/> with its placeholders filled in. Text that is not JSON is returned unchanged.</summary>
    public static string Expand(string json, TimeProvider? time = null)
    {
        if (!json.Contains('$'))
            return json;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }

        if (node is null)
            return json;

        var now = (time ?? TimeProvider.System).GetUtcNow();
        return Replace(node, now) is { } replaced ? replaced.ToJsonString() : node.ToJsonString();
    }

    /// <summary>Walks the tree, returning a replacement for a placeholder value and null otherwise.</summary>
    static JsonNode? Replace(JsonNode node, DateTimeOffset now)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is not null && Replace(child, now) is { } replacement)
                        obj[name] = replacement;
                }
                return null;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child && Replace(child, now) is { } replacement)
                        array[i] = replacement;
                }
                return null;

            case JsonValue value when value.TryGetValue<string>(out var text) && text.StartsWith('$'):
                return Resolve(text, now) is { } resolved ? JsonValue.Create(resolved) : null;

            default:
                return null;
        }
    }

    static string? Resolve(string token, DateTimeOffset now)
    {
        if (token == Uuid)
            return Guid.NewGuid().ToString();

        if (!token.StartsWith(Now, StringComparison.Ordinal))
            return null;

        var rest = token[Now.Length..];
        if (rest.Length == 0)
            return now.ToString("O", CultureInfo.InvariantCulture);

        return TryParseOffset(rest, out var offset) ? now.Add(offset).ToString("O", CultureInfo.InvariantCulture) : null;
    }

    /// <summary><c>+30s</c>, <c>-5m</c>, <c>+2h</c>, <c>-1d</c>.</summary>
    static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = default;
        if (text.Length < 3 || text[0] is not ('+' or '-'))
            return false;

        if (!Double.TryParse(text[1..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            return false;

        if (text[0] == '-')
            amount = -amount;

        offset = text[^1] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => TimeSpan.MinValue
        };

        return offset != TimeSpan.MinValue;
    }
}
