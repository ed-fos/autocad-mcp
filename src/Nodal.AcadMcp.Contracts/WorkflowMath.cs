using System.Security.Cryptography;
using System.Text.Json;

namespace Nodal.AcadMcp.Contracts;

public static class WorkflowMath
{
    public static string Fingerprint(JsonElement args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, args);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter w, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { w.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); WriteCanonical(w, p.Value); } w.WriteEndObject(); }
        else if (e.ValueKind == JsonValueKind.Array) { w.WriteStartArray(); foreach (var v in e.EnumerateArray()) WriteCanonical(w, v); w.WriteEndArray(); }
        else e.WriteTo(w);
    }
    /// <summary>Trim normal in the outgoing segment's local (forward,left) axes.</summary>
    public static (double Forward, double Left) MiterNormal(double ax, double ay, double bx, double by)
    {
        var al = Math.Sqrt(ax * ax + ay * ay); var bl = Math.Sqrt(bx * bx + by * by);
        if (!double.IsFinite(al + bl) || al < 1e-9 || bl < 1e-9) throw new ArgumentException("Segments must have nonzero finite length.");
        ax /= al; ay /= al; bx /= bl; by /= bl;
        var nx = ax + bx; var ny = ay + by;
        var forward = nx * bx + ny * by; var left = -nx * by + ny * bx;
        if (forward < 1e-6) throw new ArgumentException("A reversing or nearly reversing corner has no bounded miter.");
        return (forward, left);
    }
    /// <summary>Named view directions, as AutoCAD's ViewDirection: the vector from the target toward the viewer.</summary>
    public static readonly IReadOnlyDictionary<string, double[]> ViewDirections = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = [0, 0, 1],
        ["bottom"] = [0, 0, -1],
        ["front"] = [0, -1, 0],
        ["back"] = [0, 1, 0],
        ["left"] = [-1, 0, 0],
        ["right"] = [1, 0, 0],
        ["sw-iso"] = [-1, -1, 1],
        ["se-iso"] = [1, -1, 1],
        ["ne-iso"] = [1, 1, 1],
        ["nw-iso"] = [-1, 1, 1],
    };

    public static double[] ViewDirection(string name) => ViewDirections.TryGetValue(name, out var v)
        ? (double[])v.Clone()
        : throw new ArgumentException($"Unknown view '{name}'. Use one of {string.Join(", ", ViewDirections.Keys)}, or an [x, y, z] vector.");

    public static double MillimetersPerUnit(string unit) => unit switch
    {
        "Millimeters" => 1,
        "Centimeters" => 10,
        "Meters" => 1000,
        "Inches" => 25.4,
        "Feet" => 304.8,
        _ => throw new ArgumentException("Explicit supported units are required: Millimeters, Centimeters, Meters, Inches, Feet.")
    };
}
