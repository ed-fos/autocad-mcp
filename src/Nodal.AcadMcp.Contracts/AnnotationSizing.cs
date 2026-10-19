using System.Globalization;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// The arithmetic behind annotative sizing, kept away from AutoCAD so it can be unit-tested.
///
/// A scale's factor is drawing units per plotted unit: 1/4" = 1'-0" is 12 / 0.25 = 48, so 3/32"
/// text on the sheet is 4.5" tall in model space. AutoCAD does not apply that factor to a text
/// height set through the API (measured in AutoCAD 2027: an annotative DBText given 3/32" stays
/// 3/32" tall in model space), so the tools work out the model size themselves.
/// </summary>
public static class AnnotationSizing
{
    /// <summary>One annotation scale as the drawing lists it.</summary>
    public readonly record struct Scale(string Name, double DrawingUnits, double PaperUnits)
    {
        public double Factor => DrawingUnits / PaperUnits;
    }

    /// <summary>A size in drawing units, and the plotted size it stands for when there is one.</summary>
    public readonly record struct Size(double Model, double? Paper);

    /// <summary>
    /// Resolves a size the caller gave either in drawing units (<paramref name="model"/>) or as a
    /// plotted size (<paramref name="paper"/>), never both.
    ///
    ///   paper given        → model = paper × factor (annotative or not: a non-annotative object
    ///                        can still be sized for one scale, e.g. symbols inserted at 48×)
    ///   model given        → used as is; an annotative object's plotted size is model / factor
    ///   neither            → annotative: fallbackPaper × factor; otherwise fallbackModel
    /// </summary>
    public static Size Resolve(
        string modelArg, double? model, string paperArg, double? paper,
        double factor, bool annotative, double fallbackPaper, double fallbackModel)
    {
        if (model is not null && paper is not null)
            throw new ToolArgumentException(
                $"Give either '{modelArg}' (drawing units) or '{paperArg}' (plotted size), not both.");

        if (!(factor > 0) || double.IsInfinity(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "A scale factor must be positive and finite.");

        if (paper is { } p)
        {
            RequirePositive(paperArg, p);
            return new Size(p * factor, p);
        }

        if (model is { } m)
        {
            RequirePositive(modelArg, m);
            return new Size(m, annotative ? m / factor : null);
        }

        return annotative
            ? new Size(fallbackPaper * factor, fallbackPaper)
            : new Size(fallbackModel, null);
    }

    /// <summary>
    /// The scale an 'annotationScale' string names: an exact scale name first (case and runs of
    /// spaces ignored, so '1/4" = 1'-0"' and '1/4"=1'-0"' differ but '1:50' and ' 1:50 ' do not),
    /// then a ratio 'a:b' (plotted : drawing, as AutoCAD names metric scales) or a bare factor such
    /// as '48', matched by factor. Returns null when nothing in the list matches.
    /// </summary>
    public static int? Find(IReadOnlyList<Scale> scales, string spec, string? preferredName = null)
    {
        var wanted = Normalize(spec);
        if (wanted.Length == 0)
            throw new ToolArgumentException("Argument 'annotationScale' must not be blank.");

        for (var i = 0; i < scales.Count; i++)
            if (string.Equals(Normalize(scales[i].Name), wanted, StringComparison.OrdinalIgnoreCase))
                return i;

        var factor = ParseFactor(wanted);
        return factor is { } f ? Find(scales, f, preferredName) : null;
    }

    /// <summary>
    /// The scale with this factor (drawing units per plotted unit). When several scales share it
    /// (a drawing can list both '1:48' and '1/4" = 1'-0"'), the preferred one — normally the
    /// current CANNOSCALE — wins, otherwise the first listed. They are different scale contexts,
    /// so the choice matters: a viewport only shows annotative objects that carry its own scale.
    /// </summary>
    public static int? Find(IReadOnlyList<Scale> scales, double factor, string? preferredName = null)
    {
        if (!(factor > 0) || double.IsInfinity(factor))
            throw new ToolArgumentException(
                $"Argument 'annotationScale' as a number is drawing units per plotted unit (48 for 1/4\" = 1'-0\") and must be positive; got {factor.ToString(CultureInfo.InvariantCulture)}.");

        int? first = null;
        for (var i = 0; i < scales.Count; i++)
        {
            if (!SameFactor(scales[i].Factor, factor)) continue;
            if (preferredName is not null && string.Equals(scales[i].Name, preferredName, StringComparison.OrdinalIgnoreCase))
                return i;
            first ??= i;
        }
        return first;
    }

    /// <summary>
    /// The order to build a multi-segment leader in. MLeader.AddLeaderLine takes the vertex
    /// nearest the landing; AddFirstVertex then prepends each point back toward the arrow tip.
    /// (AddLastVertex with a mid point produced runaway vertices in the drawing session that
    /// found this.) <paramref name="bends"/> run from the arrow toward the text.
    /// </summary>
    public static (T First, IReadOnlyList<T> Prepend) LeaderOrder<T>(T arrow, IReadOnlyList<T> bends)
    {
        if (bends.Count == 0) return (arrow, Array.Empty<T>());

        var prepend = new List<T>(bends.Count);
        for (var i = bends.Count - 2; i >= 0; i--) prepend.Add(bends[i]);
        prepend.Add(arrow);
        return (bends[^1], prepend);
    }

    /// <summary>The MLeader TextAngleType member a 'textAngle' argument names.</summary>
    public static string TextAngleName(string spec) => Normalize(spec).Replace(" ", "").ToLowerInvariant() switch
    {
        "horizontal" or "level" => "HorizontalAngle",
        "insert" or "leader" or "aligned" => "InsertAngle",
        "rightreading" or "alwaysrightreading" => "AlwaysRightReadingAngle",
        _ => throw new ToolArgumentException(
            $"Argument 'textAngle' must be 'horizontal', 'insert' or 'rightReading'; got '{spec}'."),
    };

    private static double? ParseFactor(string text)
    {
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            if (TryNumber(text[..colon], out var plotted) && TryNumber(text[(colon + 1)..], out var drawing)
                && plotted > 0 && drawing > 0)
                return drawing / plotted;
            return null;
        }
        return TryNumber(text, out var f) ? f : null;
    }

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool SameFactor(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Abs(b));

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void RequirePositive(string name, double value)
    {
        if (!(value > 0) || double.IsInfinity(value))
            throw new ToolArgumentException(
                $"Argument '{name}' must be a positive number; got {value.ToString(CultureInfo.InvariantCulture)}.");
    }
}
