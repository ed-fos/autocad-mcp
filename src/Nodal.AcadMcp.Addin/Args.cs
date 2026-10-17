using System.Text.Json;
using Nodal.AcadMcp.Contracts;

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Typed, error-friendly extraction of tool arguments from the request JSON.
///
/// The rules live in Contracts (ToolArgs) so they can be unit-tested without AutoCAD; this
/// wrapper only turns their errors into AcadMcpException, which is what every tool and the
/// Router already handle. A supplied value of the wrong type is an error even for an optional
/// argument — see ToolArgs for why it must never fall back to the default.
/// </summary>
internal static class Args
{
    internal static string Str(JsonElement e, string name) => Read(() => ToolArgs.Str(e, name));

    internal static string? StrOpt(JsonElement e, string name, string? fallback = null) =>
        Read(() => ToolArgs.StrOpt(e, name, fallback));

    internal static int Int(JsonElement e, string name) => Read(() => ToolArgs.Int(e, name));

    internal static int IntOpt(JsonElement e, string name, int fallback) =>
        Read(() => ToolArgs.IntOpt(e, name, fallback));

    internal static double Dbl(JsonElement e, string name) => Read(() => ToolArgs.Dbl(e, name));

    internal static double DblOpt(JsonElement e, string name, double fallback) =>
        Read(() => ToolArgs.DblOpt(e, name, fallback));

    internal static bool BoolOpt(JsonElement e, string name, bool fallback) =>
        Read(() => ToolArgs.BoolOpt(e, name, fallback));

    internal static string[] StrArray(JsonElement e, string name) => Read(() => ToolArgs.StrArray(e, name));

    internal static string[] StrArrayOpt(JsonElement e, string name) => Read(() => ToolArgs.StrArrayOpt(e, name));

    /// <summary>A point as [x, y] or [x, y, z].</summary>
    internal static Autodesk.AutoCAD.Geometry.Point3d Point(JsonElement e, string name) =>
        ToPoint(Read(() => ToolArgs.Coords(e, name)));

    /// <summary>
    /// One already-extracted value that must be [x, y] or [x, y, z] — for tools that walk a list
    /// of points themselves. <paramref name="label"/> names it in errors, e.g. "points[3]".
    /// </summary>
    internal static double[] Coordinates(JsonElement value, string label) =>
        Read(() => ToolArgs.Coordinates(value, label));

    /// <summary>One already-extracted value that must be an array of numbers.</summary>
    internal static double[] Numbers(JsonElement value, string label) =>
        Read(() => ToolArgs.Numbers(value, label));

    /// <summary>One already-extracted value that must be an integer.</summary>
    internal static int Integer(JsonElement value, string label) =>
        Read(() => ToolArgs.Integer(value, label));

    /// <summary>One already-extracted value that must be an array of integers.</summary>
    internal static int[] Integers(JsonElement value, string label) =>
        Read(() => ToolArgs.Integers(value, label));

    /// <summary>The error for a supplied value of the wrong type, for tools that read a JSON value themselves.</summary>
    internal static AcadMcpException WrongType(string name, string expected, JsonElement got) =>
        ToAcad(ToolArgs.WrongType(name, expected, got));

    private static Autodesk.AutoCAD.Geometry.Point3d ToPoint(double[] c) =>
        new(c[0], c[1], c.Length > 2 ? c[2] : 0.0);

    private static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (ToolArgumentException ex) { throw ToAcad(ex); }
    }

    private static AcadMcpException ToAcad(ToolArgumentException ex) =>
        new(ToolArgumentException.Code, ex.Message, ex.Detail);
}
