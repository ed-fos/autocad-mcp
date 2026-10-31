using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Plotting: PDF sheet sets and PNG previews, through AutoCAD's own publish engine.
///
/// The recipe is the one the drawing session proved: BACKGROUNDPLOT forced to 0 for the call (a
/// background plot returns before the file exists), each layout made current before its PlotInfo
/// is built (a PlotInfo for a non-current layout fails with eLayoutNotCurrent), and a combined set
/// plotted with one PublishEngine — BeginDocument once, BeginPage(isLast) per sheet. The previous
/// layout and BACKGROUNDPLOT are restored even when a plot fails.
///
/// Previews plot to a temporary PDF and rasterise it with Windows' PDF renderer (PdfRaster), so the
/// picture has the real plot styles and lineweights at any pixel size, unlike PNGOUT.
/// </summary>
internal static class PlotTools
{
    private const string PdfDevice = "DWG To PDF.pc3";
    private const int DefaultPreviewWidth = 1600;
    internal const int MaxInlineBytes = 8 * 1024 * 1024;

    /// <summary>Long side, in inches, of the paper a model-space window is fitted onto when no scale is given.</summary>
    private const double ModelPreviewInches = 17.0;

    internal static void Register()
    {
        RegisterPlotLayoutsToPdf();
        RegisterPlotPreview();
    }

    // -------------------------------------------------------- plot_layouts_to_pdf

    private static void RegisterPlotLayoutsToPdf()
    {
        Router.Register("plot_layouts_to_pdf",
            "Plot layouts to PDF using each layout's own page setup (set it with set_page_setup): one PDF per "
          + "sheet and/or one combined multi-page set. Every layout must use a PDF plot device (\"DWG To "
          + "PDF.pc3\" or an \"AutoCAD PDF (...).pc3\"); a combined set needs the same device on every sheet. "
          + "Runs in the foreground (BACKGROUNDPLOT is 0 for the call and then restored), makes each layout "
          + "current while it plots, and switches back to the starting layout at the end. Args: layouts "
          + "(names, in sheet order; default every paper layout in tab order); outputDirectory (existing "
          + "folder, for per-sheet PDFs); fileNamePattern (default \"{layout}\"; tokens {layout}, {index}, "
          + "{tab}, {drawing}; \".pdf\" is added); perSheet (default true); combinedPath (full path of a "
          + "combined PDF, optional); overwrite (default false: an existing file is an error before anything "
          + "plots). Returns each file written with its size. Files cannot be un-written with Ctrl+Z.",
            mutating: true,
            UndoKind.NotUndoable,
            ToolSchema.Object([],
                ("layouts", ToolSchema.Array(ToolSchema.Text(), 1, 500)),
                ("outputDirectory", ToolSchema.Text()),
                ("fileNamePattern", ToolSchema.Text()),
                ("perSheet", ToolSchema.Flag()),
                ("combinedPath", ToolSchema.Text()),
                ("overwrite", ToolSchema.Flag())),
            PlotLayoutsToPdf);
    }

    private sealed record Sheet(string Name, ObjectId Id, int TabOrder, string? Device);

