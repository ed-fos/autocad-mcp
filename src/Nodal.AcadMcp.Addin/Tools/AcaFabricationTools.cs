using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;
#if ACA
using AB = Autodesk.Aec.DatabaseServices;
using AM = Autodesk.Aec.Modeler;
using AA = Autodesk.Aec.Arch.DatabaseServices;
using AS = Autodesk.Aec.Structural.DatabaseServices;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

internal static class AcaFabricationTools
{
    internal static void Register()
    {
        Reg("clone_aec_style", "Clone an existing mass, slab, structural member style or MV-block definition to a NEW name. Does not change existing instances.",
            Schema(["kind", "source", "name"], ("kind", ToolSchema.Choice("mass", "slab", "member", "mvblock")), ("source", ToolSchema.Text()), ("name", ToolSchema.Text())), CloneStyle);
        Reg("boolean_mass_elements", "Replace the target mass body with a native BRep boolean result. Operands are copied in WCS before calculation. Preserves target style/layer; operands remain unless eraseOperands=true. Empty results are rejected. All handles must be distinct.",
            Schema(["target", "operands", "operation"], ("target", ToolSchema.Text()), ("operands", ToolSchema.Array(ToolSchema.Text(), 1, 100)), ("operation", ToolSchema.Choice("union", "subtract", "intersect")), ("eraseOperands", ToolSchema.Flag())), BooleanMass);
        Reg("create_member_profile", "Create a structural profile and matching single-component member style from a closed contour. vertices are [profileX,profileY,bulgeToNext], with positive profileX to the path's left and profileY upward. Applies the profile at every detail level. Explicit units prevent silent 25.4x scaling.",
            Schema(["name", "vertices", "profileUnits", "drawingUnits"], ("name", ToolSchema.Text()), ("vertices", ToolSchema.Array(ToolSchema.Point(), 3, 256)), ("profileUnits", Units()), ("drawingUnits", Units())), Profile);
        Reg("create_mitered_member_path", "Create ACA structural braces along an OPEN horizontal WCS path, with native start/end miter planes. Uses a single-component, untapered profile style. Baseline justification; positive profileX lies left of travel, profileY points up. Rejects reversing corners and short segments whose miters cross.",
            Schema(["points", "style", "layer"], ("points", ToolSchema.Array(ToolSchema.Point(), 2, 128)), ("style", ToolSchema.Text()), ("layer", ToolSchema.Text())), MiterPath);
        Reg("create_mvblock_from_solids", "Clone WCS solids into a new model block and MV definition. Requires an existing templateDefinition with display representations. Creates at basePoint, or replaces only replaceHandle while retaining its location/rotation/scale. Geometry is converted into that reference's local frame, preserving the input solids' WCS size. Source solids remain unless eraseSources=true.",
            Schema(["name", "solids", "templateDefinition", "layer"], ("name", ToolSchema.Text()), ("solids", ToolSchema.Array(ToolSchema.Text(), 1, 1000)), ("templateDefinition", ToolSchema.Text()), ("layer", ToolSchema.Text()), ("basePoint", ToolSchema.Point()), ("replaceHandle", ToolSchema.Text()), ("eraseSources", ToolSchema.Flag())), MvBlock);
        Reg("edit_slab_openings", "Add polygon holes and/or remove holes by WCS point in an ACA slab. Uses native slab topology and verifies loop-count changes. add loops omit the repeated closing point; normal is the projection direction. At least one edit is required.",
            Schema(["handle"], ("handle", ToolSchema.Text()), ("add", ToolSchema.Array(ToolSchema.Array(ToolSchema.Point(), 3, 256), 0, 64)), ("remove", ToolSchema.Array(ToolSchema.Point(), 0, 64)), ("normal", ToolSchema.Point())), SlabHoles);
    }

