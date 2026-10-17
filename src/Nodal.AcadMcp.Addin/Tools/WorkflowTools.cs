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
    internal static void Register()
    {
        Router.Register("checkpoint_drawing", "Save the named active drawing, copy it to a NEW DWG, reopen the copy and verify its model-space handles. Returns SHA-256 and recovery context. File writes cannot be undone. Relative xrefs still use the original drawing directory; this is a checkpoint, not a packaged project.", true, UndoKind.NotUndoable,
            ToolSchema.Object(["expectedDocument", "path"], ("expectedDocument", ToolSchema.Text()), ("path", ToolSchema.Text()), ("expectedHandles", ToolSchema.Array(ToolSchema.Text(), 0))), Checkpoint);
        Router.Register("export_drawing_preview", "Fit selected model-space entities and synchronously export a native PNG. Restores the view, selection and system variables even on failure. Uses the viewport pixel resolution; this is a preview, not a scaled plot. All coordinates and direction are WCS. Output path must be new.", true, UndoKind.NotUndoable,
            ToolSchema.Object(["expectedDocument", "path", "handles", "direction"], ("expectedDocument", ToolSchema.Text()), ("path", ToolSchema.Text()),
                ("handles", ToolSchema.Array(ToolSchema.Text())), ("direction", ToolSchema.Point()), ("margin", ToolSchema.Number(1, 5))), Preview);
        Router.Register("validate_model", "Read entity types, layers, styles, extents and native body volumes. Optional WCS point probes test ACA mass/slab bodies in entity coordinates. Optional axis clearances use WCS bounding boxes only, not exact collision detection. A failed or unsupported check never counts as passed.", false, UndoKind.None,
            ToolSchema.Object(["expectedDocument", "entities"], ("expectedDocument", ToolSchema.Text()),
                ("entities", ToolSchema.Array(ToolSchema.Object(["handle"], ("handle", ToolSchema.Text()), ("type", ToolSchema.Text()), ("layer", ToolSchema.Text()), ("style", ToolSchema.Text()), ("minimumVolume", ToolSchema.Number(0))))),
                ("probes", ToolSchema.Array(ToolSchema.Object(["handle", "point", "inside"], ("handle", ToolSchema.Text()), ("point", ToolSchema.Point()), ("inside", ToolSchema.Flag())), 0)),
                ("clearances", ToolSchema.Array(ToolSchema.Object(["first", "second", "axis", "minimum"], ("first", ToolSchema.Text()), ("second", ToolSchema.Text()), ("axis", ToolSchema.Choice("x", "y", "z")), ("minimum", ToolSchema.Number(0))), 0))), Validate);
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
        WorkflowSupport.CheckDocument(doc, args, checkBusy: false);
        if (!doc.Database.TileMode) throw new AcadMcpException("model_space_required", "Switch to the Model tab before exporting a preview.");
        var path = WorkflowSupport.NewFile(args, ".png");
        var vector = WorkflowSupport.Point(args.GetProperty("direction")).GetAsVector();
        if (vector.Length < 1e-9) throw new AcadMcpException("bad_argument", "direction cannot be zero.");
        var ids = Args.StrArray(args, "handles").Select(h => Acad.ToObjectId(doc.Database, h)).Distinct().ToArray();
        Extents3d? bounds = null;
        using (var tr = doc.TransactionManager.StartTransaction())
        {
            foreach (var id in ids)
            {
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity ?? throw new AcadMcpException("wrong_type", "Preview handles must name entities.");
                if (e.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(doc.Database)) throw new AcadMcpException("wrong_space", "Preview handles must be top-level model-space entities.");
                if (bounds is null) bounds = e.GeometricExtents;
                else { var box = bounds.Value; box.AddExtents(e.GeometricExtents); bounds = box; }
            }
        }
        var ed = doc.Editor;
        using var previous = ed.GetCurrentView();
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
            ed.SetCurrentView(view); ed.Regen(); ed.UpdateScreen();
            using var exportSet = SelectionSet.FromObjectIds(ids);
            ed.Command("_.PNGOUT", temp, exportSet, "");
            if (!File.Exists(temp) || new FileInfo(temp).Length < 24) throw new AcadMcpException("export_failed", "PNGOUT did not produce an image.");
            File.Move(temp, path, false);
            return new { path, bytes = new FileInfo(path).Length, entities = ids.Length, viewRestored = true, bounds = WorkflowSupport.Bounds(bounds.Value) };
        }
        finally
        {
            try { ed.SetCurrentView(previous); ed.SetImpliedSelection(selection.Status == PromptStatus.OK ? selection.Value.GetObjectIds() : []); }
            finally
            {
                AcApp.SetSystemVariable("FILEDIA", filedia); AcApp.SetSystemVariable("VTENABLE", vt);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }, () => WorkflowSupport.CheckDocument(Acad.ActiveDocument, args));

    private static Task<object?> Validate(JsonElement args) => Acad.Read<object?>(ctx =>
    {
        WorkflowSupport.CheckDocument(ctx.Doc, args);
        var rows = new List<object>(); var checks = new List<object>(); var passed = true;
        void Check(string name, bool ok, object detail) { passed &= ok; checks.Add(new { name, passed = ok, detail }); }
        foreach (var request in args.GetProperty("entities").EnumerateArray())
        {
            var h = Args.Str(request, "handle");
            try
            {
                var entity = WorkflowSupport.Get<Entity>(ctx, h);
                WorkflowSupport.RequireModelEntity(entity);
                var info = NativeInfo(ctx, entity);
                rows.Add(new { handle = h, type = entity.GetType().FullName, entity.Layer, bounds = WorkflowSupport.Bounds(entity.GeometricExtents), info.Style, info.Volume });
                Check(h + ".exists", true, "Entity found.");
                if (request.TryGetProperty("type", out var type)) Check(h + ".type", type.GetString() == entity.GetType().FullName || type.GetString() == entity.GetType().Name, entity.GetType().FullName!);
                if (request.TryGetProperty("layer", out var layer)) Check(h + ".layer", layer.GetString() == entity.Layer, entity.Layer);
                if (request.TryGetProperty("style", out var style)) Check(h + ".style", style.GetString() == info.Style, info.Style ?? "unavailable");
                if (request.TryGetProperty("minimumVolume", out var volume)) Check(h + ".volume", info.Volume is { } v && v >= volume.GetDouble(), (object?)info.Volume ?? "unavailable");
            }
            catch (Exception ex) { Check(h, false, ex.Message); }
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
        return new { passed, document = ctx.Db.Filename, units = ctx.Db.Insunits.ToString(), entities = rows, checks };
    });

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
