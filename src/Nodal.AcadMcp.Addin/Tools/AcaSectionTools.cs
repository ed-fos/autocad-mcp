using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are referenced ONLY through explicit aliases, and only inside #if ACA.
// Same reasoning as AcaTools.cs: 'Entity', 'DBObject' and friends collide across Autodesk.Aec.*,
// Autodesk.AutoCAD.* and System, and on a vanilla AutoCAD box these assemblies do not exist, so
// nothing outside the guarded region may name them.
#if ACA
using AecGeo = Autodesk.Aec.DatabaseServices.Geo;
using AecClipVolume = Autodesk.Aec.DatabaseServices.ClipVolume;
using AecClipVolumeResult = Autodesk.Aec.DatabaseServices.ClipVolumeResult;
using AecSection2d = Autodesk.Aec.DatabaseServices.Section2d;
using AecDisplaySetDict = Autodesk.Aec.DatabaseServices.DictionaryDisplaySet;
using AecSection2dStyleDict = Autodesk.Aec.DatabaseServices.DictionarySection2dStyle;
using AecElevationLine = Autodesk.Aec.Arch.DatabaseServices.BuildingElevationLine;
using AecSectionLine = Autodesk.Aec.Arch.DatabaseServices.BuildingSectionLine;
using AecSpace = Autodesk.Aec.Arch.DatabaseServices.Space;
using AecSectionUtilities = Autodesk.Aec.ApplicationServices.Utility.SectionUtilities;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture 2D SECTIONS and ELEVATIONS: building elevation/section lines and the
/// AEC_2D_SECTION results ACA generates from them.
///
/// JIT SAFETY. Identical contract to AcaTools: every handler is wrapped by <see cref="Reg"/>, which
/// calls <see cref="AcaSupport.Require"/> before touching the implementation. Implementations that
/// name Aec types are separate [MethodImpl(NoInlining)] methods, so the JIT never resolves an Aec
/// type on a vanilla box.
///
/// WCS, NEVER THE UCS. The interactive commands draw the line in the
/// current UCS, so a rotated or tilted UCS puts the elevation on the wrong plane. This module
/// therefore:
///   * treats every incoming point as WCS and flattens it to one horizontal plane;
///   * passes an explicit WCS XY plane to ClipVolume.SetVertices (a UCS-derived plane is rejected
///     by ACA with eInvalidInput, or worse, accepted and tilted);
///   * pins Editor.CurrentUserCoordinateSystem to World for the duration of creation and
///     generation and restores it afterwards, so nothing ACA does internally can see the UCS;
///   * places the result with WCS displacements only.
/// Measured in ACA 2027: with the UCS moved, rotated 30° and tilted 20°, this
/// path produces geometry identical to the World-UCS run and AutoGenerate still returns the
/// Section2d in WCS at Z=0.
///
/// VIEW-DIRECTION CONVENTION (measured, not documented): a building elevation/section line looks
/// to the LEFT of its travel direction, and the generated 2D result reads start→end left-to-right
/// with Y = world Z. So walk along the line with the wall on your left: north wall W→E, east wall
/// N→S, south wall E→W, west wall S→N. 'reverse' flips the side. ClipVolume.Reverse() reorders
/// Vertices and flips the clip body but does NOT change Geo.YDir (measured), so the reported view
/// direction is derived from the effective vertex order, never from YDir.
///
/// GENERATION. Autodesk.Aec.ApplicationServices.Utility.SectionUtilities.AutoGenerate(clipVolume,
/// selection, displaySet, sectionStyle, out minX, out maxX, out minY, out maxY, out cancelled,
/// caption) needs the standard Section_Elev display set and the standard Section2d style; a null
/// display set silently yields a null result. The result is an AEC_2D_SECTION (Section2d : Geo)
/// sitting at the WCS origin on ACA's G-Elev / G-Sect layer key; it is a live, regenerable ACA
/// object, not exploded linework.
///
/// DEFAULTS COME FROM ACA, NOT FROM CONSTANTS. SetToStandard gives the line a unit-aware depth,
/// height and lower extension (120 / 120 / 12 in an imperial drawing); the tool only overrides the
/// ones the caller actually passed. Label text defaults to TEXTSIZE scaled by DIMSCALE (or the
/// annotation scale), the same convention EntityTools uses, never a raw-unit constant.
/// </summary>
internal static class AcaSectionTools
{
    internal static void Register()
    {
        Reg("create_elevation",
            "Create an AEC building ELEVATION line and (by default) generate its 2D elevation, as one "
            + "undoable step. ALL POINTS ARE WCS — the current UCS is ignored and pinned to World "
            + "while ACA works, so the line always lies in the world XY plane. Give start [x,y] and "
            + "end [x,y] (or points: an array of 2+ [x,y] for a jogged line; Z is flattened to the "
            + "first point's Z). The line looks to the LEFT of its travel direction, so draw it with "
            + "the wall on your left (north wall W->E, east wall N->S, south wall E->W, west wall "
            + "S->N); reverse (bool) flips the side. depth is how far it looks (default ACA's "
            + "standard: 120 in imperial, 3000 mm metric); height (omit to use the model extents), "
            + "lowerExtension (default ACA's), name. generate (default true) runs ACA's generator "
            + "over include (array of handles) or, by default, every AEC object, 3D solid and block "
            + "in the target space (space: model|current, default model) except spaces "
            + "(includeSpaces) and other section/elevation objects; excludeLayers narrows that. "
            + "placeAt [x,y] moves the 2D result so its lower-left corner lands there (WCS); label "
            + "adds centred text under it (labelHeight, default TEXTSIZE x DIMSCALE; labelLayer, "
            + "must exist) — placeAt/label/labelHeight/labelLayer apply only when generate is true. "
            + "Returns the line (with the side it looks to, per segment for jogged lines), the "
            + "AEC_2D_SECTION handle, its extents and the UCS state that was pinned.",
            mutating: true, CreateElevation);

        Reg("create_section",
            "Create an AEC building SECTION line and (by default) generate its 2D section, as one "
            + "undoable step. Same arguments and WCS guarantees as create_elevation: start/end or "
            + "points (WCS, flattened to one plane), reverse, depth, height, lowerExtension, name, "
            + "generate, include, includeSpaces, excludeLayers, space, and — only when generate is "
            + "true — placeAt, label, labelHeight, labelLayer. The section cuts along the line and "
            + "looks to the LEFT of its travel direction.",
            mutating: true, CreateSection);
    }

