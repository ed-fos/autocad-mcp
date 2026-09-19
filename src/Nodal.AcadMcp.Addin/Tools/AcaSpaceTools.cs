using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are referenced ONLY through explicit aliases, and only inside #if ACA.
// Same reasoning as AcaTools.cs: 'Entity', 'DBObject', 'Dictionary', 'Profile' and 'Segment2d' all
// collide across Autodesk.Aec.*, Autodesk.AutoCAD.* and System, and on a vanilla AutoCAD box these
// assemblies do not exist, so nothing outside the guarded region may name them.
#if ACA
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;

using AecProfile = Autodesk.Aec.Geometry.Profile;
using AecRing = Autodesk.Aec.Geometry.Ring;
using AecSegment2d = Autodesk.Aec.Geometry.Segment2d;

using AecSpace = Autodesk.Aec.Arch.DatabaseServices.Space;
using AecSpaceStyle = Autodesk.Aec.Arch.DatabaseServices.SpaceStyle;
using AecSpaceStyleDict = Autodesk.Aec.Arch.DatabaseServices.DictionarySpaceStyle;
using AecSpaceProfileType = Autodesk.Aec.Arch.DatabaseServices.SpaceProfileType;
using AecSpaceGeometryType = Autodesk.Aec.Arch.DatabaseServices.SpaceGeometryType;
using AecSpaceJustification = Autodesk.Aec.Arch.DatabaseServices.SpaceJustificationType;
using AecSpaceBoundaryOffset = Autodesk.Aec.Arch.DatabaseServices.SpaceBoundaryOffsetType;
using AecSpaceZType = Autodesk.Aec.Arch.DatabaseServices.SpaceZType;
using AecSpaceDefect = Autodesk.Aec.Arch.DatabaseServices.SpaceDefectType;
using AecSpaceSurface = Autodesk.Aec.Arch.DatabaseServices.SpaceSurface;
using AecSpaceSurfaceFreeForm = Autodesk.Aec.Arch.DatabaseServices.SpaceSurfaceFreeForm;

using AecPropServices = Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices;
using AecPropSet = Autodesk.Aec.PropertyData.DatabaseServices.PropertySet;
using AecPropSetDef = Autodesk.Aec.PropertyData.DatabaseServices.PropertySetDefinition;
using AecPropDef = Autodesk.Aec.PropertyData.DatabaseServices.PropertyDefinition;
using AecPropSetData = Autodesk.Aec.PropertyData.DatabaseServices.PropertySetData;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture SPACES: styles, creation from a boundary, property editing and deep reads.
///
/// JIT SAFETY. Identical contract to AcaTools: every handler is wrapped by <see cref="Reg"/>, which
/// calls <see cref="AcaSupport.Require"/> before touching the implementation. Implementations that
/// name Aec types are separate [MethodImpl(NoInlining)] methods whose signatures are
/// (JsonElement) -> Task&lt;object?&gt;, so the JIT never resolves an Aec type on a vanilla box.
///
/// HOW A SPACE GETS ITS GEOMETRY (the part the brief flagged as risky) — confirmed from the
/// declared API surface of AecArchMgd/AecBaseMgd:
///   * Autodesk.Aec.Arch.DatabaseServices.Space : CellLayoutTool : LayoutTool : Geo :
///     Autodesk.Aec.DatabaseServices.Entity : Autodesk.AutoCAD.DatabaseServices.Curve.
///   * Space exposes 'Profile BaseProfile { get; set; }' plus
///     'Void SetProfile(SpaceProfileType, Profile)' and 'Void SetBaseProfileDirty()'.
///   * Autodesk.Aec.Geometry.Profile has a public parameterless ctor, 'Void AddRing(Ring)',
///     'Void ConsistentlyOrientRings()', 'Boolean IsValidProfile(Boolean)',
///     'Void TranslateBy(Vector2d)', 'Double Area(Double)' and 'Double Perimeter(Double)'.
///   * Autodesk.Aec.Geometry.Ring has '.ctor()' and (via CompoundCurve2d)
///     'Void AddSegment(Segment2d, Boolean)'; Segment2d has '.ctor(Point2d, Point2d)' and
///     '.ctor(Curve2d)', so straight AND arc edges are both expressible.
///   * A valid style comes from 'DictionarySpaceStyle(Database)' — 'GetAt(String)',
///     'Has(String, Transaction)', 'Records', and the static 'GetStandardStyle(Database)'.
///   * Aec Entity declares 'Void SetToStandard(Database db)' and a settable 'ObjectId StyleId',
///     which is the ACA half of the creation sequence on top of the standard AutoCAD
///     SetDatabaseDefaults / AppendEntity / AddNewlyCreatedDBObject.
/// The profile is in the Space's ECS. Placement therefore uses Geo's settable Normal / Rotation /
/// Location and expresses the profile RELATIVE to Location; reads convert back with
/// Location + XDir*x + YDir*y, all of which are confirmed members.
///
/// DELIBERATE OMISSIONS (API present but not offered, or absent):
///   * 'Space.Length' and 'Space.Width' are GET-ONLY — there is no setter and no Set* method, so
///     set_space_properties cannot size a space; change its boundary instead.
///   * 'Aec Entity.GetBaseProfile(Matrix3d&amp; toWcs)' and 'Space.GetCutProfile' are not used: the
///     declared ByRef signature does not distinguish 'out' from 'ref', so calling them
///     would be a coin flip. The by-value 'Space.BaseProfile' / 'GetProfile(SpaceProfileType)'
///     getters carry the same information.
///   * 'CompoundCurve2d.AddSegmentsFrom(Polyline, Boolean)' is not used: acdbmgd publishes TWO
///     public 'Polyline' classes (DatabaseServices.Polyline and a GraphicsInterface one) and the
///     the declared API surface strips namespaces, so the parameter type is ambiguous. Polylines are decomposed with
///     GetSegmentType / GetLineSegment2dAt / GetArcSegment2dAt instead, which is unambiguous.
///   * 'SubSetDatabaseDefaults(Database)' IS public — but only on ImpObject and on
///     Autodesk.Aec.DatabaseServices.DBObject (the branch DictionaryRecord/styles live on). It is
///     NOT on the branch a Space lives on (Space -> CellLayoutTool -> LayoutTool -> Geo ->
///     Aec Entity -> acdb Curve -> acdb Entity), so this creation sequence cannot call it.
///     'SetToStandard(Database)' IS declared on Aec Entity, and is what is used instead.
/// Reminder for anyone re-checking these signatures: they list DECLARED public members only, so an
/// inherited member (Space.Description from Aec Entity, SpaceStyle.Description from Aec DBObject,
/// Space.Layer/Handle from acdbmgd) will look absent on the derived class. Walk the base chain
/// before deleting a member call as "non-existent".
///
/// THE ONE THING THE DECLARED API SURFACE CANNOT CONFIRM is the ORDER of the creation calls, and whether
/// BaseProfile is interpreted in the Space's ECS (what this module assumes) or in world XY. Every
/// individual call is verified; the sequencing is the inference. Two things exist to make that cheap
/// to check: create_space's dryRun validates a boundary and reports its area/perimeter without
/// writing anything, and a real create reads Location, Area and CentroidWCS back in the same
/// response. If a created space lands offset by exactly the base point, the ECS assumption is wrong
/// and the fix is to stop translating the profile in CreateSpaceCore.
/// </summary>
internal static class AcaSpaceTools
{
    internal static void Register()
    {
        Reg("list_space_styles",
            "Every AEC SpaceStyle in the drawing: name, description, keynote, target/min/max area, "
            + "length and width, ceiling height, category and the net/usable/gross offsets. "
            + "Args: includeDetail (default true), limit (default 500), offset.",
            mutating: false, ListSpaceStyles);

        Reg("create_space",
            "Create an AEC Space from a boundary, as one undoable step. Give EITHER points (an array "
            + "of [x,y] or [x,y,z], 3 or more, outer boundary) OR polylineHandle (a closed LWPOLYLINE "
            + "— arcs/bulges are preserved — or a CIRCLE). Optional holes: an array of point arrays, "
            + "each becoming a void ring. Optional: style (SpaceStyle name; defaults to the drawing's "
            + "standard style), name, description, layer, elevation, location ([x,y,z] ECS origin), "
            + "ceilingHeight, floorThickness, ceilingThickness, overallHeight, spaceAboveCeiling, "
            + "spaceBelowFloor, geometryType (TwoD|Extrusion, default Extrusion — FreeForm needs a "
            + "modeller body and is refused at creation), "
            + "justification (TopOfFloor|BottomOfFloor|SpaceBelowFloor), space (model|current), and "
            + "dryRun (validate the boundary and report its area/perimeter without writing anything).",
            mutating: true, CreateSpace);

        Reg("set_space_properties",
            "Edit one AEC Space by handle, as one undoable step. Args: handle plus any of name, "
            + "description, style (SpaceStyle name), ceilingHeight, floorThickness, ceilingThickness, "
            + "overallHeight, spaceAboveCeiling, spaceBelowFloor, surfaceHeightDefault, "
            + "calculationPlane1Height, calculationPlane2Height, justification, geometryType, "
            + "boundaryOffsetType (ByStyle|ByRule|Manual), drawHatch, associative. Every field is "
            + "optional; the result reports the old and new value of each one actually changed. "
            + "Length and Width are read-only in the ACA API and cannot be set here.",
            mutating: true, SetSpaceProperties);

        Reg("get_space",
            "One AEC Space in full by handle: identity and style, Name, Area, Perimeter, BaseVolume "
            + "and the other volumes, ceiling/floor heights and thicknesses, justification, geometry "
            + "type, centroid, per-profile calculated areas and perimeters (base/net/usable/gross and "
            + "more), wall areas, the boundary ring vertices in both ECS and WCS, bounding object and "
            + "bounding opening handles, surfaces, defects and attached property sets. "
            + "Args: handle, includeSurfaces (default true), includePropertySets (default true), "
            + "includeBoundary (default true).",
            mutating: false, GetSpace);

        Reg("list_spaces_detailed",
            "Richer than list_spaces: every AEC Space with its style, name, all computed areas and "
            + "perimeters per SpaceProfileType, volumes, heights, centroid, boundary/opening handles "
            + "and every attached property set with live values. Args: scope (model|current|all, "
            + "default model), deepScan, includePropertySets (default true), includeAllProfiles "
            + "(default false — base/net/usable/gross only), includeSurfaces (default false), "
            + "nameFilter (case-insensitive substring on Name), styleFilter (exact style name), "
            + "limit (default 500), offset.",
            mutating: false, ListSpacesDetailed);
    }

