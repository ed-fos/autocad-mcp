using System.Text.Json;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// The arithmetic behind the sheet tools. A wrong answer here does not fail loudly: it puts a
/// viewport at the wrong scale, crops a preview to the wrong part of the sheet, or overwrites the
/// wrong PDF — so each rule is pinned down here, away from AutoCAD.
/// </summary>
public class SheetMathTests
{
    private const double Eps = 1e-12;

    // ---------------------------------------------------------------- scales

    [Theory]
    [InlineData("1/4\" = 1'-0\"", 1.0 / 48)]
    [InlineData("1/4\"=1'-0\"", 1.0 / 48)]
    [InlineData("1/4\" = 1'", 1.0 / 48)]
    [InlineData("3/16\" = 1'-0\"", 1.0 / 64)]
    [InlineData("3/32\" = 1'-0\"", 1.0 / 128)]
    [InlineData("1/8\" = 1'-0\"", 1.0 / 96)]
    [InlineData("1-1/2\" = 1'-0\"", 1.0 / 8)]
    [InlineData("1 1/2\" = 1'-0\"", 1.0 / 8)]
    [InlineData("12\" = 1'-0\"", 1.0)]
    [InlineData("1'-0\" = 1'-0\"", 1.0)]
    [InlineData("1\" = 10'", 1.0 / 120)]
    [InlineData("1\" = 20'-0\"", 1.0 / 240)]
    [InlineData("1/2\" = 2'-6 1/2\"", 0.5 / 30.5)]
    [InlineData("1:48", 1.0 / 48)]
    [InlineData(" 1 : 100 ", 0.01)]
    [InlineData("2:1", 2.0)]
    [InlineData("1:1", 1.0)]
    [InlineData("1 = 48", 1.0 / 48)]
    [InlineData("1/48", 1.0 / 48)]
    [InlineData("0.0208333", 0.0208333)]
    [InlineData("0.25", 0.25)]
    public void Scale_strings_give_paper_units_per_drawing_unit(string text, double factor) =>
        Assert.Equal(factor, SheetMath.ParseScale(text).Factor, 12);

