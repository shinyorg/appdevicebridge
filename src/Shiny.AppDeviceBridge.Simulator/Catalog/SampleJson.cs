using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.AppDeviceBridge.Simulator.Catalog;

/// <summary>
/// A starting value for any contract, built from the bridge's own source-generated JSON metadata: property names, casing and
/// enum spelling are exactly what the page's client reads, and no reflection-based serialization is involved.
/// </summary>
public static class SampleJson
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>An indented sample of <paramref name="type"/>, or <c>null</c> when the context does not describe it.</summary>
    public static string For(Type type, JsonSerializerContext json)
        => Build(type, json, [])?.ToJsonString(Indented) ?? "null";

    /// <summary>
    /// Whether <paramref name="text"/> is something the page's client can read as <paramref name="type"/> — the simulator
    /// refuses a value the real bridge could never send.
    /// </summary>
    public static bool TryValidate(string text, Type type, JsonSerializerContext json, out string? error)
    {
        error = null;

        if (json.GetTypeInfo(type) is not { } info)
        {
            error = $"{json.GetType().Name} has no metadata for {type.Name}.";
            return false;
        }

        try
        {
            JsonSerializer.Deserialize(PayloadTokens.Expand(text), info);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <param name="building">The types being built above this one. A type that contains itself — a menu of menus — gets an empty list there, not another level.</param>
    static JsonNode? Build(Type type, JsonSerializerContext json, HashSet<Type> building)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (Primitive(type) is { } primitive)
            return primitive;

        if (json.GetTypeInfo(type) is not { } info)
            return null;

        if (type.IsEnum)
            return Enum(type, info);

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return info.ElementType is { } element && !building.Contains(Nullable.GetUnderlyingType(element) ?? element) && Build(element, json, building) is { } item
                    ? new JsonArray(item)
                    : new JsonArray();

            case JsonTypeInfoKind.Dictionary:
                var map = new JsonObject();
                if (info.ElementType is { } value && !building.Contains(Nullable.GetUnderlyingType(value) ?? value))
                    map["key"] = Build(value, json, building);
                return map;

            case JsonTypeInfoKind.Object:
                if (!building.Add(type))
                    return null;

                var obj = new JsonObject();
                foreach (var property in info.Properties)
                {
                    if (property.Get is null && property.AssociatedParameter is null)
                        continue;

                    obj[property.Name] = Build(property.PropertyType, json, building);
                }

                building.Remove(type);
                return obj;

            default:
                return null;
        }
    }

    static JsonNode? Primitive(Type type)
    {
        if (type == typeof(string)) return JsonValue.Create("string");
        if (type == typeof(bool)) return JsonValue.Create(false);
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            return JsonValue.Create(0);
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return JsonValue.Create(0.0);
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return JsonValue.Create(PayloadTokens.Now);
        if (type == typeof(DateOnly)) return JsonValue.Create(DateOnly.FromDateTime(DateTime.Today).ToString("O"));
        if (type == typeof(TimeOnly)) return JsonValue.Create("00:00:00");
        if (type == typeof(TimeSpan)) return JsonValue.Create("00:00:00");
        if (type == typeof(Guid)) return JsonValue.Create(Guid.Empty.ToString());
        if (type == typeof(Uri)) return JsonValue.Create("https://example.com/");
        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonObject)) return new JsonObject();
        if (type == typeof(byte[])) return JsonValue.Create(String.Empty);
        return null;
    }

    /// <summary>The first member, spelled however the context writes it — by name with the string converter, by number without.</summary>
    static JsonNode? Enum(Type type, JsonTypeInfo info)
    {
        var names = System.Enum.GetNames(type);
        if (names.Length == 0)
            return JsonValue.Create(0);

        var first = System.Enum.Parse(type, names[0]);
        return JsonSerializer.SerializeToNode(first, info);
    }
}
