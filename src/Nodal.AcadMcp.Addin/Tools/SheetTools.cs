using System.Collections;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Sheet production: floating viewports, page setups, drawing properties and title-block fields —
/// the paper-space work a drawing set needs, which until now took run_csharp.
///
/// create_viewport exists mostly to enforce a call pattern. AutoCAD 2027 has two paper-space
/// failures that lose all unsaved work (both reproduced through run_csharp, see its description):
/// making a layout current in the same commit that erased viewports already on it hangs AutoCAD,
/// and setting Viewport.On on a viewport created in the same commit crashes it intermittently —
/// at the commit, or at a later layout switch in the same commit. Changing how the bridge commits
/// was tested and does not help, so the only guard is the pattern: switch layouts in a commit of
/// their own, create viewports without switching, and turn them on in a later commit with their
/// layout current. The tool runs those as separate main-thread steps and never erases anything.
/// </summary>
internal static class SheetTools
{
    /// <summary>
    /// Pause between create_viewport's commits. The tested pattern had a client round trip between
    /// each call, so AutoCAD ran its own idle and regen work in between; this keeps that gap.
    /// </summary>
    private const int SettleMs = 150;

    private const int MaxViewports = 50;
    private const int MaxListed = 40;

    private static readonly string[] SummaryFields =
        ["title", "subject", "author", "keywords", "comments", "lastSavedBy", "revisionNumber", "hyperlinkBase"];

    internal static void Register()
    {
        RegisterCreateViewport();
        RegisterSetPageSetup();
        RegisterGetDrawingProperties();
        RegisterSetDrawingProperties();
        RegisterSetFieldText();
    }

    // ------------------------------------------------------------- create_viewport

    private sealed record ViewportRequest(double[] Center, string? View, double[]? ViewCenter, double? Scale,
        double? ViewHeight, double? Width, double? Height, bool Locked, string? Layer, string? AnnotationScale);

    private sealed record ViewportPlan(Point3d Center, double Width, double Height, double Scale, Vector3d Direction,
        Point3d Target, Point2d ViewCenter, double Twist, string? Layer, bool Locked, string? AnnotationScale, string? View);

    private sealed record LayoutPlan(string Layout, string PreviousLayout, bool WasCurrent, List<ViewportPlan> Viewports);

    private static void RegisterCreateViewport()
    {
        var spec = ToolSchema.Object(["center"],
            ("center", ToolSchema.Describe(ToolSchema.Point(2), "[x, y] centre of the viewport on the sheet, in paper units.")),
            ("view", ToolSchema.Describe(ToolSchema.Text(), "A named model-space view to show; the viewport is sized from it.")),
            ("viewCenter", ToolSchema.Describe(ToolSchema.Point(2), "[x, y] model-space point at the viewport's centre, instead of a named view.")),
            ("scale", ToolSchema.Describe(ToolSchema.Any(), "\"1/4\\\" = 1'-0\\\"\", \"1:48\", or a factor in paper units per model unit (1/48 = 0.0208333).")),
            ("viewHeight", ToolSchema.Describe(ToolSchema.Number(1e-9), "Model units shown top to bottom, instead of a scale.")),
            ("width", ToolSchema.Describe(ToolSchema.Number(1e-9), "Paper width. Default: the view's width x scale.")),
            ("height", ToolSchema.Describe(ToolSchema.Number(1e-9), "Paper height. Default: the view's height x scale.")),
            ("locked", ToolSchema.Describe(ToolSchema.Flag(), "Lock the display (default true).")),
            ("layer", ToolSchema.Describe(ToolSchema.Text(), "Existing layer for the viewport frame, e.g. a no-plot layer.")),
            ("annotationScale", ToolSchema.Describe(ToolSchema.Text(), "A scale name from the drawing's list. Default: the entry whose ratio equals the scale, if any.")));

        Router.Register("create_viewport",
            "Create floating viewports on a paper-space layout using the only call pattern that is safe in "
          + "AutoCAD 2027. Erasing viewports and switching layouts in one commit hangs AutoCAD, and turning on "
          + "a viewport created in the same commit crashes it intermittently, so this runs separate "
          + "main-thread steps, each its own commit: (1) make the layout current, unless it already is; "
          + "(2) create the viewports switched off; (3) turn them on and lock them; (4) optionally switch "
          + "back. It never erases anything: to replace viewports, erase the old ones with delete_entities in "
          + "an earlier call (that tool does not switch layouts). Args: layout (paper-space layout name); "
          + "viewports: array of {center [x,y] on the sheet; view (named model view) or viewCenter [x,y] in "
          + "model space; scale (\"1/4\\\" = 1'-0\\\"\", \"1:48\", or a factor such as 0.0208333) or viewHeight; "
          + "width/height on the sheet (default: the view's size x scale; with a view, both sizes and no scale, "
          + "the view is fitted); locked (default true); layer; annotationScale}; restoreLayout (default false). "
          + "Returns the handles and each viewport's state read back from the drawing. Undo: NOT one step — "
          + "one Ctrl+Z per commit the reply lists under 'commits' (up to four); to take viewports back, erase "
          + "the returned handles with delete_entities.",
            mutating: true,
            UndoKind.ToolDefined,
            ToolSchema.Object(["layout", "viewports"],
                ("layout", ToolSchema.Text()),
                ("viewports", ToolSchema.Array(spec, 1, MaxViewports)),
                ("restoreLayout", ToolSchema.Flag())),
            CreateViewport);
    }