    private static Task<object?> PlotLayoutsToPdf(JsonElement args)
    {
        var requested = Args.StrArrayOpt(args, "layouts");
        var directory = Args.StrOpt(args, "outputDirectory");
        var pattern = Args.StrOpt(args, "fileNamePattern", "{layout}")!;
        var perSheet = Args.BoolOpt(args, "perSheet", true);
        var combined = Args.StrOpt(args, "combinedPath");
        var overwrite = Args.BoolOpt(args, "overwrite", false);

        if (!perSheet && combined is null)
            throw new AcadMcpException("bad_argument", "Nothing to plot: perSheet is false and no combinedPath was given.");
        if (perSheet)
        {
            if (directory is null) throw new AcadMcpException("bad_argument", "Per-sheet PDFs need outputDirectory.");
            directory = ExistingDirectory(directory, "outputDirectory");
        }
        if (combined is not null) combined = OutputFile(combined, ".pdf", "combinedPath", overwrite);

        return Acad.Write<object?>(ctx =>
        {
            var sheets = ResolveSheets(ctx, requested);
            foreach (var s in sheets)
            {
                if (!SheetMath.IsPdfDevice(s.Device))
                    throw new AcadMcpException("not_a_pdf_setup",
                        $"Layout '{s.Name}' plots to '{s.Device ?? "None"}', not a PDF device.",
                        $"Call set_page_setup with layout '{s.Name}' and plotter \"{PdfDevice}\" (plus media) first.");
            }
            if (combined is not null && sheets.Select(s => s.Device).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new AcadMcpException("mixed_devices",
                    "A combined PDF needs every sheet on the same plot device.",
                    string.Join(", ", sheets.Select(s => $"{s.Name}: {s.Device}")));

            var drawing = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(ctx.Db.Filename) ? ctx.Doc.Name : ctx.Db.Filename);
            var targets = new List<(Sheet Sheet, string Path)>();
            if (perSheet)
            {
                for (var i = 0; i < sheets.Count; i++)
                {
                    string file;
                    try { file = SheetMath.ExpandFileName(pattern, sheets[i].Name, i + 1, sheets.Count, sheets[i].TabOrder, drawing); }
                    catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", ex.Message); }
                    var path = Path.Combine(directory!, file);
                    if (!overwrite && File.Exists(path))
                        throw new AcadMcpException("already_exists", $"'{path}' already exists. Pass overwrite:true or another pattern.");
                    targets.Add((sheets[i], path));
                }
                var clash = targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (clash is not null)
                    throw new AcadMcpException("bad_argument", $"Several sheets would be written to '{clash.Key}'; add {{layout}} or {{index}} to fileNamePattern.");
                if (combined is not null && targets.Any(t => string.Equals(t.Path, combined, StringComparison.OrdinalIgnoreCase)))
                    throw new AcadMcpException("bad_argument", "combinedPath is the same file as one of the per-sheet PDFs.");
            }

            var written = new List<object>();
            ForegroundPlot(ctx, () =>
            {
                foreach (var (sheet, path) in targets)
                {
                    PlotDocument(ctx, [sheet], path, null);
                    written.Add(Written(path, sheet.Name, 1));
                }
                if (combined is not null)
                {
                    PlotDocument(ctx, sheets, combined, null);
                    written.Add(Written(combined, string.Join(", ", sheets.Select(s => s.Name)), sheets.Count));
                }
            });

            return new
            {
                files = written,
                sheets = sheets.Select(s => s.Name).ToList(),
                currentLayout = LayoutTools.CurrentLayoutName(),
            };
        });
    }

