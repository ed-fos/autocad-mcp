using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

public class DraftingTests
{
    // The end of the side view that faces the circles: the outline's vertical edge nearest them.
    private static double EndFacingCircles(ProjectionSymbolGeometry g)
    {
        var circleX = g.Circles[0].X;
        var vertical = g.Outline.Where(l => Math.Abs(l.X1 - l.X2) < 1e-9).OrderBy(l => Math.Abs(l.X1 - circleX)).First();
        return Math.Abs(vertical.Y2 - vertical.Y1);
    }

    [Fact]
    public void First_angle_turns_the_cones_large_end_toward_the_circles()
    {
        var g = DraftingSymbols.ProjectionSymbol(firstAngle: true, 0, 0, 10);
        Assert.Equal(10, EndFacingCircles(g), 9);
        Assert.True(g.Circles.All(c => c.X > g.Outline.Max(l => Math.Max(l.X1, l.X2))), "The end view sits to the right of the side view.");
    }

    [Fact]
    public void Third_angle_turns_the_cones_small_end_toward_the_circles()
    {
        var g = DraftingSymbols.ProjectionSymbol(firstAngle: false, 0, 0, 10);
        Assert.Equal(5, EndFacingCircles(g), 9);
    }

    [Fact]
    public void Projection_symbol_circles_match_the_cone_ends_and_are_concentric()
    {
        var g = DraftingSymbols.ProjectionSymbol(true, 3, 4, 8);
        Assert.Equal(2, g.Circles.Count);
        Assert.Equal(g.Circles[0].X, g.Circles[1].X);
        Assert.Equal(new[] { 2.0, 4.0 }, g.Circles.Select(c => c.Radius).OrderBy(r => r));
        Assert.All(g.CenterLines, l => Assert.True(l.Y1 == 4 && l.Y2 == 4 || l.X1 == g.Circles[0].X));
    }

    [Theory]
    [InlineData(0, 0, 120, 50)]   // rises to the right
    [InlineData(0, 50, 120, 0)]   // rises to the left
    [InlineData(120, 50, 0, 0)]   // same line given the other way round
    public void Pitch_symbol_has_its_run_on_top_and_its_rise_dropping_to_the_roof(double x1, double y1, double x2, double y2)
    {
        var g = DraftingSymbols.SlopeSymbol(x1, y1, x2, y2, 60, 25, leg: 12, offset: 3);
        // Run leg: horizontal, the highest part of the symbol.
        Assert.Equal(g.Run.Y1, g.Run.Y2, 9);
        Assert.True(g.Run.Y1 >= Math.Max(g.Rise.Y1, g.Rise.Y2) - 1e-9);
        // Rise leg: vertical, from the top leg down to the hypotenuse's low end.
        Assert.Equal(g.Rise.X1, g.Rise.X2, 9);
        Assert.Equal(g.Hypotenuse.Y1, Math.Min(g.Rise.Y1, g.Rise.Y2), 9);
        // Hypotenuse parallel to the roof, lifted above it.
        var roofSlope = (y2 - y1) / (x2 - x1);
        Assert.Equal(roofSlope, (g.Hypotenuse.Y2 - g.Hypotenuse.Y1) / (g.Hypotenuse.X2 - g.Hypotenuse.X1), 9);
        var roofYAtLowEnd = y1 + roofSlope * (g.Hypotenuse.X1 - x1);
        Assert.Equal(3, g.Hypotenuse.Y1 - roofYAtLowEnd, 9);
        // Labels: run above its leg, rise outside the triangle.
        Assert.True(g.RunLabelY > g.Run.Y1);
        var triangleSide = (g.Run.X1 + g.Run.X2) / 2;
        Assert.True(g.RiseLabelOnLeft ? g.RiseLabelX < g.Rise.X1 && triangleSide > g.Rise.X1 : g.RiseLabelX > g.Rise.X1 && triangleSide < g.Rise.X1);
        Assert.Equal("5", g.RiseText);
    }

    [Fact]
    public void Pitch_symbol_reports_a_fractional_rise_and_refuses_lines_without_pitch()
    {
        Assert.Equal("6.94", DraftingSymbols.SlopeSymbol(0, 0, 12, 6.94, 0, 0, 1, 0).RiseText);
        Assert.Throws<ArgumentException>(() => DraftingSymbols.SlopeSymbol(0, 0, 10, 0, 0, 0, 1, 0));
        Assert.Throws<ArgumentException>(() => DraftingSymbols.SlopeSymbol(0, 0, 0, 10, 0, 0, 1, 0));
    }

