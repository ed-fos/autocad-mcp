using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// create_text, create_mtext, create_dimension, create_mleader and insert_block size annotative
/// objects themselves, because AutoCAD does not scale a size set through the API. A wrong factor
/// here is text 48 times too big or too small on a real sheet, so the arithmetic and the scale
/// lookup are pinned down without AutoCAD.
/// </summary>
public class AnnotationSizingTests
{
    private const double PaperText = 3.0 / 32.0;

    /// <summary>The scale list of the ACA "Aec Sheet (Imperial Ctb)" template, as AutoCAD 2027 reports it.</summary>
    private static readonly AnnotationSizing.Scale[] Imperial =
    [
        new("1/64\" = 1'-0\"", 12, 0.015625), new("1/32\" = 1'-0\"", 12, 0.03125),
        new("1/16\" = 1'-0\"", 12, 0.0625), new("3/32\" = 1'-0\"", 12, 0.09375),
        new("1/8\" = 1'-0\"", 12, 0.125), new("3/16\" = 1'-0\"", 12, 0.1875),
        new("1/4\" = 1'-0\"", 12, 0.25), new("3/8\" = 1'-0\"", 12, 0.375),
        new("1/2\" = 1'-0\"", 12, 0.5), new("3/4\" = 1'-0\"", 12, 0.75),
        new("1\" = 1'-0\"", 12, 1), new("1 1/2\" = 1'-0\"", 12, 1.5),
        new("3\" = 1'-0\"", 12, 3), new("6\" = 1'-0\"", 12, 6),
        new("1'-0\" = 1'-0\"", 12, 12),
    ];

    private const int QuarterInch = 6;

    // ------------------------------------------------------------------ sizes

    [Fact]
    public void A_plotted_height_becomes_the_plotted_height_times_the_scale()
    {
        var size = AnnotationSizing.Resolve("height", null, "paperHeight", PaperText, 48, annotative: true, 0.125, 0.125);

        Assert.Equal(4.5, size.Model, 12);
        Assert.Equal(PaperText, size.Paper);
    }

    [Fact]
    public void A_plotted_height_sizes_non_annotative_text_for_that_one_scale_too()
    {
        var size = AnnotationSizing.Resolve("height", null, "paperHeight", PaperText, 48, annotative: false, 0.125, 0.125);
        Assert.Equal(4.5, size.Model, 12);
    }

    [Fact]
    public void A_drawing_unit_height_is_kept_and_an_annotative_one_reports_its_plotted_size()
    {
        var annotative = AnnotationSizing.Resolve("height", 4.5, "paperHeight", null, 48, annotative: true, 0.125, 0.125);
        Assert.Equal(4.5, annotative.Model);
        Assert.Equal(PaperText, annotative.Paper!.Value, 12);

        var plain = AnnotationSizing.Resolve("height", 4.5, "paperHeight", null, 48, annotative: false, 0.125, 0.125);
        Assert.Equal(4.5, plain.Model);
        Assert.Null(plain.Paper);
    }

    [Fact]
    public void With_no_size_annotative_text_plots_at_the_fallback_and_plain_text_keeps_the_drawing_fallback()
    {
        var annotative = AnnotationSizing.Resolve("height", null, "paperHeight", null, 48, annotative: true, fallbackPaper: 0.125, fallbackModel: 0.2);
        Assert.Equal(6.0, annotative.Model, 12);
        Assert.Equal(0.125, annotative.Paper);

        // Unchanged from before annotative support: TEXTSIZE in drawing units.
        var plain = AnnotationSizing.Resolve("height", null, "paperHeight", null, 48, annotative: false, fallbackPaper: 0.125, fallbackModel: 0.2);
        Assert.Equal(0.2, plain.Model);
        Assert.Null(plain.Paper);
    }

    [Fact]
    public void Paper_space_is_one_to_one()
    {
        var size = AnnotationSizing.Resolve("height", null, "paperHeight", PaperText, 1, annotative: true, 0.125, 0.125);
        Assert.Equal(PaperText, size.Model);
    }

    [Fact]
    public void Both_a_drawing_and_a_plotted_size_is_refused_naming_both()
    {
        var ex = Assert.Throws<ToolArgumentException>(() =>
            AnnotationSizing.Resolve("textHeight", 4.5, "paperHeight", PaperText, 48, true, 0.125, 0.125));

        Assert.Contains("'textHeight'", ex.Message);
        Assert.Contains("'paperHeight'", ex.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.09375)]
    public void A_plotted_size_must_be_positive(double paper)
    {
        var ex = Assert.Throws<ToolArgumentException>(() =>
            AnnotationSizing.Resolve("height", null, "paperHeight", paper, 48, true, 0.125, 0.125));
        Assert.Contains("'paperHeight'", ex.Message);
    }

