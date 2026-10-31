using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin.Tools;

internal static class WorkflowTools
{
    /// <summary>The visual styles a preview can switch to, as -VISUALSTYLES keywords, keyed by their dictionary names.</summary>
    private static readonly Dictionary<string, string> VisualStyleKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2dwireframe"] = "_2dwireframe",
        ["wireframe"] = "_Wireframe",
        ["hidden"] = "_Hidden",
        ["realistic"] = "_Realistic",
        ["conceptual"] = "_Conceptual",
        ["shaded"] = "_Shaded",
    };

    private const int InventoryLimit = 2000;

    private static readonly JsonElement EntityExpectation = ToolSchema.Object(["handle"], ("handle", ToolSchema.Text()),
        ("type", ToolSchema.Text()), ("layer", ToolSchema.Text()), ("style", ToolSchema.Text()), ("minimumVolume", ToolSchema.Number(0)));

    internal static void Register()
    {
        Router.Register("checkpoint_drawing", "Save the named active drawing, copy it to a NEW DWG, reopen the copy and verify its model-space handles. Returns SHA-256 and recovery context. File writes cannot be undone. Relative xrefs still use the original drawing directory; this is a checkpoint, not a packaged project.", true, UndoKind.NotUndoable,
            ToolSchema.Object(["expectedDocument", "path"], ("expectedDocument", ToolSchema.Text()), ("path", ToolSchema.Text()), ("expectedHandles", ToolSchema.Array(ToolSchema.Text(), 0))), Checkpoint);
        Router.Register("export_drawing_preview", "Frame model space and export a native PNG, returned as image content as well as a path. Restores the view, visual style, selection and system variables even on failure. Uses the viewport pixel resolution; this is a preview, not a scaled plot (plot_preview is the check for sheets). Every argument is optional: handles (default every visible model-space entity); direction (top, bottom, front, back, left, right, sw-iso, se-iso, ne-iso, nw-iso, or a WCS [x, y, z] vector toward the viewer; default top); visualStyle (default the current one); margin (1 to 5, default 1.15); path (a new .png to keep; default a temp file); inline (default true); expectedDocument (when given, the active drawing must be that saved file; without it any drawing, saved or not, is previewed). Handles always set the framing, but only a 2D wireframe preview shows them alone: AutoCAD exports a selection blank in every other style, so there it exports everything in the framed view ('exported' in the result says which).", true, UndoKind.NotUndoable,
            ToolSchema.Object([], ("expectedDocument", ToolSchema.Text()), ("path", ToolSchema.Text()),
                ("handles", ToolSchema.Array(ToolSchema.Text())), ("direction", ToolSchema.Any()), ("visualStyle", ToolSchema.Choice([.. VisualStyleKeywords.Keys])),
                ("margin", ToolSchema.Number(1, 5)), ("inline", ToolSchema.Flag())), Preview);
        Router.Register("validate_model", "Read entity types, layers, styles, extents and native body volumes. entities: handles as strings, or objects {handle, type, layer, style, minimumVolume} whose expectations are checked; leave it out to inventory every top-level model-space entity (at most 2000) with counts by type and layer. expectedDocument is optional: when given, the active drawing must be that saved file. Optional WCS point probes test ACA mass/slab bodies in entity coordinates. Optional axis clearances use WCS bounding boxes only, not exact collision detection. Optional openings {depth} (ACA) checks every door, window and opening for casework, furniture, mass elements, solids and blocks in the space in front of and behind it: the opening's width, from its sill to its head, through the wall and 'depth' beyond on both sides (default 24 in, 2 ft or 600 mm by INSUNITS). Objects with plan-only extents are tested in plan and marked planOnly. A failed or unsupported check never counts as passed.", false, UndoKind.None,
            ToolSchema.Object([], ("expectedDocument", ToolSchema.Text()),
                ("entities", ToolSchema.Array(ToolSchema.Any())),
                ("probes", ToolSchema.Array(ToolSchema.Object(["handle", "point", "inside"], ("handle", ToolSchema.Text()), ("point", ToolSchema.Point()), ("inside", ToolSchema.Flag())), 0)),
                ("clearances", ToolSchema.Array(ToolSchema.Object(["first", "second", "axis", "minimum"], ("first", ToolSchema.Text()), ("second", ToolSchema.Text()), ("axis", ToolSchema.Choice("x", "y", "z")), ("minimum", ToolSchema.Number(0))), 0)),
                ("openings", ToolSchema.Object([], ("depth", ToolSchema.Number(0))))), Validate);
    }

    private static Task<object?> Checkpoint(JsonElement args) => MainThread.InCommandContextAsync<object?>(() =>
    {
        var doc = Acad.ActiveDocument;
        WorkflowSupport.CheckDocument(doc, args, checkBusy: false); // preflight runs before AutoCAD's internal command-context command
        if (doc.IsReadOnly) throw new AcadMcpException("read_only", "The source drawing is read-only.");
        var path = WorkflowSupport.NewFile(args, ".dwg");
        var expected = Args.StrArrayOpt(args, "expectedHandles");
        var source = doc.Database.Filename;
        // QSAVE must finish before any bytes are copied. No active database transaction here.
        doc.Editor.Command("_.QSAVE");
        if (Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0)
            throw new AcadMcpException("save_failed", "QSAVE did not leave a clean drawing; no checkpoint was written.");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".nodal-" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            File.Copy(source, temp, false);
            // Dispose the transaction before its side database, and both before publishing.
            int count; string units;
            using (var readback = new Database(false, true))
            {
                readback.ReadDwgFile(temp, FileOpenMode.OpenForReadAndAllShare, true, null);
                readback.CloseInput(true);
                using var tr = readback.TransactionManager.StartTransaction();
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(readback), OpenMode.ForRead);
                count = 0; foreach (ObjectId _ in ms) count++;
                foreach (var handle in expected)
                {
                    var id = Acad.ToObjectId(readback, handle);
                    if (id.IsErased) throw new AcadMcpException("verification_failed", $"Expected handle {handle} is erased.");
                    _ = tr.GetObject(id, OpenMode.ForRead);
                }
                units = readback.Insunits.ToString();
            }
            string hash; long bytes;
            using (var stream = File.OpenRead(temp)) { hash = Convert.ToHexString(SHA256.HashData(stream)); bytes = stream.Length; }
            File.Move(temp, path, false);
            return new
            {
                path,
                source,
                bytes,
                sha256 = hash,
                savedUtc = DateTime.UtcNow,
                verified = true,
                modelSpaceEntities = count,
                units,
                verifiedHandles = expected,
                xrefBaseDirectory = Path.GetDirectoryName(source)
            };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }, () => WorkflowSupport.CheckDocument(Acad.ActiveDocument, args));

    private static Task<object?> Preview(JsonElement args) => MainThread.InCommandContextAsync<object?>(() =>
    {
        var doc = Acad.ActiveDocument;
        WorkflowSupport.CheckDocument(doc, args, checkBusy: false, optional: true);
        if (!doc.Database.TileMode) throw new AcadMcpException("model_space_required", "Switch to the Model tab before exporting a preview.");
        var vector = PreviewDirection(args);
        var style = Args.StrOpt(args, "visualStyle");
        var inline = Args.BoolOpt(args, "inline", true);
        var path = WorkflowSupport.NewFileOrTemp(args, ".png", "model");
        var chosen = args.TryGetProperty("handles", out _);
        var ed = doc.Editor;
        using var previous = ed.GetCurrentView();
        ObjectId[] ids;
        Extents3d? bounds = null;
        string? previousStyle;
        using (var tr = doc.TransactionManager.StartTransaction())
        {
            if (chosen)
            {
                ids = Args.StrArray(args, "handles").Select(h => Acad.ToObjectId(doc.Database, h)).Distinct().ToArray();
                foreach (var id in ids)
                {
                    var e = tr.GetObject(id, OpenMode.ForRead) as Entity ?? throw new AcadMcpException("wrong_type", "Preview handles must name entities.");
                    if (e.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(doc.Database)) throw new AcadMcpException("wrong_space", "Preview handles must be top-level model-space entities.");
                    if (bounds is null) bounds = e.GeometricExtents;
                    else { var box = bounds.Value; box.AddExtents(e.GeometricExtents); bounds = box; }
                }
            }
            else ids = VisibleModelEntities(tr, doc.Database, out bounds);
            previousStyle = VisualStyleName(tr, doc.Database, previous.VisualStyleId);
        }
        if (bounds is null) throw new AcadMcpException("nothing_to_frame", "Model space has no visible entity with extents to frame.");
        // Only the six built-in styles can be put back by keyword, so a custom current style is reported, not guessed at.
        var switchStyle = style is not null && !string.Equals(style, previousStyle, StringComparison.OrdinalIgnoreCase);
        var styleRestored = !switchStyle || previousStyle is not null && VisualStyleKeywords.ContainsKey(previousStyle);
        // PNGOUT's object selection only renders in 2D wireframe. Measured in AutoCAD 2027: with handles,
        // Wireframe, Hidden, Conceptual, Shaded and Realistic all gave a single flat colour. In those the
        // view is still framed on the handles, but everything shown in it is exported.
        var exportSelection = chosen && string.Equals(switchStyle ? style : previousStyle, "2dWireframe", StringComparison.OrdinalIgnoreCase);
        var selection = ed.SelectImplied();
        var filedia = AcApp.GetSystemVariable("FILEDIA");
        var vt = AcApp.GetSystemVariable("VTENABLE");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".nodal-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            AcApp.SetSystemVariable("FILEDIA", 0);
            AcApp.SetSystemVariable("VTENABLE", 0);
            ed.SetImpliedSelection([]);
            using var view = ed.GetCurrentView();
            view.ViewDirection = vector; view.ViewTwist = 0; view.PerspectiveEnabled = false;
            view.Target = bounds!.Value.MinPoint + (bounds.Value.MaxPoint - bounds.Value.MinPoint) * .5;
            var wcsToDcs = (Matrix3d.Displacement(view.Target.GetAsVector()) * Matrix3d.PlaneToWorld(vector)).Inverse();
            var box = bounds.Value; box.TransformBy(wcsToDcs);
            var aspect = Math.Max(1e-6, previous.Width / Math.Max(previous.Height, 1e-6));
            var height = Math.Max(Math.Max(box.MaxPoint.Y - box.MinPoint.Y, (box.MaxPoint.X - box.MinPoint.X) / aspect), 1e-6);
            view.Height = height * Args.DblOpt(args, "margin", 1.15); view.Width = view.Height * aspect;
            view.CenterPoint = new Point2d((box.MinPoint.X + box.MaxPoint.X) / 2, (box.MinPoint.Y + box.MaxPoint.Y) / 2);
            ed.SetCurrentView(view);
            if (switchStyle) ed.Command("_.-VISUALSTYLES", "_C", VisualStyleKeywords[style!]);
            ed.Regen(); ed.UpdateScreen();
            if (exportSelection)
            {
                using var exportSet = SelectionSet.FromObjectIds(ids);
                ed.Command("_.PNGOUT", temp, exportSet, "");
            }
            else ed.Command("_.PNGOUT", temp, ""); // Enter at "Select objects" exports everything shown
            if (!File.Exists(temp) || new FileInfo(temp).Length < 24) throw new AcadMcpException("export_failed", "PNGOUT did not produce an image.");
            File.Move(temp, path, false);
            var bytes = File.ReadAllBytes(path);
            var attach = inline && bytes.Length <= PlotTools.MaxInlineBytes;
            return new
            {
                path,
                bytes = bytes.Length,
                document = doc.Name,
                entities = ids.Length,
                framed = chosen ? "handles" : "visible model space",
                exported = exportSelection ? "handles only" : "everything in the view",
                direction = new[] { vector.X, vector.Y, vector.Z },
                visualStyle = switchStyle ? style : previousStyle,
                visualStyleRestored = styleRestored,
                note = styleRestored ? null : $"The viewport was left in '{style}': its previous style '{previousStyle}' is not one this tool can set back.",
                viewRestored = true,
                bounds = WorkflowSupport.Bounds(bounds.Value),
                image = attach ? ToolResultImage.Describe("image/png", bytes) : null,
            };
        }
        finally
        {
            try
            {
                ed.SetCurrentView(previous);
                if (switchStyle && styleRestored) ed.Command("_.-VISUALSTYLES", "_C", VisualStyleKeywords[previousStyle!]);
                ed.SetImpliedSelection(selection.Status == PromptStatus.OK ? selection.Value.GetObjectIds() : []);
            }
            finally
            {
                AcApp.SetSystemVariable("FILEDIA", filedia); AcApp.SetSystemVariable("VTENABLE", vt);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }, () => WorkflowSupport.CheckDocument(Acad.ActiveDocument, args, optional: true));

    private static Vector3d PreviewDirection(JsonElement args)
    {
        if (!args.TryGetProperty("direction", out var value)) return Vector3d.ZAxis;
        double[] v;
        if (value.ValueKind == JsonValueKind.String)
        {
            try { v = WorkflowMath.ViewDirection(value.GetString()!); }
            catch (ArgumentException ex) { throw new AcadMcpException("bad_argument", ex.Message); }
        }
        else v = Args.Coordinates(value, "direction");
        var vector = new Vector3d(v[0], v[1], v.Length > 2 ? v[2] : 0);
        if (vector.Length < 1e-9) throw new AcadMcpException("bad_argument", "direction cannot be zero.");
        return vector;
    }

    /// <summary>Top-level model-space entities a viewer would see: visible, on a layer that is on and thawed, with extents.</summary>
    private static ObjectId[] VisibleModelEntities(Transaction tr, Database db, out Extents3d? bounds)
    {
        bounds = null;
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var shownLayers = new Dictionary<ObjectId, bool>();
        var ids = new List<ObjectId>();
        foreach (ObjectId id in ms)
        {
            // Construction lines are infinite and would frame nothing useful.
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e || !e.Visible || e is Xline or Ray) continue;
            if (!shownLayers.TryGetValue(e.LayerId, out var shown))
            {
                var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
                shownLayers[e.LayerId] = shown = !layer.IsOff && !layer.IsFrozen;
            }
            if (!shown) continue;
            Extents3d box;
            try { box = e.GeometricExtents; }
            catch (Exception) { continue; }
            if (bounds is null) bounds = box;
            else { var all = bounds.Value; all.AddExtents(box); bounds = all; }
            ids.Add(id);
        }
        return [.. ids];
    }

    private static string? VisualStyleName(Transaction tr, Database db, ObjectId id)
    {
        if (id.IsNull) return null;
        var styles = (DBDictionary)tr.GetObject(db.VisualStyleDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in styles) if (entry.Value == id) return entry.Key;
        return null;
    }

    private static Task<object?> Validate(JsonElement args) => Acad.Read<object?>(ctx =>
    {
        WorkflowSupport.CheckDocument(ctx.Doc, args, optional: true);
        var rows = new List<object>(); var checks = new List<object>(); var passed = true;
        void Check(string name, bool ok, object detail) { passed &= ok; checks.Add(new { name, passed = ok, detail }); }
        var inventory = !args.TryGetProperty("entities", out var requested);
        var requests = inventory ? ModelSpaceHandles(ctx) : EntityRequests(requested);
        var truncated = inventory && requests.Count > InventoryLimit;
        if (truncated) requests = requests.GetRange(0, InventoryLimit);
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal); var byLayer = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (h, request) in requests)
        {
            try
            {
                var entity = WorkflowSupport.Get<Entity>(ctx, h);
                WorkflowSupport.RequireModelEntity(entity);
                var info = NativeInfo(ctx, entity);
                rows.Add(new { handle = h, type = entity.GetType().FullName, entity.Layer, bounds = WorkflowSupport.Bounds(entity.GeometricExtents), info.Style, info.Volume });
                byType[entity.GetType().Name] = byType.GetValueOrDefault(entity.GetType().Name) + 1;
                byLayer[entity.Layer] = byLayer.GetValueOrDefault(entity.Layer) + 1;
                if (inventory) continue; // nothing was asked of it, so nothing passes or fails
                Check(h + ".exists", true, "Entity found.");
                if (request is not { } expect) continue;
                if (expect.TryGetProperty("type", out var type)) Check(h + ".type", type.GetString() == entity.GetType().FullName || type.GetString() == entity.GetType().Name, entity.GetType().FullName!);
                if (expect.TryGetProperty("layer", out var layer)) Check(h + ".layer", layer.GetString() == entity.Layer, entity.Layer);
                if (expect.TryGetProperty("style", out var style)) Check(h + ".style", style.GetString() == info.Style, info.Style ?? "unavailable");
                if (expect.TryGetProperty("minimumVolume", out var volume)) Check(h + ".volume", info.Volume is { } v && v >= volume.GetDouble(), (object?)info.Volume ?? "unavailable");
            }
            catch (Exception ex)
            {
                if (inventory) rows.Add(new { handle = h, error = ex.Message });
                else Check(h, false, ex.Message);
            }
        }
        if (args.TryGetProperty("probes", out var probes)) foreach (var probe in probes.EnumerateArray())
        {
            var h = Args.Str(probe, "handle");
            try { var inside = Contains(WorkflowSupport.Get<Entity>(ctx, h), WorkflowSupport.Point(probe.GetProperty("point"))); Check(h + ".contains", inside == probe.GetProperty("inside").GetBoolean(), new { inside }); }
            catch (Exception ex) { Check(h + ".contains", false, ex.Message); }
        }
        if (args.TryGetProperty("clearances", out var pairs)) foreach (var pair in pairs.EnumerateArray())
        {
            var first = Args.Str(pair, "first"); var second = Args.Str(pair, "second");
            try
            {
                var firstEntity = WorkflowSupport.Get<Entity>(ctx, first); var secondEntity = WorkflowSupport.Get<Entity>(ctx, second);
                WorkflowSupport.RequireModelEntity(firstEntity); WorkflowSupport.RequireModelEntity(secondEntity);
                var a = firstEntity.GeometricExtents; var b = secondEntity.GeometricExtents;
                var axis = Args.Str(pair, "axis"); var index = axis == "x" ? 0 : axis == "y" ? 1 : 2;
                var distance = Math.Max(WorkflowSupport.Xyz(b.MinPoint)[index] - WorkflowSupport.Xyz(a.MaxPoint)[index], WorkflowSupport.Xyz(a.MinPoint)[index] - WorkflowSupport.Xyz(b.MaxPoint)[index]);
                Check(first + "/" + second + ".clearance", distance >= Args.Dbl(pair, "minimum"), new { axis, distance, method = "WCS axis-aligned bounding boxes; negative means overlap on this axis" });
            }
            catch (Exception ex) { Check(first + "/" + second + ".clearance", false, ex.Message); }
        }
        if (args.TryGetProperty("openings", out var openingArgs))
        {
            var depth = Args.DblOpt(openingArgs, "depth", ctx.Db.Insunits switch
            {
                UnitsValue.Feet => 2,
                UnitsValue.Millimeters => 600,
                UnitsValue.Centimeters => 60,
                UnitsValue.Meters => 0.6,
                _ => 24,
            });
            if (!AcaSupport.IsAvailable) Check("openings", false, "Opening checks need AutoCAD Architecture.");
            else foreach (var (name, ok, detail) in OpeningClearances(ctx, depth)) Check(name, ok, detail);
        }
        return new
        {
            passed,
            document = ctx.Doc.IsNamedDrawing ? ctx.Db.Filename : ctx.Doc.Name,
            units = ctx.Db.Insunits.ToString(),
            inventory,
            truncated,
            counts = new { byType, byLayer },
            entities = rows,
            checks,
        };
    });

    private static List<(string Handle, JsonElement? Expect)> ModelSpaceHandles(AcadContext ctx)
    {
        var ms = (BlockTableRecord)ctx.Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db), OpenMode.ForRead);
        var handles = new List<(string, JsonElement?)>();
        foreach (ObjectId id in ms) handles.Add((id.Handle.ToString(), null));
        return handles;
    }

    /// <summary>Each entities item is a handle string, or an object whose expectations are checked.</summary>
    private static List<(string Handle, JsonElement? Expect)> EntityRequests(JsonElement entities)
    {
        var requests = new List<(string, JsonElement?)>();
        var i = 0;
        foreach (var item in entities.EnumerateArray())
        {
            var label = $"arguments.entities[{i++}]";
            if (item.ValueKind == JsonValueKind.String)
            {
                var handle = item.GetString()!;
                if (string.IsNullOrWhiteSpace(handle)) throw new AcadMcpException("bad_argument", $"{label} must not be blank.");
                requests.Add((handle, null));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object)
                throw new AcadMcpException("bad_argument", $"{label} must be a handle string or an object with a handle.");
            var error = ToolSchema.Validate(item, EntityExpectation, label);
            if (error is not null) throw new AcadMcpException("bad_argument", error);
            requests.Add((item.GetProperty("handle").GetString()!, item));
        }
        return requests;
    }

    private static (string? Style, double? Volume) NativeInfo(AcadContext ctx, Entity e)
    {
        if (e is Solid3d solid) return (null, solid.MassProperties.Volume);
        if (AcaSupport.IsAvailable) return AcaInfo(ctx, e);
        return (null, null);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (string? Style, double? Volume) AcaInfo(AcadContext ctx, Entity e)
    {
#if ACA
        ObjectId style = ObjectId.Null; double? volume = null;
        if (e is Autodesk.Aec.DatabaseServices.MassElement mass) { style = mass.StyleId; volume = mass.Body.Volume; }
        else if (e is Autodesk.Aec.Arch.DatabaseServices.Slab slab) { style = slab.StyleId; volume = 0; foreach (Autodesk.Aec.Modeler.Body body in slab.LayerBodies) volume += body.Volume; }
        else if (e is Autodesk.Aec.Structural.DatabaseServices.Member member) style = member.StyleId;
        else if (e is Autodesk.Aec.DatabaseServices.MultiViewBlockReference mv) style = mv.BlockDefId;
        return (style.IsNull ? null : ((Autodesk.Aec.DatabaseServices.DictionaryRecord)ctx.Tr.GetObject(style, OpenMode.ForRead)).Name, volume);
#else
        return (null, null);
#endif
    }
    /// <summary>
    /// One check per door, window or opening: nothing that occupies space (mass elements, furniture
    /// and other multi-view blocks that are not tags, solids, block references) may enter the zone
    /// in front of and behind it. The zone is built in the opening's own frame (X along the wall
    /// from the opening's start, Y across, Z up from the sill; measured in ACA 2027), because an
    /// opening's own extents are its plan representation, a few inches deep and flat.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<(string Name, bool Passed, object Detail)> OpeningClearances(AcadContext ctx, double depth)
    {
        var results = new List<(string, bool, object)>();
#if ACA
        var space = (BlockTableRecord)ctx.Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db), OpenMode.ForRead);
        var openings = new List<Autodesk.Aec.Arch.DatabaseServices.OpeningBase>();
        var things = new List<(Entity Entity, Point3d[] Corners, bool PlanOnly)>();
        var hidden = new Dictionary<ObjectId, bool>();
        foreach (ObjectId id in space)
        {
            var o = ctx.Tr.GetObject(id, OpenMode.ForRead);
            if (o is Autodesk.Aec.Arch.DatabaseServices.OpeningBase opening) { openings.Add(opening); continue; }
            if (o is not Entity e || !e.Visible) continue;
            if (!hidden.TryGetValue(e.LayerId, out var off))
            {
                var layer = (LayerTableRecord)ctx.Tr.GetObject(e.LayerId, OpenMode.ForRead);
                hidden[e.LayerId] = off = layer.IsOff || layer.IsFrozen;
            }
            if (off) continue;
            Point3d[]? corners = null;
            if (e is Autodesk.Aec.DatabaseServices.MassElement mass)
            {
                try
                {
                    var ecs = mass.GeoEcs;
                    var points = new List<Point3d>();
                    foreach (Autodesk.Aec.Modeler.Vertex v in mass.Body.Vertices) points.Add(v.Point.TransformBy(ecs));
                    if (points.Count > 0) corners = points.ToArray();
                }
                catch (Exception) { }
            }
            else if (e is Autodesk.Aec.DatabaseServices.MultiViewBlockReference mv && !mv.AnchorId.IsNull) continue; // a tag
            else if (e is not (Autodesk.Aec.DatabaseServices.MultiViewBlockReference or Solid3d or BlockReference)) continue;
            if (corners is null)
            {
                Extents3d box;
                try { box = e.GeometricExtents; } catch (Exception) { continue; }
                corners = BoxCorners(box);
            }
            things.Add((e, corners, corners.Max(p => p.Z) - corners.Min(p => p.Z) < 1e-6));
        }

        foreach (var opening in openings)
        {
            var name = opening.Handle + ".openingClear";
            try
            {
                double wallWidth = 0;
                if (!opening.AnchorId.IsNull &&
                    ctx.Tr.GetObject(opening.AnchorId, OpenMode.ForRead) is Autodesk.Aec.Arch.DatabaseServices.AnchorOpeningBaseToWall anchor &&
                    ctx.Tr.GetObject(anchor.CurveId, OpenMode.ForRead) is Autodesk.Aec.Arch.DatabaseServices.Wall wall)
                    wallWidth = wall.Width;
                var zone = new OpeningZone(opening.Width, opening.Height, wallWidth + depth);
                var toLocal = opening.GeoEcs.Inverse();
                var intruders = new List<object>();
                foreach (var (entity, corners, planOnly) in things)
                {
                    var local = corners.Select(p => p.TransformBy(toLocal)).ToArray();
                    if (zone.Intersects(local.Min(p => p.X), local.Min(p => p.Y), local.Min(p => p.Z),
                                        local.Max(p => p.X), local.Max(p => p.Y), local.Max(p => p.Z), planOnly))
                        intruders.Add(new { handle = entity.Handle.ToString(), type = entity.GetType().Name, entity.Layer, planOnly });
                }
                results.Add((name, intruders.Count == 0, new
                {
                    type = opening.GetType().Name,
                    width = opening.Width,
                    height = opening.Height,
                    sill = opening.Location.Z,
                    zoneDepth = wallWidth + depth,
                    intruders,
                }));
            }
            catch (Exception ex) { results.Add((name, false, ex.Message)); }
        }
        if (openings.Count == 0) results.Add(("openings", true, "No doors, windows or openings in model space."));
