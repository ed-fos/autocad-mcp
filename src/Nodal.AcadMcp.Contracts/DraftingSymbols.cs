using System.Globalization;

namespace Nodal.AcadMcp.Contracts;

public readonly record struct Line2(double X1, double Y1, double X2, double Y2);

public readonly record struct Circle2(double X, double Y, double Radius);

/// <summary>The projection symbol: a truncated cone's side view (Outline), its end view (Circles), and centre lines.</summary>
public sealed record ProjectionSymbolGeometry(IReadOnlyList<Line2> Outline, IReadOnlyList<Circle2> Circles, IReadOnlyList<Line2> CenterLines);

/// <summary>
/// A roof pitch symbol. Run is the horizontal leg (on top, labelled with the run), Rise the
/// vertical leg (dropping to the roof line, labelled with the rise), Hypotenuse parallel to the
/// roof. Label points are where the labels' alignment points go: the run label is centred above
/// its leg; the rise label sits beside its leg on the side away from the triangle.
/// </summary>
public sealed record SlopeSymbolGeometry(
    Line2 Run, Line2 Rise, Line2 Hypotenuse,
    double RunLabelX, double RunLabelY, double RiseLabelX, double RiseLabelY,
    bool RiseLabelOnLeft, double RiseValue, string RiseText);

/// <summary>
/// Drafting symbols whose geometry is easy to get backwards. Each is pinned by unit tests, so the
/// tools that draw them cannot drift from the convention.
/// </summary>
public static class DraftingSymbols
{
    /// <summary>
    /// ISO 128-3 projection symbol centred on (cx, cy), side view on the left and end view on the
    /// right. <paramref name="size"/> is the large diameter; the small diameter is half of it, the
    /// side view is as long as the large diameter, and the views are half of it apart. In first
    /// angle the cone's large end faces the circles; in third angle its small end does.
    /// </summary>
    public static ProjectionSymbolGeometry ProjectionSymbol(bool firstAngle, double cx, double cy, double size)
    {
        if (!(size > 0) || !double.IsFinite(size)) throw new ArgumentException("size must be a positive number.");
        double big = size, small = size / 2, length = size, gap = size / 2, overrun = size * 0.15;
        var x0 = cx - (length + gap + big) / 2;
        var x1 = x0 + length;
        var near = firstAngle ? big : small; // the end next to the circles
        var far = firstAngle ? small : big;
        var outline = new[]
        {
            new Line2(x0, cy - far / 2, x1, cy - near / 2),
            new Line2(x1, cy - near / 2, x1, cy + near / 2),
            new Line2(x1, cy + near / 2, x0, cy + far / 2),
            new Line2(x0, cy + far / 2, x0, cy - far / 2),
        };
        var circleX = x1 + gap + big / 2;
        var circles = new[] { new Circle2(circleX, cy, big / 2), new Circle2(circleX, cy, small / 2) };
        var centers = new[]
        {
            new Line2(x0 - overrun, cy, circleX + big / 2 + overrun, cy),
            new Line2(circleX, cy - big / 2 - overrun, circleX, cy + big / 2 + overrun),
        };
        return new ProjectionSymbolGeometry(outline, circles, centers);
    }

    /// <summary>
    /// A pitch symbol for the roof line (x1, y1)-(x2, y2), at the point of that line nearest
    /// (atX, atY), lifted <paramref name="offset"/> above it. <paramref name="leg"/> is the length
    /// of the horizontal leg; <paramref name="run"/> is the number it is labelled with (12 for
    /// inches per foot) and the rise label is run times the roof's slope.
    /// </summary>
    public static SlopeSymbolGeometry SlopeSymbol(double x1, double y1, double x2, double y2,
        double atX, double atY, double leg, double offset, double run = 12, double labelGap = double.NaN)
    {
        var dx = x2 - x1; var dy = y2 - y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 1e-9)) throw new ArgumentException("The roof line needs two distinct points.");
        if (Math.Abs(dx) <= 1e-9 * length) throw new ArgumentException("The roof line is vertical, so it has no pitch to show.");
        if (Math.Abs(dy) <= 1e-9 * length) throw new ArgumentException("The roof line is level, so it has no pitch to show.");
        if (!(leg > 0) || !(run > 0) || offset < 0) throw new ArgumentException("leg and run must be positive and offset not negative.");
        if (double.IsNaN(labelGap)) labelGap = leg * 0.15;

        var slope = dy / dx;
        var t = ((atX - x1) * dx + (atY - y1) * dy) / (length * length);
        var px = x1 + t * dx; var py = y1 + t * dy;

        // L is the low end of the hypotenuse, H the high end, C the corner above L.
        var risesRight = slope > 0;
        double lx = px, ly = py + offset;
        double hx = lx + (risesRight ? leg : -leg), hy = ly + leg * Math.Abs(slope);
        double cxCorner = lx, cyCorner = hy;

        var rise = run * Math.Abs(slope);
        return new SlopeSymbolGeometry(
            Run: new Line2(cxCorner, cyCorner, hx, hy),
            Rise: new Line2(lx, ly, cxCorner, cyCorner),
            Hypotenuse: new Line2(lx, ly, hx, hy),
            RunLabelX: (cxCorner + hx) / 2, RunLabelY: hy + labelGap,
            RiseLabelX: risesRight ? lx - labelGap : lx + labelGap, RiseLabelY: (ly + cyCorner) / 2,
            RiseLabelOnLeft: risesRight, RiseValue: rise, RiseText: Number(rise));
    }

    /// <summary>A label number: whole when it is whole, otherwise at most two decimals, never in exponent form.</summary>
    public static string Number(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
