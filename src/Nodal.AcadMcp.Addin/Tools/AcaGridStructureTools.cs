using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are referenced ONLY through explicit aliases, and only inside #if ACA.
// Same reasoning as AcaTools.cs: 'Entity', 'DBObject', 'Dictionary', 'Curve' and 'Justification'
// all collide across Autodesk.Aec.*, Autodesk.AutoCAD.DatabaseServices and System, and on a vanilla
// AutoCAD box these assemblies do not exist, so nothing outside the guarded region may name them.
#if ACA
using AecEntity = Autodesk.Aec.DatabaseServices.Entity;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecLayoutTool = Autodesk.Aec.DatabaseServices.LayoutTool;
using AecLayoutGrid2d = Autodesk.Aec.DatabaseServices.LayoutGrid2d;
using AecLayoutGrid3d = Autodesk.Aec.DatabaseServices.LayoutGrid3d;
using AecLayoutCurve = Autodesk.Aec.DatabaseServices.LayoutCurve;
using AecGridUv = Autodesk.Aec.DatabaseServices.GridUv;
using AecGridUvw = Autodesk.Aec.DatabaseServices.GridUvw;
using AecGridUvRadial = Autodesk.Aec.DatabaseServices.GridUvRadial;
using AecGridType = Autodesk.Aec.DatabaseServices.GridType;
using AecUvLayoutMode = Autodesk.Aec.DatabaseServices.UVLayoutModeType;
using AecCurveLayoutMode = Autodesk.Aec.DatabaseServices.LayoutModeType;