#endif
        return results;
    }

    private static Point3d[] BoxCorners(Extents3d b) =>
    [
        new(b.MinPoint.X, b.MinPoint.Y, b.MinPoint.Z), new(b.MaxPoint.X, b.MinPoint.Y, b.MinPoint.Z),
        new(b.MinPoint.X, b.MaxPoint.Y, b.MinPoint.Z), new(b.MaxPoint.X, b.MaxPoint.Y, b.MinPoint.Z),
        new(b.MinPoint.X, b.MinPoint.Y, b.MaxPoint.Z), new(b.MaxPoint.X, b.MinPoint.Y, b.MaxPoint.Z),
        new(b.MinPoint.X, b.MaxPoint.Y, b.MaxPoint.Z), new(b.MaxPoint.X, b.MaxPoint.Y, b.MaxPoint.Z),
    ];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Contains(Entity entity, Point3d point)
    {
        WorkflowSupport.RequireModelEntity(entity);
#if ACA
        if (entity is Autodesk.Aec.DatabaseServices.MassElement m) return Interior(m.Body.Contains(point.TransformBy(m.GeoEcs.Inverse())));
        if (entity is Autodesk.Aec.Arch.DatabaseServices.Slab s)
        {
            var local = point.TransformBy(s.GeoEcs.Inverse());
            foreach (Autodesk.Aec.Modeler.Body body in s.LayerBodies) if (Interior(body.Contains(local))) return true;
            return false;
        }
#endif
        throw new AcadMcpException("unsupported_probe", "Point probes currently support ACA mass elements and slabs only.");
    }
#if ACA
    private static bool Interior(Autodesk.Aec.Modeler.PointInBodyLocation location) => location switch
    {
        Autodesk.Aec.Modeler.PointInBodyLocation.PointInsideBody => true,
        Autodesk.Aec.Modeler.PointInBodyLocation.PointOutsideBody => false,
        _ => throw new AcadMcpException("boundary_probe", "Probe lies on a body boundary; move it into the material or void before asserting containment.")
    };
#endif
}