    /// <summary>
    /// Gate every AEC tool behind the ACA probe. The registered lambda names no Aec type, so it is
    /// safe to JIT on vanilla AutoCAD; <paramref name="impl"/> is only entered once Require() passed.
    /// </summary>
    private static void Reg(string name, string description, bool mutating, ToolHandler impl) =>
        Router.Register(name, description, mutating, args =>
        {
            AcaSupport.Require();
            return impl(args);
        });

    // ---------------------------------------------------------------- argument helpers (ACA-free)

    private static (int Offset, int Limit) Paging(JsonElement a)
    {
        var limit = Args.IntOpt(a, "limit", 500);
        var offset = Args.IntOpt(a, "offset", 0);
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;
        if (offset < 0) offset = 0;
        return (offset, limit);
    }

    private static string Scope(JsonElement a) => Args.StrOpt(a, "scope", "model") ?? "model";

    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    /// <summary>Sort by a typed key, then project — anonymous types cannot be ordered by a lambda key.</summary>
    private static object Page(List<(string Key, object Data)> rows, int offset, int limit)
    {
        var ordered = rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                          .Select(r => r.Data)
                          .ToList();
        var slice = ordered.Skip(offset).Take(limit).ToList();
        return new
        {
            items = slice,
            total = ordered.Count,
            offset,
            limit,
            truncated = offset + slice.Count < ordered.Count,
        };
    }

    /// <summary>A JSON array of [x,y] / [x,y,z] entries -> a 2D ring, plus the first z seen.</summary>
    private static List<Point2d> ReadRing(JsonElement arr, string label, out double elevation)
    {
        elevation = 0.0;
        var pts = new List<Point2d>();
        var sawZ = false;

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array)
                throw new AcadMcpException("bad_argument",
                    $"Every entry of '{label}' must be an array like [x, y] or [x, y, z].");

            var nums = entry.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.Number)
                            .Select(x => x.GetDouble())
                            .ToArray();

            if (nums.Length is < 2 or > 3)
                throw new AcadMcpException("bad_argument",
                    $"Every entry of '{label}' must have 2 or 3 numbers; got {nums.Length}.");

            pts.Add(new Point2d(nums[0], nums[1]));
            if (nums.Length > 2 && !sawZ) { elevation = nums[2]; sawZ = true; }
        }

        // A caller may or may not repeat the first point to close the loop; both are accepted.
        if (pts.Count > 1 && pts[0].GetDistanceTo(pts[^1]) < 1e-9)
            pts.RemoveAt(pts.Count - 1);

        if (pts.Count < 3)
            throw new AcadMcpException("bad_argument",
                $"'{label}' needs at least 3 distinct points to bound a space; got {pts.Count}.");

        return pts;
    }

    private static List<List<Point2d>> ReadHoles(JsonElement a)
    {
        var holes = new List<List<Point2d>>();
        if (!Has(a, "holes")) return holes;

        var raw = a.GetProperty("holes");
        if (raw.ValueKind != JsonValueKind.Array)
            throw new AcadMcpException("bad_argument", "'holes' must be an array of point arrays.");

        var index = 0;
        foreach (var hole in raw.EnumerateArray())
        {
            if (hole.ValueKind != JsonValueKind.Array)
                throw new AcadMcpException("bad_argument", "Every entry of 'holes' must be a point array.");
            holes.Add(ReadRing(hole, $"holes[{index}]", out _));
            index++;
        }

        return holes;
    }