    private static object Units() => ToolSchema.Choice("Millimeters", "Centimeters", "Meters", "Inches", "Feet");
    private static JsonElement Schema(string[] required, params (string Name, object Schema)[] properties) =>
        ToolSchema.Object(["expectedDocument", "operationKey", .. required], [("expectedDocument", ToolSchema.Text()), ("operationKey", ToolSchema.Text()), .. properties]);
    private static void Reg(string name, string description, JsonElement schema, ToolHandler handler) =>
        Router.Register(name, description + " Requires expectedDocument and a stable operationKey. Identical retries return the stored receipt; changed arguments with the same key fail. Save the drawing to persist receipts.", true, UndoKind.SingleStep, schema, args => { AcaSupport.Require(); return handler(args); });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CloneStyle(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "clone_aec_style", args, () =>
        {
            AB.Dictionary dict = Args.Str(args, "kind") switch
            {
                "mass" => new AB.DictionaryMassElementStyle(ctx.Db),
                "slab" => new AA.DictionarySlabStyle(ctx.Db),
                "member" => new AS.DictionaryMemberStyle(ctx.Db),
                _ => new AB.DictionaryMultiViewBlockDefinition(ctx.Db)
            };
            var id = Clone(ctx, dict, Args.Str(args, "source"), Args.Str(args, "name"));
            return (new { handle = id.Handle.ToString(), name = Args.Str(args, "name") }, new[] { id.Handle.ToString() });
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> BooleanMass(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "boolean_mass_elements", args, () =>
        {
            var target = WorkflowSupport.Get<AB.MassElement>(ctx, Args.Str(args, "target"), OpenMode.ForWrite);
            var handles = Args.StrArray(args, "operands");
            var operands = handles.Select(h => WorkflowSupport.Get<AB.MassElement>(ctx, h)).ToArray();
            WorkflowSupport.RequireModelEntity(target);
            foreach (var operand in operands) WorkflowSupport.RequireModelEntity(operand);
            if (operands.Select(m => m.ObjectId).Append(target.ObjectId).Distinct().Count() != operands.Length + 1)
                throw new AcadMcpException("bad_argument", "Target and operands must be distinct objects.");
            var operation = Args.Str(args, "operation") switch { "union" => AM.BooleanOperatorType.Unite, "subtract" => AM.BooleanOperatorType.Subtract, _ => AM.BooleanOperatorType.Intersect };
            var originalEcs = target.GeoEcs;
            var beforeVolume = target.Body.Volume;
            var body = target.Body.Copy(); body.Transform(originalEcs);
            // ACA BooleanOperation consumes its two input bodies; never pass a database-owned body.
            foreach (var operand in operands)
            {
                var copy = operand.Body.Copy(); copy.Transform(operand.GeoEcs);
                body = body.BooleanOperation(copy, operation);
            }
            if (!double.IsFinite(body.Volume) || body.Volume <= 1e-9) throw new AcadMcpException("empty_result", "The boolean result has no positive solid volume.");
            target.ChangeTo(AB.ShapeType.BoundaryRepresentation);
            body.Transform(target.GeoEcs.Inverse()); target.SetBody(body, false);
            if (Args.BoolOpt(args, "eraseOperands", false)) foreach (var operand in operands) { operand.UpgradeOpen(); operand.Erase(); }
            target.RecordGraphicsModified(true);
            return (new { handle = target.Handle.ToString(), beforeVolume, volume = target.Body.Volume, bounds = WorkflowSupport.Bounds(target.GeometricExtents) }, new[] { target.Handle.ToString() });
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> Profile(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "create_member_profile", args, () =>
        {
            var drawingUnits = Args.Str(args, "drawingUnits");
            if (ctx.Db.Insunits.ToString() != drawingUnits) throw new AcadMcpException("units_mismatch", "drawingUnits must match INSUNITS; unitless drawings require an explicit units setup first.");
            var factor = WorkflowMath.MillimetersPerUnit(Args.Str(args, "profileUnits")) / WorkflowMath.MillimetersPerUnit(drawingUnits);
            var vertices = args.GetProperty("vertices").EnumerateArray().ToArray();
            using var polyline = new Polyline();
            for (var i = 0; i < vertices.Length; i++) polyline.AddVertexAt(i, new Point2d(vertices[i][0].GetDouble() * factor, vertices[i][1].GetDouble() * factor), vertices[i][2].GetDouble(), 0, 0);
            polyline.Closed = true;
            if (Math.Abs(polyline.Area) < 1e-9) throw new AcadMcpException("bad_argument", "Profile must enclose a nonzero area.");
            var ring = new Autodesk.Aec.Geometry.Ring(); ring.AddSegmentsFrom(polyline, true);
            var profile = new Autodesk.Aec.Geometry.Profile(); profile.AddRing(ring);
            if (!profile.IsValidProfile(true)) throw new AcadMcpException("bad_argument", "Profile contour is invalid or self-intersecting.");
            var name = Args.Str(args, "name"); var shapes = new AS.DictionaryMemberNodeShape(ctx.Db); var styles = new AS.DictionaryMemberStyle(ctx.Db);
            if (shapes.Has(name, ctx.Tr) || styles.Has(name, ctx.Tr)) throw new AcadMcpException("already_exists", "Profile or style name already exists.");
            var standardShape = AS.DictionaryMemberNodeShape.GetStandardDefinition(ctx.Db);
            var standardStyle = AS.DictionaryMemberStyle.GetStandardStyle(ctx.Db);
            if (standardShape.IsNull || standardStyle.IsNull) throw new AcadMcpException("missing_standard", "Initialize Standard structural style and shape in this drawing first.");
            var shapeId = shapes.CloneEntry((AB.DictionaryRecord)ctx.Tr.GetObject(standardShape, OpenMode.ForRead), name);
            var shape = (AS.MemberNodeShape)ctx.Tr.GetObject(shapeId, OpenMode.ForWrite);
            foreach (AS.DetailLevel detail in Enum.GetValues<AS.DetailLevel>()) { shape.RemoveAllCurves(detail); shape.SetProfile(detail, profile); }
            var styleId = styles.CloneEntry((AB.DictionaryRecord)ctx.Tr.GetObject(standardStyle, OpenMode.ForRead), name);
            var style = (AS.MemberStyle)ctx.Tr.GetObject(styleId, OpenMode.ForWrite);
            if (style.Components.Count != 1) throw new AcadMcpException("invalid_template", "Standard member style must have exactly one initialized component.");
            var component = style.Components[0]; component.Name = name;
            component.StartNode.ShapeId = shapeId; component.StartNode.Scale = 1;
            component.EndNode.ShapeId = ObjectId.Null;
            return (new { name, shape = shapeId.Handle.ToString(), style = styleId.Handle.ToString(), factor, area = polyline.Area }, new[] { shapeId.Handle.ToString(), styleId.Handle.ToString() });
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> MiterPath(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "create_mitered_member_path", args, () =>
        {
            var layer = Args.Str(args, "layer"); WorkflowSupport.RequireLayer(ctx, layer);
            var styles = new AS.DictionaryMemberStyle(ctx.Db); var styleName = Args.Str(args, "style");
            if (!styles.Has(styleName, ctx.Tr)) throw new AcadMcpException("not_found", "Member style not found.");
            var styleId = styles.GetAt(styleName); var style = (AS.MemberStyle)ctx.Tr.GetObject(styleId, OpenMode.ForRead);
            if (style.Components.Count != 1) throw new AcadMcpException("unsupported_style", "Miter paths require a single-component style.");
            var component = style.Components[0];
            if (!component.EndNode.ShapeId.IsNull && component.EndNode.ShapeId != component.StartNode.ShapeId) throw new AcadMcpException("unsupported_style", "Tapered profiles are unsupported.");
            var detail = Enum.GetValues<AS.DetailLevel>().Last();
            var extents = component.StartNode.Extents(detail);
            var reach = Math.Max(Math.Abs(extents.MinPoint.X), Math.Abs(extents.MaxPoint.X));
            if (!double.IsFinite(reach) || reach <= 0) throw new AcadMcpException("invalid_profile", "Profile has no finite lateral extent.");
            var points = args.GetProperty("points").EnumerateArray().Select(WorkflowSupport.Point).ToArray();
            if (points.Any(p => Math.Abs(p.Z - points[0].Z) > 1e-8) || points[0].DistanceTo(points[^1]) < 1e-8)
                throw new AcadMcpException("bad_argument", "Use an open path whose points share the same Z elevation.");
            var directions = points.Zip(points.Skip(1), (a, b) => b - a).ToArray();
            if (directions.Any(v => v.Length < 1e-8)) throw new AcadMcpException("bad_argument", "Adjacent path points must be distinct.");
            var rows = new List<object>(); var handles = new List<string>(); var ms = WorkflowSupport.ModelSpace(ctx);
            for (var i = 0; i < directions.Length; i++)
            {
                var d = directions[i].GetNormal(); var left = new Vector3d(-d.Y, d.X, 0);
                var prev = i == 0 ? d : directions[i - 1].GetNormal(); var next = i == directions.Length - 1 ? d : directions[i + 1].GetNormal();
                var start = WorkflowMath.MiterNormal(prev.X, prev.Y, d.X, d.Y);
                // The end normal is expressed in this segment, not the next segment's frame.
                var end = WorkflowMath.MiterNormal(next.X, next.Y, d.X, d.Y);
                var startCut = reach * Math.Abs(start.Left / start.Forward); var endCut = reach * Math.Abs(end.Left / end.Forward);
                if (startCut + endCut >= directions[i].Length) throw new AcadMcpException("short_segment", "Profile miters overlap on a short segment; adjust the path or profile.");
                var pad = Math.Max(startCut, endCut) + Math.Max(reach * .01, 1e-6);
                var member = new AS.Member(); member.SetToStandard(ctx.Db); member.SetDatabaseDefaults(ctx.Db);
                member.StyleId = styleId; member.MemberType = AS.MemberType.Brace; member.Justification = AS.Justification.Baseline;
                member.Set(Point3d.Origin, new Point3d(directions[i].Length + 2 * pad, 0, 0), Vector3d.ZAxis);
                member.TrimPlanes.Add(new AS.TrimPlane { End = AS.TrimPlaneFrom.Start, Plane = new Plane(new Point3d(pad, 0, 0), new Vector3d(start.Forward, start.Left, 0)) });
                member.TrimPlanes.Add(new AS.TrimPlane { End = AS.TrimPlaneFrom.End, Plane = new Plane(new Point3d(-pad, 0, 0), new Vector3d(end.Forward, end.Left, 0)) });
                member.TransformBy(Matrix3d.Displacement((points[i] - d * pad).GetAsVector()) * Matrix3d.Rotation(Math.Atan2(d.Y, d.X), Vector3d.ZAxis, Point3d.Origin));
                member.Layer = layer; ms.AppendEntity(member); ctx.Tr.AddNewlyCreatedDBObject(member, true);
                handles.Add(member.Handle.ToString()); rows.Add(new { handle = member.Handle.ToString(), segment = i, length = directions[i].Length, startSlope = start.Left / start.Forward, endSlope = end.Left / end.Forward, pad });
            }
            return (new { members = rows }, handles.ToArray());
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> MvBlock(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "create_mvblock_from_solids", args, () =>
        {
            var name = Args.Str(args, "name"); var layer = Args.Str(args, "layer"); WorkflowSupport.RequireLayer(ctx, layer);
            var defs = new AB.DictionaryMultiViewBlockDefinition(ctx.Db); var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForWrite);
            if (bt.Has(name) || defs.Has(name, ctx.Tr)) throw new AcadMcpException("already_exists", "Block or definition name already exists.");
            var solids = Args.StrArray(args, "solids").Select(h => WorkflowSupport.Get<Solid3d>(ctx, h)).ToArray();
            foreach (var solid in solids) WorkflowSupport.RequireModelEntity(solid);
            if (solids.Select(s => s.ObjectId).Distinct().Count() != solids.Length) throw new AcadMcpException("bad_argument", "Solid handles must be distinct.");
            var replace = Args.StrOpt(args, "replaceHandle");
            if (replace is null && !args.TryGetProperty("basePoint", out _)) throw new AcadMcpException("bad_argument", "basePoint is required when creating a new reference.");
            if (replace is not null && args.TryGetProperty("basePoint", out _)) throw new AcadMcpException("bad_argument", "Omit basePoint when preserving an existing reference transform.");
            var mv = replace is null ? new AB.MultiViewBlockReference() : WorkflowSupport.Get<AB.MultiViewBlockReference>(ctx, replace, OpenMode.ForWrite);
            if (replace is not null) WorkflowSupport.RequireModelEntity(mv);
            if (replace is null) { mv.SetToStandard(ctx.Db); mv.SetDatabaseDefaults(ctx.Db); mv.Location = WorkflowSupport.Point(args.GetProperty("basePoint")); mv.Scale = new Scale3d(1); }
            if ((mv.Normal - Vector3d.ZAxis).Length > 1e-8) throw new AcadMcpException("unsupported_transform", "Tilted MV references are unsupported.");
            if (Math.Abs(mv.Scale.X * mv.Scale.Y * mv.Scale.Z) < 1e-12) throw new AcadMcpException("invalid_transform", "MV scale must be invertible.");
            var transform = Matrix3d.Displacement(mv.Location.GetAsVector()) * Matrix3d.Rotation(mv.Rotation, Vector3d.ZAxis, Point3d.Origin) * new Matrix3d(new[] { mv.Scale.X, 0, 0, 0, 0, mv.Scale.Y, 0, 0, 0, 0, mv.Scale.Z, 0, 0, 0, 0, 1.0 });
            var inverse = transform.Inverse();
            var block = new BlockTableRecord { Name = name, Origin = Point3d.Origin }; bt.Add(block); ctx.Tr.AddNewlyCreatedDBObject(block, true);
            Extents3d? worldBounds = null;
            foreach (var solid in solids)
            {
                if (worldBounds is null) worldBounds = solid.GeometricExtents; else { var b = worldBounds.Value; b.AddExtents(solid.GeometricExtents); worldBounds = b; }
                var copy = (Solid3d)solid.Clone(); copy.TransformBy(inverse); copy.Layer = "0"; block.AppendEntity(copy); ctx.Tr.AddNewlyCreatedDBObject(copy, true);
            }
            var definitionId = Clone(ctx, defs, Args.Str(args, "templateDefinition"), name);
            var definition = (AB.MultiViewBlockDefinition)ctx.Tr.GetObject(definitionId, OpenMode.ForWrite);
            var reps = 0;
            foreach (AB.MultiViewBlockDisplayRepresentationDefinition rep in definition.DisplayRepresentationDefinitions)
            {
                rep.ViewDefinitions.Clear(); var view = new AB.MultiViewBlockViewDefinition { BlockId = block.ObjectId }; view.SetAllViews(true); rep.ViewDefinitions.Add(view); reps++;
            }
            if (reps == 0) throw new AcadMcpException("invalid_template", "Template definition has no display representations.");
            definition.InterferenceBlockId = block.ObjectId; mv.BlockDefId = definitionId; mv.Layer = layer;
            if (replace is null) { WorkflowSupport.ModelSpace(ctx).AppendEntity(mv); ctx.Tr.AddNewlyCreatedDBObject(mv, true); }
            // View instances are lazy and may not exist until the transaction is committed.
            // UpdateViewInstances throws eNotApplicable for a newly created reference.
            mv.RecordGraphicsModified(true);
            if (Args.BoolOpt(args, "eraseSources", false)) foreach (var solid in solids) { solid.UpgradeOpen(); solid.Erase(); }
            return (new { handle = mv.Handle.ToString(), definition = definitionId.Handle.ToString(), block = block.Handle.ToString(), location = WorkflowSupport.Xyz(mv.Location), rotationRadians = mv.Rotation, scale = new[] { mv.Scale.X, mv.Scale.Y, mv.Scale.Z }, inputBounds = WorkflowSupport.Bounds(worldBounds!.Value) }, new[] { mv.Handle.ToString(), definitionId.Handle.ToString(), block.Handle.ToString() });
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SlabHoles(JsonElement args) => Acad.Write<object?>(ctx =>
    {
#if ACA
        return WorkflowSupport.Once(ctx, "edit_slab_openings", args, () =>
        {
            var slab = WorkflowSupport.Get<AA.Slab>(ctx, Args.Str(args, "handle"), OpenMode.ForWrite);
            WorkflowSupport.RequireModelEntity(slab);
            var add = args.TryGetProperty("add", out var a) ? a.EnumerateArray().ToArray() : [];
            var remove = args.TryGetProperty("remove", out var r) ? r.EnumerateArray().ToArray() : [];
            if (add.Length + remove.Length == 0) throw new AcadMcpException("bad_argument", "Supply at least one opening edit.");
            var normal = args.TryGetProperty("normal", out var n) ? WorkflowSupport.Point(n).GetAsVector() : Vector3d.ZAxis;
            if (normal.Length < 1e-9) throw new AcadMcpException("bad_argument", "normal cannot be zero.");
            var beforeLoops = slab.Face.LoopCount; var beforeArea = slab.NetArea;
            foreach (var point in remove)
            {
                var count = slab.Face.LoopCount; slab.RemoveHole(WorkflowSupport.Point(point)); slab.UpdateSlab();
                if (slab.Face.LoopCount != count - 1) throw new AcadMcpException("opening_not_found", "Removal did not remove exactly one hole.");
            }
            foreach (var loop in add)
            {
                var count = slab.Face.LoopCount;
                var points = loop.EnumerateArray().Select(WorkflowSupport.Point).ToArray();
                if (points.Distinct().Count() != points.Length) throw new AcadMcpException("bad_argument", "Opening vertices must be distinct, without a repeated closing point.");
                slab.AddHole(new Point3dCollection(points), normal.GetNormal(), Tolerance.Global); slab.UpdateSlab();
                if (!slab.IsValid || slab.Face.LoopCount != count + 1) throw new AcadMcpException("invalid_opening", "Opening failed native slab validation or did not add one loop.");
            }
            if (!slab.IsValid) throw new AcadMcpException("invalid_slab", "The edited slab is invalid.");
            slab.RecordGraphicsModified(true);
            return (new { handle = slab.Handle.ToString(), beforeLoops, loops = slab.Face.LoopCount, beforeArea, netArea = slab.NetArea, grossArea = slab.GrossArea }, new[] { slab.Handle.ToString() });
        });
#else
        throw AcaSupport.Unavailable();
#endif
    });

#if ACA
    private static ObjectId Clone(AcadContext ctx, AB.Dictionary dict, string source, string name)
    {
        if (dict.Has(name, ctx.Tr)) throw new AcadMcpException("already_exists", $"'{name}' already exists.");
        if (!dict.Has(source, ctx.Tr)) throw new AcadMcpException("not_found", $"Source '{source}' does not exist.");
        return dict.CloneEntry((AB.DictionaryRecord)ctx.Tr.GetObject(dict.GetAt(source), OpenMode.ForRead), name);
    }
#endif
}
