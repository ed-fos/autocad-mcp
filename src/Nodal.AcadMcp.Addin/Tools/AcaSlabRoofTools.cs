using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are referenced ONLY through explicit aliases, and only inside #if ACA
// — same rule as AcaTools.cs. 'Entity', 'DBObject', 'Dictionary' and 'Profile' all collide between
// Autodesk.Aec.*, Autodesk.AutoCAD.DatabaseServices and System, and on a vanilla AutoCAD box these
// assemblies do not exist, so nothing outside the guarded region may name them.
#if ACA
using AecEntity = Autodesk.Aec.DatabaseServices.Entity;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecDict = Autodesk.Aec.DatabaseServices.Dictionary;
using AecProfile = Autodesk.Aec.Geometry.Profile;

using AecSlab = Autodesk.Aec.Arch.DatabaseServices.Slab;
using AecSlabBase = Autodesk.Aec.Arch.DatabaseServices.SlabBase;
using AecSlabStyle = Autodesk.Aec.Arch.DatabaseServices.SlabStyle;
using AecSlabStyleDict = Autodesk.Aec.Arch.DatabaseServices.DictionarySlabStyle;
using AecRoofSlab = Autodesk.Aec.Arch.DatabaseServices.RoofSlab;
using AecRoofSlabStyleDict = Autodesk.Aec.Arch.DatabaseServices.DictionaryRoofSlabStyle;
using AecRoof = Autodesk.Aec.Arch.DatabaseServices.Roof;
using AecRoofProfile = Autodesk.Aec.Arch.Geometry.RoofProfile;
using AecRoofSegment = Autodesk.Aec.Arch.Geometry.RoofSegment;
using AecThicknessDirection = Autodesk.Aec.Arch.Geometry.ThicknessDirection;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture slabs, roof slabs and roofs: listing, creation and edit.
///
/// JIT SAFETY. Every handler is wrapped by <see cref="Reg"/>, which calls
/// <see cref="AcaSupport.Require"/> before touching the implementation. The implementations that
/// name Aec types are separate [MethodImpl(NoInlining)] methods whose signatures are only
/// (JsonElement) -> Task&lt;object?&gt;, so the JIT never resolves an Aec type reference on a box
/// without AutoCAD Architecture.
///
/// ROLLBACK GUARD. Every creation/edit handler runs inside Acad.Write, whose transaction is only
/// committed when the handler returns normally (an escaping exception disposes the transaction
/// un-committed, which aborts it). Each one therefore validates the object it built — IsValid, and
/// for roofs a non-zero body count — and throws rather than commit a malformed AEC object. A failed
/// call leaves the drawing byte-for-byte unchanged.
///
/// API NOTES / DIVERGENCES (everything below follows the declared API surface):
///  * SlabBase.Thickness is a METHOD (Double Thickness(Double)), not a settable property. The
///    settable instance thickness is BaseThickness; the realised thickness reported here is
///    |ExteriorFaceOffset - InteriorFaceOffset|. If the slab's style has UseThickness set, the
///    style's DefaultThickness wins over BaseThickness — create_slab reports that as
///    styleOverridesThickness.
///  * Slab/RoofSlab "overhang" is not a property of the slab. It is the per-edge offset
///    (SlabBase.SetEdgeOffsetAt / GetEdgeOffsetAt) plus SlabEdgeStyle.Overhang on the edge style.
///    create_roof_slab's 'overhang' is applied as a uniform edge offset to every edge; failures are
///    reported in the result rather than aborting the slab.
///  * Slabs have no elevation property. Geo.Location is the insertion point, so set_slab_properties
///    'elevation' is applied as a world-Z TranslateBy of (elevation - Location.Z).
///  * Roof has no style dictionary in AecArchMgd (there is no DictionaryRoofStyle), so create_roof
///    takes no style argument.
///  * Arc segments in a source polyline are flattened to chords — SlabBase.ConvertToArcAt exists but
///    takes an on-arc point per edge, which this module does not attempt. hadBulges flags it.
/// </summary>
internal static class AcaSlabRoofTools
{
    internal static void Register()
    {
        Reg("list_slab_styles",
            "Every SlabStyle in the drawing with its default thickness, slope and offsets, the "
            + "components it is built from, and its default edge styles. Args: limit (default 500), offset.",
            mutating: false, ListSlabStyles);

        Reg("list_roof_slab_styles",
            "Every RoofSlabStyle in the drawing (RoofSlabStyle derives from SlabStyle, so the same "
            + "fields are reported). Args: limit (default 500), offset.",
            mutating: false, ListRoofSlabStyles);

        Reg("create_slab",
            "Create an AEC Slab from a closed boundary, as one undoable step. Args: points "
            + "([[x,y,z], ...], the boundary) OR polyline (handle of a closed LWPOLYLINE); style "
            + "(slab style name, default the drawing's Standard); thickness (BaseThickness); "
            + "verticalOffset; horizontalOffset; slope (radians) or slopeDegrees; pivotPoint; "
            + "normal (default the polyline's normal, else [0,0,1]); basePoint + direction (uses the "
            + "SetBaseCurve overload that takes an origin and a direction instead of useAsIs); "
            + "useAsIs (default true); space (model|paper|current); layer. Rolled back if the "
            + "resulting slab reports itself invalid.",
            mutating: true, CreateSlab);

        Reg("create_roof_slab",
            "Create an AEC RoofSlab — same arguments as create_slab (style names come from the roof "
            + "slab style dictionary) plus overhang, which is applied as a uniform per-edge offset "
            + "via SetEdgeOffsetAt. Use slope/slopeDegrees for the pitch. Rolled back if the "
            + "resulting slab reports itself invalid.",
            mutating: true, CreateRoofSlab);

        Reg("create_roof",
            "Create an AEC Roof (the single multi-pitch roof object, not a RoofSlab) from a closed "
            + "boundary. Args: points OR polyline; slope (radians) or slopeDegrees (required); "
            + "plateHeight (eave height, default 0); overhang (default 0); thickness; "
            + "thicknessDirection (roof|floor, default roof); normal; space; layer. The boundary "
            + "becomes one peaked RoofSegment per edge. Rolled back if the roof fails to calculate.",
            mutating: true, CreateRoof);

        Reg("set_slab_properties",
            "Edit an existing Slab or RoofSlab by handle, as one undoable step. Args: handle, plus "
            + "any of thickness (BaseThickness), style (name, resolved against the slab or roof-slab "
            + "style dictionary to match the object), elevation (world Z of the insertion point), "
            + "verticalOffset, horizontalOffset, slope (radians) or slopeDegrees, pivotPoint, "
            + "layer, description. "
            + "Returns before/after and the list of properties that changed. Rolled back if the "
            + "edit leaves the slab invalid.",
            mutating: true, SetSlabProperties);

        Reg("list_slabs",
            "Every AEC Slab and RoofSlab with style, thickness, areas, perimeter, elevation, slope, "
            + "offsets and edge offsets. Args: type (slab|roofSlab|all, default all), scope "
            + "(model|current|all, default model), deepScan (opens every entity instead of "
            + "pre-filtering on class name), limit (default 500), offset.",
            mutating: false, ListSlabs);

        Reg("list_roofs",
            "Every AEC Roof with slope, rise/run, plate heights, overhang, thickness, edge shape, "
            + "body/line counts and its roof profile's area and perimeter. Args: scope, deepScan, "
            + "limit (default 500), offset.",
            mutating: false, ListRoofs);
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

    // ============================================================ ACA-free helpers
    // Nothing below this line names an Aec type, so it is safe to JIT anywhere.

    /// <summary>AutoCAD's default global tolerance, built explicitly so nothing depends on statics.</summary>
    private static Tolerance Tol => new(1e-10, 1e-10);

    private const double Eps = 1e-9;

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

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

    // ---- guarded accessors: one bad property must never lose a whole listing. The bridge drops
    // nulls (DefaultIgnoreCondition = WhenWritingNull), so a failed read simply vanishes from JSON.

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

    private static double[]? TryVec(Func<Vector3d> f)
    {
        try { var v = f(); return new[] { v.X, v.Y, v.Z }; }
        catch { return null; }
    }

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    /// <summary>Cheap pre-filter: every ACA runtime class is registered as AecDbSlab, AecDbRoof…</summary>
    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    /// <summary>Which block table records to walk when listing. Default is model space.</summary>
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

    /// <summary>Where a newly created object goes.</summary>
    private static ObjectId TargetSpaceId(AcadContext ctx, string space)
    {
        switch (space.Trim().ToLowerInvariant())
        {
            case "" or "current":
                return ctx.Db.CurrentSpaceId;

            case "model":
                return SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db);

            case "paper":
                return SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db);

            default:
                throw new AcadMcpException("bad_argument",
                    $"Unknown space '{space}'. Use 'model', 'paper' or 'current' when creating.");
        }
    }

    private static string RequireLayer(AcadContext ctx, string name)
    {
        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("layer_not_found", $"No layer named '{name}' in this drawing.");
        return name;
    }

    // ---- argument shapes

    private static List<Point3d>? PointsOpt(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object
            || !a.TryGetProperty(name, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        var points = new List<Point3d>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array)
                throw new AcadMcpException("bad_argument",
                    $"Argument '{name}' must be an array of points, each like [x, y] or [x, y, z].");

            var nums = item.EnumerateArray()
                           .Where(x => x.ValueKind == JsonValueKind.Number)
                           .Select(x => x.GetDouble())
                           .ToArray();

            if (nums.Length is < 2 or > 3)
                throw new AcadMcpException("bad_argument",
                    $"Every point in '{name}' must have 2 or 3 numbers; got {nums.Length}.");

            points.Add(new Point3d(nums[0], nums[1], nums.Length > 2 ? nums[2] : 0.0));
        }

        return points;
    }

    private static Point3d? PointOpt(JsonElement a, string name) =>
        Has(a, name) ? Args.Point(a, name) : null;

    private static Vector3d? VectorOpt(JsonElement a, string name)
    {
        if (!Has(a, name)) return null;
        var p = Args.Point(a, name);
        var v = new Vector3d(p.X, p.Y, p.Z);
        if (v.IsZeroLength())
            throw new AcadMcpException("bad_argument", $"Argument '{name}' must be a non-zero vector.");
        return v;
    }

    private static Vector3d NormalArg(JsonElement a, Vector3d fallback)
    {
        var v = VectorOpt(a, "normal");
        return v.HasValue ? v.Value.GetNormal() : fallback;
    }

    /// <summary>Slope as radians, accepting either 'slope' (radians) or 'slopeDegrees'.</summary>
    private static double? SlopeOpt(JsonElement a)
    {
        if (Has(a, "slope")) return Args.Dbl(a, "slope");
        if (Has(a, "slopeDegrees")) return Rad(Args.Dbl(a, "slopeDegrees"));
        return null;
    }

    /// <summary>
    /// The boundary for a slab or roof: an explicit point list, or the vertices of a closed
    /// LWPOLYLINE named by handle. Consecutive duplicates and a repeated closing point are removed,
    /// because a zero-length edge makes an invalid AEC face.
    /// </summary>
    private static (List<Point3d> Points, Vector3d Normal, string Source, bool HadBulges, bool WasClosed)
        Boundary(AcadContext ctx, JsonElement a)
    {
        List<Point3d> raw;
        var normal = Vector3d.ZAxis;
        string source;
        var hadBulges = false;
        var wasClosed = true;

        var explicitPoints = PointsOpt(a, "points");
        if (explicitPoints is not null)
        {
            raw = explicitPoints;
            source = "points";
        }
        else
        {
            var handle = Args.StrOpt(a, "polyline") ?? Args.StrOpt(a, "boundary");
            if (string.IsNullOrWhiteSpace(handle))
                throw new AcadMcpException("bad_argument",
                    "Supply either 'points' (a closed boundary as [[x,y,z], ...]) or 'polyline' "
                    + "(the handle of a closed polyline).");

            var id = Acad.ToObjectId(ctx.Db, handle!);
            var obj = ctx.Tr.GetObject(id, OpenMode.ForRead);

            if (obj is not Polyline pline)
                throw new AcadMcpException("bad_boundary",
                    $"Handle '{handle}' is {ClassName(id)}, not a light-weight polyline (LWPOLYLINE).",
                    "Pass 'points' instead, or convert the boundary to an LWPOLYLINE.");

            raw = new List<Point3d>();
            var count = pline.NumberOfVertices;
            for (var i = 0; i < count; i++)
            {
                raw.Add(pline.GetPoint3dAt(i));
                if (Math.Abs(TryD(() => pline.GetBulgeAt(i)) ?? 0.0) > 1e-12) hadBulges = true;
            }

            normal = TryVec(() => pline.Normal) is { } n ? new Vector3d(n[0], n[1], n[2]) : Vector3d.ZAxis;
            wasClosed = TryB(() => pline.Closed) ?? false;
            source = $"polyline:{handle}";
        }

        var points = new List<Point3d>();
        foreach (var p in raw)
            if (points.Count == 0 || points[points.Count - 1].DistanceTo(p) > Eps)
                points.Add(p);

        if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= Eps)
            points.RemoveAt(points.Count - 1);

        if (points.Count < 3)
            throw new AcadMcpException("bad_boundary",
                $"A slab or roof boundary needs at least 3 distinct points; got {points.Count}.");

        return (points, normal, source, hadBulges, wasClosed);
    }

