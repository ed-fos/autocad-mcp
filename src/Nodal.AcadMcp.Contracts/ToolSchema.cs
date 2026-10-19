using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nodal.AcadMcp.Contracts;

/// <summary>The bounded JSON-schema subset used by the new typed workflows.</summary>
public static class ToolSchema
{
    public static JsonElement Object(string[] required, params (string Name, object Schema)[] properties) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required,
            additionalProperties = false,
            properties = properties.ToDictionary(p => p.Name, p => p.Schema)
        });
    public static object Text(string? description = null) => new { type = "string", minLength = 1, maxLength = 2048, description };
    public static object Number(double minimum = -1e12, double maximum = 1e12) => new { type = "number", minimum, maximum };
    public static object Flag() => new { type = "boolean" };
    public static object Choice(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
    public static object Array(object items, int min = 1, int max = 5000) => new { type = "array", items, minItems = min, maxItems = max };
    public static object Point(int dimensions = 3) => Array(Number(), dimensions, dimensions);
    public static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    /// <summary>Any JSON value; the tool checks the type itself (e.g. a scale given as "1:48" or 0.0208).</summary>
    public static object Any() => new { };
    /// <summary>A free-form object whose members the tool checks itself (e.g. a name → value map).</summary>
    public static object Map() => new { type = "object" };

    /// <summary>Adds a description to any schema built here, for the property list a client shows the model.</summary>
    public static object Describe(object schema, string description)
    {
        var node = JsonSerializer.SerializeToNode(schema)!.AsObject();
        node["description"] = description;
        return node;
    }

    public static string? Validate(JsonElement value, JsonElement schema, string path = "arguments")
    {
        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        bool valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => true
        };
        if (!valid) return $"{path} must be {type}.";
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c =>
            c.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String
                ? c.GetString() == value.GetString() : c.GetRawText() == value.GetRawText()))
            return $"{path} must be one of {choices.GetRawText()}.";
        if (type == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var key in required.EnumerateArray())
                    if (!value.TryGetProperty(key.GetString()!, out _)) return $"{path}.{key.GetString()} is required.";
            schema.TryGetProperty("properties", out var properties);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in value.EnumerateObject())
            {
                if (!seen.Add(prop.Name)) return $"{path}.{prop.Name} is duplicated.";
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(prop.Name, out var child))
                { var error = Validate(prop.Value, child, path + "." + prop.Name); if (error is not null) return error; }
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                    return $"{path}.{prop.Name} is not supported.";
            }
        }
        if (type == "array")
        {
            var length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && length < min.GetInt32() ||
                schema.TryGetProperty("maxItems", out var max) && length > max.GetInt32()) return $"{path} has an invalid number of items.";
            if (schema.TryGetProperty("items", out var child))
            { int i = 0; foreach (var item in value.EnumerateArray()) { var error = Validate(item, child, $"{path}[{i++}]"); if (error is not null) return error; } }
        }
        if (type == "string")
        {
            var text = value.GetString()!;
            if (string.IsNullOrWhiteSpace(text)) return $"{path} must not be blank.";
            if (text.Any(char.IsControl)) return $"{path} must not contain control characters.";
            if (schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32() ||
                schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32()) return $"{path} has an invalid length.";
        }
        if (type is "number" or "integer")
        {
            var n = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var min) && n < min.GetDouble() ||
                schema.TryGetProperty("maximum", out var max) && n > max.GetDouble()) return $"{path} is outside its allowed range.";
        }
        return null;
    }
}
