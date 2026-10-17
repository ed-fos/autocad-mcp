using System.Text.Json;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// A tool argument that is missing or has the wrong JSON type. The add-in reports it as
/// <see cref="Code"/>, with <see cref="Detail"/> as the error's detail when there is a hint.
/// </summary>
public sealed class ToolArgumentException(string message, string? detail = null) : Exception(message)
{
    public const string Code = "bad_argument";

    public string? Detail { get; } = detail;
}

/// <summary>
/// Typed extraction of tool arguments from the request JSON.
///
/// This lives in Contracts, away from any AutoCAD dependency, so the rules can be unit-tested
/// without launching AutoCAD. The add-in's Args class is a thin wrapper over it.
///
/// The one rule that matters: absent and null both mean "not supplied", but a value that IS
/// supplied with the wrong JSON type is an error, for optional arguments as much as required
/// ones. Falling back to the default instead would silently run the tool with something the
/// caller did not ask for — e.g. "write": "false" (a string) committing a write, or a layer
/// name sent as an object landing on the current layer.
/// </summary>
public static class ToolArgs
{
    /// <summary>
    /// Parses a tool-call body. Empty means no arguments; anything else must be a JSON object.
    /// Returns null on success, or the error to send back.
    /// </summary>
    public static ToolError? TryParseBody(string body, out JsonElement args)
    {
        args = default;
        if (string.IsNullOrWhiteSpace(body))
        {
            using var empty = JsonDocument.Parse("{}");
            args = empty.RootElement.Clone();
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolError("bad_json",
                    $"Request body must be a JSON object of tool arguments; got {Describe(doc.RootElement)}.");

            args = doc.RootElement.Clone();
            return null;
        }
        catch (JsonException ex)
        {
            return new ToolError("bad_json", "Request body is not valid JSON.", ex.Message);
        }
    }

    // ------------------------------------------------------------------ scalars

    public static string Str(JsonElement e, string name) =>
        Prop(e, name) is { } v ? AsString(v, name) : throw Missing(name, "a string");

    public static string? StrOpt(JsonElement e, string name, string? fallback = null) =>
        Prop(e, name) is { } v ? AsString(v, name) : fallback;

    public static int Int(JsonElement e, string name) =>
        Prop(e, name) is { } v ? AsInt(v, name) : throw Missing(name, "an integer");

    public static int IntOpt(JsonElement e, string name, int fallback) =>
        Prop(e, name) is { } v ? AsInt(v, name) : fallback;

    public static double Dbl(JsonElement e, string name) =>
        Prop(e, name) is { } v ? AsDouble(v, name) : throw Missing(name, "a number");

    public static double DblOpt(JsonElement e, string name, double fallback) =>
        Prop(e, name) is { } v ? AsDouble(v, name) : fallback;

    public static bool BoolOpt(JsonElement e, string name, bool fallback) =>
        Prop(e, name) is { } v
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw WrongType(name, "true or false", v),
            }
            : fallback;

    // ------------------------------------------------------------------ arrays

    public static string[] StrArray(JsonElement e, string name) =>
        Prop(e, name) is { } v ? AsStringArray(v, name) : throw Missing(name, "an array of strings");

    public static string[] StrArrayOpt(JsonElement e, string name) =>
        Prop(e, name) is { } v ? AsStringArray(v, name) : [];

    /// <summary>A point as [x, y] or [x, y, z]; returns its 2 or 3 numbers.</summary>
    public static double[] Coords(JsonElement e, string name) =>
        Prop(e, name) is { } v ? Coordinates(v, name) : throw Missing(name, "an array like [x, y] or [x, y, z]");

    /// <summary>
    /// A value that must be [x, y] or [x, y, z]. <paramref name="label"/> names it in errors,
    /// e.g. "points[3]", so tools reading a list of points get the offending index for free.
    /// </summary>
    public static double[] Coordinates(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw WrongType(label, "an array like [x, y] or [x, y, z]", value);

        var nums = Numbers(value, label);
        if (nums.Length is < 2 or > 3)
            throw new ToolArgumentException($"Argument '{label}' must have 2 or 3 numbers; got {nums.Length}.");

        return nums;
    }

    /// <summary>A value that must be an array of numbers, every item a number.</summary>
    public static double[] Numbers(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw WrongType(label, "an array of numbers", value);

        var nums = new double[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            nums[i] = item.ValueKind == JsonValueKind.Number
                ? item.GetDouble()
                : throw WrongType($"{label}[{i}]", "a number", item);
            i++;
        }

        return nums;
    }

    /// <summary>A value that must be an integer (an integral number such as 50.0 counts).</summary>
    public static int Integer(JsonElement value, string label) => AsInt(value, label);

    /// <summary>A value that must be an array of integers, every item an integer.</summary>
    public static int[] Integers(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw WrongType(label, "an array of integers", value);

        var ints = new int[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            ints[i] = AsInt(item, $"{label}[{i}]");
            i++;
        }

        return ints;
    }

    // ------------------------------------------------------------------ internals

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : null;

    private static string AsString(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : throw WrongType(name, "a string", v);

    private static int AsInt(JsonElement v, string name)
    {
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;

            // 50.0 is how several JSON writers (Python's json, JavaScript) emit an integral
            // count, so accept it; 2.5 is a genuine mistake and is not rounded.
            var d = v.GetDouble();
            if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
        }

        throw WrongType(name, "an integer", v);
    }

    private static double AsDouble(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw WrongType(name, "a number", v);

    private static string[] AsStringArray(JsonElement v, string name)
    {
        if (v.ValueKind != JsonValueKind.Array)
            throw WrongType(name, "an array of strings", v);

        var items = new string[v.GetArrayLength()];
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            items[i] = item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw WrongType($"{name}[{i}]", "a string", item);
            i++;
        }

        return items;
    }

    private static ToolArgumentException Missing(string name, string expected) =>
        new($"Argument '{name}' is required and must be {expected}.");

    /// <summary>The error for a supplied value of the wrong type, for tools that read a JSON value themselves.</summary>
    public static ToolArgumentException WrongType(string name, string expected, JsonElement got) =>
        new($"Argument '{name}' must be {expected}; got {Describe(got)}.",
            LooksLikePowerShellString(got) ? PowerShellHint : null);

    private static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => $"the number {v.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {v.GetRawText()}",
        _ => v.ValueKind.ToString().ToLowerInvariant(),
    };

    private const string PowerShellHint =
        "This looks like a PowerShell string serialised together with its extended properties " +
        "(PSPath, PSDrive, ...), which is what ConvertTo-Json does to Get-Content output. Send the " +
        "plain string instead: [string]$text, or read the file with [IO.File]::ReadAllText(path). " +
        "Do not raise ConvertTo-Json's -Depth to work around it: at -Depth 6 that one string " +
        "already serialises to about 100 MB, and higher depths never finish.";

    /// <summary>
    /// Windows PowerShell's ConvertTo-Json emits a string that carries ETS note properties as
    /// {"value": "...", "PSPath": ..., "PSDrive": {...}, ...}. Recognising the shape costs
    /// nothing and saves the caller from guessing why a string argument "is not a string".
    /// </summary>
    private static bool LooksLikePowerShellString(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.String
        && (v.TryGetProperty("PSPath", out _) || v.TryGetProperty("PSChildName", out _)
            || v.TryGetProperty("PSDrive", out _) || v.TryGetProperty("PSProvider", out _));
}