    private static void Reg(string name, string description, bool mutating, ToolHandler impl) =>
        Router.Register(name, description, mutating, args =>
        {
            AcaSupport.Require();
            return impl(args);
        });

    // ---------------------------------------------------------------- argument helpers (ACA-free)

    private static bool HasArray(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Array;

    /// <summary>Present and not null. A present value of the wrong type is then rejected by Args.*
    /// with bad_argument instead of being silently treated as omitted.</summary>
    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v)
        && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>
    /// The line's path in WCS: 'points' (2+ of [x,y] / [x,y,z]) or start + end. Consecutive
    /// duplicates are dropped and every Z is flattened to the first point's Z, because a building
    /// elevation/section line must lie in one horizontal plane.
    /// </summary>
    private static (List<Point3d> Points, bool Flattened) ReadPath(JsonElement a)
    {
        var raw = new List<Point3d>();

        if (HasArray(a, "points"))
        {
            foreach (var item in a.GetProperty("points").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                    throw new AcadMcpException("bad_argument", "Each entry of 'points' must be an array like [x, y] or [x, y, z].");

                var nums = item.EnumerateArray()
                               .Where(x => x.ValueKind == JsonValueKind.Number)
                               .Select(x => x.GetDouble())
                               .ToArray();
                if (nums.Length is < 2 or > 3)
                    throw new AcadMcpException("bad_argument", $"Each entry of 'points' must have 2 or 3 numbers; got {nums.Length}.");

                raw.Add(new Point3d(nums[0], nums[1], nums.Length > 2 ? nums[2] : 0.0));
            }
        }
        else
        {
            raw.Add(Args.Point(a, "start"));
            raw.Add(Args.Point(a, "end"));
        }

        if (raw.Count < 2)
            throw new AcadMcpException("bad_argument", "Give at least two points (start/end or points).");

        var z0 = raw[0].Z;
        var flattened = false;
        var pts = new List<Point3d>(raw.Count);
        foreach (var p in raw)
        {
            if (Math.Abs(p.Z - z0) > 1e-9) flattened = true;
            var q = new Point3d(p.X, p.Y, z0);
            if (pts.Count > 0 && pts[^1].DistanceTo(q) < 1e-9) continue;
            pts.Add(q);
        }

        if (pts.Count < 2)
            throw new AcadMcpException("bad_argument", "The line needs two distinct points.");

        return (pts, flattened);
    }

