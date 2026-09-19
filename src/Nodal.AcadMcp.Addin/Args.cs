using System.Text.Json;

namespace Nodal.AcadMcp.Addin;

/// <summary>Typed, error-friendly extraction of tool arguments from the request JSON.</summary>
internal static class Args
{
    private static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : null;

    private static AcadMcpException Missing(string name, string expected) =>
        new("bad_argument", $"Argument '{name}' is required and must be {expected}.");

    internal static string Str(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw Missing(name, "a string");

    internal static string? StrOpt(JsonElement e, string name, string? fallback = null) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : fallback;

    internal static int Int(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : throw Missing(name, "an integer");

    internal static int IntOpt(JsonElement e, string name, int fallback) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : fallback;

    internal static double Dbl(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : throw Missing(name, "a number");

    internal static double DblOpt(JsonElement e, string name, double fallback) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : fallback;

    internal static bool BoolOpt(JsonElement e, string name, bool fallback)
    {
        var p = Prop(e, name);
        return p?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    internal static string[] StrArray(JsonElement e, string name)
    {
        if (Prop(e, name) is not { ValueKind: JsonValueKind.Array } arr)
            throw Missing(name, "an array of strings");

        return arr.EnumerateArray()
                  .Where(x => x.ValueKind == JsonValueKind.String)
                  .Select(x => x.GetString()!)
                  .ToArray();
    }

    internal static string[] StrArrayOpt(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } ? StrArray(e, name) : [];

    /// <summary>A point as [x, y] or [x, y, z].</summary>
    internal static Autodesk.AutoCAD.Geometry.Point3d Point(JsonElement e, string name)
    {
        if (Prop(e, name) is not { ValueKind: JsonValueKind.Array } arr)
            throw Missing(name, "an array like [x, y] or [x, y, z]");

        var nums = arr.EnumerateArray()
                      .Where(x => x.ValueKind == JsonValueKind.Number)
                      .Select(x => x.GetDouble())
                      .ToArray();

        if (nums.Length is < 2 or > 3)
            throw new AcadMcpException("bad_argument", $"Argument '{name}' must have 2 or 3 numbers; got {nums.Length}.");

        return new Autodesk.AutoCAD.Geometry.Point3d(nums[0], nums[1], nums.Length > 2 ? nums[2] : 0.0);
    }
}