#if ACA

    // ------------------------------------------------------------------------- guarded accessors
    // AEC property getters throw for degenerate geometry (Area on a space with a broken boundary,
    // a profile type the space does not carry, a style lookup across a broken xref). One bad
    // property must never lose a whole listing, and the bridge drops nulls
    // (DefaultIgnoreCondition = WhenWritingNull), so a failed read simply vanishes from the JSON.

    private static string? TryS(Func<string?> f) { try { return f(); } catch { return null; } }

    private static double? TryD(Func<double> f)
    {
        try
        {
            var v = f();
            return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
        }
        catch { return null; }
    }

    private static int? TryI(Func<int> f) { try { return f(); } catch { return null; } }

    private static bool? TryB(Func<bool> f) { try { return f(); } catch { return null; } }

    private static double[]? TryPt(Func<Point3d> f)
    {
        try { var p = f(); return new[] { p.X, p.Y, p.Z }; }
        catch { return null; }
    }

    private static object? TryO(Func<object?> f) { try { return f(); } catch { return null; } }

    private static object? Scalar(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        double d => double.IsNaN(d) || double.IsInfinity(d) ? (object)d.ToString() : d,
        float f => (double)f,
        int i => i,
        long l => l,
        short sh => (int)sh,
        byte by => (int)by,
        decimal m => (double)m,
        _ => TryS(() => value.ToString()),
    };

    private static List<string> Handles(ObjectIdCollection? ids)
    {
        var list = new List<string>();
        if (ids is null) return list;
        foreach (ObjectId id in ids)
        {
            if (id.IsNull) continue;
            try { list.Add(id.Handle.ToString()); } catch { }
        }
        return list;
    }

    /// <summary>
    /// ACA declares its OWN ObjectIdCollection in Autodesk.Aec.DatabaseServices, distinct from
    /// AutoCAD's. Space.GetBoundary() / FindBoundingOpenings() / SpaceModifiers return that one,
    /// so it needs its own overload rather than a cast.
    /// </summary>
    private static List<string> Handles(Autodesk.Aec.DatabaseServices.ObjectIdCollection? ids)
    {
        var list = new List<string>();
        if (ids is null) return list;
        foreach (ObjectId id in ids)
        {
            if (id.IsNull) continue;
            try { list.Add(id.Handle.ToString()); } catch { }
        }
        return list;
    }

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    private static List<ObjectId> TargetSpaces(AcadContext ctx, string scope)
    {
        var spaces = new List<ObjectId>();

        if (scope.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            spaces.Add(ctx.Db.CurrentSpaceId);
            return spaces;
        }

        if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                try
                {
                    var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout) spaces.Add(id);
                }
                catch { }
            }
            return spaces;
        }

        spaces.Add(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db));
        return spaces;
    }

    /// <summary>Cheap pass: collect Space ObjectIds without building a payload for each one.</summary>
    private static List<ObjectId> ScanSpaces(AcadContext ctx, string scope, bool deep)
    {
        var found = new List<ObjectId>();

        foreach (var spaceId in TargetSpaces(ctx, scope))
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead); }
            catch { continue; }

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                if (!deep && !LooksAec(id)) continue;

                try
                {
                    if (ctx.Tr.GetObject(id, OpenMode.ForRead) is AecSpace) found.Add(id);
                }
                catch { }
            }
        }

        return found;
    }

    // ---------------------------------------------------------------------------- style plumbing

    private static string? RecordName(AcadContext ctx, ObjectId id)
    {
        try
        {
            if (id.IsNull) return null;
            return (ctx.Tr.GetObject(id, OpenMode.ForRead) as AecRecord)?.Name;
        }
        catch { return null; }
    }

    /// <summary>Resolve a SpaceStyle by name, or the drawing's Standard style when name is null.</summary>
    private static ObjectId StyleId(AcadContext ctx, string? name)
    {
        AecSpaceStyleDict dict;
        try { dict = new AecSpaceStyleDict(ctx.Db); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                "This drawing has no space style dictionary.", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            ObjectId standard;
            try { standard = AecSpaceStyleDict.GetStandardStyle(ctx.Db); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("no_standard_style",
                    "Could not resolve the drawing's standard space style.", ex.Message);
            }

            if (standard.IsNull)
                throw new AcadMcpException("no_standard_style",
                    "The drawing's standard space style resolved to null.",
                    "Pass 'style' with a name from list_space_styles.");

            return standard;
        }

        bool has;
        try { has = dict.Has(name, ctx.Tr); }
        catch { has = false; }

        if (!has)
            throw new AcadMcpException("style_not_found",
                $"No space style named '{name}' in this drawing.",
                "Call list_space_styles for the available names.");

        var id = dict.GetAt(name);
        if (id.IsNull)
            throw new AcadMcpException("style_not_found", $"Space style '{name}' resolved to null.");

        return id;
    }

    // --------------------------------------------------------------------------- profile building

    /// <summary>
    /// Points (already de-duplicated and open) -> a Ring of straight segments, closed by an explicit
    /// last-to-first segment. Ring has no "close" flag; the closing edge has to be added by hand.
    /// </summary>
    private static AecRing RingFromPoints(List<Point2d> pts, bool isVoid)
    {
        var ring = new AecRing();
        var added = 0;

        for (var i = 0; i < pts.Count; i++)
        {
            var from = pts[i];
            var to = pts[(i + 1) % pts.Count];
            if (from.GetDistanceTo(to) < 1e-9) continue;   // skip degenerate edges
            ring.AddSegment(new AecSegment2d(from, to), true);
            added++;
        }

        if (added < 3)
            throw new AcadMcpException("bad_boundary",
                $"Those points collapse to {added} distinct edges; a boundary ring needs 3 or more.");

        if (isVoid) ring.IsVoid = true;
        return ring;
    }

    /// <summary>
    /// A closed LWPOLYLINE -> a Ring, preserving arcs. Decomposed segment by segment via
    /// GetSegmentType / GetLineSegment2dAt / GetArcSegment2dAt, which return
    /// Autodesk.AutoCAD.Geometry.LineSegment2d and CircularArc2d — both Curve2d, which is exactly
    /// what Aec's Segment2d(Curve2d) constructor takes.
    /// </summary>
    private static AecRing RingFromPolyline(Polyline pl)
    {
        var vertexCount = pl.NumberOfVertices;
        if (vertexCount < 3)
            throw new AcadMcpException("bad_boundary",
                $"Polyline '{pl.Handle}' has only {vertexCount} vertices; a space boundary needs 3 or more.");

        // A polyline drawn on a tilted plane reports its vertices in its own OCS, and mapping that
        // back to the space's ECS is not something the declared API surface lets us confirm. Refuse instead of
        // silently producing a space in the wrong place.
        if (!pl.Normal.IsParallelTo(Vector3d.ZAxis))
            throw new AcadMcpException("bad_boundary",
                $"Polyline '{pl.Handle}' is not in a world-XY plane.",
                "Flatten the polyline (its normal must be parallel to the world Z axis) and retry.");

        var ring = new AecRing();
        var segmentCount = pl.Closed ? vertexCount : vertexCount - 1;
        var added = 0;

        for (var i = 0; i < segmentCount; i++)
        {
            SegmentType kind;
            try { kind = pl.GetSegmentType(i); }
            catch { continue; }

            try
            {
                if (kind == SegmentType.Arc)
                {
                    ring.AddSegment(new AecSegment2d(pl.GetArcSegment2dAt(i)), true);
                    added++;
                }
                else if (kind == SegmentType.Line)
                {
                    ring.AddSegment(new AecSegment2d(pl.GetLineSegment2dAt(i)), true);
                    added++;
                }
                // Coincident / Point / Empty are degenerate and simply dropped.
            }
            catch { }
        }

        // An open polyline still needs a closing edge, or the ring is not a boundary.
        if (!pl.Closed)
        {
            var first = pl.GetPoint2dAt(0);
            var last = pl.GetPoint2dAt(vertexCount - 1);
            if (last.GetDistanceTo(first) > 1e-9)
            {
                ring.AddSegment(new AecSegment2d(last, first), true);
                added++;
            }
        }

        if (added < 3)
            throw new AcadMcpException("bad_boundary",
                $"Polyline '{pl.Handle}' produced only {added} usable segments.");

        return ring;
    }

    private static void ValidateProfile(AecProfile profile, string label)
    {
        var ringCount = TryI(() => profile.RingCount) ?? 0;
        if (ringCount < 1)
            throw new AcadMcpException("bad_boundary", $"{label} produced a profile with no rings.");

        if (TryB(() => profile.IsSelfIntersecting) == true)
            throw new AcadMcpException("bad_boundary",
                $"{label} is self-intersecting.",
                "An AEC Space needs a simple, non-crossing boundary.");

        // IsValidProfile(true) runs ACA's own ring-level checks. Refusing here is the whole point: an
        // AEC object appended with an invalid profile is a corrupt object, so a validator that says
        // "no" AND a validator that throws are both treated as a refusal.
        var valid = TryB(() => profile.IsValidProfile(true));
        if (valid != true)
            throw new AcadMcpException("bad_boundary",
                $"{label} is not a valid AEC profile.",
                valid is null
                    ? "ACA's own profile validator could not evaluate it, so it is refused rather than "
                      + "risk appending a corrupt object."
                    : "Check for duplicate vertices, crossing edges, or void rings outside the outer ring.");
    }

    // -------------------------------------------------------------------------------- ECS -> WCS

    /// <summary>
    /// A profile point lives in the Space's ECS. Geo publishes Location, XDir and YDir, so the world
    /// point is Location + XDir*x + YDir*y — no ByRef matrix accessor needed.
    /// </summary>
    private static double[]? EcsToWcs(AecSpace s, Point2d p)
    {
        try
        {
            var world = s.Location.Add(s.XDir.MultiplyBy(p.X)).Add(s.YDir.MultiplyBy(p.Y));
            return new[] { world.X, world.Y, world.Z };
        }
        catch { return null; }
    }

    /// <summary>Ring vertices of a profile, as ECS pairs and WCS triples.</summary>
    private static List<object> ProfileRings(AecSpace s, AecProfile? profile, int maxVertices)
    {
        var rings = new List<object>();
        if (profile is null) return rings;

        var budget = maxVertices;

        try
        {
            foreach (AecRing ring in profile.Rings)
            {
                var ecs = new List<double[]>();
                var wcs = new List<double[]>();

                try
                {
                    foreach (AecSegment2d seg in ring.Segments)
                    {
                        if (budget-- <= 0) break;
                        Point2d start;
                        try { start = seg.StartPoint; }
                        catch { continue; }

                        ecs.Add(new[] { start.X, start.Y });
                        var world = EcsToWcs(s, start);
                        if (world is not null) wcs.Add(world);
                    }
                }
                catch { }

                rings.Add(new
                {
                    isVoid = TryB(() => ring.IsVoid),
                    isCircle = TryB(() => ring.IsCircle),
                    isValid = TryB(() => ring.IsValidRing),
                    segmentCount = TryI(() => ring.SegmentCount),
                    area = TryD(() => ring.Area),
                    length = TryD(() => ring.Length),
                    vertexCountReturned = ecs.Count,
                    verticesEcs = ecs,
                    verticesWcs = wcs,
                });

                if (budget <= 0) break;
            }
        }
        catch { }

        return rings;
    }

    // ------------------------------------------------------------------------------ property data

    private static List<object> PropertySetsOf(AcadContext ctx, DBObject obj)
    {
        var result = new List<object>();

        ObjectIdCollection ids;
        try { ids = AecPropServices.GetPropertySets(obj); }
        catch (System.Exception) { return result; }

        foreach (ObjectId id in ids)
        {
            if (id.IsNull) continue;

            AecPropSet set;
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not AecPropSet opened) continue;
                set = opened;
            }
            catch { continue; }

            var properties = new List<object>();
            AecPropSetDef? def = null;
            try { def = ctx.Tr.GetObject(set.PropertySetDefinition, OpenMode.ForRead) as AecPropSetDef; }
            catch { }

            if (def is not null)
            {
                try
                {
                    foreach (AecPropDef pd in def.Definitions)
                    {
                        var pid = TryI(() => pd.Id);
                        properties.Add(new
                        {
                            id = pid,
                            name = TryS(() => pd.Name),
                            value = Scalar(TryO(() => pid is null ? null : set.GetAt(pid.Value))),
                            type = TryS(() => pd.DataType.ToString()),
                            unitType = TryS(() => pd.UnitType.ToString()),
                            isReadOnly = TryB(() => pd.IsReadOnly),
                            automatic = TryB(() => pd.Automatic),
                        });
                    }
                }
                catch { }
            }
            else
            {
                // Definition unreadable (broken xref, purged definition): fall back to the set's own
                // raw data, which carries ids and values but no names.
                try
                {
                    foreach (AecPropSetData psd in set.PropertySetData)
                    {
                        var pid = TryI(() => psd.Id);
                        properties.Add(new
                        {
                            id = pid,
                            name = TryS(() => pid is null ? null : set.PropertyIdToName(pid.Value)),
                            value = Scalar(TryO(() => psd.GetData())),
                            type = TryS(() => psd.DataType.ToString()),
                            unitType = TryS(() => psd.UnitType.ToString()),
                        });
                    }
                }
                catch { }
            }

            result.Add(new
            {
                handle = TryS(() => set.Handle.ToString()),
                name = TryS(() => set.Name),
                definitionName = TryS(() => set.PropertySetDefinitionName),
                propertyCount = properties.Count,
                properties,
            });
        }

        return result;
    }

    // ------------------------------------------------------------------------------- space views

    /// <summary>
    /// Built on demand rather than held in a static field: a static field of an Aec enum type would
    /// give this class a type initialiser that names an Aec type, and the CLR is free to run that on
    /// type load — which is exactly the thing the ACA gate exists to prevent.
    /// </summary>
    private static AecSpaceProfileType[] ProfileSet(bool all) => all
        ? new[]
        {
            AecSpaceProfileType.Base,
            AecSpaceProfileType.Net,
            AecSpaceProfileType.Usable,
            AecSpaceProfileType.Gross,
            AecSpaceProfileType.Interior,
            AecSpaceProfileType.InteriorCeiling,
            AecSpaceProfileType.Floor,
            AecSpaceProfileType.Ceiling,
            AecSpaceProfileType.Centerline,
            AecSpaceProfileType.Baseline,
        }
        : new[]
        {
            AecSpaceProfileType.Base,
            AecSpaceProfileType.Net,
            AecSpaceProfileType.Usable,
            AecSpaceProfileType.Gross,
        };

    /// <summary>Calculated area and perimeter per SpaceProfileType. Nulls drop out of the JSON.</summary>
    private static List<object> CalculatedProfiles(AecSpace s, bool all)
    {
        var rows = new List<object>();

        foreach (var type in ProfileSet(all))
        {
            var captured = type;
            var area = TryD(() => s.GetCalculatedArea(captured));
            var perimeter = TryD(() => s.GetCalculatedPerimeter(captured));
            if (area is null && perimeter is null) continue;

            rows.Add(new
            {
                profileType = captured.ToString(),
                area,
                perimeter,
            });
        }

        return rows;
    }

    private static object Identity(AcadContext ctx, AecSpace s)
    {
        AecRecord? style = null;
        try
        {
            if (!s.StyleId.IsNull) style = ctx.Tr.GetObject(s.StyleId, OpenMode.ForRead) as AecRecord;
        }
        catch { }

        return new
        {
            handle = TryS(() => s.Handle.ToString()),
            type = "Space",
            objectClass = ClassName(s.ObjectId),
            layer = TryS(() => s.Layer),
            description = TryS(() => s.Description),
            styleName = TryS(() => style?.Name),
            styleHandle = TryS(() => style?.Handle.ToString()),
        };
    }

    /// <summary>The measured core of a space: everything numeric that does not need extra passes.</summary>
    private static object Measures(AecSpace s) => new
    {
        name = TryS(() => s.Name),
        area = TryD(() => s.Area),
        perimeter = TryD(() => s.Perimeter),
        baseVolume = TryD(() => s.BaseVolume),
        overallVolume = TryD(() => s.OverallVolume),
        volumeAboveCeiling = TryD(() => s.VolumeAboveCeiling),
        volumeBelowFloor = TryD(() => s.VolumeBelowFloor),
        ceilingHeight = TryD(() => s.CeilingHeight),
        ceilingThickness = TryD(() => s.CeilingThickness),
        floorThickness = TryD(() => s.FloorThickness),
        overallHeight = TryD(() => s.OverallHeight),
        spaceAboveCeiling = TryD(() => s.SpaceAboveCeiling),
        spaceBelowFloor = TryD(() => s.SpaceBelowFloor),
        surfaceHeightDefault = TryD(() => s.SurfaceHeightDefault),
        calculationPlane1Height = TryD(() => s.CalculationPlane1Height),
        calculationPlane2Height = TryD(() => s.CalculationPlane2Height),
        heightAtTopOfFloor = TryD(() => s.HeightAtTopOfFloor),
        heightAtBottomOfSurfaces = TryD(() => s.HeightAtBottomOfSurfaces),
        heightAtBottomOfSpaceBelowFloor = TryD(() => s.HeightAtBottomOfSpaceBelowFloor),
        length = TryD(() => s.Length),
        width = TryD(() => s.Width),
        justification = TryS(() => s.Justification.ToString()),
        geometryType = TryS(() => s.GeometryType.ToString()),
        boundaryOffsetType = TryS(() => s.BoundaryOffsetType.ToString()),
        associative = TryB(() => s.Associative),
        drawHatch = TryB(() => s.DrawHatch),
        useIncrementalNames = TryB(() => s.UseIncrementalNames),
        interferencesCount = TryI(() => s.InterferencesCount),
        location = TryPt(() => s.Location),
        rotation = TryD(() => s.Rotation),
        centroid = TryPt(() => s.CentroidWCS(false)),
    };

    private static object WallAreas(AecSpace s) => new
    {
        topOfFloorToBottomOfCeilingGross =
            TryD(() => s.WallArea(AecSpaceZType.ZTopOfFloor, AecSpaceZType.ZBottomOfCeiling, false)),
        topOfFloorToBottomOfCeilingNet =
            TryD(() => s.WallArea(AecSpaceZType.ZTopOfFloor, AecSpaceZType.ZBottomOfCeiling, true)),
        bottomOfFloorToTopOfCeilingGross =
            TryD(() => s.WallArea(AecSpaceZType.ZBottomOfFloor, AecSpaceZType.ZTopOfCeiling, false)),
        bottomOfFloorToTopOfCeilingNet =
            TryD(() => s.WallArea(AecSpaceZType.ZBottomOfFloor, AecSpaceZType.ZTopOfCeiling, true)),
    };

    private static List<object> Surfaces(AcadContext ctx, AecSpace s, int max)
    {
        var rows = new List<object>();

        try
        {
            foreach (AecSpaceSurface surface in s.Surfaces)
            {
                if (rows.Count >= max) break;
                rows.Add(new
                {
                    kind = "surface",
                    name = TryS(() => surface.Name),
                    surfaceType = TryS(() => surface.SurfaceType.ToString()),
                    area = TryD(() => surface.Area),
                    height = TryD(() => surface.Height),
                    heightIsOverridden = TryB(() => surface.HeightIsOverridden),
                    heightOverride = TryD(() => surface.HeightOverride),
                    thickness = TryD(() => surface.Thickness),
                    componentCount = TryI(() => surface.Components.Count),
                    styleName = TryS(() => RecordName(ctx, surface.StyleId)),
                });
            }
        }
        catch { }

        // Free-form spaces keep their faces in a separate collection.
        try
        {
            foreach (AecSpaceSurfaceFreeForm surface in s.SurfacesFreeForm)
            {
                if (rows.Count >= max) break;
                rows.Add(new
                {
                    kind = "freeFormSurface",
                    name = TryS(() => surface.Name),
                    surfaceType = TryS(() => surface.SurfaceType.ToString()),
                    area = TryD(() => surface.Area),
                    isSloped = TryB(() => surface.IsSloped),
                    faceId = TryI(() => surface.FaceId),
                    thickness = TryD(() => surface.Thickness),
                    componentCount = TryI(() => surface.Components.Count),
                    centroid = TryPt(() => surface.Centroid),
                    styleName = TryS(() => RecordName(ctx, surface.StyleId)),
                });
            }
        }
        catch { }

        return rows;
    }

    private static List<string> Defects(AecSpace s)
    {
        var list = new List<string>();
        try
        {
            foreach (var code in s.GetDefects().ToArray())
            {
                var name = TryS(() => AecSpace.DefectDisplayString((AecSpaceDefect)code));
                list.Add(string.IsNullOrWhiteSpace(name) ? $"defect({code})" : name!);
            }
        }
        catch { }
        return list;
    }

    private static AecSpace OpenSpace(AcadContext ctx, JsonElement a, OpenMode mode)
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);

        DBObject obj;
        try { obj = ctx.Tr.GetObject(id, mode); }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new AcadMcpException("open_failed",
                $"Could not open handle '{handle}'.", ex.ErrorStatus.ToString());
        }

        if (obj is not AecSpace s)
            throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC Space.",
                "Call list_spaces_detailed for the space handles in this drawing.");

        return s;
    }

    // ----------------------------------------------------------------------------- implementations

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListSpaceStyles(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeDetail = Args.BoolOpt(a, "includeDetail", true);

        AecSpaceStyleDict dict;
        try { dict = new AecSpaceStyleDict(ctx.Db); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                "This drawing has no space style dictionary.", ex.Message);
        }

        ObjectId standardId;
        try { standardId = AecSpaceStyleDict.GetStandardStyle(ctx.Db); }
        catch { standardId = ObjectId.Null; }

        ObjectIdCollection records;
        try { records = dict.Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                "Could not enumerate the space style dictionary.", ex.Message);
        }

        var rows = new List<(string Key, object Data)>();

        foreach (ObjectId id in records)
        {
            if (id.IsNull || id.IsErased) continue;

            AecSpaceStyle style;
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not AecSpaceStyle opened) continue;
                style = opened;
            }
            catch { continue; }

            var name = TryS(() => style.Name) ?? id.Handle.ToString();

            object data = includeDetail
                ? new
                {
                    handle = id.Handle.ToString(),
                    name,
                    localizedName = TryS(() => style.LocalizedName),
                    alternateName = TryS(() => style.AlternateName),
                    // Description is NOT declared on SpaceStyle or on DictionaryRecord. It is
                    // inherited from Autodesk.Aec.DatabaseServices.DBObject — ACA's own DBObject
                    // subclass, which DictionaryRecord (and Anchor, TextNote, ReferenceDocument)
                    // derive from, and which adds Description / DisplayName / Overrides /
                    // SetToStandard on top of acdbmgd's DBObject. Do not "fix" this away: the
                    // declared API surface lists declared members only, so it looks absent on SpaceStyle.
                    description = TryS(() => style.Description),
                    keynoteValue = TryS(() => style.KeynoteValue),
                    isStandard = !standardId.IsNull && standardId == id,
                    isLocked = TryB(() => style.IsLocked),
                    keynote = TryS(() => style.Keynote),
                    category = TryS(() => style.Category),
                    categoryId = TryI(() => style.CategoryId),
                    constraintId = TryI(() => style.ConstraintId),
                    targetArea = TryD(() => style.Area),
                    minimumArea = TryD(() => style.MinimumArea),
                    maximumArea = TryD(() => style.MaximumArea),
                    targetLength = TryD(() => style.Length),
                    minimumLength = TryD(() => style.MinimumLength),
                    maximumLength = TryD(() => style.MaximumLength),
                    targetWidth = TryD(() => style.Width),
                    minimumWidth = TryD(() => style.MinimumWidth),
                    maximumWidth = TryD(() => style.MaximumWidth),
                    ceilingHeight = TryD(() => style.CeilingHeight),
                    netOffset = TryD(() => style.NetOffset),
                    netToUsableOffset = TryD(() => style.NetToUsableOffset),
                    netToGrossOffset = TryD(() => style.NetToGrossOffset),
                    nameDefinitionStyle = TryS(() => RecordName(ctx, style.NameDefinitionStyleId)),
                }
                : new
                {
                    handle = id.Handle.ToString(),
                    name,
                    isStandard = !standardId.IsNull && standardId == id,
                };

            rows.Add((name, data));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateSpace(JsonElement a)
    {
        var dryRun = Args.BoolOpt(a, "dryRun", false);

        // One lambda, two entry points: dryRun goes through Acad.Read (transaction ABORTED, so the
        // drawing cannot be touched even by accident), the real thing through Acad.Write.
        Func<AcadContext, object?> body = ctx => CreateSpaceCore(ctx, a, dryRun);
        return dryRun ? Acad.Read(body) : Acad.Write(body);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? CreateSpaceCore(AcadContext ctx, JsonElement a, bool dryRun)
    {
        var hasPoints = Has(a, "points");
        var hasPolyline = Has(a, "polylineHandle");

        if (!hasPoints && !hasPolyline)
            throw new AcadMcpException("bad_argument",
                "Give either 'points' (an array of [x,y] / [x,y,z]) or 'polylineHandle'.");

        AecProfile profile;
        string source;
        double sourceElevation = 0.0;
        Point2d anchor;

        if (hasPoints)
        {
            var raw = a.GetProperty("points");
            if (raw.ValueKind != JsonValueKind.Array)
                throw new AcadMcpException("bad_argument", "'points' must be an array of [x,y] / [x,y,z].");

            var pts = ReadRing(raw, "points", out sourceElevation);
            profile = new AecProfile();
            profile.AddRing(RingFromPoints(pts, isVoid: false));
            anchor = pts[0];
            source = $"{pts.Count} explicit points";
        }
        else
        {
            var plHandle = Args.Str(a, "polylineHandle");
            var plId = Acad.ToObjectId(ctx.Db, plHandle);

            DBObject boundaryObj;
            try { boundaryObj = ctx.Tr.GetObject(plId, OpenMode.ForRead); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new AcadMcpException("open_failed",
                    $"Could not open handle '{plHandle}'.", ex.ErrorStatus.ToString());
            }

            if (boundaryObj is Polyline pl)
            {
                profile = new AecProfile();
                profile.AddRing(RingFromPolyline(pl));
                anchor = pl.GetPoint2dAt(0);
                sourceElevation = TryD(() => pl.Elevation) ?? 0.0;
                source = $"polyline {plHandle} ({pl.NumberOfVertices} vertices"
                         + (pl.HasBulges ? ", with arcs)" : ")");
            }
            else if (boundaryObj is Circle circle)
            {
                // Profile.CreateCircle builds the ring itself — no vertex decomposition needed.
                var centre = circle.Center;
                var made = AecProfile.CreateCircle(circle.Radius, new Vector2d(centre.X, centre.Y));
                if (made is null)
                    throw new AcadMcpException("bad_boundary",
                        $"Could not build a circular profile from '{plHandle}'.");

                profile = made;
                anchor = new Point2d(centre.X, centre.Y);
                sourceElevation = centre.Z;
                source = $"circle {plHandle} (r={circle.Radius})";
            }
            else
            {
                throw new AcadMcpException("wrong_type",
                    $"Handle '{plHandle}' is a {ClassName(plId)}; create_space accepts a closed "
                    + "LWPOLYLINE or a CIRCLE.",
                    "Pass 'points' instead for an arbitrary boundary.");
            }
        }

        foreach (var hole in ReadHoles(a))
            profile.AddRing(RingFromPoints(hole, isVoid: true));

        // Orient outer/void rings consistently before validating; ACA's own checks assume it.
        try { profile.ConsistentlyOrientRings(); } catch { }
        ValidateProfile(profile, "The supplied boundary");

        // ---- placement -------------------------------------------------------------------------
        // The profile is in the Space's ECS. Anchor the ECS at a base point and express the profile
        // relative to it, so the space lands where the caller drew the boundary.
        var elevation = Args.DblOpt(a, "elevation", sourceElevation);
        var basePoint = Has(a, "location")
            ? Args.Point(a, "location")
            : new Point3d(anchor.X, anchor.Y, elevation);

        try { profile.TranslateBy(new Vector2d(-basePoint.X, -basePoint.Y)); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("bad_boundary",
                "Could not move the boundary profile into the space's local coordinate system.",
                ex.Message);
        }

        var area = TryD(() => profile.Area(1e-6));
        var perimeter = TryD(() => profile.Perimeter(1e-6));
        var ringCount = TryI(() => profile.RingCount);

        // ---- style -----------------------------------------------------------------------------
        var styleName = Args.StrOpt(a, "style");
        var styleId = StyleId(ctx, styleName);          // throws rather than leave a style-less space
        var resolvedStyle = RecordName(ctx, styleId);

        if (dryRun)
        {
            return new
            {
                dryRun = true,
                created = false,
                source,
                ringCount,
                area,
                perimeter,
                basePoint = new[] { basePoint.X, basePoint.Y, basePoint.Z },
                styleName = resolvedStyle ?? styleName,
                styleHandle = TryS(() => styleId.Handle.ToString()),
                note = "Boundary validated. Re-run without dryRun to create the space.",
            };
        }

        // ---- geometry type / justification ------------------------------------------------------
        var geometryTypeArg = Args.StrOpt(a, "geometryType", "Extrusion")!;
        if (!Enum.TryParse<AecSpaceGeometryType>(geometryTypeArg, true, out var geometryType))
            throw new AcadMcpException("bad_argument",
                $"'geometryType' must be TwoD or Extrusion; got '{geometryTypeArg}'.");

        // A FreeForm space is driven by a modeller Body (Space.SetBody(Body, SpaceProfileType) and
        // the SurfacesFreeForm collection), NOT by the 2D BaseProfile this tool builds. Declaring a
        // brand-new space FreeForm with nothing but a boundary profile behind it would append an
        // AEC object whose geometry type has no geometry — precisely the invalid-object case that
        // must never reach a drawing. Refuse at creation; converting an existing, valid space is a
        // supported ACA operation and stays available through set_space_properties.
        if (geometryType == AecSpaceGeometryType.FreeForm)
            throw new AcadMcpException("unsupported",
                "create_space cannot build a FreeForm space.",
                "A FreeForm space needs a modeller Body, not a boundary profile. Create the space "
                + "as Extrusion (or TwoD), then change geometryType with set_space_properties.");

        AecSpaceJustification? justification = null;
        if (Has(a, "justification"))
        {
            var text = Args.Str(a, "justification");
            if (!Enum.TryParse<AecSpaceJustification>(text, true, out var parsed))
                throw new AcadMcpException("bad_argument",
                    $"'justification' must be one of TopOfFloor, BottomOfFloor, SpaceBelowFloor; got '{text}'.");
            justification = parsed;
        }

        // ---- target block table record ----------------------------------------------------------
        var target = Args.StrOpt(a, "space", "model") ?? "model";
        var btrId = target.Equals("current", StringComparison.OrdinalIgnoreCase)
            ? ctx.Db.CurrentSpaceId
            : SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db);

        BlockTableRecord btr;
        try { btr = (BlockTableRecord)ctx.Tr.GetObject(btrId, OpenMode.ForWrite); }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new AcadMcpException("open_failed",
                "Could not open the target block table record for write.", ex.ErrorStatus.ToString());
        }

        // ---- create -----------------------------------------------------------------------------
        // Only the two calls that TAKE a Database happen before the append: SetDatabaseDefaults
        // (AutoCAD) and ACA's SetToStandard, which is what gives the fresh object a valid style and
        // display defaults — so the object is never appended style-less. Everything else (layer by
        // name, ECS placement, the profile) needs the object to be database-resident, so it comes
        // after AppendEntity/AddNewlyCreatedDBObject.
        var space = new AecSpace();
        var warnings = new List<string>();

        try
        {
            space.SetDatabaseDefaults(ctx.Db);
            space.SetToStandard(ctx.Db);
        }
        catch (System.Exception ex)
        {
            space.Dispose();
            throw new AcadMcpException("create_failed",
                "Could not initialise the space before appending it.", ex.Message);
        }

        try { btr.AppendEntity(space); }
        catch (System.Exception ex)
        {
            space.Dispose();
            throw new AcadMcpException("create_failed",
                "Could not append the new space to the drawing.", ex.Message);
        }

        // Appended: the BlockTableRecord owns it now, so never dispose it from here on. A failure
        // past this point aborts the transaction (Acad.Write only commits on a clean return), so a
        // half-built space is never left in the drawing.
        ctx.Tr.AddNewlyCreatedDBObject(space, true);

        try
        {
            space.StyleId = styleId;
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                $"Could not apply space style '{resolvedStyle ?? styleName}'.", ex.Message);
        }

        try
        {
            space.Normal = Vector3d.ZAxis;
            space.Rotation = 0.0;
            space.Location = basePoint;
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "Could not place the space's local coordinate system.", ex.Message);
        }

        try
        {
            space.BaseProfile = profile;
            space.SetBaseProfileDirty();
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "Could not apply the boundary profile to the new space.",
                ex.Message + " — nothing was committed to the drawing.");
        }

        var layer = Args.StrOpt(a, "layer");
        if (!string.IsNullOrWhiteSpace(layer))
        {
            try { space.Layer = layer; }
            catch (System.Exception ex) { warnings.Add($"layer '{layer}' not applied: {ex.Message}"); }
        }
        else
        {
            // ACA layer keys (A-Area-Spce and friends) come from the layer standard, if one is loaded.
            try { space.SetDefaultLayer(); } catch { }
        }

        try { space.GeometryType = geometryType; }
        catch (System.Exception ex) { warnings.Add($"geometryType not applied: {ex.Message}"); }

        if (justification is not null)
        {
            try { space.Justification = justification.Value; }
            catch (System.Exception ex) { warnings.Add($"justification not applied: {ex.Message}"); }
        }

        var name = Args.StrOpt(a, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { space.Name = name; }
            catch (System.Exception ex) { warnings.Add($"name not applied: {ex.Message}"); }
        }

        var description = Args.StrOpt(a, "description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            try { space.Description = description; }
            catch (System.Exception ex) { warnings.Add($"description not applied: {ex.Message}"); }
        }

        ApplyDouble(a, "ceilingHeight", v => space.CeilingHeight = v, warnings);
        ApplyDouble(a, "floorThickness", v => space.FloorThickness = v, warnings);
        ApplyDouble(a, "ceilingThickness", v => space.CeilingThickness = v, warnings);
        ApplyDouble(a, "overallHeight", v => space.OverallHeight = v, warnings);
        ApplyDouble(a, "spaceAboveCeiling", v => space.SpaceAboveCeiling = v, warnings);
        ApplyDouble(a, "spaceBelowFloor", v => space.SpaceBelowFloor = v, warnings);

        return new
        {
            created = true,
            handle = TryS(() => space.Handle.ToString()),
            source,
            ringCount,
            profileArea = area,
            profilePerimeter = perimeter,
            basePoint = new[] { basePoint.X, basePoint.Y, basePoint.Z },
            styleName = resolvedStyle ?? styleName,
            styleHandle = TryS(() => styleId.Handle.ToString()),
            geometryType = TryS(() => space.GeometryType.ToString()),
            layer = TryS(() => space.Layer),
            name = TryS(() => space.Name),
            reportedArea = TryD(() => space.Area),
            reportedPerimeter = TryD(() => space.Perimeter),
            reportedVolume = TryD(() => space.BaseVolume),
            // Read back straight after writing so the caller can confirm in one round trip that the
            // space landed where the boundary was drawn — profileArea vs reportedArea, and
            // reportedCentroid against the expected centre of the input boundary.
            reportedLocation = TryPt(() => space.Location),
            reportedCentroid = TryPt(() => space.CentroidWCS(false)),
            defects = Defects(space),
            warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    private static void ApplyDouble(JsonElement a, string name, Action<double> set, List<string> warnings)
    {
        if (!Has(a, name)) return;
        var value = Args.Dbl(a, name);
        try { set(value); }
        catch (System.Exception ex) { warnings.Add($"{name} not applied: {ex.Message}"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetSpaceProperties(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var space = OpenSpace(ctx, a, OpenMode.ForWrite);

        var changes = new List<(string Key, object Data)>();
        var warnings = new List<string>();

        void Record(string field, object? before, object? after) =>
            changes.Add((field, new { field, oldValue = before, newValue = after }));

        void Numeric(string field, Func<double> get, Action<double> set)
        {
            if (!Has(a, field)) return;
            var before = TryD(get);
            var value = Args.Dbl(a, field);
            try
            {
                set(value);
                Record(field, before, TryD(get));
            }
            catch (System.Exception ex) { warnings.Add($"{field} not applied: {ex.Message}"); }
        }

        // -- style -------------------------------------------------------------------------------
        if (Has(a, "style"))
        {
            var wanted = Args.Str(a, "style");
            var before = RecordName(ctx, space.StyleId);
            var id = StyleId(ctx, wanted);
            try
            {
                space.StyleId = id;
                Record("style", before, RecordName(ctx, space.StyleId));
            }
            catch (System.Exception ex) { warnings.Add($"style not applied: {ex.Message}"); }
        }

        // -- text --------------------------------------------------------------------------------
        if (Has(a, "name"))
        {
            var before = TryS(() => space.Name);
            var wanted = Args.Str(a, "name");
            try
            {
                space.Name = wanted;
                Record("name", before, TryS(() => space.Name));
            }
            catch (System.Exception ex)
            {
                warnings.Add($"name not applied: {ex.Message} — the style may drive names from a name list.");
            }
        }

        if (Has(a, "description"))
        {
            var before = TryS(() => space.Description);
            var wanted = Args.Str(a, "description");
            try
            {
                space.Description = wanted;
                Record("description", before, TryS(() => space.Description));
            }
            catch (System.Exception ex) { warnings.Add($"description not applied: {ex.Message}"); }
        }

        // -- numbers -----------------------------------------------------------------------------
        Numeric("ceilingHeight", () => space.CeilingHeight, v => space.CeilingHeight = v);
        Numeric("floorThickness", () => space.FloorThickness, v => space.FloorThickness = v);
        Numeric("ceilingThickness", () => space.CeilingThickness, v => space.CeilingThickness = v);
        Numeric("overallHeight", () => space.OverallHeight, v => space.OverallHeight = v);
        Numeric("spaceAboveCeiling", () => space.SpaceAboveCeiling, v => space.SpaceAboveCeiling = v);
        Numeric("spaceBelowFloor", () => space.SpaceBelowFloor, v => space.SpaceBelowFloor = v);
        Numeric("surfaceHeightDefault", () => space.SurfaceHeightDefault, v => space.SurfaceHeightDefault = v);
        Numeric("calculationPlane1Height",
                () => space.CalculationPlane1Height, v => space.CalculationPlane1Height = v);
        Numeric("calculationPlane2Height",
                () => space.CalculationPlane2Height, v => space.CalculationPlane2Height = v);

        // -- enums -------------------------------------------------------------------------------
        if (Has(a, "justification"))
        {
            var text = Args.Str(a, "justification");
            if (!Enum.TryParse<AecSpaceJustification>(text, true, out var parsed))
                throw new AcadMcpException("bad_argument",
                    $"'justification' must be one of TopOfFloor, BottomOfFloor, SpaceBelowFloor; got '{text}'.");

            var before = TryS(() => space.Justification.ToString());
            try
            {
                space.Justification = parsed;
                Record("justification", before, TryS(() => space.Justification.ToString()));
            }
            catch (System.Exception ex) { warnings.Add($"justification not applied: {ex.Message}"); }
        }

        if (Has(a, "geometryType"))
        {
            var text = Args.Str(a, "geometryType");
            if (!Enum.TryParse<AecSpaceGeometryType>(text, true, out var parsed))
                throw new AcadMcpException("bad_argument",
                    $"'geometryType' must be one of TwoD, Extrusion, FreeForm; got '{text}'.");

            var before = TryS(() => space.GeometryType.ToString());
            try
            {
                space.GeometryType = parsed;
                Record("geometryType", before, TryS(() => space.GeometryType.ToString()));
            }
            catch (System.Exception ex) { warnings.Add($"geometryType not applied: {ex.Message}"); }
        }

        if (Has(a, "boundaryOffsetType"))
        {
            var text = Args.Str(a, "boundaryOffsetType");
            if (!Enum.TryParse<AecSpaceBoundaryOffset>(text, true, out var parsed))
                throw new AcadMcpException("bad_argument",
                    $"'boundaryOffsetType' must be one of ByStyle, ByRule, Manual; got '{text}'.");

            var before = TryS(() => space.BoundaryOffsetType.ToString());
            try
            {
                space.BoundaryOffsetType = parsed;
                Record("boundaryOffsetType", before, TryS(() => space.BoundaryOffsetType.ToString()));
            }
            catch (System.Exception ex) { warnings.Add($"boundaryOffsetType not applied: {ex.Message}"); }
        }

        // -- flags -------------------------------------------------------------------------------
        if (Has(a, "drawHatch"))
        {
            var before = TryB(() => space.DrawHatch);
            var wanted = Args.BoolOpt(a, "drawHatch", before ?? false);
            try
            {
                space.DrawHatch = wanted;
                Record("drawHatch", before, TryB(() => space.DrawHatch));
            }
            catch (System.Exception ex) { warnings.Add($"drawHatch not applied: {ex.Message}"); }
        }

        if (Has(a, "associative"))
        {
            var before = TryB(() => space.Associative);
            var wanted = Args.BoolOpt(a, "associative", before ?? false);
            try
            {
                space.Associative = wanted;
                Record("associative", before, TryB(() => space.Associative));
            }
            catch (System.Exception ex) { warnings.Add($"associative not applied: {ex.Message}"); }
        }

        if (changes.Count == 0 && warnings.Count == 0)
            throw new AcadMcpException("nothing_to_do",
                "No editable field was supplied.",
                "Pass at least one of name, description, style, ceilingHeight, floorThickness, "
                + "ceilingThickness, overallHeight, spaceAboveCeiling, spaceBelowFloor, "
                + "surfaceHeightDefault, calculationPlane1Height, calculationPlane2Height, "
                + "justification, geometryType, boundaryOffsetType, drawHatch, associative.");

        return new
        {
            handle = TryS(() => space.Handle.ToString()),
            changed = changes.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                             .Select(c => c.Data)
                             .ToList(),
            changeCount = changes.Count,
            warnings = warnings.Count == 0 ? null : warnings,
            measures = Measures(space),
            defects = Defects(space),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetSpace(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var space = OpenSpace(ctx, a, OpenMode.ForRead);

        var includeSurfaces = Args.BoolOpt(a, "includeSurfaces", true);
        var includeSets = Args.BoolOpt(a, "includePropertySets", true);
        var includeBoundary = Args.BoolOpt(a, "includeBoundary", true);

        // Read the boundary through the by-value getters only. Aec Entity's
        // GetBaseProfile(Matrix3d& toWcs) is ByRef and the declared ByRef signature does not distinguish 'out' from
        // 'ref', so it is deliberately not called.
        int? ringCount = null;
        double? profileArea = null;
        double? profilePerimeter = null;
        var rings = new List<object>();

        if (includeBoundary)
        {
            var baseProfile = TryO(() => space.BaseProfile) as AecProfile
                              ?? TryO(() => space.GetProfile(AecSpaceProfileType.Base)) as AecProfile;

            if (baseProfile is not null)
            {
                var p = baseProfile;
                ringCount = TryI(() => p.RingCount);
                profileArea = TryD(() => p.Area(1e-6));
                profilePerimeter = TryD(() => p.Perimeter(1e-6));
                rings = ProfileRings(space, p, 2000);
            }
        }

        return new
        {
            identity = Identity(ctx, space),
            measures = Measures(space),
            calculated = CalculatedProfiles(space, all: true),
            wallAreas = WallAreas(space),
            axes = new
            {
                xDir = TryPt(() => new Point3d(space.XDir.X, space.XDir.Y, space.XDir.Z)),
                yDir = TryPt(() => new Point3d(space.YDir.X, space.YDir.Y, space.YDir.Z)),
                normal = TryPt(() => new Point3d(space.Normal.X, space.Normal.Y, space.Normal.Z)),
            },
            boundary = !includeBoundary ? null : new
            {
                ringCount,
                profileArea,
                profilePerimeter,
                rings,
            },
            boundingObjectHandles = TryO(() => Handles(space.GetBoundary())),
            boundingOpeningHandles = TryO(() => Handles(space.FindBoundingOpenings())),
            spaceModifierHandles = TryO(() => Handles(space.SpaceModifiers)),
            surfaces = includeSurfaces ? Surfaces(ctx, space, 500) : null,
            surfaceCount = TryI(() => space.Surfaces.Count),
            defects = Defects(space),
            propertySets = includeSets ? PropertySetsOf(ctx, space) : null,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListSpacesDetailed(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var deep = Args.BoolOpt(a, "deepScan", false);
        var includeSets = Args.BoolOpt(a, "includePropertySets", true);
        var includeAll = Args.BoolOpt(a, "includeAllProfiles", false);
        var includeSurfaces = Args.BoolOpt(a, "includeSurfaces", false);
        var nameFilter = Args.StrOpt(a, "nameFilter");
        var styleFilter = Args.StrOpt(a, "styleFilter");

        // Pass one: classify cheaply so 'total' is exact without a payload per space.
        var matched = new List<(string Key, ObjectId Id)>();

        foreach (var id in ScanSpaces(ctx, Scope(a), deep))
        {
            AecSpace space;
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not AecSpace opened) continue;
                space = opened;
            }
            catch { continue; }

            var name = TryS(() => space.Name) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(nameFilter)
                && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (!string.IsNullOrWhiteSpace(styleFilter))
            {
                var styleName = RecordName(ctx, space.StyleId);
                if (!string.Equals(styleName, styleFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }

            matched.Add((name + "|" + id.Handle, id));
        }

        var ordered = matched.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var slice = ordered.Skip(offset).Take(limit).ToList();

        // Pass two: the expensive reads, page only.
        var items = new List<object>();
        foreach (var entry in slice)
        {
            AecSpace space;
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is not AecSpace opened) continue;
                space = opened;
            }
            catch { continue; }

            items.Add(new
            {
                identity = Identity(ctx, space),
                measures = Measures(space),
                calculated = CalculatedProfiles(space, includeAll),
                wallAreas = WallAreas(space),
                boundingObjectHandles = TryO(() => Handles(space.GetBoundary())),
                boundingOpeningHandles = TryO(() => Handles(space.FindBoundingOpenings())),
                surfaces = includeSurfaces ? Surfaces(ctx, space, 200) : null,
                surfaceCount = TryI(() => space.Surfaces.Count),
                defects = Defects(space),
                propertySets = includeSets ? PropertySetsOf(ctx, space) : null,
            });
        }

        return new
        {
            items,
            total = ordered.Count,
            offset,
            limit,
            truncated = offset + slice.Count < ordered.Count,
            scope = Scope(a),
        };
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListSpaceStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateSpace(JsonElement a) => throw Unavailable();
    private static Task<object?> SetSpaceProperties(JsonElement a) => throw Unavailable();
    private static Task<object?> GetSpace(JsonElement a) => throw Unavailable();
    private static Task<object?> ListSpacesDetailed(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