    private static List<Sheet> ResolveSheets(AcadContext ctx, string[] requested)
    {
        var sheets = new List<Sheet>();
        if (requested.Length == 0)
        {
            foreach (var (_, id) in LayoutTools.LayoutIds(ctx))
            {
                var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);
                if (!layout.ModelType) sheets.Add(SheetOf(layout));
            }
            sheets.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));
            if (sheets.Count == 0) throw new AcadMcpException("not_found", "The drawing has no paper-space layouts.");
            return sheets;
        }

        foreach (var name in requested)
        {
            var layout = (Layout)ctx.Tr.GetObject(LayoutTools.LayoutIdByName(ctx, LayoutTools.ResolveLayoutName(ctx, name)), OpenMode.ForRead);
            if (sheets.Any(s => s.Id == layout.ObjectId))
                throw new AcadMcpException("bad_argument", $"Layout '{layout.LayoutName}' is listed twice.");
            sheets.Add(SheetOf(layout));
        }
        return sheets;
    }

    private static Sheet SheetOf(Layout layout) =>
        new(layout.LayoutName, layout.ObjectId, layout.TabOrder,
            string.IsNullOrWhiteSpace(layout.PlotConfigurationName) ? null : layout.PlotConfigurationName);

    private static object Written(string path, string sheets, int pages)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new AcadMcpException("plot_failed", $"The plot finished but '{path}' was not written.");
        return new { path, sheets, pages, bytes = info.Length };
    }

    // ------------------------------------------------------------ plot_preview

    private static void RegisterPlotPreview()
    {
        Router.Register("plot_preview",
            "Plot a layout, or a window of it, and return a PNG — with plot styles and lineweights, unlike "
          + "export_drawing_preview. The best visual check of a sheet: it shows collisions, runaway leaders "
          + "and wrong rotations exactly as they will print. Without a window it plots the layout through its "
          + "own page setup (switched to a PDF device if it has none), so the image is the sheet as it will "
          + "plot; with a window it plots that area (the layout's own coordinates: model units for Model, "
          + "paper units for a sheet) at the given scale — default 1:1 on a sheet, fit for Model. Args: "
          + "layout (default current); window [minX, minY, maxX, maxY]; scale (window plots only); crop "
          + "[minX, minY, maxX, maxY] as fractions 0..1 of the plotted area, origin bottom-left; width and/or "
          + "height in pixels (the image keeps its aspect and fits inside both; default width 1600, at most "
          + "12000 a side); plotStyleTable (default the layout's own); lineweights (default the layout's own); "
          + "path (a new .png to keep; default a temp file); inline (default true: the PNG comes back as image "
          + "content as well as a path); keepPdf (default false). The drawing is not changed: the layout and "
          + "BACKGROUNDPLOT are restored. Writes files, which Ctrl+Z cannot remove.",
            mutating: true,
            UndoKind.NotUndoable,
            ToolSchema.Object([],
                ("layout", ToolSchema.Text()),
                ("window", ToolSchema.Array(ToolSchema.Number(), 4, 4)),
                ("scale", ToolSchema.Any()),
                ("crop", ToolSchema.Array(ToolSchema.Number(0, 1), 4, 4)),
                ("width", ToolSchema.Integer(1, 12000)),
                ("height", ToolSchema.Integer(1, 12000)),
                ("plotStyleTable", ToolSchema.Text()),
                ("lineweights", ToolSchema.Flag()),
                ("path", ToolSchema.Text()),
                ("inline", ToolSchema.Flag()),
                ("keepPdf", ToolSchema.Flag())),
            PlotPreview);
    }

    /// <summary>What the plot step tells the render step: where the plotted area sits on the page.</summary>
    private sealed record PlottedPage(string Layout, string Mode, string Device, string Media, string? StyleSheet,
        bool Lineweights, string Scale, double PageWidth, double PageHeight, Box Region)
    {
        /// <summary>A whole-layout plot: the image is the PDF page as it came out, whatever its orientation.</summary>
        public bool WholePage => Mode == "layout";
    }

    private static async Task<object?> PlotPreview(JsonElement args)
    {
        var layoutArg = Args.StrOpt(args, "layout");
        var window = SheetTools.Present(args, "window") ? Args.Numbers(args.GetProperty("window"), "window") : null;
        var scale = SheetTools.ScaleOpt(args, "scale");
        var crop = SheetTools.Present(args, "crop") ? Args.Numbers(args.GetProperty("crop"), "crop") : null;
        int? width = SheetTools.Present(args, "width") ? Args.IntOpt(args, "width", 0) : null;
        int? height = SheetTools.Present(args, "height") ? Args.IntOpt(args, "height", 0) : null;
        var style = Args.StrOpt(args, "plotStyleTable");
        var lineweights = SheetTools.BoolOrNull(args, "lineweights");
        var inline = Args.BoolOpt(args, "inline", true);
        var keepPdf = Args.BoolOpt(args, "keepPdf", false);

        if (scale is not null && window is null)
            throw new AcadMcpException("bad_argument", "'scale' applies to window previews; a whole layout plots at its page setup's scale.");
        if (window is not null && (Math.Abs(window[2] - window[0]) < 1e-9 || Math.Abs(window[3] - window[1]) < 1e-9))
            throw new AcadMcpException("bad_argument", "The preview window must have a positive width and height.");
        try
        {
            SheetMath.Crop(new Box(0, 0, 1, 1), 1, 1, crop);
            SheetMath.FitPixels(1, 1, width, height, DefaultPreviewWidth);
        }
        catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", ex.Message); }

        var work = Path.Combine(Path.GetTempPath(), "Nodal.AcadMcp", "previews");
        Directory.CreateDirectory(work);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var png = Args.StrOpt(args, "path") is { } p ? OutputFile(p, ".png", "path", overwrite: false) : Path.Combine(work, $"preview-{stamp}.png");
        var pdf = Path.Combine(work, $"preview-{stamp}.pdf");

        PlottedPage page;
        try { page = await Acad.Write(ctx => PlotPreviewPdf(ctx, layoutArg, window, scale, style, lineweights, pdf)).ConfigureAwait(false); }
        catch (Exception)
        {
            try { File.Delete(pdf); } catch (Exception) { }
            throw;
        }
        PdfRenderResult rendered;
        try
        {
            rendered = await Task.Run(() => PdfRaster.Render(pdf, png, 0, (dipW, dipH) =>
            {
                double pageW = page.PageWidth, pageH = page.PageHeight;
                var region = page.Region;
                if (page.WholePage) { pageW = dipW; pageH = dipH; region = new Box(0, 0, dipW, dipH); }
                // A window plot's region was computed on the media the plot step chose; a page that came
                // out a different shape would put the crop in the wrong place, so refuse rather than guess.
                else if (Math.Abs(dipW / dipH - pageW / pageH) > 0.01 * (pageW / pageH))
                    throw new InvalidOperationException(
                        $"The plotted page is {dipW / 96:0.##} x {dipH / 96:0.##} in, not the expected {pageW:0.##} x {pageH:0.##} in.");
                var area = SheetMath.Crop(region, pageW, pageH, crop);
                var (pxW, pxH) = SheetMath.FitPixels(area.Width, area.Height, width, height, DefaultPreviewWidth);
                var (x, y, w, h) = SheetMath.ToTopLeft(area, pageW, pageH, dipW, dipH);
                return new PdfRenderPlan(x, y, w, h, pxW, pxH);
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AcadMcpException)
        {
            try { File.Delete(png); } catch (Exception) { }
            throw new AcadMcpException("render_failed", $"The plot worked but the PDF could not be rendered: {ex.Message}",
                keepPdf ? $"PDF kept at {pdf}" : null);
        }
        finally
        {
            if (!keepPdf) { try { File.Delete(pdf); } catch (Exception) { } }
        }

        var bytes = await File.ReadAllBytesAsync(png).ConfigureAwait(false);
        var attach = inline && bytes.Length <= MaxInlineBytes;
        return new
        {
            path = png,
            width = rendered.PixelWidth,
            height = rendered.PixelHeight,
            bytes = bytes.Length,
            layout = page.Layout,
            mode = page.Mode,
            device = page.Device,
            media = page.Media,
            plotStyleTable = page.StyleSheet,
            lineweights = page.Lineweights,
            scale = page.Scale,
            window,
            crop,
            pdf = keepPdf ? pdf : null,
            note = inline && !attach ? $"The PNG is {bytes.Length / 1048576.0:0.0} MB, too big to attach; read it from 'path'." : null,
            image = attach ? ToolResultImage.Describe("image/png", bytes) : null,
        };
    }

    /// <summary>Plots the preview PDF on the main thread and says where the plotted area is on the page (inches).</summary>
    private static PlottedPage PlotPreviewPdf(AcadContext ctx, string? layoutArg, double[]? window, double? scale,
        string? style, bool? lineweights, string pdf)
    {
        var name = layoutArg is null ? LayoutTools.CurrentLayoutName() : LayoutTools.ResolveLayoutName(ctx, layoutArg);
        var layout = (Layout)ctx.Tr.GetObject(LayoutTools.LayoutIdByName(ctx, name), OpenMode.ForRead);
        var sheet = SheetOf(layout);
        var psv = PlotSettingsValidator.Current;
        PlottedPage? result = null;

        ForegroundPlot(ctx, () =>
        {
            PlotDocument(ctx, [sheet], pdf, ps =>
            {
                var styleSheet = style is null ? null : ResolveStyle(psv, style);
                var prepared = window is null
                    ? PrepareLayoutPreview(psv, ps, sheet)
                    : PrepareWindowPreview(psv, ps, sheet, window, scale);

                if (styleSheet is not null) { psv.SetCurrentStyleSheet(ps, styleSheet); ps.PlotPlotStyles = true; }
                if (lineweights is { } lw) ps.PrintLineweights = lw;
                result = prepared with { StyleSheet = NullIfEmpty(ps.CurrentStyleSheet), Lineweights = ps.PrintLineweights };
            });
        });
        return result!;
    }

    /// <summary>The whole layout through its own page setup, moved onto a PDF device when it has none.</summary>
    private static PlottedPage PrepareLayoutPreview(PlotSettingsValidator psv, PlotSettings ps, Sheet sheet)
    {
        if (!SheetMath.IsPdfDevice(ps.PlotConfigurationName))
        {
            var paper = ps.PlotPaperSize; // millimetres
            psv.SetPlotConfigurationName(ps, PdfDevice, null);
            psv.RefreshLists(ps);
            var match = SheetTools.Strings(psv.GetCanonicalMediaNameList(ps))
                .Select(SheetMath.ParseMediaSize).Where(m => m is not null).Select(m => m!.Value)
                .OrderBy(m => m.Name.Contains("full_bleed", StringComparison.OrdinalIgnoreCase) ? 0 : 1) // no margins to clip
                .FirstOrDefault(m =>
                    (Math.Abs(m.WidthInches * 25.4 - paper.X) < 0.5 && Math.Abs(m.HeightInches * 25.4 - paper.Y) < 0.5) ||
                    (Math.Abs(m.WidthInches * 25.4 - paper.Y) < 0.5 && Math.Abs(m.HeightInches * 25.4 - paper.X) < 0.5));
            if (match.Name is null)
                throw new AcadMcpException("no_pdf_media",
                    $"Layout '{sheet.Name}' plots to '{sheet.Device ?? "None"}' and {PdfDevice} has no {paper.X / 25.4:0.##} x {paper.Y / 25.4:0.##} in media.",
                    $"Give it a PDF page setup with set_page_setup (plotter \"{PdfDevice}\" plus media), or preview a window.");
            psv.SetCanonicalMediaName(ps, match.Name);
        }

        // The render step uses the PDF page as it comes out, so the page size here is only informative.
        var size = ps.PlotPaperSize;
        return new PlottedPage(sheet.Name, "layout", ps.PlotConfigurationName, ps.CanonicalMediaName, null, ps.PrintLineweights,
            ps.UseStandardScale ? ps.StdScaleType.ToString() : $"{ps.CustomPrintScale.Numerator:0.####}:{ps.CustomPrintScale.Denominator:0.####}",
            size.X / 25.4, size.Y / 25.4, new Box(0, 0, size.X / 25.4, size.Y / 25.4));
    }

    /// <summary>A window, centred on the smallest full-bleed PDF media that holds it at the requested scale.</summary>
    private static PlottedPage PrepareWindowPreview(PlotSettingsValidator psv, PlotSettings ps, Sheet sheet, double[] w, double? scale)
    {
        var win = new Box(Math.Min(w[0], w[2]), Math.Min(w[1], w[3]), Math.Max(w[0], w[2]), Math.Max(w[1], w[3]));
        var s = scale ?? (ps.ModelType ? (double?)null : 1.0);
        double targetW, targetH;
        if (s is { } given) { targetW = win.Width * given; targetH = win.Height * given; }
        else if (win.Width >= win.Height) { targetW = ModelPreviewInches; targetH = ModelPreviewInches * win.Height / win.Width; }
        else { targetH = ModelPreviewInches; targetW = ModelPreviewInches * win.Width / win.Height; }

        psv.SetPlotConfigurationName(ps, PdfDevice, null);
        psv.RefreshLists(ps);
        var media = SheetMath.ChoosePreviewMedia(SheetTools.Strings(psv.GetCanonicalMediaNameList(ps)), targetW, targetH)
            ?? throw new AcadMcpException("no_pdf_media", $"{PdfDevice} offers no full-bleed media to plot the preview on.");
        if (media.WidthInches + 1e-6 < targetW || media.HeightInches + 1e-6 < targetH) s = null; // too big for any media: fit it

        psv.SetCanonicalMediaName(ps, media.Name);
        psv.SetPlotPaperUnits(ps, PlotPaperUnit.Inches);
        psv.SetPlotRotation(ps, PlotRotation.Degrees000);
        psv.SetPlotWindowArea(ps, new Extents2d(win.MinX, win.MinY, win.MaxX, win.MaxY));
        psv.SetPlotType(ps, DbPlotType.Window);
        if (s is { } fixedScale)
        {
            psv.SetUseStandardScale(ps, false);
            psv.SetCustomPrintScale(ps, new CustomScale(fixedScale, 1.0));
        }
        else
        {
            psv.SetUseStandardScale(ps, true);
            psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
        }
        psv.SetPlotCentered(ps, true);

        // Page and printable area in inches (AutoCAD reports both in millimetres).
        var size = ps.PlotPaperSize; var margins = ps.PlotPaperMargins;
        var pw = size.X / 25.4; var ph = size.Y / 25.4;
        var printable = new Box(margins.MinPoint.X / 25.4, margins.MinPoint.Y / 25.4, pw - margins.MaxPoint.X / 25.4, ph - margins.MaxPoint.Y / 25.4);
        var (region, used) = SheetMath.CenteredPlotRegion(printable, win.Width, win.Height, s);
        return new PlottedPage(sheet.Name, "window", PdfDevice, media.Name, null, ps.PrintLineweights,
            s is null ? $"fit (1:{1 / used:0.###})" : $"{used:0.######} paper inch per unit", pw, ph, region);
    }

    private static string ResolveStyle(PlotSettingsValidator psv, string requested)
    {
        var sheets = SheetTools.Strings(psv.GetPlotStyleSheetList());
        foreach (var candidate in new[] { requested, requested + ".ctb", requested + ".stb" })
        {
            var hit = sheets.FirstOrDefault(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        throw new AcadMcpException("not_found", $"No plot style table named '{requested}'.", "Tables: " + SheetTools.Listing(sheets));
    }

    // ------------------------------------------------------------ plot engine

    /// <summary>
    /// Runs plots with BACKGROUNDPLOT at 0 and puts it, and the current layout, back afterwards — even
    /// when a plot throws.
    /// </summary>
    private static void ForegroundPlot(AcadContext ctx, Action plot)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new AcadMcpException("plotter_busy", "AutoCAD is already plotting; try again when it has finished.");

        var startLayout = LayoutTools.CurrentLayoutName();
        var background = AcApp.GetSystemVariable("BACKGROUNDPLOT");
        AcApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
        try { plot(); }
        finally
        {
            try { MakeCurrent(startLayout); } catch (System.Exception) { /* reported via currentLayout */ }
            try { AcApp.SetSystemVariable("BACKGROUNDPLOT", background); } catch (System.Exception) { }
        }
    }

    /// <summary>
    /// One PDF from one or more layouts: one PublishEngine, BeginDocument once, BeginPage(isLast) per
    /// sheet. Each layout is made current before its PlotInfo is built and validated.
    /// </summary>
    private static void PlotDocument(AcadContext ctx, IReadOnlyList<Sheet> sheets, string path, Action<PlotSettings>? customize)
    {
        var docName = ctx.Doc.Name;
        var disposables = new List<IDisposable>();
        PlotEngine? engine = null;
        try
        {
            for (var i = 0; i < sheets.Count; i++)
            {
                MakeCurrent(sheets[i].Name);
                var layout = (Layout)ctx.Tr.GetObject(sheets[i].Id, OpenMode.ForRead);
                var settings = new PlotSettings(layout.ModelType);
                disposables.Add(settings);
                settings.CopyFrom(layout);
                customize?.Invoke(settings);

                var info = new PlotInfo { Layout = sheets[i].Id, OverrideSettings = settings };
                disposables.Add(info);
                new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);

                if (i == 0)
                {
                    // Only once the first sheet's settings have validated, so a bad setup never
                    // leaves a half-started plot behind.
                    engine = PlotFactory.CreatePublishEngine();
                    engine.BeginPlot(null, null);
                    engine.BeginDocument(info, docName, null, 1, true, path);
                }
                using var pageInfo = new PlotPageInfo();
                engine!.BeginPage(pageInfo, info, i == sheets.Count - 1, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
            }
            engine!.EndDocument(null);
            engine.EndPlot(null);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new AcadMcpException("plot_failed", $"AutoCAD could not plot '{path}': {ex.ErrorStatus}.", ex.Message);
        }
        finally
        {
            try { engine?.Dispose(); } catch (System.Exception) { }
            foreach (var d in disposables) { try { d.Dispose(); } catch (System.Exception) { } }
        }
    }

    private static void MakeCurrent(string layout)
    {
        if (!string.Equals(LayoutTools.CurrentLayoutName(), layout, StringComparison.OrdinalIgnoreCase))
            LayoutManager.Current.CurrentLayout = layout;
    }

    // ------------------------------------------------------------ files

    private static string ExistingDirectory(string path, string arg)
    {
        if (!Path.IsPathFullyQualified(path)) throw new AcadMcpException("bad_argument", $"{arg} must be a full path.");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new AcadMcpException("not_found", $"{arg} '{full}' does not exist.");
        return full;
    }

    private static string OutputFile(string path, string extension, string arg, bool overwrite)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("bad_argument", $"{arg} must be a full {extension} file path.");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new AcadMcpException("not_found", $"The folder for {arg} does not exist.");
        if (!overwrite && File.Exists(full)) throw new AcadMcpException("already_exists", $"'{full}' already exists.");
        return full;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