#if ACA

    // ============================================================ ACA-only implementation

    // ---- styles

    private static AecRecord? StyleRecord(AcadContext ctx, AecEntity e)
    {
        try
        {
            if (e.StyleId.IsNull) return null;
            return ctx.Tr.GetObject(e.StyleId, OpenMode.ForRead) as AecRecord;
        }
        catch { return null; }
    }

    private static string? RecordName(AcadContext ctx, ObjectId id)
    {
        try
        {
            if (id.IsNull) return null;
            return (ctx.Tr.GetObject(id, OpenMode.ForRead) as AecRecord)?.Name;
        }
        catch { return null; }
    }

    private static ObjectIdCollection StyleRecords(AecDict dict, string kind)
    {
        try { return dict.Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                $"This drawing has no {kind} style dictionary.", ex.Message);
        }
    }

    /// <summary>Resolve a style by name, listing what is actually there when it misses.</summary>
    private static ObjectId StyleIdByName(AcadContext ctx, AecDict dict, string name, string kind)
    {
        ObjectId id;
        try { id = dict.GetAt(name); }
        catch { id = ObjectId.Null; }

        if (!id.IsNull) return id;

        var available = new List<string>();
        foreach (ObjectId recordId in StyleRecords(dict, kind))
        {
            var recordName = RecordName(ctx, recordId);
            if (!string.IsNullOrEmpty(recordName)) available.Add(recordName!);
        }

        available.Sort(StringComparer.OrdinalIgnoreCase);

        throw new AcadMcpException("style_not_found",
            $"No {kind} style named '{name}' in this drawing.",
            available.Count == 0
                ? $"The {kind} style dictionary is empty."
                : "Available: " + string.Join(", ", available.Take(40)));
    }

    private static AecDict SlabStyleDictionary(AcadContext ctx, bool roofSlab) =>
        roofSlab ? new AecRoofSlabStyleDict(ctx.Db) : (AecDict)new AecSlabStyleDict(ctx.Db);

    /// <summary>
    /// The drawing's Standard style WITHOUT creating it. DictionarySlabStyle.GetStandardStyle
    /// CREATES the Standard record when the drawing has none — a write, which a non-mutating
    /// listing tool must never perform (Acad.Read aborts its transaction, but ACA dictionary
    /// writes are not all transaction-scoped). Dictionary.Has/GetAt are pure lookups.
    /// </summary>
    private static ObjectId StandardStyleIdNoCreate(AcadContext ctx, AecDict dict)
    {
        try
        {
            if (!dict.Has("Standard", ctx.Tr)) return ObjectId.Null;
            return dict.GetAt("Standard");
        }
        catch { return ObjectId.Null; }
    }

    private static ObjectId StandardSlabStyle(AcadContext ctx, bool roofSlab)
    {
        ObjectId id;
        try
        {
            id = roofSlab
                ? AecRoofSlabStyleDict.GetStandardStyle(ctx.Db)
                : AecSlabStyleDict.GetStandardStyle(ctx.Db);
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("style_not_found",
                $"Could not resolve the Standard {(roofSlab ? "roof slab" : "slab")} style in this drawing.",
                ex.Message);
        }

        if (id.IsNull)
            throw new AcadMcpException("style_not_found",
                $"This drawing has no Standard {(roofSlab ? "roof slab" : "slab")} style.",
                "Pass 'style' with the name of an existing style.");

        return id;
    }

    private static object StyleInfo(AcadContext ctx, AecSlabStyle style, ObjectId standardId)
    {
        var slope = TryD(() => style.DefaultSlope);

        // Enumerated, not indexed: SlabStyleComponentCollection's only confirmed sequential
        // accessor in the declared API surface is 'IEnumerator GetEnumerator()'. Its 'Item' member
        // is almost certainly an indexer, but "almost certainly" is not worth a build cycle.
        var components = new List<string>();
        try
        {
            foreach (Autodesk.Aec.Arch.DatabaseServices.SlabStyleComponent component in style.Components)
            {
                var name = TryS(() => component.Name);
                components.Add(name ?? $"#{components.Count}");
            }
        }
        catch { }

        return new
        {
            name = TryS(() => style.Name),
            handle = TryS(() => style.Handle.ToString()),
            isStandard = style.ObjectId == standardId,
            defaultThickness = TryD(() => style.DefaultThickness),
            defaultSlope = slope,
            defaultSlopeDegrees = slope.HasValue ? Deg(slope.Value) : (double?)null,
            defaultVerticalOffset = TryD(() => style.DefaultVerticalOffset),
            defaultHorizontalOffset = TryD(() => style.DefaultHorizontalOffset),
            useThickness = TryB(() => style.UseThickness),
            useSlope = TryB(() => style.UseSlope),
            useVerticalOffset = TryB(() => style.UseVerticalOffset),
            useHorizontalOffset = TryB(() => style.UseHorizontalOffset),
            usePerimeterEdgeStyle = TryB(() => style.UsePerimeterEdgeStyle),
            useBaselineEdgeStyle = TryB(() => style.UseBaselineEdgeStyle),
            isVariableThickness = TryB(() => style.IsVariableThickness),
            perimeterEdgeStyle = RecordName(ctx, TryStyleId(() => style.DefaultPerimeterEdgeStyleId)),
            baseEdgeStyle = RecordName(ctx, TryStyleId(() => style.DefaultBaseEdgeStyleId)),
            componentCount = components.Count,
            components,
            isLocked = TryB(() => style.IsLocked),
        };
    }

    private static ObjectId TryStyleId(Func<ObjectId> f)
    {
        try { return f(); } catch { return ObjectId.Null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListStyles(JsonElement a, bool roofSlab) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var kind = roofSlab ? "roof slab" : "slab";
        var dict = SlabStyleDictionary(ctx, roofSlab);

        // Deliberately NOT GetStandardStyle(db): that creates the record when it is missing, and
        // this tool is registered mutating:false.
        var standardId = StandardStyleIdNoCreate(ctx, dict);

        var rows = new List<(string Key, object Data)>();

        foreach (ObjectId id in StyleRecords(dict, kind))
        {
            if (id.IsNull || id.IsErased) continue;

            AecSlabStyle? style;
            try { style = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecSlabStyle; }
            catch { continue; }
            if (style is null) continue;

            // RoofSlabStyle derives from SlabStyle, so filter on the concrete type.
            var isRoofStyle = style is Autodesk.Aec.Arch.DatabaseServices.RoofSlabStyle;
            if (isRoofStyle != roofSlab) continue;

            rows.Add((TryS(() => style.Name) ?? id.Handle.ToString(), StyleInfo(ctx, style, standardId)));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListSlabStyles(JsonElement a) => ListStyles(a, roofSlab: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListRoofSlabStyles(JsonElement a) => ListStyles(a, roofSlab: true);

    // ---- scanning

    /// <summary>Classify the slab/roof objects in scope. RoofSlab is tested before Slab: both
    /// derive from SlabBase, and a RoofSlab is not a Slab.</summary>
    private static List<(string Type, string Handle, ObjectId Id)> Scan(AcadContext ctx, JsonElement a)
    {
        var deep = Args.BoolOpt(a, "deepScan", false);
        var found = new List<(string Type, string Handle, ObjectId Id)>();

        foreach (var spaceId in TargetSpaces(ctx, Scope(a)))
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead); }
            catch { continue; }

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                if (!deep && !LooksAec(id)) continue;

                DBObject obj;
                try { obj = ctx.Tr.GetObject(id, OpenMode.ForRead); }
                catch { continue; }

                var type = obj switch
                {
                    AecRoofSlab => "RoofSlab",
                    AecSlab => "Slab",
                    AecRoof => "Roof",
                    AecSlabBase => "SlabBase",
                    _ => null,
                };

                if (type is null) continue;
                found.Add((type, id.Handle.ToString(), id));
            }
        }

        return found;
    }

    // ---- slab reporting

    private static List<double>? EdgeOffsets(AecSlabBase slab)
    {
        var count = TryI(() => slab.Face.EdgeCount);
        if (count is null || count.Value <= 0) return null;

        var offsets = new List<double>();
        var cap = Math.Min(count.Value, 256);
        for (var i = 0; i < cap; i++)
        {
            var offset = TryD(() => slab.GetEdgeOffsetAt(i));
            if (offset is null) return offsets.Count > 0 ? offsets : null;
            offsets.Add(offset.Value);
        }
        return offsets;
    }

    private static double? SlabPerimeter(AecSlabBase slab)
    {
        var fromProfile = TryD(() =>
        {
            AecProfile profile = slab.Face.GetProfile();
            return profile.Perimeter(Eps);
        });
        if (fromProfile.HasValue && fromProfile.Value > 0) return fromProfile;

        return TryD(() =>
        {
            var count = slab.Face.EdgeCount;
            var total = 0.0;
            for (var i = 0; i < count; i++) total += slab.EdgeLengthAtOffset(i);
            return total;
        });
    }

    private static object SlabInfo(AcadContext ctx, AecSlabBase slab, string type)
    {
        var style = StyleRecord(ctx, slab);
        var slope = TryD(() => slab.Slope);
        var exterior = TryD(() => slab.ExteriorFaceOffset);
        var interior = TryD(() => slab.InteriorFaceOffset);

        double? thickness = exterior.HasValue && interior.HasValue
            ? Math.Abs(exterior.Value - interior.Value)
            : null;

        return new
        {
            handle = TryS(() => slab.Handle.ToString()),
            type,
            objectClass = ClassName(slab.ObjectId),
            layer = TryS(() => slab.Layer),
            description = TryS(() => slab.Description),
            styleName = TryS(() => style?.Name),
            styleHandle = TryS(() => style?.Handle.ToString()),
            baseThickness = TryD(() => slab.BaseThickness),
            thickness,
            exteriorFaceOffset = exterior,
            interiorFaceOffset = interior,
            slope,
            slopeDegrees = slope.HasValue ? Deg(slope.Value) : (double?)null,
            verticalOffset = TryD(() => slab.VerticalOffset),
            horizontalOffset = TryD(() => slab.HorizontalOffset),
            grossArea = TryD(() => slab.GrossArea),
            netArea = TryD(() => slab.NetArea),
            faceArea = TryD(() => slab.Face.Area),
            perimeter = SlabPerimeter(slab),
            edgeCount = TryI(() => slab.Face.EdgeCount),
            edgeOffsets = EdgeOffsets(slab),
            elevation = TryD(() => slab.Location.Z),
            location = TryPt(() => slab.Location),
            pivotPoint = TryPt(() => slab.PivotPoint),
            centroid = TryPt(() => slab.Centroid),
            normal = TryVec(() => slab.Normal),
            rotation = TryD(() => slab.Rotation),
            highPoint = TryD(() => slab.HighPoint),
            lowPoint = TryD(() => slab.LowPoint),
            overhangIsConstant = TryB(() => slab.OverhangIsConstant),
            verticalOffsetIsConstant = TryB(() => slab.VerticalOffsetIsConstant),
            fasciaHeightIsConstant = TryB(() => slab.FasciaHeightIsConstant),
            hasEdgeStyles = TryB(() => slab.DoesSlabEdgesHaveStyle),
            interferenceCount = TryI(() => slab.InterferencesCount),
            customGeometryBodies = TryI(() => slab.CustomGeometryBodyCount),
            isValid = TryB(() => slab.IsValid),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListSlabs(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var wanted = (Args.StrOpt(a, "type", "all") ?? "all").Trim().ToLowerInvariant();

        if (wanted is not ("all" or "slab" or "roofslab"))
            throw new AcadMcpException("bad_argument",
                $"Unknown type '{wanted}'. Use 'slab', 'roofSlab' or 'all'.");

        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan(ctx, a))
        {
            if (entry.Type == "Roof") continue;

            var isRoofSlab = entry.Type == "RoofSlab";
            if (wanted == "slab" && isRoofSlab) continue;
            if (wanted == "roofslab" && !isRoofSlab) continue;

            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecSlabBase slab)
                    rows.Add((entry.Handle, SlabInfo(ctx, slab, entry.Type)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    // ---- roof reporting

    private static object? RoofProfileInfo(AecRoof roof)
    {
        AecRoofProfile? profile;
        try { profile = roof.RoofProfile; }
        catch { return null; }
        if (profile is null) return null;

        return new
        {
            segmentCount = TryI(() => profile.SegmentCount),
            countSegments = TryI(() => profile.CountSegments),
            vertexCount = TryI(() => profile.Vertices.Count),
            ringCount = TryI(() => profile.RingCount),
            area = TryD(() => profile.Area(Eps)),
            perimeter = TryD(() => profile.Perimeter(Eps)),
            isGable = TryB(() => profile.IsGable),
            minEavesHeight = TryD(() => profile.MinEavesHeight),
            maxEavesHeight = TryD(() => profile.MaxEavesHeight),
            minHeight = TryD(() => profile.MinHeight),
            isSelfIntersecting = TryB(() => profile.IsSelfIntersecting),
        };
    }

    private static object RoofInfo(AcadContext ctx, AecRoof roof)
    {
        var slope = TryD(() => roof.Slope);
        var upperSlope = TryD(() => roof.UpperSlope);
        var style = StyleRecord(ctx, roof);

        return new
        {
            handle = TryS(() => roof.Handle.ToString()),
            type = "Roof",
            objectClass = ClassName(roof.ObjectId),
            layer = TryS(() => roof.Layer),
            description = TryS(() => roof.Description),
            styleName = TryS(() => style?.Name),
            slope,
            slopeDegrees = slope.HasValue ? Deg(slope.Value) : (double?)null,
            upperSlope,
            upperSlopeDegrees = upperSlope.HasValue ? Deg(upperSlope.Value) : (double?)null,
            rise = TryD(() => roof.Rise),
            run = TryD(() => roof.Run),
            upperRise = TryD(() => roof.UpperRise),
            upperRun = TryD(() => roof.UpperRun),
            plateHeight = TryD(() => roof.PlateHeight),
            upperPlateHeight = TryD(() => roof.UpperPlateHeight),
            overhang = TryD(() => roof.Overhang),
            thickness = TryD(() => roof.Thickness),
            thicknessDirection = TryS(() => roof.ThicknessDirection.ToString()),
            edgeShape = TryS(() => roof.EdgeShape.ToString()),
            roofBodies = TryI(() => roof.CountRoofBodies),
            roofLines = TryI(() => roof.CountRoofLines),
            elevation = TryD(() => roof.Location.Z),
            location = TryPt(() => roof.Location),
            normal = TryVec(() => roof.Normal),
            rotation = TryD(() => roof.Rotation),
            isValid = TryB(() => roof.IsValid),
            profile = RoofProfileInfo(roof),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListRoofs(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan(ctx, a))
        {
            if (entry.Type != "Roof") continue;

            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecRoof roof)
                    rows.Add((entry.Handle, RoofInfo(ctx, roof)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    // ---- creation

    /// <summary>
    /// Slab and RoofSlab share a creation path: the two classes differ only in their style
    /// dictionary and in which edge behaviour ACA applies — every geometry member used here is
    /// declared on their common base, SlabBase.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateSlabBase(JsonElement a, bool roofSlab) => Acad.Write<object?>(ctx =>
    {
        var kind = roofSlab ? "roof slab" : "slab";
        var boundary = Boundary(ctx, a);
        var normal = NormalArg(a, boundary.Normal);
        var warnings = new List<string>();

        if (boundary.HadBulges)
            warnings.Add("The source polyline has arc segments; they were flattened to chords.");
        if (!boundary.WasClosed)
            warnings.Add("The source polyline is open; it was closed implicitly from the last point back to the first.");

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var styleName = Args.StrOpt(a, "style");
        var styleId = string.IsNullOrWhiteSpace(styleName)
            ? StandardSlabStyle(ctx, roofSlab)
            : StyleIdByName(ctx, SlabStyleDictionary(ctx, roofSlab), styleName!, kind);

        // Style first, then geometry, then append — the order the ACA object needs: an AEC entity
        // appended without a style is an invalid object.
        AecSlabBase slab = roofSlab ? new AecRoofSlab() : (AecSlabBase)new AecSlab();
        slab.SetDatabaseDefaults(ctx.Db);

        // SetToStandard is NOT optional: it is what initialises an AEC entity's internal state
        // against the drawing. Swallowing a failure here would append a half-built AEC object,
        // which is exactly the class of thing that corrupts a drawing. Fail instead.
        try { slab.SetToStandard(ctx.Db); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("aec_init_failed",
                $"AutoCAD Architecture could not initialise a new {kind} against this drawing, "
                + "so nothing was created.", ex.Message);
        }

        slab.StyleId = styleId;

        var points = new Point3dCollection();
        foreach (var p in boundary.Points) points.Add(p);

        var basePoint = PointOpt(a, "basePoint");
        var direction = VectorOpt(a, "direction");

        try
        {
            if (basePoint.HasValue && direction.HasValue)
                slab.SetBaseCurve(points, normal, basePoint.Value, direction.Value);
            else
                slab.SetBaseCurve(points, normal, Args.BoolOpt(a, "useAsIs", true));
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("bad_boundary",
                $"AutoCAD Architecture rejected this boundary for a {kind}.", ex.Message);
        }

        btr.AppendEntity(slab);
        ctx.Tr.AddNewlyCreatedDBObject(slab, true);

        // Layer key styles resolve against the database, so this only works once appended.
        try { slab.SetDefaultLayer(); } catch { }

        var layer = Args.StrOpt(a, "layer");
        if (!string.IsNullOrWhiteSpace(layer)) slab.Layer = RequireLayer(ctx, layer!);

        var description = Args.StrOpt(a, "description");
        if (description is not null) { try { slab.Description = description; } catch { } }

        if (Has(a, "thickness")) slab.BaseThickness = Args.Dbl(a, "thickness");
        if (Has(a, "verticalOffset")) slab.VerticalOffset = Args.Dbl(a, "verticalOffset");
        if (Has(a, "horizontalOffset")) slab.HorizontalOffset = Args.Dbl(a, "horizontalOffset");
        if (Has(a, "pivotPoint")) slab.PivotPoint = Args.Point(a, "pivotPoint");

        var slope = SlopeOpt(a);
        if (slope.HasValue) slab.Slope = slope.Value;

        // Gives every edge the style's default edge style — without this a slab's edges have no
        // style record of their own.
        try { slab.InitializeEdgeStyles(); } catch { }

        int? overhangEdges = null;
        string? overhangError = null;
        if (Has(a, "overhang"))
        {
            var overhang = Args.Dbl(a, "overhang");
            var edges = TryI(() => slab.Face.EdgeCount) ?? 0;
            var applied = 0;

            for (var i = 0; i < edges; i++)
            {
                try { slab.SetEdgeOffsetAt(i, overhang, true); applied++; }
                catch (System.Exception ex) { overhangError ??= ex.Message; }
            }

            overhangEdges = applied;
            if (applied < edges)
                warnings.Add($"Overhang applied to {applied} of {edges} edges.");
        }

        try { slab.UpdateSlab(); } catch { }

        // Refuse to commit a malformed AEC object: throwing here leaves the transaction
        // un-committed, so the drawing is untouched.
        // '!= true', not '== false': if the new object cannot even report its own validity we do
        // not commit it. A failed creation is recoverable; a committed malformed AEC object is not.
        var slabValid = TryB(() => slab.IsValid);
        if (slabValid != true)
            throw new AcadMcpException("invalid_geometry",
                slabValid is null
                    ? $"The new {kind} could not report whether it is valid, so nothing was created."
                    : $"The {kind} built from this boundary reports itself invalid, so nothing was created.",
                "Check that the boundary is planar, closed and non-self-intersecting.");

        var style = StyleRecord(ctx, slab) as AecSlabStyle;

        return new
        {
            handle = TryS(() => slab.Handle.ToString()),
            type = roofSlab ? "RoofSlab" : "Slab",
            space = TryS(() => btr.Name),
            boundarySource = boundary.Source,
            boundaryPoints = boundary.Points.Count,
            normal = new[] { normal.X, normal.Y, normal.Z },
            styleName = TryS(() => style?.Name),
            // With UseThickness set, the style's DefaultThickness wins over BaseThickness.
            styleOverridesThickness = TryB(() => style != null && style.UseThickness),
            styleOverridesSlope = TryB(() => style != null && style.UseSlope),
            overhangEdges,
            overhangError,
            warnings = warnings.Count > 0 ? warnings : null,
            slab = SlabInfo(ctx, slab, roofSlab ? "RoofSlab" : "Slab"),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateSlab(JsonElement a) => CreateSlabBase(a, roofSlab: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateRoofSlab(JsonElement a) => CreateSlabBase(a, roofSlab: true);

    /// <summary>
    /// Roof is not a SlabBase: it owns a RoofProfile of RoofSegments, one per boundary edge, each
    /// carrying its own plate height, slope and overhang, and it is realised by CalculateRoof().
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateRoof(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var boundary = Boundary(ctx, a);
        var normal = NormalArg(a, boundary.Normal);
        var warnings = new List<string>();

        if (boundary.HadBulges)
            warnings.Add("The source polyline has arc segments; they were flattened to chords.");
        if (!boundary.WasClosed)
            warnings.Add("The source polyline is open; it was closed implicitly from the last point back to the first.");

        var slope = SlopeOpt(a)
            ?? throw new AcadMcpException("bad_argument",
                "A roof needs a pitch: pass 'slope' (radians) or 'slopeDegrees'.");

        var plateHeight = Args.DblOpt(a, "plateHeight", 0.0);
        var overhang = Args.DblOpt(a, "overhang", 0.0);

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var roof = new AecRoof();
        roof.SetDatabaseDefaults(ctx.Db);

        // As in CreateSlabBase: a failed SetToStandard means the AEC object is not initialised,
        // and an uninitialised AEC object must never reach AppendEntity.
        try { roof.SetToStandard(ctx.Db); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("aec_init_failed",
                "AutoCAD Architecture could not initialise a new roof against this drawing, "
                + "so nothing was created.", ex.Message);
        }

        // Establishes the roof's placement and coordinate system from the first boundary edge.
        try { roof.SetLocation(boundary.Points[0], boundary.Points[1], normal, Tol); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("bad_boundary",
                "AutoCAD Architecture rejected the first boundary edge as the roof's location.", ex.Message);
        }

        // The profile is 2d in the roof's own ECS, so take the boundary through the inverse of
        // whatever ECS SetLocation produced. If that is identity, this is a plain XY projection.
        var ecs = Matrix3d.Identity;
        try { ecs = roof.GeoEcs; } catch { }

        var toLocal = Matrix3d.Identity;
        try { toLocal = ecs.Inverse(); } catch { }

        var local = new List<Point2d>();
        foreach (var p in boundary.Points)
        {
            var q = p.TransformBy(toLocal);
            local.Add(new Point2d(q.X, q.Y));
        }

        var profile = new AecRoofProfile();
        var segments = 0;

        for (var i = 0; i < local.Count; i++)
        {
            var start = local[i];
            var end = local[(i + 1) % local.Count];
            if (start.GetDistanceTo(end) <= Eps) continue;

            var segment = new AecRoofSegment(new LineSegment2d(start, end));
            segment.SetPeaked(plateHeight, slope);
            if (Math.Abs(overhang) > 0) { try { segment.SetOverhang(overhang, Eps); } catch { } }

            segments++;
            try { segment.Id = segments; } catch { }

            profile.Segments.Add(segment);
        }

        if (segments < 3)
            throw new AcadMcpException("bad_boundary",
                $"A roof needs at least 3 edges; this boundary produced {segments}.");

        try { profile.CorrectRoofProfile(Tol); } catch { }

        // A malformed contour must NOT reach CalculateRoof. Native ACA roof construction over a
        // non-closed or self-intersecting profile is a crash risk rather than a clean exception,
        // and a crash costs the user their unsaved work. Refuse before the profile is attached
        // to the roof and before anything is appended. (This validity result was previously
        // computed and then ignored.)
        var profileValid = TryB(() => profile.IsValid(true, Tol));
        if (profileValid != true)
            throw new AcadMcpException("bad_boundary",
                profileValid is null
                    ? "The roof profile built from this boundary could not be validated, so nothing was created."
                    : "The roof profile built from this boundary is not a valid closed roof contour, so nothing was created.",
                "The boundary must be planar, closed, non-self-intersecting and free of zero-length edges.");

        if (TryB(() => profile.IsSelfIntersecting) == true)
            throw new AcadMcpException("bad_boundary",
                "The roof profile built from this boundary self-intersects, so nothing was created.",
                "Give create_roof a simple (non-self-intersecting) closed boundary.");

        try { roof.RoofProfile = profile; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("bad_boundary",
                "AutoCAD Architecture rejected the roof profile built from this boundary.", ex.Message);
        }

        btr.AppendEntity(roof);
        ctx.Tr.AddNewlyCreatedDBObject(roof, true);

        try { roof.SetDefaultLayer(); } catch { }

        var layer = Args.StrOpt(a, "layer");
        if (!string.IsNullOrWhiteSpace(layer)) roof.Layer = RequireLayer(ctx, layer!);

        var description = Args.StrOpt(a, "description");
        if (description is not null) { try { roof.Description = description; } catch { } }

        if (Has(a, "thickness"))
        {
            var thickness = Args.Dbl(a, "thickness");
            var wanted = (Args.StrOpt(a, "thicknessDirection", "roof") ?? "roof").Trim().ToLowerInvariant();
            var direction = wanted switch
            {
                "roof" => AecThicknessDirection.Roof,
                "floor" => AecThicknessDirection.Floor,
                _ => throw new AcadMcpException("bad_argument",
                    $"Unknown thicknessDirection '{wanted}'. Use 'roof' or 'floor'."),
            };

            try { roof.SetThickness(thickness, direction, Eps); }
            catch (System.Exception ex) { warnings.Add($"Thickness not applied: {ex.Message}"); }
        }

        try { roof.CalculateRoof(); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("roof_construction_failed",
                "CalculateRoof failed for this boundary, so nothing was created.", ex.Message);
        }

        var bodies = TryI(() => roof.CountRoofBodies) ?? 0;
        var roofValid = TryB(() => roof.IsValid);

        // Nothing is committed unless the roof actually built itself.
        if (roofValid == false || bodies <= 0)
            throw new AcadMcpException("roof_construction_failed",
                roofValid == false
                    ? $"The roof reports itself invalid ({bodies} bodies), so nothing was created."
                    : "The roof calculated to zero bodies, so nothing was created.",
                "Check that the boundary is planar, closed, non-self-intersecting and that the slope is sane.");

        return new
        {
            handle = TryS(() => roof.Handle.ToString()),
            type = "Roof",
            space = TryS(() => btr.Name),
            boundarySource = boundary.Source,
            boundaryPoints = boundary.Points.Count,
            segments,
            profileWasValid = profileValid,
            normal = new[] { normal.X, normal.Y, normal.Z },
            requestedSlope = slope,
            requestedSlopeDegrees = Deg(slope),
            requestedPlateHeight = plateHeight,
            requestedOverhang = overhang,
            warnings = warnings.Count > 0 ? warnings : null,
            roof = RoofInfo(ctx, roof),
        };
    });

    // ---- edit

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetSlabProperties(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);

        DBObject obj;
        try { obj = ctx.Tr.GetObject(id, OpenMode.ForWrite); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("not_writable", $"Could not open handle '{handle}' for write.", ex.Message);
        }

        if (obj is not AecSlabBase slab)
            throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is {ClassName(id)}, not an AEC Slab or RoofSlab.");

        var isRoofSlab = slab is AecRoofSlab;
        var type = isRoofSlab ? "RoofSlab" : slab is AecSlab ? "Slab" : "SlabBase";
        var before = SlabInfo(ctx, slab, type);
        var changed = new List<string>();

        if (Has(a, "thickness")) { slab.BaseThickness = Args.Dbl(a, "thickness"); changed.Add("thickness"); }
        if (Has(a, "verticalOffset")) { slab.VerticalOffset = Args.Dbl(a, "verticalOffset"); changed.Add("verticalOffset"); }
        if (Has(a, "horizontalOffset")) { slab.HorizontalOffset = Args.Dbl(a, "horizontalOffset"); changed.Add("horizontalOffset"); }
        if (Has(a, "pivotPoint")) { slab.PivotPoint = Args.Point(a, "pivotPoint"); changed.Add("pivotPoint"); }

        var slope = SlopeOpt(a);
        if (slope.HasValue) { slab.Slope = slope.Value; changed.Add("slope"); }

        var description = Args.StrOpt(a, "description");
        if (description is not null) { slab.Description = description; changed.Add("description"); }

        var styleName = Args.StrOpt(a, "style");
        if (!string.IsNullOrWhiteSpace(styleName))
        {
            var kind = isRoofSlab ? "roof slab" : "slab";
            slab.StyleId = StyleIdByName(ctx, SlabStyleDictionary(ctx, isRoofSlab), styleName!, kind);
            changed.Add("style");
        }

        var layer = Args.StrOpt(a, "layer");
        if (!string.IsNullOrWhiteSpace(layer)) { slab.Layer = RequireLayer(ctx, layer!); changed.Add("layer"); }

        if (Has(a, "elevation"))
        {
            // A slab has no elevation property; move it in world Z from where its insertion point is.
            var target = Args.Dbl(a, "elevation");
            var current = TryD(() => slab.Location.Z)
                ?? throw new AcadMcpException("not_supported",
                    "This slab's insertion point could not be read, so 'elevation' cannot be applied.");

            var delta = target - current;
            if (Math.Abs(delta) > Eps)
            {
                try { slab.TranslateBy(new Vector3d(0.0, 0.0, delta)); }
                catch (System.Exception ex)
                {
                    throw new AcadMcpException("move_failed",
                        "Could not move the slab to the requested elevation.", ex.Message);
                }
            }
            changed.Add("elevation");
        }

        if (changed.Count == 0)
            throw new AcadMcpException("bad_argument",
                "Nothing to change. Pass at least one of thickness, style, elevation, verticalOffset, "
                + "horizontalOffset, slope, slopeDegrees, pivotPoint, description or layer.");

        try { slab.UpdateSlab(); } catch { }

        if (TryB(() => slab.IsValid) == false)
            throw new AcadMcpException("invalid_geometry",
                "That edit leaves the slab invalid, so it was rolled back and nothing changed.");

        return new
        {
            handle,
            type,
            changed,
            before,
            after = SlabInfo(ctx, slab, type),
        };
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListSlabStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListRoofSlabStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateSlab(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateRoofSlab(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateRoof(JsonElement a) => throw Unavailable();
    private static Task<object?> SetSlabProperties(JsonElement a) => throw Unavailable();
    private static Task<object?> ListSlabs(JsonElement a) => throw Unavailable();
    private static Task<object?> ListRoofs(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