    // ------------------------------------------------------------------ scale lookup

    [Theory]
    [InlineData("1/4\" = 1'-0\"")]
    [InlineData("1/4\"  =  1'-0\"")]
    [InlineData("  1/4\" = 1'-0\" ")]
    public void A_scale_is_found_by_name_ignoring_extra_spaces(string spec) =>
        Assert.Equal(QuarterInch, AnnotationSizing.Find(Imperial, spec));

    [Theory]
    [InlineData("48")]
    [InlineData("1:48")]
    [InlineData(" 1 : 48 ")]
    public void A_scale_is_found_by_factor_or_ratio_text(string spec) =>
        Assert.Equal(QuarterInch, AnnotationSizing.Find(Imperial, spec));

    [Fact]
    public void A_scale_is_found_by_a_numeric_factor() =>
        Assert.Equal(QuarterInch, AnnotationSizing.Find(Imperial, 48.0));

    [Fact]
    public void Metric_names_match_directly()
    {
        AnnotationSizing.Scale[] metric = [new("1:1", 1, 1), new("1:50", 50, 1), new("1:100", 100, 1)];

        Assert.Equal(1, AnnotationSizing.Find(metric, "1:50"));
        Assert.Equal(2, AnnotationSizing.Find(metric, 100.0));
    }

    [Fact]
    public void Among_scales_with_the_same_factor_the_preferred_one_wins_else_the_first()
    {
        AnnotationSizing.Scale[] both = [.. Imperial, new("1:48", 48, 1)];

        Assert.Equal(QuarterInch, AnnotationSizing.Find(both, 48.0));
        Assert.Equal(both.Length - 1, AnnotationSizing.Find(both, 48.0, preferredName: "1:48"));
        Assert.Equal(both.Length - 1, AnnotationSizing.Find(both, "1:48"));   // an exact name beats a factor match
    }

    [Fact]
    public void One_to_one_prefers_the_named_current_scale()
    {
        AnnotationSizing.Scale[] scales = [.. Imperial, new("1:1", 1, 1)];

        Assert.Equal(Imperial.Length - 1, AnnotationSizing.Find(scales, 1.0, preferredName: "1'-0\" = 1'-0\""));
        Assert.Equal(scales.Length - 1, AnnotationSizing.Find(scales, 1.0, preferredName: "1:1"));
    }

    [Theory]
    [InlineData("1/5\" = 1'-0\"")]
    [InlineData("47")]
    [InlineData("1:47")]
    public void A_scale_the_drawing_does_not_list_is_not_found(string spec) =>
        Assert.Null(AnnotationSizing.Find(Imperial, spec));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-48.0)]
    public void A_numeric_factor_must_be_positive(double factor) =>
        Assert.Throws<ToolArgumentException>(() => AnnotationSizing.Find(Imperial, factor));

    [Fact]
    public void A_blank_scale_name_is_refused() =>
        Assert.Throws<ToolArgumentException>(() => AnnotationSizing.Find(Imperial, "   "));

    // ------------------------------------------------------------------ leaders

    [Fact]
    public void A_straight_leader_starts_at_the_arrow()
    {
        var (first, prepend) = AnnotationSizing.LeaderOrder("tip", Array.Empty<string>());

        Assert.Equal("tip", first);
        Assert.Empty(prepend);
    }

    [Fact]
    public void A_bent_leader_starts_nearest_the_landing_and_prepends_back_to_the_arrow()
    {
        var (first, prepend) = AnnotationSizing.LeaderOrder("tip", ["bend1", "bend2"]);

        Assert.Equal("bend2", first);
        Assert.Equal(["bend1", "tip"], prepend);

        // What AddLeaderLine + AddFirstVertex leave in the drawing: arrow first, landing side last.
        var drawn = new List<string> { first };
        foreach (var p in prepend) drawn.Insert(0, p);
        Assert.Equal(["tip", "bend1", "bend2"], drawn);
    }

    [Theory]
    [InlineData("horizontal", "HorizontalAngle")]
    [InlineData("Horizontal", "HorizontalAngle")]
    [InlineData("insert", "InsertAngle")]
    [InlineData("rightReading", "AlwaysRightReadingAngle")]
    [InlineData("right reading", "AlwaysRightReadingAngle")]
    public void Text_angle_names_map_to_the_MLeader_enum(string spec, string member) =>
        Assert.Equal(member, AnnotationSizing.TextAngleName(spec));

    [Fact]
    public void An_unknown_text_angle_is_refused_listing_the_choices()
    {
        var ex = Assert.Throws<ToolArgumentException>(() => AnnotationSizing.TextAngleName("diagonal"));
        Assert.Contains("'horizontal'", ex.Message);
    }
}