    [Fact]
    public void An_architectural_scale_keeps_both_sides_for_a_custom_plot_scale()
    {
        var r = SheetMath.ParseScale("1/4\" = 1'-0\"");
        Assert.Equal(0.25, r.Paper, 12);
        Assert.Equal(12.0, r.Drawing, 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("quarter inch")]
    [InlineData("1:0")]
    [InlineData("0:1")]
    [InlineData("-1:48")]
    [InlineData("1/0")]
    [InlineData("1/4\"")]          // a length is not a scale
    [InlineData("1/4\" = ")]
    [InlineData("1\"2 = 1'")]
    [InlineData("0")]
    public void Malformed_scales_are_refused_not_guessed(string text) =>
        Assert.Throws<ArgumentException>(() => SheetMath.ParseScale(text));

    [Fact]
    public void Same_scale_tolerates_rounding_but_not_a_different_scale()
    {
        Assert.True(SheetMath.SameScale(1.0 / 48, 0.25 / 12));
        Assert.True(SheetMath.SameScale(1.0 / 48, 1.0 / 48 * (1 + 1e-9)));
        Assert.False(SheetMath.SameScale(1.0 / 48, 1.0 / 50));
        Assert.False(SheetMath.SameScale(0, 0));
    }

    // ---------------------------------------------------------------- viewports

    [Fact]
    public void A_named_view_with_a_scale_sizes_the_viewport_from_the_view()
    {
        // The drawing session's floor plan: a 585.14 x 369.02 view at 1/4" = 1'-0".
        var v = SheetMath.ResolveViewport(585.14, 369.02, null, null, 1.0 / 48, null);
        Assert.Equal(585.14 / 48, v.Width, 9);
        Assert.Equal(369.02 / 48, v.Height, 9);
        Assert.Equal(1.0 / 48, v.Scale, 12);
    }

    [Fact]
    public void A_named_view_with_a_paper_size_and_no_scale_is_fitted()
    {
        var v = SheetMath.ResolveViewport(400, 100, 10, 10, null, null);
        Assert.Equal(10, v.Width, 12);
        Assert.Equal(10, v.Height, 12);
        Assert.Equal(10.0 / 400, v.Scale, 12); // the width governs
    }

    [Fact]
    public void A_view_height_sets_the_scale_from_the_paper_height()
    {
        var v = SheetMath.ResolveViewport(null, null, 12, 6, null, 288);
        Assert.Equal(6.0 / 288, v.Scale, 12);
        Assert.Equal(12, v.Width, 12);
    }

    [Fact]
    public void An_explicit_size_wins_over_the_view_size()
    {
        var v = SheetMath.ResolveViewport(480, 480, 8, null, 1.0 / 48, null);
        Assert.Equal(8, v.Width, 12);
        Assert.Equal(10, v.Height, 12);
    }

    [Theory]
    [InlineData(null, null, 10.0, 10.0, null, null)]      // no scale and no view to fit
    [InlineData(null, null, 10.0, null, 0.02, null)]      // no height and no view
    [InlineData(100.0, 100.0, null, null, null, null)]    // view but no scale or size
    [InlineData(100.0, 100.0, 10.0, 10.0, 0.02, 400.0)]  // scale and viewHeight both
    [InlineData(null, null, 10.0, null, null, 400.0)]     // viewHeight without a paper height
    [InlineData(100.0, 100.0, -1.0, 10.0, 0.02, null)]    // negative size
    [InlineData(100.0, 100.0, null, null, 0.0, null)]     // zero scale
    public void Underdetermined_or_contradictory_viewports_are_refused(double? vw, double? vh, double? w, double? h, double? s, double? shown) =>
        Assert.Throws<ArgumentException>(() => SheetMath.ResolveViewport(vw, vh, w, h, s, shown));

    // ---------------------------------------------------------------- plot regions and crops

    [Fact]
    public void A_fitted_window_is_centred_on_the_printable_area()
    {
        var (region, scale) = SheetMath.CenteredPlotRegion(new Box(0, 0, 17, 11), 340, 110, null);
        Assert.Equal(17.0 / 340, scale, 12);
        Assert.Equal(new Box(0, 2.75, 17, 8.25), region);
    }

    [Fact]
    public void A_fixed_scale_window_is_centred_too()
    {
        var (region, scale) = SheetMath.CenteredPlotRegion(new Box(0.25, 0.25, 11.75, 8.25), 6, 4, 1.0);
        Assert.Equal(1.0, scale);
        Assert.Equal(new Box(3, 2.25, 9, 6.25), region);
    }

    [Fact]
    public void Crop_fractions_are_of_the_region_from_its_bottom_left()
    {
        var region = new Box(2, 1, 12, 6); // 10 x 5
        var crop = SheetMath.Crop(region, 20, 10, [0.5, 0.0, 1.0, 0.5]);
        Assert.Equal(new Box(7, 1, 12, 3.5), crop);
        Assert.Equal(region, SheetMath.Crop(region, 20, 10, null));
    }

    [Fact]
    public void A_crop_is_clipped_to_the_page()
    {
        var crop = SheetMath.Crop(new Box(-2, -1, 8, 4), 6, 3, null);
        Assert.Equal(new Box(0, 0, 6, 3), crop);
    }

    [Theory]
    [InlineData(new[] { 0.5, 0.0, 0.5, 1.0 })]   // zero width
    [InlineData(new[] { 0.6, 0.0, 0.4, 1.0 })]   // inverted
    [InlineData(new[] { -0.1, 0.0, 1.0, 1.0 })]  // outside 0..1
    [InlineData(new[] { 0.0, 0.0, 1.0, 1.1 })]
    [InlineData(new[] { 0.0, 0.0, 1.0 })]        // not four numbers
    public void Bad_crops_are_refused(double[] fractions) =>
        Assert.Throws<ArgumentException>(() => SheetMath.Crop(new Box(0, 0, 1, 1), 1, 1, fractions));

    [Fact]
    public void A_region_wholly_off_the_page_is_refused() =>
        Assert.Throws<ArgumentException>(() => SheetMath.Crop(new Box(20, 20, 30, 30), 10, 10, null));

    [Fact]
    public void Bottom_left_boxes_become_top_left_renderer_rectangles()
    {
        // A 36 x 24 in page rendered in DIPs (96 per inch); the box is the top-right quarter.
        var (x, y, w, h) = SheetMath.ToTopLeft(new Box(18, 12, 36, 24), 36, 24, 36 * 96, 24 * 96);
        Assert.Equal(18 * 96, x, 9);
        Assert.Equal(0, y, 9);
        Assert.Equal(18 * 96, w, 9);
        Assert.Equal(12 * 96, h, 9);

        var (_, y2, _, _) = SheetMath.ToTopLeft(new Box(0, 0, 36, 6), 36, 24, 3456, 2304);
        Assert.Equal(18 * 96, y2, 9); // the bottom strip starts 18 in down from the top
    }

    [Theory]
    [InlineData(36.0, 24.0, null, null, 1600, 1067)]
    [InlineData(36.0, 24.0, 3000, null, 3000, 2000)]
    [InlineData(36.0, 24.0, null, 1200, 1800, 1200)]
    [InlineData(36.0, 24.0, 3000, 1000, 1500, 1000)]  // fits inside both, keeping the aspect
    [InlineData(10.0, 40.0, 1000, 1000, 250, 1000)]
    [InlineData(1.0, 1.0, 1, null, 1, 1)]
    public void Pixel_sizes_keep_the_region_aspect(double rw, double rh, int? w, int? h, int ew, int eh)
    {
        var (pw, ph) = SheetMath.FitPixels(rw, rh, w, h);
        Assert.Equal(ew, pw);
        Assert.Equal(eh, ph);
    }

    [Theory]
    [InlineData(1.0, 1.0, 0, null)]
    [InlineData(1.0, 1.0, 13000, null)]
    [InlineData(1.0, 1.0, 9000, 9000)]     // 81 MP
    [InlineData(1000.0, 1.0, null, 100)]   // 100000 px wide
    public void Absurd_pixel_sizes_are_refused(double rw, double rh, int? w, int? h) =>
        Assert.Throws<ArgumentException>(() => SheetMath.FitPixels(rw, rh, w, h));

    // ---------------------------------------------------------------- media

    [Fact]
    public void Media_sizes_are_read_from_canonical_names()
    {
        var d = SheetMath.ParseMediaSize("ARCH_full_bleed_D_(36.00_x_24.00_Inches)")!.Value;
        Assert.Equal((36.0, 24.0, "Inches"), (d.Width, d.Height, d.Unit));

        var a1 = SheetMath.ParseMediaSize("ISO_full_bleed_A1_(841.00_x_594.00_MM)")!.Value;
        Assert.Equal("MM", a1.Unit);
        Assert.Equal(841 / 25.4, a1.WidthInches, 9);

        Assert.Null(SheetMath.ParseMediaSize("Letter"));
    }

    [Fact]
    public void Canonical_and_display_media_names_match_after_folding()
    {
        Assert.Equal(SheetMath.MediaKey("ARCH_full_bleed_D_(36.00_x_24.00_Inches)"),
                     SheetMath.MediaKey("ARCH full bleed D (36.00 x 24.00 Inches)"));
        Assert.Equal("arch d (36.00 x 24.00 inches)", SheetMath.MediaKey("  ARCH  D  (36.00 x 24.00 Inches) "));
        Assert.NotEqual(SheetMath.MediaKey("ARCH_D_(36.00_x_24.00_Inches)"), SheetMath.MediaKey("ARCH_full_bleed_D_(36.00_x_24.00_Inches)"));
    }

    private static readonly string[] PdfMedia =
    [
        "ANSI_A_(8.50_x_11.00_Inches)",
        "ANSI_full_bleed_A_(11.00_x_8.50_Inches)",
        "ANSI_full_bleed_A_(8.50_x_11.00_Inches)",
        "ANSI_full_bleed_B_(17.00_x_11.00_Inches)",
        "ANSI_full_bleed_B_(11.00_x_17.00_Inches)",
        "ARCH_full_bleed_D_(36.00_x_24.00_Inches)",
        "ARCH_full_bleed_D_(24.00_x_36.00_Inches)",
        "ARCH_full_bleed_E1_(42.00_x_30.00_Inches)",
        "ISO_full_bleed_A0_(841.00_x_1189.00_MM)",
    ];

    [Theory]
    [InlineData(9.0, 6.0, "ANSI_full_bleed_A_(11.00_x_8.50_Inches)")]
    [InlineData(6.0, 9.0, "ANSI_full_bleed_A_(8.50_x_11.00_Inches)")]
    [InlineData(12.0, 6.0, "ANSI_full_bleed_B_(17.00_x_11.00_Inches)")]
    [InlineData(36.0, 24.0, "ARCH_full_bleed_D_(36.00_x_24.00_Inches)")]
    [InlineData(100.0, 50.0, "ARCH_full_bleed_E1_(42.00_x_30.00_Inches)")]   // nothing fits: the largest
    [InlineData(20.0, 40.0, "ISO_full_bleed_A0_(841.00_x_1189.00_MM)")]
    public void Preview_media_is_the_smallest_full_bleed_sheet_that_holds_the_window(double w, double h, string expected) =>
        Assert.Equal(expected, SheetMath.ChoosePreviewMedia(PdfMedia, w, h)!.Value.Name);

    [Fact]
    public void Preview_media_needs_a_full_bleed_size() =>
        Assert.Null(SheetMath.ChoosePreviewMedia(["ANSI_A_(8.50_x_11.00_Inches)", "Letter"], 5, 5));

    [Theory]
    [InlineData("DWG To PDF.pc3", true)]
    [InlineData("AutoCAD PDF (High Quality Print).pc3", true)]
    [InlineData("dwg to pdf.PC3", true)]
    [InlineData("None", false)]
    [InlineData("Microsoft Print to PDF", false)]   // a system printer: it opens a Save dialog
    [InlineData("PublishToWeb PNG.pc3", false)]
    [InlineData(null, false)]
    public void Only_pdf_pc3_devices_count_as_pdf(string? device, bool expected) =>
        Assert.Equal(expected, SheetMath.IsPdfDevice(device));

    // ---------------------------------------------------------------- file names

    [Theory]
    [InlineData("{layout}", "A-01", 1, 2, 1, "Tiny", "A-01.pdf")]
    [InlineData("{drawing} - {layout}", "A-01", 1, 2, 1, "Sample House", "Sample House - A-01.pdf")]
    [InlineData("{index} {layout}", "A-02", 2, 12, 5, "x", "02 A-02.pdf")]
    [InlineData("Sheet {tab}.pdf", "A-02", 2, 2, 7, "x", "Sheet 7.pdf")]
    [InlineData("{LAYOUT}", "A/01:B?", 1, 1, 1, "x", "A_01_B_.pdf")]
    [InlineData("{layout}", "CON", 1, 1, 1, "x", "_CON.pdf")]
    [InlineData("{layout}. ", "A-01", 1, 1, 1, "x", "A-01.pdf")]
    public void File_name_patterns_expand_and_are_made_safe(string pattern, string layout, int index, int count, int tab, string drawing, string expected) =>
        Assert.Equal(expected, SheetMath.ExpandFileName(pattern, layout, index, count, tab, drawing));

    [Theory]
    [InlineData("{sheet}")]
    [InlineData("{layout")]
    [InlineData("{}")]
    [InlineData("  ")]
    [InlineData("...")]
    public void Bad_file_name_patterns_are_refused(string pattern) =>
        Assert.Throws<ArgumentException>(() => SheetMath.ExpandFileName(pattern, "A-01", 1, 1, 1, "x"));

    // ---------------------------------------------------------------- schema helpers

    [Fact]
    public void A_described_property_still_validates_as_its_schema()
    {
        var schema = ToolSchema.Object(["layout"],
            ("layout", ToolSchema.Describe(ToolSchema.Text(), "Layout name.")),
            ("scale", ToolSchema.Describe(ToolSchema.Any(), "String or number.")),
            ("custom", ToolSchema.Map()));

        Assert.Null(ToolSchema.Validate(Json("""{"layout":"A-01","scale":"1:48","custom":{"a":"b"}}"""), schema));
        Assert.Null(ToolSchema.Validate(Json("""{"layout":"A-01","scale":0.02}"""), schema));
        Assert.NotNull(ToolSchema.Validate(Json("""{"layout":5}"""), schema));
        Assert.NotNull(ToolSchema.Validate(Json("""{"layout":"A-01","centre":[1,2]}"""), schema));
        Assert.NotNull(ToolSchema.Validate(Json("""{"layout":"A-01","custom":[1]}"""), schema));
        Assert.Contains("Layout name.", JsonSerializer.Serialize(schema), StringComparison.Ordinal);
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
}
