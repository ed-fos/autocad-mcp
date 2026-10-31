namespace Nodal.AcadMcp.Contracts;

/// <summary>A straight piece of linework. Owner identifies the drawing entity it came from.</summary>
public readonly record struct Segment2(int Owner, double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

/// <summary>An axis-aligned box around a piece of annotation. Owner identifies its entity.</summary>
public readonly record struct Rect2(int Owner, double MinX, double MinY, double MaxX, double MaxY);

public readonly record struct BoxOverlap(int A, int B, double Width, double Height);

/// <summary>Two parallel segments lying along each other: Separation 0 is a doubled line, a small one a sliver gap.</summary>
public readonly record struct ParallelOverlap(int A, int B, double Overlap, double Separation);

/// <summary>An end of segment Segment that touches nothing but comes within Distance of segment Other.</summary>
public readonly record struct NearMiss(int Segment, int Other, double X, double Y, double Distance);

/// <summary>
/// Geometry checks for a finished drawing: annotation that collides, lines drawn twice or a
/// sliver apart, and ends that stop just short of what they should meet. Pure 2D math on
/// segments and boxes the caller extracts, so it is testable without AutoCAD.
/// </summary>
public static class DraftingChecks
{
    /// <summary>Pairs of boxes from different owners that overlap by more than <paramref name="tolerance"/> both ways.</summary>
    public static List<BoxOverlap> OverlappingBoxes(IReadOnlyList<Rect2> boxes, double tolerance)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderBy(i => boxes[i].MinX).ToArray();
        var active = new List<int>();
        var found = new List<BoxOverlap>();
        foreach (var i in order)
        {
            var a = boxes[i];
            active.RemoveAll(j => boxes[j].MaxX < a.MinX);
            foreach (var j in active)
            {
                var b = boxes[j];
                if (a.Owner == b.Owner) continue;
                var w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                var h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                if (w > tolerance && h > tolerance) found.Add(new BoxOverlap(Math.Min(i, j), Math.Max(i, j), w, h));
            }
            active.Add(i);
        }
        return found;
    }

    /// <summary>
    /// Parallel segments within <paramref name="maxSeparation"/> of each other that run alongside
    /// each other for more than <paramref name="minOverlap"/>. Separation is the larger distance
    /// from either segment's ends to the other's line.
    /// </summary>
    public static List<ParallelOverlap> ParallelOverlaps(IReadOnlyList<Segment2> segments, double maxSeparation, double minOverlap)
    {
        var grid = new SegmentGrid(segments, maxSeparation);
        var found = new List<ParallelOverlap>();
        foreach (var (i, j) in grid.CandidatePairs())
        {
            var a = segments[i]; var b = segments[j];
            var la = a.Length; var lb = b.Length;
            if (la <= 0 || lb <= 0) continue;
            double ux = (a.X2 - a.X1) / la, uy = (a.Y2 - a.Y1) / la;
            double vx = (b.X2 - b.X1) / lb, vy = (b.Y2 - b.Y1) / lb;
            // Parallel enough that the far end of the shorter one drifts less than maxSeparation.
            var cross = Math.Abs(ux * vy - uy * vx);
            if (cross * Math.Min(la, lb) > maxSeparation) continue;
            var separation = Math.Max(
                Math.Max(LineDistance(a, b.X1, b.Y1), LineDistance(a, b.X2, b.Y2)),
                Math.Max(LineDistance(b, a.X1, a.Y1), LineDistance(b, a.X2, a.Y2)));
            if (separation > maxSeparation) continue;
            var t1 = (b.X1 - a.X1) * ux + (b.Y1 - a.Y1) * uy;
            var t2 = (b.X2 - a.X1) * ux + (b.Y2 - a.Y1) * uy;
            var overlap = Math.Min(la, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
            if (overlap > minOverlap) found.Add(new ParallelOverlap(i, j, overlap, separation));
        }
        return found;
    }

    /// <summary>
    /// Segment ends that touch nothing (no segment within <paramref name="touch"/>) but come within
    /// <paramref name="gap"/> of another segment: the end of a line that should meet another and
    /// stops short. At most one report per end, for the nearest segment.
    /// </summary>
    public static List<NearMiss> NearMisses(IReadOnlyList<Segment2> segments, double gap, double touch)
    {
        var grid = new SegmentGrid(segments, gap);
        var found = new List<NearMiss>();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            foreach (var (x, y) in new[] { (s.X1, s.Y1), (s.X2, s.Y2) })
            {
                var touches = false; var best = double.MaxValue; var bestIndex = -1;
                foreach (var j in grid.Near(x, y, gap))
                {
                    if (j == i) continue;
                    var d = SegmentDistance(segments[j], x, y);
                    if (d <= touch) { touches = true; break; }
                    if (d <= gap && d < best) { best = d; bestIndex = j; }
                }
                if (!touches && bestIndex >= 0) found.Add(new NearMiss(i, bestIndex, x, y, best));
            }
        }
        return found;
    }

    public static double SegmentDistance(Segment2 s, double x, double y)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, l2 = dx * dx + dy * dy;
        var t = l2 > 0 ? Math.Clamp(((x - s.X1) * dx + (y - s.Y1) * dy) / l2, 0, 1) : 0;
        double px = s.X1 + t * dx - x, py = s.Y1 + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }

    private static double LineDistance(Segment2 s, double x, double y)
    {
        var l = s.Length;
        return l > 0 ? Math.Abs((s.X2 - s.X1) * (y - s.Y1) - (s.Y2 - s.Y1) * (x - s.X1)) / l : Math.Sqrt((x - s.X1) * (x - s.X1) + (y - s.Y1) * (y - s.Y1));
    }

    /// <summary>
    /// A uniform grid over the segments' extents. Each segment is registered in the cells its box,
    /// grown by the search radius, covers; a segment that would span too many cells is kept in a
    /// list every query checks instead.
    /// </summary>
    private sealed class SegmentGrid
    {
        private const int MaxCellsPerSegment = 256;
        private readonly Dictionary<(long, long), List<int>> _cells = new();
        private readonly List<int> _wide = new();
        private readonly double _size;

        public SegmentGrid(IReadOnlyList<Segment2> segments, double radius)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in segments)
            {
                minX = Math.Min(minX, Math.Min(s.X1, s.X2)); maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
                minY = Math.Min(minY, Math.Min(s.Y1, s.Y2)); maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));
            }
            var extent = segments.Count == 0 ? 1 : Math.Max(maxX - minX, maxY - minY);
            _size = Math.Max(Math.Max(radius * 4, extent / 512), 1e-9);
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                long x0 = Cell(Math.Min(s.X1, s.X2) - radius), x1 = Cell(Math.Max(s.X1, s.X2) + radius);
                long y0 = Cell(Math.Min(s.Y1, s.Y2) - radius), y1 = Cell(Math.Max(s.Y1, s.Y2) + radius);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerSegment) { _wide.Add(i); continue; }
                for (var cx = x0; cx <= x1; cx++)
                    for (var cy = y0; cy <= y1; cy++)
                    {
                        if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = new List<int>();
                        list.Add(i);
                    }
            }
        }

        private long Cell(double v) => (long)Math.Floor(v / _size);

        public IEnumerable<int> Near(double x, double y, double radius)
        {
            var seen = new HashSet<int>(_wide);
            foreach (var i in _wide) yield return i;
            for (var cx = Cell(x - radius); cx <= Cell(x + radius); cx++)
                for (var cy = Cell(y - radius); cy <= Cell(y + radius); cy++)
                    if (_cells.TryGetValue((cx, cy), out var list))
                        foreach (var i in list) if (seen.Add(i)) yield return i;
        }

        public IEnumerable<(int, int)> CandidatePairs()
        {
            var seen = new HashSet<(int, int)>();
            foreach (var list in _cells.Values)
                for (var a = 0; a < list.Count; a++)
                    for (var b = a + 1; b < list.Count; b++)
                    {
                        var pair = list[a] < list[b] ? (list[a], list[b]) : (list[b], list[a]);
                        if (seen.Add(pair)) yield return pair;
                    }
            var all = _cells.Values.SelectMany(l => l).Concat(_wide).Distinct().ToList();
            foreach (var w in _wide)
                foreach (var o in all)
                {
                    if (o == w) continue;
                    var pair = w < o ? (w, o) : (o, w);
                    if (seen.Add(pair)) yield return pair;
                }
        }
    }
}

/// <summary>
/// The space in front of and behind a door or window, in the opening's own frame (X along the
/// wall from the opening's start, Y across the wall, Z up from the sill), that furniture and
/// casework should stay out of.
/// </summary>
public readonly record struct OpeningZone(double Width, double Height, double HalfDepth)
{
    /// <summary>
    /// Whether a box given in the opening's frame enters the zone. With <paramref name="planOnly"/>
    /// the box has no usable height (plan extents only), so only X and Y are tested.
    /// </summary>
    public bool Intersects(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, bool planOnly, double tolerance = 1e-6) =>
        maxX > tolerance && minX < Width - tolerance &&
        maxY > -HalfDepth + tolerance && minY < HalfDepth - tolerance &&
        (planOnly || (maxZ > tolerance && minZ < Height - tolerance));
}