    /// <summary>
    /// Default label height: the drawing's plotted text height (TEXTSIZE, the EntityTools
    /// convention) scaled to model space by DIMSCALE, or by 1/CANNOSCALE when DIMSCALE is 0
    /// ("use annotation scale"). Never a raw-unit constant. E.g. 0.09375 x 96 = 9 in.
    /// </summary>
    private static double DefaultLabelHeight(Database db)
    {
        double paper;
        try { paper = db.Textsize > 0 ? db.Textsize : 2.5; }
        catch (Exception) { paper = 2.5; }

        double scale = 1.0;
        try
        {
            if (db.Dimscale > 0)
                scale = db.Dimscale;
            else if (db.Cannoscale is { } cs && cs.Scale > 0)
                scale = 1.0 / cs.Scale;
        }
        catch (Exception) { scale = 1.0; }

        return paper * scale;
    }

    private static void RequireLayer(AcadContext ctx, string layer, string argName)
    {
        var lt = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!lt.Has(layer))
            throw new AcadMcpException("layer_not_found",
                $"No layer named '{layer}' in this drawing.",
                $"Create the layer first, or omit '{argName}' to put the label on the 2D result's layer.");
    }

    private static double[] Arr(Point3d p) => [p.X, p.Y, p.Z];
    private static double[] Arr(Vector3d v) => [v.X, v.Y, v.Z];