    [Fact]
    public void Overlapping_annotation_is_found_but_pieces_of_one_entity_are_not()
    {
        var boxes = new[]
        {
            new Rect2(1, 0, 0, 10, 2),   // note
            new Rect2(2, 9, 0.5, 15, 2.2), // dimension text overlapping the note's first line
            new Rect2(1, 0, 2.5, 10, 4), // second line of the note, near its own first line
            new Rect2(3, 20, 0, 25, 2),  // clear
        };
        var hits = DraftingChecks.OverlappingBoxes(boxes, 0.01);
        Assert.Single(hits);
        Assert.Equal((0, 1), (hits[0].A, hits[0].B));
        Assert.Equal(1, hits[0].Width, 9);
    }

    [Fact]
    public void Doubled_lines_and_sliver_gaps_are_found_with_their_separation()
    {
        var segments = new[]
        {
            new Segment2(1, 0, 0, 100, 0),
            new Segment2(2, 40, 0, 140, 0),     // doubled over 60
            new Segment2(3, 0, 0.25, 100, 0.25),// a quarter unit above the first: a sliver
            new Segment2(4, 0, 20, 100, 20),    // a separate line, far away
            new Segment2(5, 0, 0, 0, 50),       // perpendicular, touching
        };
        var hits = DraftingChecks.ParallelOverlaps(segments, maxSeparation: 0.5, minOverlap: 1);
        Assert.Contains(hits, h => h.A == 0 && h.B == 1 && Math.Abs(h.Overlap - 60) < 1e-9 && h.Separation < 1e-9);
        Assert.Contains(hits, h => h.A == 0 && h.B == 2 && Math.Abs(h.Separation - 0.25) < 1e-9);
        Assert.DoesNotContain(hits, h => h.A == 3 || h.B == 3 || h.A == 4 || h.B == 4);
    }

    [Fact]
    public void An_end_that_stops_short_is_a_near_miss_and_a_joined_end_is_not()
    {
        var segments = new[]
        {
            new Segment2(1, 0, 0, 100, 0),     // wall top
            new Segment2(2, 50, 0.3, 50, 40),  // a line that should sit on the wall but stops 0.3 short
            new Segment2(3, 0, 0, 0, 40),      // joined exactly at the wall's end
            new Segment2(4, 100, 0, 100, 40),  // joined exactly
        };
        var misses = DraftingChecks.NearMisses(segments, gap: 1, touch: 1e-6);
        var miss = Assert.Single(misses);
        Assert.Equal(1, miss.Segment);
        Assert.Equal(0, miss.Other);
        Assert.Equal(0.3, miss.Distance, 9);
        Assert.Equal((50.0, 0.3), (miss.X, miss.Y));
    }

    [Fact]
    public void Checks_scale_to_long_segments_and_many_pieces()
    {
        var segments = new List<Segment2> { new(0, -1e5, 0, 1e5, 0), new(1, -1e5, 0, 1e5, 0) };
        for (var i = 0; i < 2000; i++) segments.Add(new Segment2(i + 2, i * 10, 5, i * 10 + 4, 5));
        var hit = Assert.Single(DraftingChecks.ParallelOverlaps(segments, 0.5, 1));
        Assert.Equal((0, 1), (hit.A, hit.B));
    }

    [Theory]
    [InlineData(10, -5, 50, 30, 5, 72, false, true)]   // a range in front of the window, up to the sill and above
    [InlineData(10, -5, 0, 30, 5, 30, false, false)]  // below a 36" sill: clear
    [InlineData(40, -5, 40, 60, 5, 60, false, false)] // beside the opening: clear
    [InlineData(10, -60, 40, 30, -50, 60, false, false)] // too far into the room: clear
    [InlineData(10, -5, 0, 30, 5, 0, true, true)]     // plan-only extents over the opening: reported
    public void Opening_zone_catches_what_sits_in_front_of_the_glass(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, bool planOnly, bool expected)
    {
        // A 36" wide window with a 48" tall opening, sill 36" above the floor: the zone starts at the sill.
        var zone = new OpeningZone(36, 48, 24);
        Assert.Equal(expected, zone.Intersects(minX, minY, minZ - 36, maxX, maxY, maxZ - 36, planOnly));
    }
}
