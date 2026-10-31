using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Drafting conventions and checks: symbols whose geometry is easy to draw backwards (projection,
/// roof pitch), AutoCAD's associative centre marks and centrelines, a check for colliding
/// annotation and broken linework, and reading the vectors on a PDF page. The geometry of the
/// symbols and the checks lives in Contracts (DraftingSymbols, DraftingChecks), where it is
/// unit-tested; this module only turns it into entities and reads entities into it.
/// </summary>
internal static class DraftingTools
{
    private const int LispTimeoutMs = 15000;

    internal static void Register()
    {
        Router.Register("create_projection_symbol",
            "Draw the ISO 128-3 projection symbol: a truncated cone's side view beside its end view (two circles), with centre lines. "
          + "In first angle the cone's large end faces the circles; in third angle its small end does, which is the difference "
          + "people draw backwards. Args: angle ('first' | 'third'), position [x, y(, z)] (the symbol's centre), size (the large "
          + "diameter in drawing units; the whole symbol is 2.5 x size wide); optional layer, centerLayer (default layer), "
          + "centerLinetype (a loaded linetype for the centre lines), space (model | paper | current, default current).",
            mutating: true, UndoKind.SingleStep,
            ToolSchema.Object(["angle", "position", "size"], ("angle", ToolSchema.Choice("first", "third")), ("position", ToolSchema.Any()),
                ("size", ToolSchema.Number(1e-9)), ("layer", ToolSchema.Text()), ("centerLayer", ToolSchema.Text()),
                ("centerLinetype", ToolSchema.Text()), ("space", ToolSchema.Choice("model", "paper", "current"))),
            ProjectionSymbol);

        Router.Register("create_slope_symbol",
            "Draw a roof pitch symbol above a roof line: a right triangle with its hypotenuse parallel to the roof, the horizontal leg "
          + "on top labelled with the run (12) and the vertical leg dropping to the roof labelled with the rise, which is read off the "
          + "line's own slope. Args: line (handle of a LINE on the roof edge) or points [[x1, y1], [x2, y2]] (two points on it); leg "
          + "(length of the horizontal leg in drawing units); optional at [x, y] (where along the roof, default the middle), offset "
          + "(gap above the roof line, default leg / 4), run (default 12), rise (label text to use instead of the computed one), "
          + "textHeight (default leg / 3), layer, space.",
            mutating: true, UndoKind.SingleStep,
            ToolSchema.Object(["leg"], ("line", ToolSchema.Text()), ("points", ToolSchema.Array(ToolSchema.Any(), 2, 2)), ("leg", ToolSchema.Number(1e-9)),
                ("at", ToolSchema.Any()), ("offset", ToolSchema.Number(0)), ("run", ToolSchema.Number(1e-9)), ("rise", ToolSchema.Text()),
                ("textHeight", ToolSchema.Number(1e-9)), ("layer", ToolSchema.Text()), ("space", ToolSchema.Choice("model", "paper", "current"))),
            SlopeSymbol);

        Router.Register("create_center_mark",
            "Add AutoCAD's associative centre mark (the CENTERMARK command) to a circle or arc: it follows the circle and extends "
          + "past its edge, unlike centre lines drawn as plain lines. The mark takes the CENTERLAYER, CENTERLTYPE and CENTEREXE "
          + "settings. Args: handle (a circle or arc in the current space); optional layer. Runs the command at the command line, so "
          + "the mark and a layer change are separate undo steps.",
            mutating: true, UndoKind.ToolDefined,
            ToolSchema.Object(["handle"], ("handle", ToolSchema.Text()), ("layer", ToolSchema.Text())),
            CenterMark);

        Router.Register("create_centerline",
            "Add AutoCAD's associative centreline (the CENTERLINE command) between two lines, e.g. the sides of a slot or a hole in "
          + "section. Args: first, second (handles of two lines in the current space); optional layer. Runs the command at the "
          + "command line, so the centreline and a layer change are separate undo steps.",
            mutating: true, UndoKind.ToolDefined,
            ToolSchema.Object(["first", "second"], ("first", ToolSchema.Text()), ("second", ToolSchema.Text()), ("layer", ToolSchema.Text())),
            Centerline);

        Router.Register("check_drafting",
            "Check model-space drafting before calling it done. Reports annotation that overlaps other annotation (text, mtext, "
          + "attributes, dimension and leader text), lines drawn twice, parallel lines a sliver apart, and line ends that touch "
          + "nothing but stop within 'gap' of another line (a roof sitting just above a wall, a jamb short of its sill). Lines, "
          + "polylines, arcs and circles count as linework, including inside block references; hidden layers are skipped. Read "
          + "only. Args, all optional: window [minX, minY, maxX, maxY] (WCS), layers [names], checks (any of 'annotation', "
          + "'duplicates', 'gaps'; default all), gap (default 1 in, 1/12 ft, 25 mm, 2.5 cm or 0.025 m by INSUNITS), sliver "
          + "(largest separation reported as a sliver, default gap / 4), maxResults (per check, default 200).",
            mutating: false, UndoKind.None,
            ToolSchema.Object([], ("window", ToolSchema.Array(ToolSchema.Number(), 4, 4)), ("layers", ToolSchema.Array(ToolSchema.Text(), 1)),
                ("checks", ToolSchema.Array(ToolSchema.Choice("annotation", "duplicates", "gaps"), 1, 3)), ("gap", ToolSchema.Number(1e-9)),
                ("sliver", ToolSchema.Number(0)), ("maxResults", ToolSchema.Integer(1, 5000))),
            CheckDrafting);

        Router.Register("read_pdf_page",
            "Read the vector content of one PDF page, so lengths and angles come from the drawing's geometry instead of a measured "
          + "picture. Imports the page with -PDFIMPORT at the origin of the active model space, reads what arrived, and undoes the "
          + "import back to its own UNDO mark, so the drawing is left as it was (undo must be on). Text comes back only where the "
          + "PDF used real fonts; lettering drawn with SHX fonts arrives as linework, which 'lettering' then says. Coordinates are "
          + "in the imported units, measured from the page's lower-left corner; 'page' gives its size in inches. Args: path (a PDF), "
          + "page (default 1); optional window [minX, minY, maxX, maxY] (only return linework inside it), minLength (skip shorter "
          + "segments, e.g. glyph strokes), maxSegments (default 2000), maxTexts (default 2000), includeImages (default false; "
          + "raster images are written to a temp folder that is deleted).",
            mutating: true, UndoKind.ToolDefined,
            ToolSchema.Object(["path"], ("path", ToolSchema.Text()), ("page", ToolSchema.Integer(1, 100000)),
                ("window", ToolSchema.Array(ToolSchema.Number(), 4, 4)), ("minLength", ToolSchema.Number(0)),
                ("maxSegments", ToolSchema.Integer(0, 100000)), ("maxTexts", ToolSchema.Integer(0, 100000)), ("includeImages", ToolSchema.Flag())),
            ReadPdfPage);
    }

    // ---------------------------------------------------------------------------------- symbols

    private static Task<object?> ProjectionSymbol(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var first = Args.Str(a, "angle") == "first";
        var at = Args.Coordinates(a.GetProperty("position"), "position");
        var z = at.Length > 2 ? at[2] : 0;
        var g = Geometry(() => DraftingSymbols.ProjectionSymbol(first, at[0], at[1], Args.Dbl(a, "size")));
        var space = (BlockTableRecord)ctx.Tr.GetObject(EntityTools.TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!), OpenMode.ForWrite);
        var layer = Args.StrOpt(a, "layer") is { } l ? EntityTools.RequireLayer(ctx, l) : null;
        var centerLayer = Args.StrOpt(a, "centerLayer") is { } cl ? EntityTools.RequireLayer(ctx, cl) : layer;
        var centerLinetype = Args.StrOpt(a, "centerLinetype") is { } lt ? EntityTools.RequireLinetype(ctx, lt) : null;

        string Add(Entity e, string? onLayer, string? linetype = null)
        {
            e.SetDatabaseDefaults(ctx.Db);
            if (onLayer is not null) e.Layer = onLayer;
            if (linetype is not null) e.Linetype = linetype;
            space.AppendEntity(e);
            ctx.Tr.AddNewlyCreatedDBObject(e, true);
            return e.Handle.ToString();
        }
        Line ToLine(Line2 s) => new(new Point3d(s.X1, s.Y1, z), new Point3d(s.X2, s.Y2, z));

        return new
        {
            angle = first ? "first" : "third",
            outline = g.Outline.Select(s => Add(ToLine(s), layer)).ToArray(),
            circles = g.Circles.Select(c => Add(new Circle(new Point3d(c.X, c.Y, z), Vector3d.ZAxis, c.Radius), layer)).ToArray(),
            centerLines = g.CenterLines.Select(s => Add(ToLine(s), centerLayer, centerLinetype)).ToArray(),
            note = first ? "First angle: the cone's large end faces the circles." : "Third angle: the cone's small end faces the circles.",
        };
    });

    private static Task<object?> SlopeSymbol(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        Point3d p1, p2;
        if (Args.StrOpt(a, "line") is { } handle)
        {
            if (ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handle), OpenMode.ForRead) is not Line roof)
                throw new AcadMcpException("wrong_type", $"'{handle}' is not a LINE; pass 'points' for any other roof edge.");
            (p1, p2) = (roof.StartPoint, roof.EndPoint);
        }
        else if (a.TryGetProperty("points", out var pts))
        {
            var c1 = Args.Coordinates(pts[0], "points[0]"); var c2 = Args.Coordinates(pts[1], "points[1]");
            (p1, p2) = (new Point3d(c1[0], c1[1], c1.Length > 2 ? c1[2] : 0), new Point3d(c2[0], c2[1], c2.Length > 2 ? c2[2] : 0));
        }
        else throw new AcadMcpException("bad_argument", "Give the roof edge as 'line' (a LINE handle) or 'points' (two points on it).");

        var leg = Args.Dbl(a, "leg");
        double[] at = a.TryGetProperty("at", out var atArg) ? Args.Coordinates(atArg, "at") : [(p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2];
        var g = Geometry(() => DraftingSymbols.SlopeSymbol(p1.X, p1.Y, p2.X, p2.Y, at[0], at[1], leg,
            Args.DblOpt(a, "offset", leg / 4), Args.DblOpt(a, "run", 12)));
        var height = Args.DblOpt(a, "textHeight", leg / 3);
        var z = (p1.Z + p2.Z) / 2;
        var space = (BlockTableRecord)ctx.Tr.GetObject(EntityTools.TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!), OpenMode.ForWrite);
        var layer = Args.StrOpt(a, "layer") is { } l ? EntityTools.RequireLayer(ctx, l) : null;

        T Add<T>(T e) where T : Entity
        {
            e.SetDatabaseDefaults(ctx.Db);
            if (layer is not null) e.Layer = layer;
            space.AppendEntity(e);
            ctx.Tr.AddNewlyCreatedDBObject(e, true);
            return e;
        }
        Line ToLine(Line2 s) => new(new Point3d(s.X1, s.Y1, z), new Point3d(s.X2, s.Y2, z));
        DBText Label(string text, double x, double y, TextHorizontalMode horizontal, TextVerticalMode vertical)
        {
            var t = new DBText { TextString = text };
            t.SetDatabaseDefaults(ctx.Db);
            t.Height = height;
            t.HorizontalMode = horizontal; t.VerticalMode = vertical;
            t.Position = t.AlignmentPoint = new Point3d(x, y, z);
            Add(t);
            t.AdjustAlignment(ctx.Db);
            return t;
        }

        var run = Args.DblOpt(a, "run", 12);
        var riseText = Args.StrOpt(a, "rise") ?? g.RiseText;
        var lines = new[] { Add(ToLine(g.Run)), Add(ToLine(g.Rise)), Add(ToLine(g.Hypotenuse)) };
        var runLabel = Label(DraftingSymbols.Number(run), g.RunLabelX, g.RunLabelY, TextHorizontalMode.TextCenter, TextVerticalMode.TextBottom);
        var riseLabel = Label(riseText, g.RiseLabelX, g.RiseLabelY,
            g.RiseLabelOnLeft ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft, TextVerticalMode.TextVerticalMid);
        return new
        {
            lines = lines.Select(e => e.Handle.ToString()).ToArray(),
            labels = new[] { runLabel.Handle.ToString(), riseLabel.Handle.ToString() },
            run = DraftingSymbols.Number(run),
            rise = riseText,
            computedRise = g.RiseText,
            slopeDegrees = Math.Round(Math.Atan(g.RiseValue / run) * 180 / Math.PI, 3),
        };
    });

    private static T Geometry<T>(Func<T> build)
    {
        try { return build(); }
        catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", ex.Message); }
    }

    // ---------------------------------------------------------------------------------- centre marks

    private static async Task<object?> CenterMark(JsonElement a)
    {
        var handle = Args.Str(a, "handle");
        var pick = await Acad.Read(ctx =>
        {
            var e = ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handle), OpenMode.ForRead) as Curve;
            if (e is not (Circle or Arc)) throw new AcadMcpException("wrong_type", $"'{handle}' is not a circle or arc.");
            RequireCurrentSpace(ctx, e);
            return e.GetPointAtParameter((e.StartParam + e.EndParam) / 2);
        }).ConfigureAwait(false);
        var created = await RunCommand("CENTERMARK", $"(list (handent \"{handle}\") (trans {Lisp(pick)} 0 1)) \"\"").ConfigureAwait(false);
        return await Finish(created, a).ConfigureAwait(false);
    }

    private static async Task<object?> Centerline(JsonElement a)
    {
        var first = Args.Str(a, "first"); var second = Args.Str(a, "second");
        var picks = await Acad.Read(ctx => new[] { first, second }.Select(h =>
        {
            if (ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, h), OpenMode.ForRead) is not Line line)
                throw new AcadMcpException("wrong_type", $"'{h}' is not a LINE.");
            RequireCurrentSpace(ctx, line);
            return line.StartPoint + (line.EndPoint - line.StartPoint) / 2;
        }).ToArray()).ConfigureAwait(false);
        var created = await RunCommand("CENTERLINE",
            $"(list (handent \"{first}\") (trans {Lisp(picks[0])} 0 1)) (list (handent \"{second}\") (trans {Lisp(picks[1])} 0 1))").ConfigureAwait(false);
        return await Finish(created, a).ConfigureAwait(false);
    }

    private static void RequireCurrentSpace(AcadContext ctx, Entity e)
    {
        if (e.OwnerId != ctx.Db.CurrentSpaceId)
            throw new AcadMcpException("wrong_space", "The entity must be in the current space, where the command can pick it.");
    }

    private static string Lisp(Point3d p) => string.Create(CultureInfo.InvariantCulture, $"(list {p.X:R} {p.Y:R} {p.Z:R})");

    /// <summary>Runs the command at the command line and returns the handle of what it created.</summary>
    private static async Task<string> RunCommand(string command, string inputs)
    {
        var source = $"(progn (setq nmcp-before (entlast)) (command \"_.{command}\" {inputs}) "
                   + "(if (equal (entlast) nmcp-before) \"\" (cdr (assoc 5 (entget (entlast))))))";
        var (value, _, commandLine) = await LispBridge.EvalAsync(source, LispTimeoutMs).ConfigureAwait(false);
        if (commandLine is not null)
            throw new AcadMcpException("command_pending", $"{command} was left waiting for input.", commandLine.Hint);
        var handle = value as string;
        if (string.IsNullOrEmpty(handle))
            throw new AcadMcpException("command_failed", $"{command} did not create anything.",
                "Check that the geometry is visible in the current view and on an unlocked layer.");
        return handle;
    }

    private static Task<object?> Finish(string handle, JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var e = (Entity)ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handle), OpenMode.ForWrite);
        if (Args.StrOpt(a, "layer") is { } layer) e.Layer = EntityTools.RequireLayer(ctx, layer);
        return new { handle, type = e.GetType().Name, layer = e.Layer };
    });

    // ---------------------------------------------------------------------------------- check_drafting

    private sealed record Source(string Handle, string Type, string? Text, bool InBlock);

    private static Task<object?> CheckDrafting(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var checks = a.TryGetProperty("checks", out var c) ? Args.StrArray(a, "checks").ToHashSet() : ["annotation", "duplicates", "gaps"];
        var gap = Args.DblOpt(a, "gap", DefaultGap(ctx.Db.Insunits));
        var sliver = Args.DblOpt(a, "sliver", gap / 4);
        var touch = gap / 1000;
        var max = Args.IntOpt(a, "maxResults", 200);
        var window = a.TryGetProperty("window", out var w) ? Args.Numbers(w, "window") : null;
        var layers = a.TryGetProperty("layers", out _) ? Args.StrArray(a, "layers").ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var sources = new List<Source>();
        var segments = new List<Segment2>();
        var boxes = new List<Rect2>();
        var hidden = new Dictionary<ObjectId, bool>();
        bool Shown(Entity e)
        {
            if (!e.Visible) return false;
            if (layers is not null && !layers.Contains(e.Layer)) return false;
            if (!hidden.TryGetValue(e.LayerId, out var off))
            {
                var record = (LayerTableRecord)ctx.Tr.GetObject(e.LayerId, OpenMode.ForRead);
                hidden[e.LayerId] = off = record.IsOff || record.IsFrozen;
            }
            return !off;
        }
        bool Inside(double minX, double minY, double maxX, double maxY) =>
            window is null || (maxX >= window[0] && minX <= window[2] && maxY >= window[1] && minY <= window[3]);

        void AddSegment(int owner, Point3d p, Point3d q)
        {
            if (Inside(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Max(p.X, q.X), Math.Max(p.Y, q.Y)) && p.DistanceTo(q) > touch)
                segments.Add(new Segment2(owner, p.X, p.Y, q.X, q.Y));
        }
        void AddBox(int owner, Extents3d box)
        {
            if (Inside(box.MinPoint.X, box.MinPoint.Y, box.MaxPoint.X, box.MaxPoint.Y))
                boxes.Add(new Rect2(owner, box.MinPoint.X, box.MinPoint.Y, box.MaxPoint.X, box.MaxPoint.Y));
        }
        void AddCurve(int owner, Curve curve, int arcPieces)
        {
            switch (curve)
            {
                case Line line: AddSegment(owner, line.StartPoint, line.EndPoint); break;
                case Polyline pl:
                    var count = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (var i = 0; i < count; i++)
                    {
                        if (pl.GetSegmentType(i) == SegmentType.Arc) Sampled(owner, pl, i, i + 1, arcPieces);
                        else AddSegment(owner, pl.GetPoint3dAt(i), pl.GetPoint3dAt((i + 1) % pl.NumberOfVertices));
                    }
                    break;
                case Arc or Circle: Sampled(owner, curve, curve.StartParam, curve.EndParam, arcPieces * 2); break;
            }
        }
        void Sampled(int owner, Curve curve, double from, double to, int pieces)
        {
            var previous = curve.GetPointAtParameter(from);
            for (var k = 1; k <= pieces; k++)
            {
                var next = curve.GetPointAtParameter(from + (to - from) * k / pieces);
                AddSegment(owner, previous, next);
                previous = next;
            }
        }
        void AddText(int owner, Entity e)
        {
            try { AddBox(owner, e.GeometricExtents); } catch (Exception) { }
        }
        void Explode(int owner, Entity e, int depth)
        {
            using var parts = new DBObjectCollection();
            try { e.Explode(parts); } catch (Exception) { return; }
            foreach (DBObject part in parts)
            {
                using (part)
                {
                    switch (part)
                    {
                        case DBText or MText: if (e is Dimension or MLeader) AddText(owner, (Entity)part); break;
                        case BlockReference nested when depth < 3 && e is BlockReference: Explode(owner, nested, depth + 1); break;
                        case Curve curve when e is BlockReference && part is not AttributeDefinition && ((Entity)part).Visible: AddCurve(owner, curve, 8); break;
                    }
                }
            }
        }

        var space = (BlockTableRecord)ctx.Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db), OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not Entity e || !Shown(e)) continue;
            var owner = sources.Count;
            string? text = e switch { DBText t => t.TextString, MText m => m.Text, _ => null };
            sources.Add(new Source(e.Handle.ToString(), e.GetType().Name, text, e is BlockReference));
            switch (e)
            {
                case DBText or MText: AddText(owner, e); break;
                case Dimension or MLeader: Explode(owner, e, 0); break;
                case BlockReference br:
                    foreach (ObjectId attId in br.AttributeCollection)
                        if (ctx.Tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att && !att.Invisible && att.TextString.Length > 0) AddText(owner, att);
                    Explode(owner, br, 0);
                    break;
                case Curve curve: AddCurve(owner, curve, 8); break;
            }
        }

        object Ref(int owner) => new { handle = sources[owner].Handle, type = sources[owner].Type, text = sources[owner].Text };
        object? annotation = null, duplicates = null, slivers = null, gaps = null;
        if (checks.Contains("annotation"))
        {
            var hits = DraftingChecks.OverlappingBoxes(boxes, touch);
            annotation = new
            {
                count = hits.Count,
                items = hits.Take(max).Select(h => new
                {
                    a = Ref(boxes[h.A].Owner), b = Ref(boxes[h.B].Owner),
                    overlap = new[] { Math.Round(h.Width, 4), Math.Round(h.Height, 4) },
                    at = new[] { Math.Max(boxes[h.A].MinX, boxes[h.B].MinX), Math.Max(boxes[h.A].MinY, boxes[h.B].MinY) },
                }),
            };
        }
        if (checks.Contains("duplicates"))
        {
            // A block's own doubled lines are a problem in its definition, reported once there, not at every insert.
            var hits = DraftingChecks.ParallelOverlaps(segments, sliver, gap)
                .Where(h => segments[h.A].Owner != segments[h.B].Owner || !sources[segments[h.A].Owner].InBlock).ToList();
            object Item(ParallelOverlap h) => new
            {
                a = Ref(segments[h.A].Owner), b = Ref(segments[h.B].Owner),
                overlap = Math.Round(h.Overlap, 4), separation = Math.Round(h.Separation, 6),
                at = new[] { segments[h.A].X1, segments[h.A].Y1 },
            };
            var doubled = hits.Where(h => h.Separation <= touch).ToList();
            var thin = hits.Where(h => h.Separation > touch).ToList();
            duplicates = new { count = doubled.Count, items = doubled.Take(max).Select(Item) };
            slivers = new { count = thin.Count, items = thin.Take(max).Select(Item) };
        }
        if (checks.Contains("gaps"))
        {
            var hits = DraftingChecks.NearMisses(segments, gap, touch);
            gaps = new
            {
                count = hits.Count,
                items = hits.Take(max).Select(h => new
                {
                    end = Ref(segments[h.Segment].Owner), near = Ref(segments[h.Other].Owner),
                    at = new[] { h.X, h.Y }, distance = Math.Round(h.Distance, 6),
                }),
            };
        }
        return new
        {
            units = ctx.Db.Insunits.ToString(),
            gap, sliver,
            scanned = new { entities = sources.Count, segments = segments.Count, annotationBoxes = boxes.Count },
            annotationOverlaps = annotation,
            doubledLines = duplicates,
            slivers,
            nearMisses = gaps,
            note = "Overlaps are between axis-aligned boxes, so rotated text can be flagged when its box overlaps without its "
                 + "letters touching. Slivers can be deliberate (a reveal, a finish line); judge them against the design.",
        };
    });

    private static double DefaultGap(UnitsValue units) => units switch
    {
        UnitsValue.Feet => 1.0 / 12,
        UnitsValue.Millimeters => 25,
        UnitsValue.Centimeters => 2.5,
        UnitsValue.Meters => 0.025,
        _ => 1,
    };

    // ---------------------------------------------------------------------------------- read_pdf_page

    private static async Task<object?> ReadPdfPage(JsonElement a)
    {
        var path = Args.Str(a, "path");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new AcadMcpException("not_found", "path must be the full path of an existing PDF file.");
        var page = Args.IntOpt(a, "page", 1);

        // The import command re-prompts for a page that does not exist, so check it first, and take
        // the page size while at it (Windows' PDF renderer reports it in 1/96 inch).
        var probe = Path.Combine(Path.GetTempPath(), "Nodal.AcadMcp", "pdfimport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        PdfRenderResult info;
        try
        {
            info = await Task.Run(() => PdfRaster.Render(path, Path.Combine(probe, "page.png"), page - 1,
                (w, h) => new PdfRenderPlan(0, 0, w, h, 16, 16))).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex) { Directory.Delete(probe, true); throw new AcadMcpException("bad_argument", ex.Message); }
        catch (Exception ex) when (ex is not AcadMcpException) { Directory.Delete(probe, true); throw new AcadMcpException("bad_pdf", $"The PDF could not be opened: {ex.Message}"); }

        try { return await MainThread.InCommandContextAsync<object?>(() => ImportAndRead(a, path, page, probe, info)).ConfigureAwait(false); }
        finally { try { Directory.Delete(probe, true); } catch (Exception) { } }
    }

    private static object ImportAndRead(JsonElement a, string path, int page, string imageFolder, PdfRenderResult info)
    {
        var doc = Acad.ActiveDocument;
        var db = doc.Database;
        var ed = doc.Editor;
        if (!db.TileMode) throw new AcadMcpException("model_space_required", "Switch to the Model tab first: the page is imported into model space and then undone.");
        var undo = Convert.ToInt32(AcApp.GetSystemVariable("UNDOCTL"));
        if ((undo & 1) == 0 || (undo & 2) != 0)
            throw new AcadMcpException("undo_off", "UNDO is off or limited to one step, so the import could not be taken back. Turn undo on (UNDO, All) first.");

        var window = a.TryGetProperty("window", out var w) ? Args.Numbers(w, "window") : null;
        var minLength = Args.DblOpt(a, "minLength", 0);
        var maxSegments = Args.IntOpt(a, "maxSegments", 2000);
        var maxTexts = Args.IntOpt(a, "maxTexts", 2000);
        var images = Args.BoolOpt(a, "includeImages", false);

        var saved = new Dictionary<string, object>();
        foreach (var name in new[] { "FILEDIA", "PDFIMPORTFILTER", "PDFIMPORTIMAGEPATH" }) saved[name] = AcApp.GetSystemVariable(name);
        var ucs = ed.CurrentUserCoordinateSystem;
        var seed = db.Handseed.Value;
        ed.Command("_.UNDO", "_Mark");
        try
        {
            AcApp.SetSystemVariable("FILEDIA", 0);
            // PDFIMPORTFILTER lists what to LEAVE OUT (8 = raster images), not what to bring in.
            AcApp.SetSystemVariable("PDFIMPORTFILTER", images ? 0 : 8);
            AcApp.SetSystemVariable("PDFIMPORTIMAGEPATH", imageFolder);
            ed.CurrentUserCoordinateSystem = Matrix3d.Identity;
            ed.Command("_.-PDFIMPORT", "_File", path, page.ToString(CultureInfo.InvariantCulture), "0,0", "1", "0");
            return Collect(db, seed, window, minLength, maxSegments, maxTexts, page, info);
        }
        finally
        {
            try { ed.Command("_.UNDO", "_Back"); }
            finally
            {
                ed.CurrentUserCoordinateSystem = ucs;
                foreach (var (name, value) in saved) { try { AcApp.SetSystemVariable(name, value); } catch (Exception) { } }
            }
        }
    }

    private static object Collect(Database db, long seed, double[]? window, double minLength, int maxSegments, int maxTexts, int page, PdfRenderResult info)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var texts = new List<object>();
        var segments = new List<double[]>();
        int textCount = 0, segmentCount = 0, shortSegments = 0;
        Extents3d? extents = null;
        using var tr = db.TransactionManager.StartTransaction();
        var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        bool Inside(Point3d p, Point3d q) => window is null ||
            (Math.Max(p.X, q.X) >= window[0] && Math.Min(p.X, q.X) <= window[2] && Math.Max(p.Y, q.Y) >= window[1] && Math.Min(p.Y, q.Y) <= window[3]);
        void Segment(Point3d p, Point3d q)
        {
            if (p.DistanceTo(q) < minLength) { shortSegments++; return; }
            if (!Inside(p, q)) return;
            segmentCount++;
            if (segments.Count < maxSegments) segments.Add([R(p.X), R(p.Y), R(q.X), R(q.Y)]);
        }
        foreach (ObjectId id in space)
        {
            if (id.Handle.Value < seed || tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
            var type = e.GetType().Name;
            counts[type] = counts.GetValueOrDefault(type) + 1;
            try { var box = e.GeometricExtents; if (extents is null) extents = box; else { var all = extents.Value; all.AddExtents(box); extents = all; } } catch (Exception) { }
            switch (e)
            {
                case DBText t:
                    textCount++;
                    if (texts.Count < maxTexts && Inside(t.Position, t.Position)) texts.Add(new { text = t.TextString, at = new[] { R(t.Position.X), R(t.Position.Y) }, height = R(t.Height), rotationDegrees = R(t.Rotation * 180 / Math.PI) });
                    break;
                case MText m:
                    textCount++;
                    if (texts.Count < maxTexts && Inside(m.Location, m.Location)) texts.Add(new { text = m.Text, at = new[] { R(m.Location.X), R(m.Location.Y) }, height = R(m.TextHeight), rotationDegrees = R(m.Rotation * 180 / Math.PI) });
                    break;
                case Line l: Segment(l.StartPoint, l.EndPoint); break;
                case Polyline pl:
                    var n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (var i = 0; i < n; i++)
                        if (pl.GetSegmentType(i) == SegmentType.Line) Segment(pl.GetPoint3dAt(i), pl.GetPoint3dAt((i + 1) % pl.NumberOfVertices));
                    break;
            }
        }
        var lettering = textCount == 0 && counts.GetValueOrDefault("Polyline") > 100
            ? "No text objects arrived, so the lettering on this page is linework (SHX fonts). Read printed values from a rendered image of the page, and take lengths and angles from these segments."
            : null;
        return new
        {
            page = new { number = page, of = info.PageCount, widthInches = R(info.PageWidthDips / 96), heightInches = R(info.PageHeightDips / 96) },
            counts,
            extents = extents is { } x ? new { min = new[] { R(x.MinPoint.X), R(x.MinPoint.Y) }, max = new[] { R(x.MaxPoint.X), R(x.MaxPoint.Y) } } : null,
            texts = new { count = textCount, returned = texts.Count, items = texts },
            segments = new { count = segmentCount, returned = segments.Count, skippedShorterThanMinLength = shortSegments, items = segments },
            lettering,
            undone = true,
        };
    }

    private static double R(double v) => Math.Round(v, 6);
}