    private static async Task<object?> CreateViewport(JsonElement args)
    {
        var layoutArg = Args.Str(args, "layout");
        var restore = Args.BoolOpt(args, "restoreLayout", false);
        var requests = args.GetProperty("viewports").EnumerateArray().Select(ReadViewportRequest).ToList();

        // Resolve every name and size before anything changes, so a typo costs nothing.
        var plan = await Acad.Read(ctx => PlanViewports(ctx, layoutArg, requests)).ConfigureAwait(false);
        var commits = new List<string>();

        if (!plan.WasCurrent)
        {
            await Acad.Write<object?>(_ => { LayoutManager.Current.CurrentLayout = plan.Layout; return null; }).ConfigureAwait(false);
            commits.Add($"made layout '{plan.Layout}' current");
            await Task.Delay(SettleMs).ConfigureAwait(false);
        }

        var handles = await Acad.Write(ctx => CreateOff(ctx, plan)).ConfigureAwait(false);
        commits.Add($"created {handles.Length} viewport(s), switched off");
        await Task.Delay(SettleMs).ConfigureAwait(false);

        try
        {
            await Acad.Write(ctx => TurnOn(ctx, plan, handles)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new AcadMcpException("viewport_on_failed",
                $"The viewports were created but could not be turned on: {ex.Message}",
                $"Created and still off: {string.Join(", ", handles)}. Commits so far: {string.Join("; ", commits)}.");
        }
        commits.Add($"turned on {handles.Length} viewport(s)" +
                    (plan.Viewports.Any(v => v.Locked) ? " and locked " + plan.Viewports.Count(v => v.Locked) : ""));

        if (restore && !plan.WasCurrent)
        {
            await Task.Delay(SettleMs).ConfigureAwait(false);
            await Acad.Write<object?>(_ => { LayoutManager.Current.CurrentLayout = plan.PreviousLayout; return null; }).ConfigureAwait(false);
            commits.Add($"switched back to layout '{plan.PreviousLayout}'");
        }

        return await Acad.Read<object?>(ctx => new
        {
            layout = plan.Layout,
            previousLayout = plan.PreviousLayout,
            currentLayout = LayoutTools.CurrentLayoutName(),
            handles,
            viewports = handles.Select(h =>
                LayoutTools.ViewportInfo(ctx, (Viewport)ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, h), OpenMode.ForRead), plan.Layout, false)).ToList(),
            commits,
            undoSteps = commits.Count,
        }).ConfigureAwait(false);
    }

    private static ViewportRequest ReadViewportRequest(JsonElement e, int i)
    {
        var center = Args.Coordinates(e.GetProperty("center"), $"viewports[{i}].center");
        var view = Args.StrOpt(e, "view");
        var viewCenter = Present(e, "viewCenter") ? Args.Coordinates(e.GetProperty("viewCenter"), $"viewports[{i}].viewCenter") : null;
        if ((view is null) == (viewCenter is null))
            throw new AcadMcpException("bad_argument", $"viewports[{i}] needs exactly one of 'view' or 'viewCenter'.");

        return new ViewportRequest(center, view, viewCenter, ScaleOpt(e, "scale"), DblOrNull(e, "viewHeight"),
            DblOrNull(e, "width"), DblOrNull(e, "height"), Args.BoolOpt(e, "locked", true),
            Args.StrOpt(e, "layer"), Args.StrOpt(e, "annotationScale"));
    }

    private static LayoutPlan PlanViewports(AcadContext ctx, string layoutArg, List<ViewportRequest> requests)
    {
        var name = LayoutTools.ResolveLayoutName(ctx, layoutArg);
        var layout = (Layout)ctx.Tr.GetObject(LayoutTools.LayoutIdByName(ctx, name), OpenMode.ForRead);
        if (layout.ModelType)
            throw new AcadMcpException("model_space", "Floating viewports go on a paper-space layout, not Model.");

        var current = LayoutTools.CurrentLayoutName();
        var views = (ViewTable)ctx.Tr.GetObject(ctx.Db.ViewTableId, OpenMode.ForRead);
        var layers = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        var scales = AnnotationScales(ctx.Db);
        var plans = new List<ViewportPlan>();

        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            ViewTableRecord? view = null;
            if (r.View is { } viewName)
            {
                if (!views.Has(viewName))
                    throw new AcadMcpException("not_found", $"viewports[{i}]: no named view '{viewName}' in this drawing.",
                        "Views: " + Listing(NamedViews(ctx, views)));
                view = (ViewTableRecord)ctx.Tr.GetObject(views[viewName], OpenMode.ForRead);
                if (view.IsPaperspaceView)
                    throw new AcadMcpException("wrong_view", $"viewports[{i}]: '{viewName}' is a paper-space view; name a model-space view.");
            }

            ViewportSize size;
            try { size = SheetMath.ResolveViewport(view?.Width, view?.Height, r.Width, r.Height, r.Scale, r.ViewHeight); }
            catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", $"viewports[{i}]: {ex.Message}"); }

            if (r.Layer is { } layer && !layers.Has(layer))
                throw new AcadMcpException("not_found", $"viewports[{i}]: layer '{layer}' does not exist. Create it first.");

            string? anno;
            if (r.AnnotationScale is { } wanted)
            {
                anno = scales.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)).Name
                    ?? throw new AcadMcpException("not_found", $"viewports[{i}]: no annotation scale named '{wanted}'.",
                        "Scales: " + Listing(scales.Select(s => s.Name)));
            }
            else
            {
                anno = scales.FirstOrDefault(s => SheetMath.SameScale(s.Ratio, size.Scale)).Name;
            }

            plans.Add(new ViewportPlan(
                new Point3d(r.Center[0], r.Center[1], 0), size.Width, size.Height, size.Scale,
                view?.ViewDirection ?? Vector3d.ZAxis,
                view?.Target ?? Point3d.Origin,
                view?.CenterPoint ?? new Point2d(r.ViewCenter![0], r.ViewCenter[1]),
                view?.ViewTwist ?? 0.0,
                r.Layer, r.Locked, anno, view?.Name));
        }

        return new LayoutPlan(name, current, string.Equals(current, name, StringComparison.OrdinalIgnoreCase), plans);
    }

    /// <summary>Step 2: append the viewports with their view set up, and leave them off.</summary>
    private static string[] CreateOff(AcadContext ctx, LayoutPlan plan)
    {
        RequireCurrent(plan.Layout, "create the viewports");
        var layout = (Layout)ctx.Tr.GetObject(LayoutTools.LayoutIdByName(ctx, plan.Layout), OpenMode.ForRead);
        var space = (BlockTableRecord)ctx.Tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        var handles = new List<string>();

        foreach (var p in plan.Viewports)
        {
            var vp = new Viewport();
            vp.SetDatabaseDefaults(ctx.Db);
            vp.CenterPoint = p.Center;
            vp.Width = p.Width;
            vp.Height = p.Height;
            if (p.Layer is not null) vp.Layer = p.Layer;

            space.AppendEntity(vp);
            ctx.Tr.AddNewlyCreatedDBObject(vp, true);

            vp.ViewDirection = p.Direction;
            vp.ViewTarget = p.Target;
            vp.ViewCenter = p.ViewCenter;
            vp.TwistAngle = p.Twist;
            vp.CustomScale = p.Scale;
            if (p.AnnotationScale is not null && FindScale(ctx.Db, p.AnnotationScale) is { } scale)
                vp.AnnotationScale = scale;

            // Deliberately not vp.On: that waits for its own commit (see the class remarks).
            handles.Add(vp.Handle.ToString());
        }
        return handles.ToArray();
    }

    /// <summary>Step 3: with the layout current and the viewports already committed, turn them on.</summary>
    private static object? TurnOn(AcadContext ctx, LayoutPlan plan, string[] handles)
    {
        RequireCurrent(plan.Layout, "turn the viewports on");
        for (var i = 0; i < handles.Length; i++)
        {
            // A no-plot frame layer is often locked; that must not stop the viewport being switched on.
            var vp = (Viewport)ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handles[i]), OpenMode.ForWrite, false, true);
            vp.On = true;
            vp.Locked = plan.Viewports[i].Locked;
        }
        return null;
    }

    private static void RequireCurrent(string layout, string action)
    {
        var current = LayoutTools.CurrentLayoutName();
        if (!string.Equals(current, layout, StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("layout_changed",
                $"Layout '{current}' became current before the tool could {action} on '{layout}'; nothing more was changed.");
    }

    private static List<(string Name, double Ratio)> AnnotationScales(Database db)
    {
        var list = new List<(string Name, double Ratio)>();
        try
        {
            var scales = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (scales is null) return list;
            foreach (ObjectContext context in scales)
            {
                if (context is AnnotationScale a && a.DrawingUnits > 0)
                    list.Add((a.Name, a.PaperUnits / a.DrawingUnits));
            }
        }
        catch (System.Exception) { /* no scale list: leave AutoCAD's default */ }
        return list;
    }

    private static AnnotationScale? FindScale(Database db, string name)
    {
        var scales = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
        if (scales is null) return null;
        foreach (ObjectContext context in scales)
        {
            if (context is AnnotationScale a && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        }
        return null;
    }

    private static IEnumerable<string> NamedViews(AcadContext ctx, ViewTable views)
    {
        foreach (ObjectId id in views)
        {
            if (id.IsErased) continue;
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is ViewTableRecord v && !v.IsPaperspaceView) yield return v.Name;
        }
    }

    // ------------------------------------------------------------- set_page_setup

    private static void RegisterSetPageSetup()
    {
        Router.Register("set_page_setup",
            "Set the plot configuration (page setup) of one or more layouts — what plot_layouts_to_pdf uses. "
          + "Only the arguments you pass change; the reply shows each layout's settings before and after. "
          + "Templates often carry surprises (an Arch D setup with a 90 degree rotation and an offset origin), "
          + "so read 'before' and pass rotation and centered/origin explicitly. Args: layout or layouts; "
          + "plotter (e.g. \"DWG To PDF.pc3\"; the reply to a bad name lists the devices); media (canonical "
          + "name such as \"ARCH_full_bleed_D_(36.00_x_24.00_Inches)\" or its display name; a bad name lists "
          + "close matches); plotArea (layout for a sheet; limits for Model; extents | display | window | view); window "
          + "[minX, minY, maxX, maxY] in the layout's own coordinates; viewName; scale (\"fit\", \"1:1\", "
          + "\"1:48\", \"1/4\\\" = 1'-0\\\"\" or a factor); paperUnits (inches | millimeters | pixels); "
          + "rotation (0, 90, 180, 270); centered, or origin [x, y]; plotStyleTable (\"monochrome.ctb\", or "
          + "\"none\"); plotWithPlotStyles; printLineweights; scaleLineweights. All layouts change in one "
          + "commit, or none do.",
            mutating: true,
            UndoKind.SingleStep,
            ToolSchema.Object([],
                ("layout", ToolSchema.Text()),
                ("layouts", ToolSchema.Array(ToolSchema.Text(), 1, 500)),
                ("plotter", ToolSchema.Text()),
                ("media", ToolSchema.Text()),
                ("plotArea", ToolSchema.Choice("layout", "limits", "extents", "display", "window", "view")),
                ("window", ToolSchema.Array(ToolSchema.Number(), 4, 4)),
                ("viewName", ToolSchema.Text()),
                ("scale", ToolSchema.Any()),
                ("paperUnits", ToolSchema.Choice("inches", "millimeters", "pixels")),
                ("rotation", new Dictionary<string, object> { ["type"] = "integer", ["enum"] = new[] { 0, 90, 180, 270 } }),
                ("centered", ToolSchema.Flag()),
                ("origin", ToolSchema.Point(2)),
                ("plotStyleTable", ToolSchema.Text()),
                ("plotWithPlotStyles", ToolSchema.Flag()),
                ("printLineweights", ToolSchema.Flag()),
                ("scaleLineweights", ToolSchema.Flag())),
            SetPageSetup);
    }

    private sealed record PageSetupRequest(string? Plotter, string? Media, string? PlotArea, double[]? Window, string? ViewName,
        string? Scale, double? ScaleFactor, string? PaperUnits, int? Rotation, bool? Centered, double[]? Origin,
        string? StyleTable, bool? PlotStyles, bool? Lineweights, bool? ScaleLineweights);

    private static Task<object?> SetPageSetup(JsonElement args)
    {
        var names = LayoutNamesArg(args);
        var req = ReadPageSetup(args);

        return Acad.Write<object?>(ctx =>
        {
            var psv = PlotSettingsValidator.Current;
            var results = new List<object>();
            foreach (var requested in names)
            {
                var name = LayoutTools.ResolveLayoutName(ctx, requested);
                var layout = (Layout)ctx.Tr.GetObject(LayoutTools.LayoutIdByName(ctx, name), OpenMode.ForWrite);
                var before = LayoutTools.PlotInfo(layout);
                ApplyPageSetup(psv, layout, req);
                results.Add(new { name, before, after = LayoutTools.PlotInfo(layout) });
            }
            return new { layouts = results };
        });
    }

    private static PageSetupRequest ReadPageSetup(JsonElement args)
    {
        var area = Args.StrOpt(args, "plotArea");
        var window = Present(args, "window") ? Args.Numbers(args.GetProperty("window"), "window") : null;
        var viewName = Args.StrOpt(args, "viewName");
        if (window is not null && area is not null && area != "window")
            throw new AcadMcpException("bad_argument", "A 'window' only applies with plotArea 'window'.");
        if (window is not null) area = "window";
        if (area == "window" && window is null)
            throw new AcadMcpException("bad_argument", "plotArea 'window' needs window: [minX, minY, maxX, maxY].");
        if (window is not null && (Math.Abs(window[2] - window[0]) < 1e-9 || Math.Abs(window[3] - window[1]) < 1e-9))
            throw new AcadMcpException("bad_argument", "The plot window must have a positive width and height.");
        if (area == "view" && viewName is null)
            throw new AcadMcpException("bad_argument", "plotArea 'view' needs viewName.");
        if (viewName is not null && area != "view")
            throw new AcadMcpException("bad_argument", "'viewName' only applies with plotArea 'view'.");

        string? scaleText = null; double? factor = null;
        if (Present(args, "scale"))
        {
            var v = args.GetProperty("scale");
            if (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Equals("fit", StringComparison.OrdinalIgnoreCase))
                scaleText = "fit";
            else
            {
                factor = ScaleOpt(args, "scale");
                scaleText = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
        }

        var centered = BoolOrNull(args, "centered");
        var origin = Present(args, "origin") ? Args.Coordinates(args.GetProperty("origin"), "origin") : null;
        if (origin is not null && centered == true)
            throw new AcadMcpException("bad_argument", "Give centered:true or an origin, not both.");

        return new PageSetupRequest(Args.StrOpt(args, "plotter"), Args.StrOpt(args, "media"), area, window, viewName,
            scaleText, factor, Args.StrOpt(args, "paperUnits"),
            Present(args, "rotation") ? Args.IntOpt(args, "rotation", 0) : null, centered, origin,
            Args.StrOpt(args, "plotStyleTable"), BoolOrNull(args, "plotWithPlotStyles"),
            BoolOrNull(args, "printLineweights"), BoolOrNull(args, "scaleLineweights"));
    }

    private static void ApplyPageSetup(PlotSettingsValidator psv, Layout layout, PageSetupRequest req)
    {
        // AutoCAD answers both of these with a bare eInvalidInput.
        if (req.PlotArea == "limits" && !layout.ModelType)
            throw new AcadMcpException("bad_argument", $"plotArea 'limits' is for Model; '{layout.LayoutName}' is a paper layout, so use 'layout'.");
        if (req.PlotArea == "layout" && layout.ModelType)
            throw new AcadMcpException("bad_argument", "plotArea 'layout' is for paper layouts; for Model use 'limits', 'extents', 'display', 'window' or 'view'.");

        var step = "plot device";
        try { ApplyPageSetupSteps(psv, layout, req, s => step = s); }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new AcadMcpException("page_setup_rejected",
                $"AutoCAD rejected the {step} for '{layout.LayoutName}': {ex.ErrorStatus}.", ex.Message);
        }
    }

    private static void ApplyPageSetupSteps(PlotSettingsValidator psv, Layout layout, PageSetupRequest req, Action<string> step)
    {
        if (req.Plotter is not null)
        {
            var device = ResolveDevice(psv, req.Plotter);
            var oldMedia = layout.CanonicalMediaName;
            psv.SetPlotConfigurationName(layout, device, null);
            psv.RefreshLists(layout);
            if (req.Media is null && !string.IsNullOrEmpty(oldMedia) && Strings(psv.GetCanonicalMediaNameList(layout)).Contains(oldMedia))
                psv.SetCanonicalMediaName(layout, oldMedia);
        }
        else
        {
            psv.RefreshLists(layout);
        }

        step("media");
        if (req.Media is not null)
            psv.SetCanonicalMediaName(layout, ResolveMedia(psv, layout, req.Media));

        step("paper units");
        if (req.PaperUnits is { } units)
            psv.SetPlotPaperUnits(layout, units switch
            {
                "inches" => PlotPaperUnit.Inches,
                "millimeters" => PlotPaperUnit.Millimeters,
                _ => PlotPaperUnit.Pixels,
            });

        step("rotation");
        if (req.Rotation is { } degrees)
            psv.SetPlotRotation(layout, degrees switch
            {
                0 => PlotRotation.Degrees000,
                90 => PlotRotation.Degrees090,
                180 => PlotRotation.Degrees180,
                _ => PlotRotation.Degrees270,
            });

        step($"plot area '{req.PlotArea}'");
        switch (req.PlotArea)
        {
            case "window":
                var w = req.Window!;
                psv.SetPlotWindowArea(layout, new Extents2d(Math.Min(w[0], w[2]), Math.Min(w[1], w[3]), Math.Max(w[0], w[2]), Math.Max(w[1], w[3])));
                psv.SetPlotType(layout, PlotType.Window);
                break;
            case "view":
                psv.SetPlotViewName(layout, req.ViewName);
                psv.SetPlotType(layout, PlotType.View);
                break;
            case "layout": psv.SetPlotType(layout, PlotType.Layout); break;
            case "limits": psv.SetPlotType(layout, PlotType.Limits); break;
            case "extents": psv.SetPlotType(layout, PlotType.Extents); break;
            case "display": psv.SetPlotType(layout, PlotType.Display); break;
        }

        step("plot scale");
        if (req.Scale == "fit")
        {
            psv.SetUseStandardScale(layout, true);
            psv.SetStdScaleType(layout, StdScaleType.ScaleToFit);
        }
        else if (req.ScaleFactor is { } factor)
        {
            if (SheetMath.SameScale(factor, 1.0))
            {
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
            }
            else
            {
                var ratio = req.Scale is { } text ? SheetMath.ParseScale(text) : new ScaleRatio(factor, 1.0);
                psv.SetUseStandardScale(layout, false);
                psv.SetCustomPrintScale(layout, new CustomScale(ratio.Paper, ratio.Drawing));
            }
        }

        step("plot offset");
        if (req.Centered == true) psv.SetPlotCentered(layout, true);
        else if (req.Origin is { } o)
        {
            psv.SetPlotCentered(layout, false);
            psv.SetPlotOrigin(layout, new Point2d(o[0], o[1]));
        }
        else if (req.Centered == false) psv.SetPlotCentered(layout, false);

        step("plot style table");
        if (req.StyleTable is { } style)
            psv.SetCurrentStyleSheet(layout, style.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : ResolveStyleSheet(psv, style));

        step("plot style and lineweight options");
        if (req.PlotStyles is { } plotStyles) layout.PlotPlotStyles = plotStyles;
        if (req.Lineweights is { } lineweights) layout.PrintLineweights = lineweights;
        if (req.ScaleLineweights is { } scaleLineweights) layout.ScaleLineweights = scaleLineweights;
    }

    internal static string ResolveDevice(PlotSettingsValidator psv, string requested)
    {
        var devices = Strings(psv.GetPlotDeviceList());
        foreach (var candidate in new[] { requested, requested + ".pc3" })
        {
            var hit = devices.FirstOrDefault(d => string.Equals(d, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        throw new AcadMcpException("not_found", $"No plot device named '{requested}'.", "Devices: " + Listing(devices));
    }

    internal static string ResolveMedia(PlotSettingsValidator psv, PlotSettings ps, string requested)
    {
        var canonical = Strings(psv.GetCanonicalMediaNameList(ps));
        var key = SheetMath.MediaKey(requested);
        var hit = canonical.FirstOrDefault(c => SheetMath.MediaKey(c) == key);
        if (hit is not null) return hit;

        foreach (var c in canonical)
        {
            string? locale = null;
            try { locale = psv.GetLocaleMediaName(ps, c); } catch (System.Exception) { }
            if (locale is not null && SheetMath.MediaKey(locale) == key) return c;
        }

        var words = key.Split([' ', '(', ')', 'x'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
        var close = canonical.Where(c => words.Any(w => SheetMath.MediaKey(c).Contains(w, StringComparison.Ordinal))).ToList();
        throw new AcadMcpException("not_found", $"Device '{ps.PlotConfigurationName}' has no media named '{requested}'.",
            "Media: " + Listing(close.Count > 0 ? close : canonical));
    }

    private static string ResolveStyleSheet(PlotSettingsValidator psv, string requested)
    {
        var sheets = Strings(psv.GetPlotStyleSheetList());
        foreach (var candidate in new[] { requested, requested + ".ctb", requested + ".stb" })
        {
            var hit = sheets.FirstOrDefault(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        throw new AcadMcpException("not_found", $"No plot style table named '{requested}'.", "Tables: " + Listing(sheets));
    }

    // ------------------------------------------------------ drawing properties

    private static void RegisterGetDrawingProperties()
    {
        Router.Register("get_drawing_properties",
            "Read the drawing's properties (DWGPROPS): title, subject, author, keywords, comments, last saved by, "
          + "revision number, hyperlink base, and the custom properties that %<\\AcVar CustomDP.Name>% fields show.",
            mutating: false,
            _ => Acad.Read<object?>(ctx => DescribeSummary(ctx.Db.SummaryInfo)));
    }

    private static void RegisterSetDrawingProperties()
    {
        Router.Register("set_drawing_properties",
            "Set the drawing's properties (DWGPROPS). Title-block text like %<\\AcSm Sheet.Number>% or "
          + "%<\\AecPr \"ProjectName\">% shows #### without a sheet set or ACA project; custom properties set "
          + "here plus set_field_text with %<\\AcVar CustomDP.ProjectName>% fields work in any drawing. Args: "
          + "title, subject, author, keywords, comments, lastSavedBy, revisionNumber, hyperlinkBase (strings); "
          + "clear (names of those to empty); custom ({name: value} to add or overwrite); removeCustom (names); "
          + "updateFields (default true: re-evaluate the drawing's fields so title blocks show the new values). "
          + "Returns the properties before and after.",
            mutating: true,
            UndoKind.SingleStep,
            ToolSchema.Object([],
                ("title", ToolSchema.Text()), ("subject", ToolSchema.Text()), ("author", ToolSchema.Text()),
                ("keywords", ToolSchema.Text()), ("comments", ToolSchema.Text()), ("lastSavedBy", ToolSchema.Text()),
                ("revisionNumber", ToolSchema.Text()), ("hyperlinkBase", ToolSchema.Text()),
                ("clear", ToolSchema.Array(ToolSchema.Choice(SummaryFields), 1, SummaryFields.Length)),
                ("custom", ToolSchema.Map()),
                ("removeCustom", ToolSchema.Array(ToolSchema.Text(), 1, 1000)),
                ("updateFields", ToolSchema.Flag())),
            SetDrawingProperties);
    }

    private static Task<object?> SetDrawingProperties(JsonElement args)
    {
        var values = SummaryFields.ToDictionary(f => f, f => Args.StrOpt(args, f));
        foreach (var name in Args.StrArrayOpt(args, "clear"))
        {
            if (values[name] is not null)
                throw new AcadMcpException("bad_argument", $"'{name}' is both set and cleared.");
            values[name] = string.Empty;
        }

        var custom = new List<(string Key, string Value)>();
        if (Present(args, "custom"))
        {
            foreach (var p in args.GetProperty("custom").EnumerateObject())
            {
                if (p.Name.Trim().Length == 0) throw new AcadMcpException("bad_argument", "Custom property names cannot be blank.");
                if (p.Value.ValueKind != JsonValueKind.String) throw Args.WrongType($"custom.{p.Name}", "a string", p.Value);
                custom.Add((p.Name, p.Value.GetString()!));
            }
        }
        var remove = Args.StrArrayOpt(args, "removeCustom");
        if (remove.Intersect(custom.Select(c => c.Key), StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } both)
            throw new AcadMcpException("bad_argument", $"Custom property '{both}' is both set and removed.");
        var updateFields = Args.BoolOpt(args, "updateFields", true);

        return Acad.Write<object?>(ctx =>
        {
            var before = DescribeSummary(ctx.Db.SummaryInfo);
            var b = new DatabaseSummaryInfoBuilder(ctx.Db.SummaryInfo);
            if (values["title"] is { } title) b.Title = title;
            if (values["subject"] is { } subject) b.Subject = subject;
            if (values["author"] is { } author) b.Author = author;
            if (values["keywords"] is { } keywords) b.Keywords = keywords;
            if (values["comments"] is { } comments) b.Comments = comments;
            if (values["lastSavedBy"] is { } lastSavedBy) b.LastSavedBy = lastSavedBy;
            if (values["revisionNumber"] is { } revision) b.RevisionNumber = revision;
            if (values["hyperlinkBase"] is { } hyperlink) b.HyperlinkBase = hyperlink;

            var table = b.CustomPropertyTable;
            var missing = new List<string>();
            foreach (var key in remove)
            {
                var existing = Key(table, key);
                if (existing is null) missing.Add(key); else table.Remove(existing);
            }
            foreach (var (key, value) in custom)
            {
                var existing = Key(table, key);
                if (existing is null) table.Add(key, value); else table[existing] = value;
            }

            ctx.Db.SummaryInfo = b.ToDatabaseSummaryInfo();
            if (updateFields) ctx.Db.EvaluateFields();

            return new { before, after = DescribeSummary(ctx.Db.SummaryInfo), notFound = missing, fieldsEvaluated = updateFields };
        });
    }

    /// <summary>The table's own spelling of a custom property name, matched case-insensitively.</summary>
    private static object? Key(IDictionary table, string name)
    {
        foreach (var k in table.Keys)
        {
            if (k is string s && string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return k;
        }
        return null;
    }

    private static object DescribeSummary(DatabaseSummaryInfo si)
    {
        var custom = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var e = si.CustomProperties;
        while (e.MoveNext())
        {
            if (e.Key is string k) custom[k] = e.Value?.ToString();
        }
        return new
        {
            title = si.Title,
            subject = si.Subject,
            author = si.Author,
            keywords = si.Keywords,
            comments = si.Comments,
            lastSavedBy = si.LastSavedBy,
            revisionNumber = si.RevisionNumber,
            hyperlinkBase = si.HyperlinkBase,
            custom,
        };
    }

    // ------------------------------------------------------------ set_field_text

    private static void RegisterSetFieldText()
    {
        var item = ToolSchema.Object(["handle", "field"],
            ("handle", ToolSchema.Text()),
            ("field", new { type = "string", minLength = 1, maxLength = 16384 }));

        Router.Register("set_field_text",
            "Replace the contents of MText, single-line text or attributes with a field expression and evaluate "
          + "it — how title blocks get live values. The expression is ordinary text with field codes in it, "
          + "e.g. \"%<\\AcVar CustomDP.ProjectName>%\" (a custom drawing property, see set_drawing_properties), "
          + "\"SHEET %<\\AcVar CustomDP.SheetNo>%\", or MText formatting around several fields. Any field already "
          + "on the object is removed first. Args: handle and field, or items: [{handle, field}] to set many in "
          + "one commit. Returns each object's evaluated text and evaluation status; #### in the text or a "
          + "status other than Success means the field could not be resolved.",
            mutating: true,
            UndoKind.SingleStep,
            ToolSchema.Object([],
                ("handle", ToolSchema.Text()),
                ("field", new { type = "string", minLength = 1, maxLength = 16384 }),
                ("items", ToolSchema.Array(item, 1, 1000))),
            SetFieldText);
    }

    private static Task<object?> SetFieldText(JsonElement args)
    {
        var items = new List<(string Handle, string Code)>();
        var single = Args.StrOpt(args, "handle");
        if (Present(args, "items"))
        {
            if (single is not null || Present(args, "field"))
                throw new AcadMcpException("bad_argument", "Give handle + field, or items, not both.");
            foreach (var i in args.GetProperty("items").EnumerateArray()) items.Add((Args.Str(i, "handle"), Args.Str(i, "field")));
        }
        else
        {
            if (single is null) throw new AcadMcpException("bad_argument", "Give handle + field, or items: [{handle, field}].");
            items.Add((single, Args.Str(args, "field")));
        }

        return Acad.Write<object?>(ctx =>
        {
            var results = new List<object>();
            foreach (var (handle, code) in items)
            {
                var obj = ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handle), OpenMode.ForWrite);
                if (obj.HasFields) obj.RemoveField();
                switch (obj)
                {
                    case MText m: m.Contents = string.Empty; break;
                    case DBText t: t.TextString = string.Empty; break; // also AttributeReference/AttributeDefinition
                    default:
                        throw new AcadMcpException("wrong_type", $"{handle} is a {obj.GetType().Name}; fields go on MText, text or attributes.");
                }

                var field = new Field(code) { EvaluationOption = FieldEvaluationOptions.Automatic };
                obj.SetField(field);
                ctx.Tr.AddNewlyCreatedDBObject(field, true);
                field.Evaluate();

                var status = field.EvaluationStatus;
                var text = obj switch { MText m => m.Text, DBText t => t.TextString, _ => null };
                results.Add(new
                {
                    handle,
                    type = obj.GetType().Name,
                    text,
                    fieldCode = field.GetFieldCode(),
                    status = status.Status.ToString(),
                    error = string.IsNullOrEmpty(status.ErrorMessage) ? null : status.ErrorMessage,
                    unresolved = text?.Contains("####", StringComparison.Ordinal) == true,
                });
            }
            return new { items = results };
        });
    }

    // ------------------------------------------------------------------ arguments

    internal static bool Present(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    internal static double? DblOrNull(JsonElement e, string name) => Present(e, name) ? Args.Dbl(e, name) : null;

    internal static bool? BoolOrNull(JsonElement e, string name) => Present(e, name) ? Args.BoolOpt(e, name, false) : null;

    /// <summary>A scale given as a string ("1/4\" = 1'-0\"", "1:48", "0.25") or a number; the factor, or null when absent.</summary>
    internal static double? ScaleOpt(JsonElement e, string name)
    {
        if (!Present(e, name)) return null;
        var v = e.GetProperty(name);
        if (v.ValueKind == JsonValueKind.Number)
        {
            var d = v.GetDouble();
            return d > 0 && double.IsFinite(d) ? d : throw new AcadMcpException("bad_argument", $"Argument '{name}' must be positive.");
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            try { return SheetMath.ParseScale(v.GetString()!).Factor; }
            catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", $"Argument '{name}': {ex.Message}"); }
        }
        throw Args.WrongType(name, "a scale such as \"1/4\\\" = 1'-0\\\"\", \"1:48\" or a number", v);
    }

    /// <summary>'layout' (one name) or 'layouts' (several), exactly one of them.</summary>
    private static string[] LayoutNamesArg(JsonElement args)
    {
        var one = Args.StrOpt(args, "layout");
        var many = Args.StrArrayOpt(args, "layouts");
        if ((one is null) == (many.Length == 0))
            throw new AcadMcpException("bad_argument", "Give 'layout' (one name) or 'layouts' (several), exactly one of them.");
        return one is not null ? [one] : many.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static List<string> Strings(System.Collections.Specialized.StringCollection? items)
    {
        var list = new List<string>();
        if (items is null) return list;
        foreach (var s in items) if (!string.IsNullOrEmpty(s)) list.Add(s);
        return list;
    }

    internal static string Listing(IEnumerable<string> names)
    {
        var all = names.ToList();
        var shown = string.Join(", ", all.Take(MaxListed));
        return all.Count > MaxListed ? $"{shown}, ... ({all.Count - MaxListed} more)" : shown;
    }
}