using AecsColumnGrid = Autodesk.Aec.Structural.DatabaseServices.ColumnGrid;
using AecsEnhancedGridData = Autodesk.Aec.Structural.DatabaseServices.EnhancedColumnGridData;
using AecsMember = Autodesk.Aec.Structural.DatabaseServices.Member;
using AecsMemberStyle = Autodesk.Aec.Structural.DatabaseServices.MemberStyle;
using AecsMemberStyleDict = Autodesk.Aec.Structural.DatabaseServices.DictionaryMemberStyle;
using AecsNodeShapeDict = Autodesk.Aec.Structural.DatabaseServices.DictionaryMemberNodeShape;
using AecsNodeShape = Autodesk.Aec.Structural.DatabaseServices.MemberNodeShape;
using AecsMemberComponent = Autodesk.Aec.Structural.DatabaseServices.MemberComponent;
using AecsMemberNodeComponent = Autodesk.Aec.Structural.DatabaseServices.MemberNodeComponent;
using AecsMemberType = Autodesk.Aec.Structural.DatabaseServices.MemberType;
using AecsJustification = Autodesk.Aec.Structural.DatabaseServices.Justification;
using AecsComponentsJust = Autodesk.Aec.Structural.DatabaseServices.ComponentsJustification;
using AecsOverallJust = Autodesk.Aec.Structural.DatabaseServices.OverallJustification;
using AecsTrimType = Autodesk.Aec.Structural.DatabaseServices.TrimType;
using AecsDetailLevel = Autodesk.Aec.Structural.DatabaseServices.DetailLevel;
using AecsMemberNode = Autodesk.Aec.Structural.MemberNode;
using AecsMemberNodeData = Autodesk.Aec.Structural.MemberNodeData;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture / Structural: column grids, the generic layout tools (2d grid, 3d grid,
/// layout curve) that other AEC objects anchor to, and structural members with their styles.
///
/// JIT SAFETY. Identical contract to AcaTools: every handler goes through <see cref="Reg"/>, which
/// calls <see cref="AcaSupport.Require"/> before entering the implementation. Implementations that
/// name Aec types are separate [MethodImpl(NoInlining)] methods whose signatures are ACA-free
/// (JsonElement) -> Task&lt;object?&gt;, so the JIT never resolves an Aec type on a vanilla box.
///
/// CREATION SEQUENCE (verified against the declared API surface):
///   new T()                        -- every type used here has a public parameterless ctor
///   SetToStandard(db)              -- Autodesk.Aec.DatabaseServices.Entity.SetToStandard(Database):
///                                     gives the object its standard AEC defaults / display props.
///                                     An AEC object appended without this is not a valid object.
///   SetDatabaseDefaults(db)        -- Autodesk.AutoCAD.DatabaseServices.Entity.SetDatabaseDefaults,
///                                     which internally runs the protected subSetDatabaseDefaults
///                                     override the ACA samples call explicitly in ARX.
///   btr.AppendEntity + tr.AddNewlyCreatedDBObject
///   ...then geometry / style / layout, which need the object to be database-resident.
/// If any step before the append throws, the half-built object is disposed rather than leaked; if
/// anything after it throws, Acad.Write aborts the transaction and the append is undone.
///
/// NOT IMPLEMENTED / DIVERGENCES FROM THE BRIEF (could not be confirmed from the declared API surface):
///  * Radial and custom grids are not created. LayoutGrid2d.GridType is settable to UVRadial /
///    UVCustom and BaseGrid then presents GridUvRadial (UInsideRadius, VAngleSpacing, VAxisAngle,
///    VStartAngleOffset, VEndAngleOffset) or GridCustom (segment based), but the order in which the
///    type switch and the base-grid rebuild happen is not derivable from the declared API surface, so
///    creation is restricted to UVRectangular. Radial grids are still fully REPORTED by the list
///    tools, including their radial properties.
///  * Enhanced-custom-column-grid data (bubbles, labels, GridTypeEx, LabelStyleName) is reported but
///    never written. EnhancedColumnGridData.IsEnhancedCustomGrid is settable, but ColumnGrid exposes
///    only a get-only IsEnhancedCustomColumnGrid and a ConvertFrom(LayoutGrid2d); flipping the flag
///    without the rest of that conversion would leave a half-converted object in the drawing.
///  * LayoutGrid3d creation is not offered: GridUvw's W axis is settable, but a 3d grid also needs
///    its volume layout established and VolumeLayoutTool exposes only readers.
///  * MemberNodeShape creation (profiles for new member styles) is not offered: AddCurve/SetProfile
///    exist, but building a valid Autodesk.Aec.Geometry.Profile from JSON is not confirmable here.
///  * Member has no settable start/end point pair. Geometry goes through Member.Set(start, end) /
///    Set(start, end, normal); the endpoints read back off Aec Entity.BaseCurve, guarded.
///  * GridUv.UParameters / VParameters are get-only DoubleCollections, so a non-uniform grid is
///    built with ULayoutMode = Manual plus AppendGridColumn / AppendGridRow, and the resulting
///    parameters are echoed back in the response so the caller can see exactly where lines landed.
/// </summary>
internal static class AcaGridStructureTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;
    private const int MaxNodesReported = 2000;

    /// <summary>Grid parameters are distances in drawing units; this separates two distinct lines.</summary>
    private const double ParameterTolerance = 1e-8;

    internal static void Register()
    {
        Reg("create_column_grid",
            "Create an AEC structural column grid (AecsDbColumnGrid) in model space. X/Y bays are "
            + "either uniform — 'xBays' (count) with 'xSpacing', same for y — or explicitly "
            + "non-uniform via 'xSpacings' / 'ySpacings' (arrays of bay widths, laid out as grid "
            + "lines at the running total, so [20,20,30] gives lines at 0, 20, 40, 70). Optional: "
            + "'origin' [x,y,z] (default 0,0,0), 'rotation' (degrees) or 'rotationRadians', 'normal' "
            + "[x,y,z], 'xAxisLength'/'yAxisLength' overrides, 'xStartOffset'/'xEndOffset' and the y "
            + "equivalents, 'layer', 'space' (model|paper|current|<layout name>, default current). "
            + "Rectangular only — see the module notes for radial/custom. One undo step.",
            mutating: true, CreateColumnGrid);

        Reg("create_layout_grid_2d",
            "Create a generic AEC 2d layout grid (AecDbLayoutGrid2d) — the anchoring host used to "
            + "array objects on a rectangular grid. Identical arguments to create_column_grid; the "
            + "difference is the object class, so this one carries no column-grid labelling data.",
            mutating: true, CreateLayoutGrid2d);

        Reg("create_layout_curve",
            "Create an AEC layout curve (AecDbLayoutCurve) along an existing curve entity — the "
            + "anchoring host used to array objects along a line, arc or polyline. Required: "
            + "'curveHandle'. Optional: 'layoutMode' (Manual|Repeat|SpaceOut, default Repeat), "
            + "'spacing', 'startOffset', 'endOffset', 'positions' (array of node positions along the "
            + "curve, used with Manual), 'layer', 'space'. One undo step.",
            mutating: true, CreateLayoutCurve);

        Reg("list_member_styles",
            "Every structural MemberStyle in the drawing with its components (name, priority, "
            + "offsets, material and the node shape at each end). Args: includeComponents (default "
            + "true), limit (default 500), offset.",
            mutating: false, ListMemberStyles);

        Reg("list_member_node_shapes",
            "Every MemberNodeShape (the cross-section profiles member styles extrude) with the "
            + "detail levels each one defines and its 2d extents. Args: limit, offset.",
            mutating: false, ListMemberNodeShapes);

        Reg("create_member",
            "Create a structural member (AecsDbMember) from 'start' to 'end'. Optional: 'memberType' "
            + "(Beam|Column|Brace, default Beam), 'style' (member style name, default the drawing's "
            + "Standard), 'justification' (Baseline|TopLeft|TopCenter|TopRight|MiddleLeft|"
            + "MiddleCenter|MiddleRight|BottomLeft|BottomCenter|BottomRight), 'roll' (degrees) or "
            + "'rollRadians', 'normal' [x,y,z], 'startOffset'/'endOffset', 'autoTrim' (yes|no|"
            + "byStyle), 'layer', 'space'. One undo step.",
            mutating: true, CreateMember);

        Reg("list_column_grids",
            "Every AEC column grid with handle, location, rotation, grid type, U/V axis layout "
            + "(mode, spacing, axis length, offsets and the actual grid-line parameters), node and "
            + "cell counts, node positions, boundary, and enhanced-grid labelling data when present. "
            + "Args: scope (model|current|paper|all, default model), includeNodes (default true), "
            + "deepScan, limit, offset.",
            mutating: false, ListColumnGrids);

        Reg("list_layout_grids",
            "Every generic AEC layout host: LayoutGrid2d (excluding column grids, which "
            + "list_column_grids covers), LayoutGrid3d and LayoutCurve, with their layout mode, "
            + "spacing, parameters and node positions. Args: scope, type (LayoutGrid2d|LayoutGrid3d|"
            + "LayoutCurve), includeNodes, deepScan, limit, offset.",
            mutating: false, ListLayoutGrids);

        Reg("list_members",
            "Every structural member with handle, style, member type, geometry (start/end/mid point, "
            + "length, geometry type), justification, roll, offsets, trim state, node positions and "
            + "the members connected at each node. Args: scope, memberType (Beam|Column|Brace), "
            + "includeNodes (default true), deepScan, limit, offset.",
            mutating: false, ListMembers);

        Reg("set_member_properties",
            "Change a structural member in place, by handle. Any of: 'style' (member style name), "
            + "'memberType' (Beam|Column|Brace), 'justification', 'roll' (degrees) or 'rollRadians', "
            + "'startOffset', 'endOffset', 'autoTrim' (yes|no|byStyle), 'justificationComponents' "
            + "(HighestPriority|JustifyAll), 'justificationOverall' (Maximum|EachNode), 'layer'. "
            + "Returns the properties that actually changed. One undo step.",
            mutating: true, SetMemberProperties);
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

    // ------------------------------------------------------------- argument helpers (ACA-free)

    private static bool HasProp(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    private static double[] DblArrayOpt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty(name, out var v)
            || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<double>();

        return v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Number)
                .Select(x => x.GetDouble())
                .ToArray();
    }

    private static (int Offset, int Limit) Paging(JsonElement a)
    {
        var limit = Args.IntOpt(a, "limit", DefaultLimit);
        var offset = Args.IntOpt(a, "offset", 0);
        if (limit < 1) limit = 1;
        if (limit > MaxLimit) limit = MaxLimit;
        if (offset < 0) offset = 0;
        return (offset, limit);
    }

    private static string Scope(JsonElement a) => Args.StrOpt(a, "scope", "model") ?? "model";

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    /// <summary>Angle in radians from either '&lt;name&gt;' (degrees) or '&lt;name&gt;Radians'.</summary>
    private static double? AngleOpt(JsonElement a, string name)
    {
        if (HasProp(a, name + "Radians")) return Args.Dbl(a, name + "Radians");
        if (HasProp(a, name)) return Rad(Args.Dbl(a, name));
        return null;
    }

    private static T ParseEnum<T>(string value, string argName) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value.Trim(), true, out var parsed) && Enum.IsDefined(typeof(T), parsed))
            return parsed;

        throw new AcadMcpException("bad_argument",
            $"'{argName}' must be one of: {string.Join(", ", Enum.GetNames(typeof(T)))}; got '{value}'.");
    }

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

    // ------------------------------------------------------------------- space / symbol helpers

    private static ObjectId TargetSpaceId(AcadContext ctx, string space)
    {
        var wanted = space.Trim();

        if (wanted.Length == 0 || wanted.Equals("current", StringComparison.OrdinalIgnoreCase))
            return ctx.Db.CurrentSpaceId;

        if (wanted.Equals("model", StringComparison.OrdinalIgnoreCase))
            return SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db);

        if (wanted.Equals("paper", StringComparison.OrdinalIgnoreCase))
            return SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db);

        var blocks = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (blocks.Has(wanted))
        {
            var id = blocks[wanted];
            var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout) return id;
        }

        throw new AcadMcpException("bad_argument",
            $"'space' must be model, paper, current or the name of a layout block; got '{space}'.");
    }

    private static List<ObjectId> ScanSpaces(AcadContext ctx, string scope)
    {
        var spaces = new List<ObjectId>();

        if (scope.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            spaces.Add(ctx.Db.CurrentSpaceId);
            return spaces;
        }

        if (scope.Equals("paper", StringComparison.OrdinalIgnoreCase))
        {
            spaces.Add(SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db));
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

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// Cheap pre-filter: every ACA runtime class is registered as AecDb*/AecsDb*, so the name test
    /// rejects plain AutoCAD geometry without opening the object. deepScan opens everything instead.
    /// </summary>
    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    private static string RequireLayer(AcadContext ctx, string name)
    {
        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("not_found", $"No layer named '{name}' in this drawing.",
                "Call list_layers to see what exists; this tool does not create layers.");

        var record = (LayerTableRecord)ctx.Tr.GetObject(table[name], OpenMode.ForRead);
        return record.Name; // normalise the caller's casing to the record's
    }

#if ACA

    // ----------------------------------------------------------------------- guarded accessors
    // AEC property getters throw for degenerate geometry and for objects whose enabler is partly
    // loaded. One bad property must never lose a whole listing, and the bridge drops nulls
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

    private static double[]? TryVec(Func<Vector3d> f)
    {
        try { var v = f(); return new[] { v.X, v.Y, v.Z }; }
        catch { return null; }
    }

    private static object? TryO(Func<object?> f) { try { return f(); } catch { return null; } }

    private static double[] Nums(Func<DoubleCollection> f)
    {
        try { return f()?.ToArray() ?? Array.Empty<double>(); }
        catch { return Array.Empty<double>(); }
    }

    private static int[] Ints(Func<IntegerCollection> f)
    {
        try { return f()?.ToArray() ?? Array.Empty<int>(); }
        catch { return Array.Empty<int>(); }
    }

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

    /// <summary>Coordinate frame of a Matrix3d, which is how members report orientation.</summary>
    private static object? Frame(Func<Matrix3d> f)
    {
        try
        {
            var cs = f().CoordinateSystem3d;
            return new
            {
                origin = new[] { cs.Origin.X, cs.Origin.Y, cs.Origin.Z },
                xAxis = new[] { cs.Xaxis.X, cs.Xaxis.Y, cs.Xaxis.Z },
                yAxis = new[] { cs.Yaxis.X, cs.Yaxis.Y, cs.Yaxis.Z },
                zAxis = new[] { cs.Zaxis.X, cs.Zaxis.Y, cs.Zaxis.Z },
            };
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

    private static string? StyleName(AcadContext ctx, AecEntity e) => TryS(() => RecordName(ctx, e.StyleId));

    // ------------------------------------------------------------------------------- scanning

    private static List<DBObject> Collect(AcadContext ctx, JsonElement a, Func<DBObject, bool> accept)
    {
        var deep = Args.BoolOpt(a, "deepScan", false);
        var hits = new List<DBObject>();

        foreach (var spaceId in ScanSpaces(ctx, Scope(a)))
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

                bool ok;
                try { ok = accept(obj); }
                catch { ok = false; }

                if (ok) hits.Add(obj);
            }
        }

        return hits;
    }

    // -------------------------------------------------------------------------- axis planning

    /// <summary>
    /// One axis of a rectangular grid, resolved from arguments before anything is allocated so a
    /// bad request never leaves a half-built AEC object behind.
    /// </summary>
    private sealed class AxisPlan
    {
        public bool Manual;
        public double Spacing;
        public double AxisLength;
        public double StartOffset;
        public double EndOffset;
        public bool HasStartOffset;
        public bool HasEndOffset;
        public double[] Positions = Array.Empty<double>();
    }

    private static AxisPlan PlanAxis(JsonElement a, string prefix)
    {
        var plan = new AxisPlan
        {
            HasStartOffset = HasProp(a, prefix + "StartOffset"),
            HasEndOffset = HasProp(a, prefix + "EndOffset"),
        };
        if (plan.HasStartOffset) plan.StartOffset = Args.Dbl(a, prefix + "StartOffset");
        if (plan.HasEndOffset) plan.EndOffset = Args.Dbl(a, prefix + "EndOffset");

        var spacings = DblArrayOpt(a, prefix + "Spacings");
        if (spacings.Length > 0)
        {
            foreach (var s in spacings)
                if (s <= 0.0 || double.IsNaN(s) || double.IsInfinity(s))
                    throw new AcadMcpException("bad_argument",
                        $"Every entry of '{prefix}Spacings' must be a finite number greater than zero.");

            // Bay widths become grid lines at the running total: [20,20,30] -> 0, 20, 40, 70.
            var positions = new double[spacings.Length + 1];
            var run = 0.0;
            positions[0] = 0.0;
            for (var i = 0; i < spacings.Length; i++)
            {
                run += spacings[i];
                positions[i + 1] = run;
            }

            plan.Manual = true;
            plan.Positions = positions;
            plan.Spacing = spacings[0];
            plan.AxisLength = Args.DblOpt(a, prefix + "AxisLength", run);
            return plan;
        }

        var bays = Args.IntOpt(a, prefix + "Bays", 0);
        var spacing = Args.DblOpt(a, prefix + "Spacing", 0.0);

        if (bays < 1 || spacing <= 0.0 || double.IsNaN(spacing) || double.IsInfinity(spacing))
            throw new AcadMcpException("bad_argument",
                $"Give either '{prefix}Spacings' (an array of bay widths) or both '{prefix}Bays' "
                + $"(an integer >= 1) and '{prefix}Spacing' (> 0).");

        plan.Manual = false;
        plan.Spacing = spacing;
        plan.AxisLength = Args.DblOpt(a, prefix + "AxisLength", bays * spacing);
        return plan;
    }

    /// <summary>
    /// Drive a manual axis to exactly the requested set of lines: append what is missing, then drop
    /// whatever the fresh grid brought with it that was not asked for. Removal is best-effort —
    /// a grid that refuses to drop its last line is still a valid grid.
    /// </summary>
    private static void SyncManualAxis(double[] existing, double[] wanted,
                                       Action<double> append, Action<double> remove)
    {
        foreach (var w in wanted)
            if (!existing.Any(c => Math.Abs(c - w) <= ParameterTolerance))
                append(w);

        foreach (var c in existing)
            if (!wanted.Any(w => Math.Abs(c - w) <= ParameterTolerance))
            {
                try { remove(c); } catch { }
            }
    }

    private static void ApplyAxes(AecGridUv grid, AxisPlan x, AxisPlan y)
    {
        grid.ULayoutMode = x.Manual ? AecUvLayoutMode.Manual : AecUvLayoutMode.Repeat;
        grid.UAxisLength = x.AxisLength;
        if (x.HasStartOffset) grid.UStartOffset = x.StartOffset;
        if (x.HasEndOffset) grid.UEndOffset = x.EndOffset;
        if (x.Manual)
            SyncManualAxis(Nums(() => grid.UParameters), x.Positions,
                           grid.AppendGridColumn, grid.RemoveGridColumn);
        else
            grid.USpacing = x.Spacing;

        grid.VLayoutMode = y.Manual ? AecUvLayoutMode.Manual : AecUvLayoutMode.Repeat;
        grid.VAxisLength = y.AxisLength;
        if (y.HasStartOffset) grid.VStartOffset = y.StartOffset;
        if (y.HasEndOffset) grid.VEndOffset = y.EndOffset;
        if (y.Manual)
            SyncManualAxis(Nums(() => grid.VParameters), y.Positions,
                           grid.AppendGridRow, grid.RemoveGridRow);
        else
            grid.VSpacing = y.Spacing;

        grid.UpdateParameters();
    }

    // ---------------------------------------------------------------------------- grid creation

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateColumnGrid(JsonElement a) =>
        Acad.Write<object?>(ctx => BuildGrid2d(ctx, a, () => new AecsColumnGrid(), "ColumnGrid"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateLayoutGrid2d(JsonElement a) =>
        Acad.Write<object?>(ctx => BuildGrid2d(ctx, a, () => new AecLayoutGrid2d(), "LayoutGrid2d"));

    private static object BuildGrid2d(AcadContext ctx, JsonElement a,
                                      Func<AecLayoutGrid2d> make, string typeName)
    {
        // Validate everything BEFORE allocating an AEC object, so a bad request costs nothing.
        var planX = PlanAxis(a, "x");
        var planY = PlanAxis(a, "y");

        var gridType = Args.StrOpt(a, "gridType", "rectangular")!;
        if (!gridType.Equals("rectangular", StringComparison.OrdinalIgnoreCase)
            && !gridType.Equals("UVRectangular", StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("not_implemented",
                $"Only rectangular grids can be created; got gridType '{gridType}'.",
                "Radial and custom grids are reported by list_column_grids but the creation "
                + "sequence for them is not confirmable from the managed API surface.");

        var origin = HasProp(a, "origin") ? Args.Point(a, "origin") : Point3d.Origin;
        var rotation = AngleOpt(a, "rotation");

        Vector3d? normal = null;
        if (HasProp(a, "normal"))
        {
            var n = Args.Point(a, "normal").GetAsVector();
            if (n.Length <= 0.0)
                throw new AcadMcpException("bad_argument", "'normal' must not be a zero-length vector.");
            normal = n;
        }

        var layer = Args.StrOpt(a, "layer");
        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var grid = make();
        try
        {
            grid.SetToStandard(ctx.Db);
            grid.SetDatabaseDefaults(ctx.Db);
            btr.AppendEntity(grid);
            ctx.Tr.AddNewlyCreatedDBObject(grid, true);
        }
        catch (System.Exception)
        {
            // Never appended, so nothing in the database references it; dispose rather than leak.
            grid.Dispose();
            throw;
        }

        // Layer by NAME needs the entity database-resident, which it now is.
        if (!string.IsNullOrWhiteSpace(layer)) grid.Layer = RequireLayer(ctx, layer!);

        grid.GridType = AecGridType.UVRectangular;
        grid.Location = origin;
        if (normal.HasValue) grid.Normal = normal.Value;
        if (rotation.HasValue) grid.Rotation = rotation.Value;

        ApplyAxes(grid.BaseGrid, planX, planY);

        var info = Grid2dInfo(ctx, grid, typeName, includeNodes: true, includeEnhanced: false);
        return new
        {
            created = typeName,
            space = TryS(() => btr.Name),
            grid = info,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateLayoutCurve(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var curveHandle = Args.Str(a, "curveHandle");
        var curveId = Acad.ToObjectId(ctx.Db, curveHandle);

        if (ctx.Tr.GetObject(curveId, OpenMode.ForRead) is not Curve curveEnt)
            throw new AcadMcpException("bad_argument",
                $"Handle '{curveHandle}' is a {ClassName(curveId)}, not a curve.",
                "A layout curve must reference a line, arc, polyline or other curve entity.");

        // Every AEC object derives from AutoCAD's Curve, so the test above lets walls, slabs and
        // even other layout tools through. They are not real curves and a layout curve pointed at
        // one is an invalid object, so reject them explicitly.
        if (curveEnt is AecEntity)
            throw new AcadMcpException("bad_argument",
                $"Handle '{curveHandle}' is an AEC object ({ClassName(curveId)}), not a plain curve.",
                "AEC objects are curve-derived but are not valid layout-curve references; pass a "
                + "line, arc, circle, ellipse, polyline or spline. To attach objects to an AEC "
                + "object, use an anchor instead.");

        var modeName = Args.StrOpt(a, "layoutMode", "Repeat")!;
        var mode = ParseEnum<AecCurveLayoutMode>(modeName, "layoutMode");
        var positions = DblArrayOpt(a, "positions");

        var hasSpacing = HasProp(a, "spacing");
        var spacing = hasSpacing ? Args.Dbl(a, "spacing") : 0.0;
        if (hasSpacing && (spacing <= 0.0 || double.IsNaN(spacing) || double.IsInfinity(spacing)))
            throw new AcadMcpException("bad_argument", "'spacing' must be a finite number greater than zero.");

        if (mode == AecCurveLayoutMode.Manual && positions.Length == 0)
            throw new AcadMcpException("bad_argument",
                "layoutMode 'Manual' needs 'positions' — an array of node positions along the curve.");

        if (mode != AecCurveLayoutMode.Manual && !hasSpacing)
            throw new AcadMcpException("bad_argument",
                $"layoutMode '{mode}' needs 'spacing'.");

        var layer = Args.StrOpt(a, "layer");
        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);

        // A layout curve and the curve it lays out along must live in the same block; a cross-space
        // reference produces an object AutoCAD cannot resolve. Checked before anything is allocated.
        ObjectId curveOwner;
        try { curveOwner = curveEnt.OwnerId; }
        catch { curveOwner = ObjectId.Null; }

        if (curveOwner != spaceId)
            throw new AcadMcpException("bad_argument",
                $"Curve '{curveHandle}' is not in the space the layout curve would be created in.",
                "A layout curve must be in the same block as the curve it references. Pass 'space' "
                + "to match the curve's own space, or pick a curve in the target space.");

        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var lc = new AecLayoutCurve();
        try
        {
            lc.SetToStandard(ctx.Db);
            lc.SetDatabaseDefaults(ctx.Db);
            btr.AppendEntity(lc);
            ctx.Tr.AddNewlyCreatedDBObject(lc, true);
        }
        catch (System.Exception)
        {
            lc.Dispose();
            throw;
        }

        if (!string.IsNullOrWhiteSpace(layer)) lc.Layer = RequireLayer(ctx, layer!);

        lc.CurveId = curveId;
        lc.LayoutMode = mode;
        if (hasSpacing) lc.Spacing = spacing;
        if (HasProp(a, "startOffset")) lc.StartOffset = Args.Dbl(a, "startOffset");
        if (HasProp(a, "endOffset")) lc.EndOffset = Args.Dbl(a, "endOffset");

        if (positions.Length > 0)
        {
            var existing = Nums(() => lc.NodePositions);
            foreach (var p in positions)
                if (!existing.Any(c => Math.Abs(c - p) <= ParameterTolerance))
                    lc.AppendLayoutNode(p);
        }

        lc.UpdateParameters();

        return new
        {
            created = "LayoutCurve",
            space = TryS(() => btr.Name),
            layoutCurve = LayoutCurveInfo(ctx, lc, includeNodes: true),
        };
    });

    // ------------------------------------------------------------------------- member creation

    private static ObjectId ResolveMemberStyle(AcadContext ctx, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // Creates the Standard style if the drawing has never had one. This only ever runs
            // inside Acad.Write, so it joins the same single undo step.
            var standard = AecsMemberStyleDict.GetStandardStyle(ctx.Db);
            if (standard.IsNull)
                throw new AcadMcpException("style_not_found",
                    "This drawing has no Standard member style and one could not be created.",
                    "Call list_member_styles and pass an explicit 'style'.");
            return VerifyMemberStyle(ctx, standard, "Standard");
        }

        var dict = new AecsMemberStyleDict(ctx.Db);

        bool has;
        try { has = dict.Has(name!, ctx.Tr); }
        catch { has = false; }

        if (!has)
            throw new AcadMcpException("style_not_found",
                $"No member style named '{name}' in this drawing.",
                "Call list_member_styles for the available names.");

        var id = dict.GetAt(name!);
        if (id.IsNull)
            throw new AcadMcpException("style_not_found", $"Member style '{name}' resolved to null.");

        return VerifyMemberStyle(ctx, id, name!);
    }

    /// <summary>
    /// A Member whose StyleId is not a MemberStyle is an invalid object, so no id reaches
    /// Member.StyleId unverified: open the record and confirm its class first. Cheap (the record
    /// is opened for read in the same transaction) and it closes the only corruption path in this
    /// module's creation code.
    /// </summary>
    private static ObjectId VerifyMemberStyle(AcadContext ctx, ObjectId id, string label)
    {
        try
        {
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is AecsMemberStyle) return id;
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("style_not_found",
                $"Member style '{label}' could not be opened.", ex.Message);
        }

        throw new AcadMcpException("style_not_found",
            $"'{label}' resolved to a {ClassName(id)}, not a member style.",
            "Call list_member_styles for the available names.");
    }

    private static AecsTrimType ParseTrim(string value)
    {
        var v = value.Trim().Replace("_", string.Empty).Replace("-", string.Empty);
        if (v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return AecsTrimType.MemberAutoTrimYes;
        if (v.Equals("no", StringComparison.OrdinalIgnoreCase)) return AecsTrimType.MemberAutoTrimNo;
        if (v.Equals("bystyle", StringComparison.OrdinalIgnoreCase)) return AecsTrimType.MemberAutoTrimByStyle;
        return ParseEnum<AecsTrimType>(value, "autoTrim");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateMember(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var start = Args.Point(a, "start");
        var end = Args.Point(a, "end");
        if (start.DistanceTo(end) <= 1e-9)
            throw new AcadMcpException("bad_argument", "'start' and 'end' must not be the same point.");

        var memberType = ParseEnum<AecsMemberType>(Args.StrOpt(a, "memberType", "Beam")!, "memberType");

        AecsJustification? justification = null;
        if (HasProp(a, "justification"))
            justification = ParseEnum<AecsJustification>(Args.Str(a, "justification"), "justification");

        var roll = AngleOpt(a, "roll");

        AecsTrimType? autoTrim = null;
        if (HasProp(a, "autoTrim")) autoTrim = ParseTrim(Args.Str(a, "autoTrim"));

        Vector3d? normal = null;
        if (HasProp(a, "normal"))
        {
            var n = Args.Point(a, "normal").GetAsVector();
            if (n.Length <= 0.0)
                throw new AcadMcpException("bad_argument", "'normal' must not be a zero-length vector.");

            // Member.Set(start, end, normal) uses the normal to orient the cross-section around the
            // axis. A normal along the axis leaves that undefined, which is a degenerate member.
            if (n.IsParallelTo(start.GetVectorTo(end)))
                throw new AcadMcpException("bad_argument",
                    "'normal' must not be parallel to the member axis.",
                    "The normal orients the cross-section around the start-to-end axis, so it has "
                    + "to point across the member, not along it.");

            normal = n;
        }

        var hasStartOffset = HasProp(a, "startOffset");
        var hasEndOffset = HasProp(a, "endOffset");
        var startOffset = hasStartOffset ? Args.Dbl(a, "startOffset") : 0.0;
        var endOffset = hasEndOffset ? Args.Dbl(a, "endOffset") : 0.0;

        var layer = Args.StrOpt(a, "layer");
        var styleId = ResolveMemberStyle(ctx, Args.StrOpt(a, "style"));

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var member = new AecsMember();
        try
        {
            member.SetToStandard(ctx.Db);
            member.SetDatabaseDefaults(ctx.Db);
            btr.AppendEntity(member);
            ctx.Tr.AddNewlyCreatedDBObject(member, true);
        }
        catch (System.Exception)
        {
            member.Dispose();
            throw;
        }

        if (!string.IsNullOrWhiteSpace(layer)) member.Layer = RequireLayer(ctx, layer!);

        member.StyleId = styleId;
        member.MemberType = memberType;

        // Geometry last of the structural essentials: Set() builds the base curve and the nodes.
        if (normal.HasValue) member.Set(start, end, normal.Value);
        else member.Set(start, end);

        if (justification.HasValue) member.Justification = justification.Value;
        if (roll.HasValue) member.Roll = roll.Value;
        if (hasStartOffset || hasEndOffset) member.SetStartEndOffset(startOffset, endOffset);
        if (autoTrim.HasValue) member.AutoTrim = autoTrim.Value;

        return new
        {
            created = "Member",
            space = TryS(() => btr.Name),
            member = MemberInfo(ctx, member, includeNodes: true),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetMemberProperties(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);
        var obj = ctx.Tr.GetObject(id, OpenMode.ForWrite);

        if (obj is not AecsMember member)
            throw new AcadMcpException("bad_argument",
                $"Handle '{handle}' is a {ClassName(id)}, not a structural member.",
                "Call list_members for the members in this drawing.");

        var changed = new List<string>();

        var styleName = Args.StrOpt(a, "style");
        if (!string.IsNullOrWhiteSpace(styleName))
        {
            member.StyleId = ResolveMemberStyle(ctx, styleName);
            changed.Add("style");
        }

        if (HasProp(a, "memberType"))
        {
            member.MemberType = ParseEnum<AecsMemberType>(Args.Str(a, "memberType"), "memberType");
            changed.Add("memberType");
        }

        if (HasProp(a, "justification"))
        {
            member.Justification = ParseEnum<AecsJustification>(Args.Str(a, "justification"), "justification");
            changed.Add("justification");
        }

        if (HasProp(a, "justificationComponents"))
        {
            member.JustificationComponentsExtents =
                ParseEnum<AecsComponentsJust>(Args.Str(a, "justificationComponents"), "justificationComponents");
            changed.Add("justificationComponents");
        }

        if (HasProp(a, "justificationOverall"))
        {
            member.JustificationOverallExtents =
                ParseEnum<AecsOverallJust>(Args.Str(a, "justificationOverall"), "justificationOverall");
            changed.Add("justificationOverall");
        }

        var roll = AngleOpt(a, "roll");
        if (roll.HasValue)
        {
            member.Roll = roll.Value;
            changed.Add("roll");
        }

        var hasStartOffset = HasProp(a, "startOffset");
        var hasEndOffset = HasProp(a, "endOffset");
        if (hasStartOffset || hasEndOffset)
        {
            // SetStartEndOffset takes both; keep whichever the caller did not supply.
            var startOffset = hasStartOffset ? Args.Dbl(a, "startOffset") : TryD(() => member.StartOffset) ?? 0.0;
            var endOffset = hasEndOffset ? Args.Dbl(a, "endOffset") : TryD(() => member.EndOffset) ?? 0.0;
            member.SetStartEndOffset(startOffset, endOffset);
            if (hasStartOffset) changed.Add("startOffset");
            if (hasEndOffset) changed.Add("endOffset");
        }

        if (HasProp(a, "autoTrim"))
        {
            member.AutoTrim = ParseTrim(Args.Str(a, "autoTrim"));
            changed.Add("autoTrim");
        }

        var layer = Args.StrOpt(a, "layer");
        if (!string.IsNullOrWhiteSpace(layer))
        {
            member.Layer = RequireLayer(ctx, layer!);
            changed.Add("layer");
        }

        if (changed.Count == 0)
            throw new AcadMcpException("bad_argument",
                "Nothing to change: supply at least one of style, memberType, justification, "
                + "justificationComponents, justificationOverall, roll, startOffset, endOffset, "
                + "autoTrim or layer.");

        return new
        {
            handle = TryS(() => member.Handle.ToString()),
            changed,
            member = MemberInfo(ctx, member, includeNodes: true),
        };
    });

    // ------------------------------------------------------------------------------ projection

    private static object GeoIdentity(AcadContext ctx, DBObject obj, string type)
    {
        var entity = obj as AecEntity;
        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            type,
            objectClass = ClassName(obj.ObjectId),
            layer = TryS(() => (obj as Entity)?.Layer),
            description = TryS(() => entity?.Description),
            styleName = entity is null ? null : StyleName(ctx, entity),
            styleHandle = entity is null ? null : TryS(() => entity.StyleId.IsNull
                ? null
                : entity.StyleId.Handle.ToString()),
        };
    }

    private static object Placement(Autodesk.Aec.DatabaseServices.Geo geo) => new
    {
        location = TryPt(() => geo.Location),
        normal = TryVec(() => geo.Normal),
        direction = TryVec(() => geo.Direction),
        rotationRadians = TryD(() => geo.Rotation),
        rotationDegrees = TryD(() => Deg(geo.Rotation)),
        isAnchored = TryB(() => geo.IsAnchored),
        anchorHandle = TryS(() => geo.IsAnchored && !geo.AnchorId.IsNull
            ? geo.AnchorId.Handle.ToString()
            : null),
    };

    private static List<object> LayoutNodes(AecLayoutTool tool)
    {
        var nodes = new List<object>();
        var ids = Ints(() => tool.LayoutNodeIds);
        var count = Math.Min(ids.Length, MaxNodesReported);

        for (var i = 0; i < count; i++)
        {
            var nodeId = ids[i];
            nodes.Add(new
            {
                id = nodeId,
                point = TryPt(() => tool.GetLayoutNodeLocation(nodeId)),
            });
        }

        return nodes;
    }

    private static object AxisInfo(string name, Func<AecUvLayoutMode> mode, Func<double> axisLength,
                                   Func<double> spacing, Func<double> startOffset, Func<double> endOffset,
                                   Func<DoubleCollection> parameters)
    {
        var values = Nums(parameters);
        return new
        {
            axis = name,
            layoutMode = TryS(() => mode().ToString()),
            axisLength = TryD(axisLength),
            spacing = TryD(spacing),
            startOffset = TryD(startOffset),
            endOffset = TryD(endOffset),
            lineCount = values.Length,
            parameters = values,
        };
    }

    private static object? RadialInfo(AecGridUv grid)
    {
        if (grid is not AecGridUvRadial radial) return null;
        return new
        {
            insideRadius = TryD(() => radial.UInsideRadius),
            angleSpacingRadians = TryD(() => radial.VAngleSpacing),
            angleSpacingDegrees = TryD(() => Deg(radial.VAngleSpacing)),
            axisAngleRadians = TryD(() => radial.VAxisAngle),
            axisAngleDegrees = TryD(() => Deg(radial.VAxisAngle)),
            startAngleOffset = TryD(() => radial.VStartAngleOffset),
            endAngleOffset = TryD(() => radial.VEndAngleOffset),
        };
    }

    private static object? BaseGridInfo(AecGridUv? grid)
    {
        if (grid is null) return null;
        return new
        {
            gridType = TryS(() => grid.GridType.ToString()),
            nodeCount = TryI(() => grid.NodeCount),
            cellCount = TryI(() => grid.CellCount),
            u = AxisInfo("u", () => grid.ULayoutMode, () => grid.UAxisLength, () => grid.USpacing,
                         () => grid.UStartOffset, () => grid.UEndOffset, () => grid.UParameters),
            v = AxisInfo("v", () => grid.VLayoutMode, () => grid.VAxisLength, () => grid.VSpacing,
                         () => grid.VStartOffset, () => grid.VEndOffset, () => grid.VParameters),
            w = grid is AecGridUvw uvw
                ? AxisInfo("w", () => uvw.WLayoutMode, () => uvw.WAxisLength, () => uvw.WSpacing,
                           () => uvw.WStartOffset, () => uvw.WEndOffset, () => uvw.WParameters)
                : null,
            volumeCount = grid is AecGridUvw v3 ? TryI(() => v3.VolumeCount) : null,
            radial = RadialInfo(grid),
        };
    }

    /// <summary>
    /// Enhanced-column-grid labelling. Read-only: see the module notes for why this module never
    /// writes it. EnhancedData throws on a grid that was never converted, hence the outer guard.
    /// </summary>
    private static object? EnhancedInfo(AecsColumnGrid grid)
    {
        try
        {
            var data = grid.EnhancedData;
            if (data is null) return null;

            return new
            {
                isEnhancedCustomGrid = TryB(() => data.IsEnhancedCustomGrid),
                gridType = TryS(() => data.GridType.ToString()),
                labelStyleName = TryS(() => data.LabelStyleName),
                labelExtension = TryD(() => data.LabelExtension),
                gridLineExtension = TryD(() => data.GridLineExtension),
                gridEndExtension = TryD(() => data.GridEndExtension),
                generateLabelOnTop = TryB(() => data.GenerateLabelOnTop),
                generateLabelOnBottom = TryB(() => data.GenerateLabelOnBottom),
                generateLabelOnLeft = TryB(() => data.GenerateLabelOnLeft),
                generateLabelOnRight = TryB(() => data.GenerateLabelOnRight),
                startLabelStringX = TryS(() => data.StartLabelStringX),
                startLabelStringY = TryS(() => data.StartLabelStringY),
                xLabelSortingStyle = TryS(() => data.XLabelSortingStyle.ToString()),
                yLabelSortingStyle = TryS(() => data.YLabelSortingStyle.ToString()),
                isXDirectionAscending = TryB(() => data.IsXDirectionAscending),
                isYDirectionAscending = TryB(() => data.IsYDirectionAscending),
                isMainBeforeSub = TryB(() => data.IsMainBeforeSub),
                isArcClockwise = TryB(() => data.IsArcClockwise),
                skipCharacters = TryS(() => data.SkipCharacters),
                splitCharacter = TryS(() => data.SplitCharacter.ToString()),
            };
        }
        catch { return null; }
    }

    /// <param name="includeEnhanced">
    /// False on the creation path. ColumnGrid.EnhancedData is a getter on a struct the object may
    /// not yet own, and this module cannot prove from the declared API surface that reading it on a
    /// grid that was never converted has no lazy-initialising side effect. Inside Acad.Read that
    /// would not matter (the transaction aborts), but the creation path commits, so a freshly
    /// appended grid is never asked for it — and its enhanced data would be all defaults anyway.
    /// </param>
    private static object Grid2dInfo(AcadContext ctx, AecLayoutGrid2d grid, string typeName,
                                     bool includeNodes, bool includeEnhanced = true)
    {
        AecGridUv? baseGrid = null;
        try { baseGrid = grid.BaseGrid; } catch { }

        return new
        {
            identity = GeoIdentity(ctx, grid, typeName),
            placement = Placement(grid),
            gridType = TryS(() => grid.GridType.ToString()),
            boundaryHandle = TryS(() => grid.BoundaryId.IsNull ? null : grid.BoundaryId.Handle.ToString()),
            boundaryHoles = TryO(() => Handles(grid.BoundaryHoles)),
            cellIds = Ints(() => grid.LayoutCellIds).Length,
            baseGrid = BaseGridInfo(baseGrid),
            isEnhancedCustomColumnGrid = grid is AecsColumnGrid cg1 ? TryB(() => cg1.IsEnhancedCustomColumnGrid) : null,
            enhanced = includeEnhanced && grid is AecsColumnGrid cg2 ? EnhancedInfo(cg2) : null,
            nodes = includeNodes ? LayoutNodes(grid) : null,
        };
    }

    private static object Grid3dInfo(AcadContext ctx, AecLayoutGrid3d grid, bool includeNodes)
    {
        AecGridUv? baseGrid = null;
        try { baseGrid = grid.BaseGrid; } catch { }

        return new
        {
            identity = GeoIdentity(ctx, grid, "LayoutGrid3d"),
            placement = Placement(grid),
            gridType = TryS(() => grid.GridType.ToString()),
            cellIds = Ints(() => grid.LayoutCellIds).Length,
            volumeIds = Ints(() => grid.LayoutVolumeIds).Length,
            baseGrid = BaseGridInfo(baseGrid),
            nodes = includeNodes ? LayoutNodes(grid) : null,
        };
    }

    private static object LayoutCurveInfo(AcadContext ctx, AecLayoutCurve lc, bool includeNodes)
    {
        var positions = Nums(() => lc.NodePositions);
        return new
        {
            identity = GeoIdentity(ctx, lc, "LayoutCurve"),
            placement = Placement(lc),
            curveHandle = TryS(() => lc.CurveId.IsNull ? null : lc.CurveId.Handle.ToString()),
            curveClass = TryS(() => lc.CurveId.IsNull ? null : ClassName(lc.CurveId)),
            layoutMode = TryS(() => lc.LayoutMode.ToString()),
            spacing = TryD(() => lc.Spacing),
            startOffset = TryD(() => lc.StartOffset),
            endOffset = TryD(() => lc.EndOffset),
            nodeCount = TryI(() => lc.NodeCount),
            nodePositions = positions,
            nodes = includeNodes ? LayoutNodes(lc) : null,
        };
    }

    private static List<object> MemberConnectionsInfo(AecsMember member)
    {
        var result = new List<object>();
        try
        {
            var connections = member.ConnectingMembers;
            if (connections is null) return result;

            var count = Math.Min(connections.Count, MaxNodesReported);
            for (var i = 0; i < count; i++)
            {
                AecsMemberNode? node;
                try { node = connections.Connection(i); }
                catch { continue; }
                if (node is null) continue;

                var links = new List<object>();
                var dataCount = TryI(() => node.Count) ?? 0;
                for (var j = 0; j < dataCount; j++)
                {
                    AecsMemberNodeData? data;
                    try { data = node.Data(j); }
                    catch { continue; }
                    if (data is null) continue;

                    links.Add(new
                    {
                        memberHandle = TryS(() => data.MemberId.IsNull ? null : data.MemberId.Handle.ToString()),
                        classification = TryS(() => data.Classification.ToString()),
                    });
                }

                result.Add(new
                {
                    index = i,
                    point = TryPt(() => node.Point),
                    connected = links,
                });
            }
        }
        catch { }
        return result;
    }

    private static object MemberInfo(AcadContext ctx, AecsMember member, bool includeNodes) => new
    {
        identity = GeoIdentity(ctx, member, "Member"),
        placement = Placement(member),
        memberType = TryS(() => member.MemberType.ToString()),
        geometryType = TryS(() => member.GeometryType.ToString()),
        isSimple = TryB(() => member.IsSimple),
        length = TryD(() => member.Length),
        // Member is a LayoutTool, not a Geo with endpoints; they come off the base curve. Cast
        // through object: AecEntity.BaseCurve is declared as an unqualified 'Curve' in the declared
        // API surface and Autodesk.Aec.Modeler also defines a Curve, so this compiles either way and
        // degrades to null at runtime if the cast is wrong.
        startPoint = TryPt(() => ((Curve)(object)member.BaseCurve).StartPoint),
        endPoint = TryPt(() => ((Curve)(object)member.BaseCurve).EndPoint),
        midPoint = TryPt(() => member.MidPoint),
        justification = TryS(() => member.Justification.ToString()),
        justificationComponents = TryS(() => member.JustificationComponentsExtents.ToString()),
        justificationOverall = TryS(() => member.JustificationOverallExtents.ToString()),
        rollRadians = TryD(() => member.Roll),
        rollDegrees = TryD(() => Deg(member.Roll)),
        startOffset = TryD(() => member.StartOffset),
        endOffset = TryD(() => member.EndOffset),
        autoTrim = TryS(() => member.AutoTrim.ToString()),
        supportsBaseCurveCommands = TryB(() => member.SupportsBaseCurveCommands),
        bodyModifierCount = TryI(() => member.BodyModifierCount),
        interferenceCount = TryI(() => member.InterferencesCount),
        trimPlaneCount = TryI(() => member.TrimPlaneCount),
        orientation = Frame(() => member.Orientation),
        alignment = Frame(() => member.Alignment),
        nodes = includeNodes ? LayoutNodes(member) : null,
        connections = includeNodes ? MemberConnectionsInfo(member) : null,
    };

    // --------------------------------------------------------------------------------- listings

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListColumnGrids(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeNodes = Args.BoolOpt(a, "includeNodes", true);

        var rows = new List<(string Key, object Data)>();
        foreach (var obj in Collect(ctx, a, o => o is AecsColumnGrid))
        {
            var grid = (AecsColumnGrid)obj;
            rows.Add((TryS(() => grid.Handle.ToString()) ?? string.Empty,
                      Grid2dInfo(ctx, grid, "ColumnGrid", includeNodes)));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListLayoutGrids(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeNodes = Args.BoolOpt(a, "includeNodes", true);
        var typeFilter = Args.StrOpt(a, "type");

        bool Wanted(string type) =>
            string.IsNullOrWhiteSpace(typeFilter)
            || type.Equals(typeFilter!.Trim(), StringComparison.OrdinalIgnoreCase);

        var rows = new List<(string Key, object Data)>();

        // ColumnGrid derives from LayoutGrid2d, so it is excluded here — list_column_grids owns it.
        foreach (var obj in Collect(ctx, a,
                     o => (o is AecLayoutGrid2d && o is not AecsColumnGrid)
                          || o is AecLayoutGrid3d
                          || o is AecLayoutCurve))
        {
            var handle = TryS(() => obj.Handle.ToString()) ?? string.Empty;

            switch (obj)
            {
                case AecLayoutGrid3d g3 when Wanted("LayoutGrid3d"):
                    rows.Add((handle, Grid3dInfo(ctx, g3, includeNodes)));
                    break;

                case AecLayoutCurve lc when Wanted("LayoutCurve"):
                    rows.Add((handle, LayoutCurveInfo(ctx, lc, includeNodes)));
                    break;

                case AecLayoutGrid2d g2 when Wanted("LayoutGrid2d"):
                    rows.Add((handle, Grid2dInfo(ctx, g2, "LayoutGrid2d", includeNodes)));
                    break;
            }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMembers(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeNodes = Args.BoolOpt(a, "includeNodes", true);

        AecsMemberType? typeFilter = null;
        if (HasProp(a, "memberType"))
            typeFilter = ParseEnum<AecsMemberType>(Args.Str(a, "memberType"), "memberType");

        var rows = new List<(string Key, object Data)>();
        foreach (var obj in Collect(ctx, a, o => o is AecsMember))
        {
            var member = (AecsMember)obj;

            if (typeFilter.HasValue)
            {
                AecsMemberType actual;
                try { actual = member.MemberType; }
                catch { continue; }
                if (actual != typeFilter.Value) continue;
            }

            rows.Add((TryS(() => member.Handle.ToString()) ?? string.Empty,
                      MemberInfo(ctx, member, includeNodes)));
        }

        return Page(rows, offset, limit);
    });

    private static object NodeComponentInfo(AcadContext ctx, Func<AecsMemberNodeComponent?> get)
    {
        AecsMemberNodeComponent? node;
        try { node = get(); }
        catch { node = null; }

        if (node is null) return new { shapeName = (string?)null };

        return new
        {
            shapeName = TryS(() => RecordName(ctx, node.ShapeId)),
            shapeHandle = TryS(() => node.ShapeId.IsNull ? null : node.ShapeId.Handle.ToString()),
            rotationRadians = TryD(() => node.Rotation),
            rotationDegrees = TryD(() => Deg(node.Rotation)),
            scale = TryD(() => node.Scale),
            mirror = TryB(() => node.Mirror),
            translation = TryO(() =>
            {
                var t = node.Translation;
                return (object)new[] { t.X, t.Y };
            }),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMemberStyles(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeComponents = Args.BoolOpt(a, "includeComponents", true);

        var rows = new List<(string Key, object Data)>();
        var dict = new AecsMemberStyleDict(ctx.Db);

        ObjectIdCollection? records = null;
        try { records = dict.Records; } catch { }
        if (records is null) return Page(rows, offset, limit);

        foreach (ObjectId id in records)
        {
            if (id.IsNull) continue;

            AecsMemberStyle? style;
            try { style = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecsMemberStyle; }
            catch { continue; }
            if (style is null) continue;

            var components = new List<object>();
            if (includeComponents)
            {
                try
                {
                    foreach (AecsMemberComponent component in style.Components)
                    {
                        components.Add(new
                        {
                            id = TryI(() => component.Id),
                            name = TryS(() => component.Name),
                            priority = TryI(() => component.Priority),
                            startOffset = TryD(() => component.StartOffset),
                            endOffset = TryD(() => component.EndOffset),
                            materialName = TryS(() => RecordName(ctx, component.MaterialId)),
                            startNode = NodeComponentInfo(ctx, () => component.StartNode),
                            endNode = NodeComponentInfo(ctx, () => component.EndNode),
                        });
                    }
                }
                catch { }
            }

            var name = TryS(() => style.Name) ?? id.Handle.ToString();
            rows.Add((name, new
            {
                name,
                handle = TryS(() => style.Handle.ToString()),
                description = TryS(() => style.Description),
                autoTrim = TryB(() => style.AutoTrim),
                isLocked = TryB(() => style.IsLocked),
                componentCount = TryI(() => style.Components.Count),
                components = includeComponents ? components : null,
            }));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMemberNodeShapes(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var levels = new[] { AecsDetailLevel.Low, AecsDetailLevel.Medium, AecsDetailLevel.High };

        var rows = new List<(string Key, object Data)>();
        var dict = new AecsNodeShapeDict(ctx.Db);

        ObjectIdCollection? records = null;
        try { records = dict.Records; } catch { }
        if (records is null) return Page(rows, offset, limit);

        foreach (ObjectId id in records)
        {
            if (id.IsNull) continue;

            AecsNodeShape? shape;
            try { shape = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecsNodeShape; }
            catch { continue; }
            if (shape is null) continue;

            var detail = new List<object>();
            foreach (var level in levels)
            {
                var levelName = level.ToString();
                detail.Add(new
                {
                    detailLevel = levelName,
                    profileDefined = TryB(() => shape.IsProfileDefined(level)),
                    curveCount = TryI(() => shape.GetCurveCount(level)),
                    extents = TryO(() =>
                    {
                        var box = shape.GetExtents(level);
                        if (box is null || !box.IsValid) return null;
                        return (object)new
                        {
                            min = new[] { box.MinPoint.X, box.MinPoint.Y },
                            max = new[] { box.MaxPoint.X, box.MaxPoint.Y },
                            width = box.Width,
                            depth = box.Depth,
                        };
                    }),
                });
            }

            var name = TryS(() => shape.Name) ?? id.Handle.ToString();
            rows.Add((name, new
            {
                name,
                handle = TryS(() => shape.Handle.ToString()),
                description = TryS(() => shape.Description),
                isLocked = TryB(() => shape.IsLocked),
                detailLevels = detail,
            }));
        }

        return Page(rows, offset, limit);
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> CreateColumnGrid(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateLayoutGrid2d(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateLayoutCurve(JsonElement a) => throw Unavailable();
    private static Task<object?> ListMemberStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListMemberNodeShapes(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateMember(JsonElement a) => throw Unavailable();
    private static Task<object?> ListColumnGrids(JsonElement a) => throw Unavailable();
    private static Task<object?> ListLayoutGrids(JsonElement a) => throw Unavailable();
    private static Task<object?> ListMembers(JsonElement a) => throw Unavailable();
    private static Task<object?> SetMemberProperties(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