#if ACA

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateElevation(JsonElement a) =>
        Acad.Write<object?>(ctx => CreateCore(ctx, a, isSection: false));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateSection(JsonElement a) =>
        Acad.Write<object?>(ctx => CreateCore(ctx, a, isSection: true));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? CreateCore(AcadContext ctx, JsonElement a, bool isSection)
    {
        var db = ctx.Db;
        var tr = ctx.Tr;
        var ed = ctx.Ed;

        // ---- every argument is read and validated BEFORE anything is created, so a bad value
        //      fails cheaply instead of after ACA has spent seconds generating -------------------
        var (pts, flattened) = ReadPath(a);

        var hasDepth = Has(a, "depth");
        var depth = hasDepth ? Args.Dbl(a, "depth") : 0.0;
        if (hasDepth && depth <= 0)
            throw new AcadMcpException("bad_argument", "'depth' must be positive (omit it to use ACA's standard depth).");

        var hasHeight = Has(a, "height");
        var height = hasHeight ? Args.Dbl(a, "height") : 0.0;
        if (hasHeight && height <= 0)
            throw new AcadMcpException("bad_argument", "'height' must be positive (omit it to use the model extents).");

        var hasLower = Has(a, "lowerExtension");
        var lower = hasLower ? Args.Dbl(a, "lowerExtension") : 0.0;
        if (hasLower && lower < 0)
            throw new AcadMcpException("bad_argument", "'lowerExtension' cannot be negative.");

        var reverse = Args.BoolOpt(a, "reverse", false);
        var name = Args.StrOpt(a, "name");
        var generate = Args.BoolOpt(a, "generate", true);
        var target = Args.StrOpt(a, "space", "model") ?? "model";

        Point3d? placeAt = Has(a, "placeAt") ? Args.Point(a, "placeAt") : null;
        var label = Args.StrOpt(a, "label");
        var hasLabelHeight = Has(a, "labelHeight");
        var labelHeight = hasLabelHeight ? Args.Dbl(a, "labelHeight") : 0.0;
        if (hasLabelHeight && labelHeight <= 0)
            throw new AcadMcpException("bad_argument", "'labelHeight' must be positive.");
        var labelLayer = Args.StrOpt(a, "labelLayer");
        if (!string.IsNullOrWhiteSpace(labelLayer))
            RequireLayer(ctx, labelLayer, "labelLayer");

        var spaceId = target.Equals("current", StringComparison.OrdinalIgnoreCase)
            ? db.CurrentSpaceId
            : SymbolUtilityServices.GetBlockModelSpaceId(db);
        var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForWrite);

        // ---- pin the UCS to World for everything below -------------------------------------
        var savedUcs = ed.CurrentUserCoordinateSystem;
        var ucsWasWorld = savedUcs.IsEqualTo(Matrix3d.Identity);
        if (!ucsWasWorld) ed.CurrentUserCoordinateSystem = Matrix3d.Identity;

        try
        {
            // ---- the line ------------------------------------------------------------------
            AecClipVolume line = isSection ? new AecSectionLine() : new AecElevationLine();
            line.SetDatabaseDefaults(db);
            line.SetToStandard(db);          // unit-aware depth / height / lower extension
            line.SetDefaultLayer();
            btr.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            var coll = new Point3dCollection();
            foreach (var p in pts) coll.Add(p);
            // WCS XY plane through the first vertex. NEVER the UCS plane: ACA rejects a plane that
            // does not contain the (WCS) points, and a tilted plane that does would tilt the view.
            line.SetVertices(coll, new Plane(pts[0], Vector3d.ZAxis));
            if (reverse) line.Reverse();

            if (hasDepth) line.LengthA = depth;        // else keep ACA's standard
            if (hasHeight)
            {
                line.UseModelExtentsForHeight = false;
                line.Height = height;
            }
            else
            {
                line.UseModelExtentsForHeight = true;
            }
            if (hasLower) line.LowerExtension = lower;
            if (!string.IsNullOrWhiteSpace(name)) line.Name = name;

            // The side each segment looks to = left of its travel, from the EFFECTIVE vertex order
            // (Reverse() reorders Vertices but leaves YDir untouched, so YDir would lie here).
            var verts = line.Vertices;
            var viewDirs = new List<double[]>(Math.Max(0, verts.Count - 1));
            for (var i = 0; i + 1 < verts.Count; i++)
                viewDirs.Add(Arr(Vector3d.ZAxis.CrossProduct(verts[i + 1] - verts[i]).GetNormal()));

            var lineInfo = new
            {
                handle = line.Handle.ToString(),
                kind = isSection ? "BuildingSectionLine" : "BuildingElevationLine",
                name = line.Name,
                layer = line.Layer,
                start = Arr(line.StartPoint),
                end = Arr(line.EndPoint),
                vertices = verts.Count,
                viewDirection = viewDirs.Count > 0 ? viewDirs[0] : null,   // first segment
                viewDirections = viewDirs.Count > 1 ? viewDirs : null,      // per segment, jogged lines
                jogged = viewDirs.Count > 1,
                depth = line.LengthA,
                height = line.Height,
                useModelExtentsForHeight = line.UseModelExtentsForHeight,
                lowerExtension = line.LowerExtension,
            };

            if (!generate)
            {
                var ignored = new[] { "placeAt", "label", "labelHeight", "labelLayer" }
                    .Where(k => Has(a, k))
                    .ToArray();
                return new
                {
                    line = lineInfo,
                    generated = false,
                    ignored = ignored.Length > 0 ? ignored : null,   // only meaningful with generate:true
                    flattenedZ = flattened,
                    ucs = new { wasWorld = ucsWasWorld, pinnedToWorld = !ucsWasWorld },
                };
            }

            // ---- generation ----------------------------------------------------------------
            var setId = AecDisplaySetDict.GetStandardSectionElevId(db);
            if (setId.IsNull)
            {
                // Aec Dictionary.GetAt throws eKeyNotFound on a missing key (measured); Has() does not.
                var dict = new AecDisplaySetDict(db, false);
                var stdName = AecDisplaySetDict.StandardSectionElevName;   // static on the type
                if (dict.Has(stdName, tr)) setId = dict.GetAt(stdName);
            }
            if (setId.IsNull)
                throw new AcadMcpException("display_set_missing",
                    "The drawing has no standard Section_Elev display set, so ACA cannot generate a 2D result.",
                    "Start from an AutoCAD Architecture template, or import the display set from one.");

            var styleId = AecSection2dStyleDict.GetStandardStyle(db);
            if (styleId.IsNull)
                throw new AcadMcpException("style_missing", "The drawing has no standard 2D Section/Elevation style.");

            var (selection, selectionNote) = BuildSelection(ctx, a, btr, line.ObjectId);
            if (selection.Count == 0)
                throw new AcadMcpException("nothing_to_section",
                    "No objects to generate from.",
                    "Pass include (handles) or draw the line in a space that contains AEC objects, solids or blocks.");

            double minX, maxX, minY, maxY;
            bool cancelled;
            var caption = isSection ? "Nodal section" : "Nodal elevation";
            var resultId = AecSectionUtilities.AutoGenerate(line.ObjectId, selection, setId, styleId,
                out minX, out maxX, out minY, out maxY, out cancelled, caption);

            if (cancelled)
                throw new AcadMcpException("generation_cancelled", "ACA reported the generation as cancelled.");
            if (resultId.IsNull)
                throw new AcadMcpException("generation_failed",
                    "ACA produced no 2D result for this line.",
                    $"Nothing in the selection ({selection.Count} objects) fell inside the clip volume? "
                    + $"depth={line.LengthA}, height={(hasHeight ? height.ToString() : "model extents")}. "
                    + "Check the line looks toward the objects (left of its travel) — try reverse:true.");

            var section = (AecSection2d)tr.GetObject(resultId, OpenMode.ForWrite);
            var ext = section.GeometricExtents;

            // ---- placement (WCS displacement only) -----------------------------------------
            double[]? placedAt = null;
            if (placeAt is { } at)
            {
                section.TransformBy(Matrix3d.Displacement(
                    new Vector3d(at.X - ext.MinPoint.X, at.Y - ext.MinPoint.Y, 0.0)));
                ext = section.GeometricExtents;
                placedAt = [at.X, at.Y];
            }

            var width = ext.MaxPoint.X - ext.MinPoint.X;
            var tall = ext.MaxPoint.Y - ext.MinPoint.Y;

            // ---- label ---------------------------------------------------------------------
            string? labelHandle = null;
            double? labelHeightUsed = null;
            if (!string.IsNullOrWhiteSpace(label))
            {
                var textHeight = hasLabelHeight ? labelHeight : DefaultLabelHeight(db);

                var text = new DBText();
                text.SetDatabaseDefaults(db);
                text.Layer = string.IsNullOrWhiteSpace(labelLayer) ? section.Layer : labelLayer;
                text.Height = textHeight;
                text.TextString = label;
                text.HorizontalMode = TextHorizontalMode.TextCenter;
                text.VerticalMode = TextVerticalMode.TextTop;
                text.AlignmentPoint = new Point3d(ext.MinPoint.X + width / 2.0, ext.MinPoint.Y - textHeight * 1.5, ext.MinPoint.Z);
                btr.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                labelHandle = text.Handle.ToString();
                labelHeightUsed = textHeight;
            }

            return new
            {
                line = lineInfo,
                generated = true,
                section = new
                {
                    handle = section.Handle.ToString(),
                    dxfName = resultId.ObjectClass.DxfName,
                    layer = section.Layer,
                    min = Arr(ext.MinPoint),
                    max = Arr(ext.MaxPoint),
                    width,
                    height = tall,
                    placedAt,
                },
                generation = new { minX, maxX, minY, maxY, selection = selection.Count, selectionNote },
                label = labelHandle is null ? null : new { handle = labelHandle, height = labelHeightUsed },
                flattenedZ = flattened,
                ucs = new { wasWorld = ucsWasWorld, pinnedToWorld = !ucsWasWorld },
            };
        }
        finally
        {
            if (!ucsWasWorld) ed.CurrentUserCoordinateSystem = savedUcs;
        }
    }

    /// <summary>
    /// What ACA generates from. Explicit 'include' handles win; otherwise every AEC object, 3D solid
    /// and block reference in the target space, minus spaces (unless includeSpaces), minus other
    /// clip volumes / 2D results, minus anything on 'excludeLayers'. The line itself is never included.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ObjectIdCollection Ids, string Note) BuildSelection(AcadContext ctx, JsonElement a, BlockTableRecord btr, ObjectId lineId)
    {
        var tr = ctx.Tr;
        var ids = new ObjectIdCollection();

        var include = Args.StrArrayOpt(a, "include");
        if (include.Length > 0)
        {
            foreach (var h in include)
            {
                var id = Acad.ToObjectId(ctx.Db, h);
                if (id == lineId) continue;
                ids.Add(id);
            }
            return (ids, $"explicit include list ({include.Length} handles)");
        }

        var includeSpaces = Args.BoolOpt(a, "includeSpaces", false);
        var excluded = new HashSet<string>(Args.StrArrayOpt(a, "excludeLayers"), StringComparer.OrdinalIgnoreCase);

        int aec = 0, solids = 0, blocks = 0, skipped = 0;
        foreach (ObjectId id in btr)
        {
            if (id == lineId) continue;
            var obj = tr.GetObject(id, OpenMode.ForRead);
            if (obj is not Entity ent || ent.IsErased) continue;
            if (excluded.Contains(ent.Layer)) { skipped++; continue; }

            // Order matters: clip volumes and 2D results are themselves Geo, so they must be
            // rejected before the general AecGeo case admits them.
            switch (ent)
            {
                case AecClipVolume or AecClipVolumeResult or AecSection2d:
                    skipped++;
                    continue;
                case AecSpace when !includeSpaces:
                    skipped++;
                    continue;
                case AecGeo:
                    aec++;
                    break;
                case Solid3d:
                    solids++;
                    break;
                case BlockReference:
                    blocks++;
                    break;
                default:
                    continue;
            }

            ids.Add(id);
        }

        return (ids, $"auto: {aec} AEC objects, {solids} solids, {blocks} blocks; skipped {skipped}");
    }

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> CreateElevation(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateSection(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
