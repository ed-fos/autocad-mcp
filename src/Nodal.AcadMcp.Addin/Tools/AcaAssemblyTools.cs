using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are referenced ONLY through explicit aliases, and only inside #if ACA.
// Same reasoning as AcaTools.cs: 'Entity', 'DBObject', 'Dictionary', 'Polyline2d' and 'Curve' all
// collide between Autodesk.Aec.*, Autodesk.Aec.Geometry, Autodesk.AutoCAD.DatabaseServices and
// System, and on a vanilla AutoCAD box these assemblies do not exist at all.
#if ACA
using AecEntity = Autodesk.Aec.DatabaseServices.Entity;
using AecGeo = Autodesk.Aec.DatabaseServices.Geo;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecDict = Autodesk.Aec.DatabaseServices.Dictionary;
using AecAnchorRef = Autodesk.Aec.DatabaseServices.AnchorToReference;
using AecAnchorCurve = Autodesk.Aec.DatabaseServices.AnchorEntityToCurve;
using AecGridAssembly = Autodesk.Aec.DatabaseServices.GridAssembly;

// Autodesk.Aec.Geometry.Polyline2d is a CompoundCurve2d (pure geometry), NOT the AutoCAD
// Polyline2d DB entity. Confirmed by its base type and by Railing.GetPath()/Stair.LeftPolyline
// returning it by value — the AEC API always returns ObjectId for database objects.
using AecPolyline2d = Autodesk.Aec.Geometry.Polyline2d;
using AecSegment2d = Autodesk.Aec.Geometry.Segment2d;

using AecOpenShape = Autodesk.Aec.Arch.DatabaseServices.OpenShape;
using AecWindowAssembly = Autodesk.Aec.Arch.DatabaseServices.WindowAssembly;
using AecWindowAssemblyStyle = Autodesk.Aec.Arch.DatabaseServices.WindowAssemblyStyle;
using AecCurtainWall = Autodesk.Aec.Arch.DatabaseServices.CurtainWallLayout;
using AecCurtainWallStyle = Autodesk.Aec.Arch.DatabaseServices.CurtainWallLayoutStyle;
using AecCurtainWallUnit = Autodesk.Aec.Arch.DatabaseServices.CurtainWallUnit;
using AecCurtainWallUnitStyle = Autodesk.Aec.Arch.DatabaseServices.CurtainWallUnitStyle;
using AecStair = Autodesk.Aec.Arch.DatabaseServices.Stair;
using AecStairStyle = Autodesk.Aec.Arch.DatabaseServices.StairStyle;
using AecRailing = Autodesk.Aec.Arch.DatabaseServices.Railing;
using AecRailingStyle = Autodesk.Aec.Arch.DatabaseServices.RailingStyle;

using DictWindowAssemblyStyle = Autodesk.Aec.Arch.DatabaseServices.DictionaryWindowAssemblyStyle;
using DictCurtainWallStyle = Autodesk.Aec.Arch.DatabaseServices.DictionaryCurtainWallLayoutStyle;
using DictCurtainWallUnitStyle = Autodesk.Aec.Arch.DatabaseServices.DictionaryCurtainWallUnitStyle;
using DictStairStyle = Autodesk.Aec.Arch.DatabaseServices.DictionaryStairStyle;
using DictRailingStyle = Autodesk.Aec.Arch.DatabaseServices.DictionaryRailingStyle;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture assemblies: door/window assemblies, curtain walls, stairs and railings.
///
/// JIT SAFETY — identical contract to AcaTools. Every handler is wrapped by <see cref="Reg"/>,
/// which calls <see cref="AcaSupport.Require"/> first. The implementations that name Aec types are
/// separate [MethodImpl(NoInlining)] methods whose signatures are (JsonElement) -> Task&lt;object?&gt;,
/// so a vanilla AutoCAD box never has to resolve an Aec type reference.
///
/// SAFETY MODEL FOR THE CREATORS. Acad.Write aborts its transaction if the handler throws, so every
/// creator validates its inputs first, sets a *verified* style before the object is appended, and
/// re-reads the finished object afterwards — any failure throws and the whole append is rolled
/// back rather than leaving a malformed AEC object in the drawing. The brief's named hazard ("an
/// AEC object appended without a style is invalid") is handled by ResolveStyle, which refuses to
/// hand back an id whose record is not the exact style class for the object being built.
///
/// NOT IMPLEMENTED — the member exists but the *sequence* could not be confirmed from the
/// declared API surface, and guessing risks a malformed object:
///
///  * create_stair. Stair exposes 'StairCreator Creator { get; set; }', 'Void Create()' and
///    'StairCalculatorData CalculatorData { get; set; }' (ctor takes storeyHeight). What is NOT
///    determinable from metadata is (a) whether the Creator getter hands back a live reference or
///    a detached copy — every other AEC sub-object on these classes (RailingStyle.PostTemplate,
///    StairComponent.*) is read-modify-writeback, so 'stair.Creator = c; c.Set(...)' and
///    'c.Set(...); stair.Creator = c' are not interchangeable; (b) whether Create() must run
///    before or after AppendEntity; (c) which StairCalculatorFlags must be passed to
///    StairCalculatorData.UpdateCalculation to make the rise/tread solve converge. A stair built
///    with the wrong order silently produces zero treads. Reported in skipped[].
///
///  * Anchoring a window assembly to a wall. AnchorWindowAssemblyToWall : AnchorOpeningBaseToWall
///    : AnchorEntityToWall : AnchorEntityToCurve : AnchorToReference exists with a parameterless
///    ctor, 'ObjectId CurveId { get; set; }' and 'Void SetSingleReference(ObjectId, RelationType)',
///    and Geo.SetAnchor(Anchor) is the attach point. The unconfirmable part is whether CurveId
///    alone wires the relationship or SetSingleReference is also required, and which of the eight
///    RelationType members is correct. Free-standing placement is implemented instead.
///
///  * Anchoring a railing to a stair (AnchorRailingToStair / AnchorFreeRailingToStair) — same
///    unresolved anchor-attachment sequence, plus an undocumented stair creation timestamp
///    (SetStairCreationTimeStamp) that pairs with Stair.CreationTimestamp.
///
///  * Creating or editing grid definitions (GridAssemblyDefinition / GridAssemblyComponentSet /
///    the NestedGrid* collections). These drive the divisions, mullions and infills of every
///    assembly here; they are reported read-only. New assemblies take their grid from their style.
/// </summary>
internal static class AcaAssemblyTools
{
    internal static void Register()
    {
        // ---------------------------------------------------------------------- style dictionaries

        Reg("list_window_assembly_styles",
            "Every WindowAssemblyStyle (ACA Door/Window Assembly style) in the drawing, with its "
            + "default width/height/rise and opening shape. Args: limit (default 500), offset.",
            mutating: false, ListWindowAssemblyStyles);

        Reg("list_curtain_wall_styles",
            "Every CurtainWallLayoutStyle in the drawing, with default base height and floor/roof "
            + "line offsets. Args: limit, offset.",
            mutating: false, ListCurtainWallStyles);

        Reg("list_curtain_wall_unit_styles",
            "Every CurtainWallUnitStyle in the drawing (the styles used for curtain-wall infills). "
            + "Args: limit, offset.",
            mutating: false, ListCurtainWallUnitStyles);

        Reg("list_stair_styles",
            "Every StairStyle with its rise/tread rule limits, landing and riser settings. "
            + "Args: limit, offset.",
            mutating: false, ListStairStyles);

        Reg("list_railing_styles",
            "Every RailingStyle with guardrail/handrail/bottomrail heights, post and baluster "
            + "settings. Args: limit, offset.",
            mutating: false, ListRailingStyles);

        // ------------------------------------------------------------------------ object listings

        Reg("list_window_assemblies",
            "Every AEC WindowAssembly (Door/Window Assembly) with length, height, rise, anchoring, "
            + "grid cell/edge counts and style. Args: scope (model|current|all, default model), "
            + "limit (default 500), offset, deepScan.",
            mutating: false, ListWindowAssemblies);

        Reg("list_curtain_walls",
            "Every AEC CurtainWallLayout with length, base height, base-curve type and endpoints, "
            + "grid cell/edge counts and style. Args: scope, limit, offset, deepScan.",
            mutating: false, ListCurtainWalls);

        Reg("list_curtain_wall_units",
            "Every AEC CurtainWallUnit (a curtain-wall unit used as an infill) with length, height "
            + "and style. Args: scope, limit, offset, deepScan.",
            mutating: false, ListCurtainWallUnits);

        Reg("list_stairs",
            "Every AEC Stair with width, tread count, calculated rise-and-tread, justification, "
            + "flight limits, validity and style. Args: scope, limit, offset, deepScan.",
            mutating: false, ListStairs);

        Reg("list_railings",
            "Every AEC Railing with rail heights, post and baluster counts, path length and style. "
            + "Args: scope, limit, offset, deepScan.",
            mutating: false, ListRailings);

        // -------------------------------------------------------------------------- detail by id

        Reg("get_window_assembly",
            "One AEC WindowAssembly in full by handle: placement, size, grid state and style. Args: handle.",
            mutating: false, GetWindowAssembly);

        Reg("get_curtain_wall",
            "One AEC CurtainWallLayout in full by handle: baseline, height, grid state and style. Args: handle.",
            mutating: false, GetCurtainWall);

        Reg("get_stair",
            "One AEC Stair in full by handle: geometry, flight rules, component/body counts, creator "
            + "shape and style. Args: handle.",
            mutating: false, GetStair);

        Reg("get_railing",
            "One AEC Railing in full by handle: every rail/post/baluster parameter, path geometry "
            + "and style. Args: handle.",
            mutating: false, GetRailing);

        // ------------------------------------------------------------------------------- creators

        Reg("create_curtain_wall",
            "Create an AEC CurtainWallLayout from a baseline, as one undo step. Args: startPoint "
            + "[x,y,z], endPoint [x,y,z], optional arcPoint [x,y,z] (a third point ON the arc — "
            + "supply it for a curved curtain wall), style (CurtainWallLayoutStyle name; the "
            + "drawing's Standard style is used when omitted), height (base height; the style "
            + "default is used when omitted), normal [x,y,z] (default [0,0,1]), space "
            + "(current|model|paper, default current), layer, description. The grid (divisions, "
            + "mullions, infills) comes from the style and is not editable here.",
            mutating: true, CreateCurtainWall);

        Reg("create_window_assembly",
            "Create a free-standing AEC WindowAssembly (Door/Window Assembly), as one undo step. "
            + "Args: location [x,y,z] (the assembly's insertion point), style "
            + "(WindowAssemblyStyle name; Standard when omitted), width (aka length), height, rise "
            + "(all default to the style's defaults), rotation (degrees about the normal) or "
            + "rotationRadians, normal [x,y,z] (default [0,0,1]), space, layer, description. "
            + "NOTE: this places the assembly free-standing. Anchoring it into a wall "
            + "(AnchorWindowAssemblyToWall) is not implemented — see the class remarks.",
            mutating: true, CreateWindowAssembly);

        Reg("create_railing",
            "Create a free-standing AEC Railing along a path, as one undo step. Args: style "
            + "(RailingStyle name; Standard when omitted), and EITHER pathHandle (handle of an "
            + "existing LWPOLYLINE, LINE or ARC to trace — arcs are kept as arcs) OR startPoint "
            + "[x,y] + endPoint [x,y] for a single straight run. Optional: space, layer, "
            + "description. The path is taken in the WCS XY plane at Z=0; attaching the railing to "
            + "a stair (AnchorRailingToStair) is not implemented — see the class remarks.",
            mutating: true, CreateRailing);
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

    /// <summary>
    /// "These two points are the same" for baseline input, in drawing units. Deliberately looser
    /// than AutoCAD's global equal-point tolerance (1e-10): a curtain wall 1e-9 units long is a bug, not a
    /// curtain wall, and it is better to refuse it than to append a degenerate assembly.
    /// </summary>
    private const double MinSpan = 1e-6;

    /// <summary>Below this length a direction vector is treated as zero.</summary>
    private const double MinVectorLength = 1e-10;

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

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
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

#if ACA

    // ------------------------------------------------------------------------- guarded accessors
    // AEC property getters throw for degenerate or not-yet-regenerated geometry. One bad property
    // must never lose a whole listing, and the bridge drops nulls, so a failed read just vanishes.

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

    private static double[]? TryPt2(Func<Point2d> f)
    {
        try { var p = f(); return new[] { p.X, p.Y }; }
        catch { return null; }
    }

    private static double[]? TryVec(Func<Vector3d> f)
    {
        try { var v = f(); return new[] { v.X, v.Y, v.Z }; }
        catch { return null; }
    }

    private static object? TryO(Func<object?> f) { try { return f(); } catch { return null; } }

    // ------------------------------------------------------------------------------- AEC scanning

    /// <summary>Which block table records to walk. Default is model space.</summary>
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

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// Cheap pre-filter: every ACA runtime class is registered as AecDbWall, AecDbStair… so the
    /// name test rejects plain AutoCAD geometry without opening the object. deepScan skips it.
    /// </summary>
    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    /// <summary>Every object of exactly one managed AEC type in the requested spaces.</summary>
    private static List<(string Handle, ObjectId Id)> Scan<T>(AcadContext ctx, JsonElement a) where T : class
    {
        var deep = Args.BoolOpt(a, "deepScan", false);
        var found = new List<(string Handle, ObjectId Id)>();

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

                if (obj is T) found.Add((id.Handle.ToString(), id));
            }
        }

        return found;
    }

    /// <summary>Open by handle and demand a specific managed AEC type.</summary>
    private static T Require<T>(AcadContext ctx, JsonElement a, string typeName) where T : class
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);
        var obj = ctx.Tr.GetObject(id, OpenMode.ForRead);

        if (obj is not T typed)
            throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC {typeName}.");

        return typed;
    }

    // ------------------------------------------------------------------------------ shared pieces

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

    /// <summary>Handle of whatever a Geo is anchored to — for an assembly in a wall, its host wall.</summary>
    private static string? HostHandle(AcadContext ctx, AecGeo geo)
    {
        try
        {
            if (!geo.IsAnchored || geo.AnchorId.IsNull) return null;
            var anchor = ctx.Tr.GetObject(geo.AnchorId, OpenMode.ForRead);

            if (anchor is AecAnchorCurve curveAnchor && !curveAnchor.CurveId.IsNull)
                return curveAnchor.CurveId.Handle.ToString();

            if (anchor is AecAnchorRef refAnchor && !refAnchor.SingleReferenceId.IsNull)
                return refAnchor.SingleReferenceId.Handle.ToString();
        }
        catch { }
        return null;
    }

    private static object Identity(AcadContext ctx, DBObject obj, string type)
    {
        var style = obj is AecEntity ae ? StyleRecord(ctx, ae) : null;
        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            type,
            objectClass = ClassName(obj.ObjectId),
            layer = TryS(() => (obj as Entity)?.Layer),
            description = TryS(() => (obj as AecEntity)?.Description),
            styleName = TryS(() => style?.Name),
            styleHandle = TryS(() => style?.Handle.ToString()),
        };
    }

    /// <summary>Where a Geo sits: ECS origin, axes and rotation.</summary>
    private static object Placement(AecGeo geo) => new
    {
        location = TryPt(() => geo.Location),
        rotation = TryD(() => geo.Rotation),
        rotationDegrees = TryD(() => geo.Rotation * 180.0 / Math.PI),
        normal = TryVec(() => geo.Normal),
        direction = TryVec(() => geo.Direction),
        isAnchored = TryB(() => geo.IsAnchored),
    };

    /// <summary>
    /// Aec Entity.BaseCurve is declared as an unqualified 'Curve' in the declared API surface, and
    /// Autodesk.Aec.Modeler also defines a Curve. Going via object compiles either way and simply
    /// degrades to null at runtime if this is the modeller type rather than the DB curve.
    /// </summary>
    private static double[]? BaseCurveStart(AecEntity e) =>
        TryPt(() => ((Curve)(object)e.BaseCurve).StartPoint);

    private static double[]? BaseCurveEnd(AecEntity e) =>
        TryPt(() => ((Curve)(object)e.BaseCurve).EndPoint);

    /// <summary>Everything a GridAssembly exposes about its grid without touching the definition tree.</summary>
    private static object GridState(AecGridAssembly g) => new
    {
        cellCount = TryI(() => g.CellCount),
        edgeCount = TryI(() => g.EdgeCount),
        isStyleBased = TryB(() => g.IsStyleBased),
        gridDefinitionLocation = TryS(() => g.GridDefinitionLocation.ToString()),
        gridDefinitionName = TryS(() => g.GridAssemblyDefinition?.Name),
        editDepth = TryI(() => g.EditDepth),
        xAxisLength = TryD(() => g.XAxisLength),
        yAxisLength = TryD(() => g.YAxisLength),
        yAxisOffset = TryD(() => g.YAxisOffset),
        baseCurveRadius = TryD(() => g.BaseCurveRadius),
        startMiterAngle = TryD(() => g.StartMiterAngle),
        endMiterAngle = TryD(() => g.EndMiterAngle),
        isManualGridDivision = TryB(() => g.IsManualGridDivision),
        hasClippingBoundary = TryB(() => g.HasClippingBoundary),
        hasClippingProfile = TryB(() => g.HasClippingProfile),
        hasClippingRectangle = TryB(() => g.HasClippingRectangle),
        hasEdgeProfile = TryB(() => g.HasEdgeProfile),
        interferencesCount = TryI(() => g.InterferencesCount),
        supportsStyleBasedGridDefinition = TryB(() => g.SupportsStyleBasedGridDefinition),
        supportsInstanceBasedGridDefinition = TryB(() => g.SupportsInstanceBasedGridDefinition),
        supportsInterference = TryB(() => g.SupportsInterference),
    };

    // ------------------------------------------------------------------------------- style views

    private static object StyleIdentity(AecRecord rec) => new
    {
        name = TryS(() => rec.Name),
        handle = TryS(() => rec.Handle.ToString()),
        description = TryS(() => rec.Description),
        localizedName = TryS(() => rec.LocalizedName),
        isLocked = TryB(() => rec.IsLocked),
    };

    private static object WindowAssemblyStyleInfo(AecWindowAssemblyStyle s) => new
    {
        shape = TryS(() => s.Shape.ToString()),
        defaultWidth = TryD(() => s.DefaultWidth),
        defaultHeight = TryD(() => s.DefaultHeight),
        defaultRise = TryD(() => s.DefaultRise),
        defaultFlags = TryI(() => s.DefaultFlags),
        customShapeHandle = TryS(() => s.CustomShapeId.IsNull ? null : s.CustomShapeId.Handle.ToString()),
    };

    private static object CurtainWallStyleInfo(AecCurtainWallStyle s) => new
    {
        defaultBaseHeight = TryD(() => s.DefaultBaseHeight),
        defaultFloorlineOffset = TryD(() => s.DefaultFloorlineOffset),
        defaultRooflineOffset = TryD(() => s.DefaultRooflineOffset),
        defaultFlags = TryI(() => s.DefaultFlags),
    };

    private static object StairStyleInfo(AecStairStyle s) => new
    {
        flags = TryS(() => s.Flags.ToString()),
        riserType = TryS(() => s.RiserType.ToString()),
        riserThickness = TryD(() => s.RiserThickness),
        treadThickness = TryD(() => s.TreadThickness),
        nosing = TryD(() => s.Nosing),
        minimumRise = TryD(() => s.MinimumRise),
        optimumRise = TryD(() => s.OptimumRise),
        maximumRise = TryD(() => s.MaximumRise),
        minimumTread = TryD(() => s.MinimumTread),
        optimumTread = TryD(() => s.OptimumTread),
        maximumTread = TryD(() => s.MaximumTread),
        riseFactor = TryD(() => s.RiseFactor),
        treadFactor = TryD(() => s.TreadFactor),
        lowerRuleLimit = TryD(() => s.LowerRuleLimit),
        upperRuleLimit = TryD(() => s.UpperRuleLimit),
        overrideCalculator = TryB(() => s.OverrideCalculator),
        landingLength = TryD(() => s.LandingLength),
        landingThickness = TryD(() => s.LandingThickness),
        landingLocation = TryS(() => s.LandingLocation.ToString()),
        additionalWidth = TryD(() => s.AdditionalWidth),
        allowRisersInLandingArea = TryB(() => s.AllowRisersInLandingArea),
        extendLandingForStringer = TryB(() => s.ExtendLandingForStringer),
        curveCenterStringer = TryB(() => s.CurveCenterStringer),
        isSlab = TryB(() => s.Slab),
        componentDefinitionCount = TryI(() => s.ComponentDefinitions.Count),
    };

    private static object RailingStyleInfo(AecRailingStyle s) => new
    {
        hasGuardrail = TryB(() => s.Guardrail),
        guardrailHeight = TryD(() => s.GuardrailHeight),
        guardrailOffset = TryD(() => s.GuardrailOffset),
        guardrailPlacement = TryS(() => s.GuardrailPlacement.ToString()),
        guardrailSlopingHeight = TryD(() => s.GuardrailSlopingHeight),
        hasHandrail = TryB(() => s.Handrail),
        handrailHeight = TryD(() => s.HandrailHeight),
        handrailOffset = TryD(() => s.HandrailOffset),
        handrailPlacement = TryS(() => s.HandrailPlacement.ToString()),
        handrailSlopingHeight = TryD(() => s.HandrailSlopingHeight),
        hasBottomrail = TryB(() => s.Bottomrail),
        bottomrailCount = TryI(() => s.BottomrailCount),
        bottomrailHeight = TryD(() => s.BottomrailHeight),
        bottomrailSpacing = TryD(() => s.BottomrailSpacing),
        bottomrailPlacement = TryS(() => s.BottomrailPlacement.ToString()),
        hasPosts = TryB(() => s.Posts),
        hasDynamicPosts = TryB(() => s.DynamicPosts),
        dynamicPostSpacing = TryD(() => s.DynamicPostSpacing),
        hasCornerPosts = TryB(() => s.CornerPosts),
        hasBalusters = TryB(() => s.Balusters),
        balusterSpacing = TryD(() => s.BalusterSpacing),
        balustersPerTread = TryI(() => s.BalustersPerTread),
        balusterTreadOverride = TryB(() => s.BalusterTreadOverride),
        postDimensionsFixed = TryB(() => s.PostDimensionsFixed),
        railDimensionsFixed = TryB(() => s.RailDimensionsFixed),
        extensionsFixed = TryB(() => s.ExtensionsFixed),
        useStairLandingExtensionAtFloor = TryB(() => s.UseStairLandingExtensionAtFloor),
        useStairLandingExtensionAtLanding = TryB(() => s.UseStairLandingExtensionAtLanding),
    };

    // ------------------------------------------------------------------------------ detail views

    /// <summary>The resolved opening shape of an assembly — the frame profile ACA actually built.</summary>
    private static object? OpenShapeInfo(AecOpenShape? shape) => shape is null ? null : new
    {
        isValid = TryB(() => shape.ShapeIsValid),
        hasRise = TryB(() => shape.HasRise),
        width = TryD(() => shape.Width),
        height = TryD(() => shape.Height),
        rise = TryD(() => shape.Rise),
        frameWidth = TryD(() => shape.FrameWidth),
        frameDepth = TryD(() => shape.FrameDepth),
        trimBottom = TryB(() => shape.TrimBottom),
    };

    private static object WindowAssemblyInfo(AcadContext ctx, AecWindowAssembly wa)
    {
        var style = StyleRecord(ctx, wa) as AecWindowAssemblyStyle;
        return new
        {
            identity = Identity(ctx, wa, "WindowAssembly"),
            length = TryD(() => wa.Length),
            height = TryD(() => wa.Height),
            rise = TryD(() => wa.Rise),
            isCustomShape = TryB(() => wa.IsCustomShape),
            isAnchoredToWall = TryB(() => wa.IsAnchoredToWall),
            hostHandle = HostHandle(ctx, wa),
            endcapStyleName = TryS(() => wa.EndcapStyleName),
            openShape = OpenShapeInfo(TryO(() => wa.OpenShape) as AecOpenShape),
            placement = Placement(wa),
            grid = GridState(wa),
            style = style is null ? null : new
            {
                identity = StyleIdentity(style),
                detail = WindowAssemblyStyleInfo(style),
            },
        };
    }

    private static object CurtainWallInfo(AcadContext ctx, AecCurtainWall cw)
    {
        var style = StyleRecord(ctx, cw) as AecCurtainWallStyle;
        return new
        {
            identity = Identity(ctx, cw, "CurtainWall"),
            length = TryD(() => cw.Length),
            baseHeight = TryD(() => cw.BaseHeight),
            baseCurveType = TryS(() => cw.BaseCurveType.ToString()),
            startPoint = BaseCurveStart(cw),
            endPoint = BaseCurveEnd(cw),
            // CurtainWallLayoutBaseCurve is an Autodesk.AutoCAD.Geometry.Curve2d in the ECS.
            baseCurveStart2d = TryPt2(() => ((Curve2d)(object)cw.CurtainWallLayoutBaseCurve).StartPoint),
            baseCurveEnd2d = TryPt2(() => ((Curve2d)(object)cw.CurtainWallLayoutBaseCurve).EndPoint),
            referencedBaseCurveHandle =
                TryS(() => cw.ReferencedBaseCurve.IsNull ? null : cw.ReferencedBaseCurve.Handle.ToString()),
            floorLineIsStandard = TryB(() => cw.FloorLine.IsStandardCutLine),
            floorLineSegmentCount = TryI(() => cw.FloorLine.SegmentCount),
            roofLineIsStandard = TryB(() => cw.RoofLine.IsStandardCutLine),
            roofLineSegmentCount = TryI(() => cw.RoofLine.SegmentCount),
            placement = Placement(cw),
            grid = GridState(cw),
            style = style is null ? null : new
            {
                identity = StyleIdentity(style),
                detail = CurtainWallStyleInfo(style),
            },
        };
    }

    private static object CurtainWallUnitInfo(AcadContext ctx, AecCurtainWallUnit cu)
    {
        var style = StyleRecord(ctx, cu) as AecCurtainWallUnitStyle;
        return new
        {
            identity = Identity(ctx, cu, "CurtainWallUnit"),
            length = TryD(() => cu.Length),
            height = TryD(() => cu.Height),
            placement = Placement(cu),
            grid = GridState(cu),
            style = style is null ? null : StyleIdentity(style),
        };
    }

    private static object StairInfo(AcadContext ctx, AecStair st)
    {
        var style = StyleRecord(ctx, st) as AecStairStyle;
        return new
        {
            identity = Identity(ctx, st, "Stair"),
            width = TryD(() => st.Width),
            treadCount = TryI(() => st.TreadCount),
            calculatedRiseAndTread = TryD(() => st.CalculatedRiseAndTread),
            justification = TryS(() => st.Justification.ToString()),
            flags = TryS(() => st.Flags.ToString()),
            riserType = TryS(() => st.RiserType.ToString()),
            terminateWith = TryS(() => st.TerminateWith.ToString()),
            isRamp = TryB(() => st.IsRamp),
            isValid = TryB(() => st.IsValid),
            isCustomShape = TryB(() => st.IsCustomShape),
            isOverlapping = TryB(() => st.IsOverlapping),
            openRiser = TryB(() => st.OpenRiser),
            nosing = TryD(() => st.Nosing),
            verticalOrientationUp = TryB(() => st.VerticalOrientationUp),
            requiredPathLength = TryD(() => st.RequiredPathLength),
            userPathOffset = TryD(() => st.UserPathOffset),
            distanceFromNarrowEdge = TryD(() => st.DistanceFromNarrowEdge),
            additionalLandingWidth = TryD(() => st.AdditionalLandingWidth),
            allowRisersInLandingArea = TryB(() => st.AllowRisersInLandingArea),
            extendLandingForStringer = TryB(() => st.ExtendLandingForStringer),
            extendAlignmentOnUpperFlight = TryB(() => st.ExtendAlignmentOnUpperFlight),
            placeUnevenTreadOnUpperFlight = TryB(() => st.PlaceUnevenTreadOnUpperFlight),
            maximumFlightLimitType = TryS(() => st.MaximumFlightLimitType.ToString()),
            maximumHeightPerFlight = TryD(() => st.MaximumHeightPerFlight),
            maximumTreadsPerFlight = TryI(() => st.MaximumTreadsPerFlight),
            minimumFlightLimitType = TryS(() => st.MinimumFlightLimitType.ToString()),
            minimumHeightPerFlight = TryD(() => st.MinimumHeightPerFlight),
            minimumTreadsPerFlight = TryI(() => st.MinimumTreadsPerFlight),
            minimumLandingStartExtension = TryD(() => st.MinimumLandingStartExtension),
            minimumLandingEndExtension = TryD(() => st.MinimumLandingEndExtension),
            componentCount = TryI(() => st.Components.Count),
            treadBodyCount = TryI(() => st.TreadBodyCount),
            riserBodyCount = TryI(() => st.RiserBodyCount),
            landingBodyCount = TryI(() => st.LandingBodyCount),
            stringerBodyCount = TryI(() => st.StringerBodyCount),
            concreteBodyCount = TryI(() => st.ConcreteBodyCount),
            clearanceBodyCount = TryI(() => st.ClearanceBodyCount),
            interferencesCount = TryI(() => st.InterferencesCount),
            creationTimestamp = TryI(() => st.CreationTimestamp),
            winderStyleName = TryS(() => RecordName(ctx, st.WinderStyleId)),
            creator = TryO(() => new
            {
                shapeType = TryS(() => st.Creator.ShapeType.ToString()),
                width = TryD(() => st.Creator.Width),
                isValid = TryB(() => st.Creator.IsValid),
                pathLength = TryD(() => st.Creator.Curve.Length),
                pathSegmentCount = TryI(() => st.Creator.Curve.SegmentCount),
                pathStart = TryPt2(() => st.Creator.Curve.StartPoint),
                pathEnd = TryPt2(() => st.Creator.Curve.EndPoint),
            }),
            calculator = TryO(() => new
            {
                storeyHeight = TryD(() => st.CalculatorData.StoreyHeight),
                stairHeight = TryD(() => st.CalculatorData.StairHeight),
                rise = TryD(() => st.CalculatorData.Rise),
                tread = TryD(() => st.CalculatorData.Tread),
                riserCount = TryI(() => st.CalculatorData.RiserCount),
                riseLength = TryD(() => st.CalculatorData.RiseLength),
                treadThickness = TryD(() => st.CalculatorData.TreadThickness),
                riserThickness = TryD(() => st.CalculatorData.RiserThickness),
                headroomHeight = TryD(() => st.CalculatorData.HeadroomHeight),
                landingThickness = TryD(() => st.CalculatorData.LandingThickness),
                topFloorLevel = TryD(() => st.CalculatorData.TopFloorLevel),
                bottomFloorLevel = TryD(() => st.CalculatorData.BottomFloorLevel),
                overrideCalculator = TryB(() => st.CalculatorData.OverrideCalculator),
            }),
            walkline = TryO(() => new
            {
                length = TryD(() => st.Walkline.Length),
                segmentCount = TryI(() => st.Walkline.SegmentCount),
                start = TryPt2(() => st.Walkline.StartPoint),
                end = TryPt2(() => st.Walkline.EndPoint),
            }),
            placement = Placement(st),
            style = style is null ? null : new
            {
                identity = StyleIdentity(style),
                detail = StairStyleInfo(style),
            },
        };
    }

    private static object RailingInfo(AcadContext ctx, AecRailing r)
    {
        var style = StyleRecord(ctx, r) as AecRailingStyle;
        return new
        {
            identity = Identity(ctx, r, "Railing"),
            guardrailCount = TryI(() => r.GuardrailCount),
            guardrailHeight = TryD(() => r.GuardrailHeight),
            guardrailOffset = TryD(() => r.GuardrailOffset),
            guardrailPlacement = TryS(() => r.GuardrailPlacement.ToString()),
            guardrailSlopingHeight = TryD(() => r.GuardrailSlopingHeight),
            handrailCount = TryI(() => r.HandrailCount),
            handrailHeight = TryD(() => r.HandrailHeight),
            handrailOffset = TryD(() => r.HandrailOffset),
            handrailPlacement = TryS(() => r.HandrailPlacement.ToString()),
            handrailSlopingHeight = TryD(() => r.HandrailSlopingHeight),
            bottomrailCount = TryI(() => r.BottomrailCount),
            bottomrailHeight = TryD(() => r.BottomrailHeight),
            bottomrailOffset = TryD(() => r.BottomrailOffset),
            bottomrailPlacement = TryS(() => r.BottomrailPlacement.ToString()),
            bottomrailSpacing = TryD(() => r.BottomrailSpacing),
            bottomrailSlopingHeight = TryD(() => r.BottomrailSlopingHeight),
            lowerRailCount = TryI(() => r.LowerRailCount),
            postCount = TryI(() => r.PostCount),
            mainPostCount = TryI(() => r.MainPostCount),
            subPostCount = TryI(() => r.SubPostCount),
            explicitPostCount = TryI(() => r.ExplicitPostCount),
            minPostSpacing = TryD(() => r.MinPostSpacing),
            maxPostSpacing = TryD(() => r.MaxPostSpacing),
            postsPerpendicular = TryB(() => r.PostsPerpendicular),
            postsPerpendicularToSurface = TryB(() => r.PostsPerpendicularToSurface),
            postsMaintainSlopeOnLandings = TryB(() => r.PostsMaintainSlopeOnLandings),
            balusterCount = TryI(() => r.BalusterCount),
            balusterSpacing = TryD(() => r.BalusterSpacing),
            balustersPerTread = TryI(() => r.BalustersPerTread),
            balusterTreadOverride = TryB(() => r.BalusterTreadOverride),
            railComponents = TryI(() => r.RailComponents),
            minimumLength = TryD(() => r.MinimumLength),
            useStairLandingExtensionAtFloor = TryB(() => r.UseStairLandingExtensionAtFloor),
            useStairLandingExtensionAtLanding = TryB(() => r.UseStairLandingExtensionAtLanding),
            hostHandle = HostHandle(ctx, r),
            path = TryO(() => new
            {
                length = TryD(() => r.GetPath().Length),
                segmentCount = TryI(() => r.GetPath().SegmentCount),
                isClosed = TryB(() => r.GetPath().IsClosed),
                start = TryPt2(() => r.GetPath().StartPoint),
                end = TryPt2(() => r.GetPath().EndPoint),
            }),
            placement = Placement(r),
            style = style is null ? null : new
            {
                identity = StyleIdentity(style),
                detail = RailingStyleInfo(style),
            },
        };
    }

    // ------------------------------------------------------------------------ style dictionaries

    /// <summary>
    /// Page a style dictionary. <paramref name="open"/> is a factory rather than an instance so
    /// that a drawing which has never held this kind of style — where constructing the dictionary
    /// wrapper itself fails — reports a clean error instead of an internal_error.
    /// </summary>
    private static object ListStyleDictionary(
        AcadContext ctx, JsonElement a, Func<AecDict> open, Func<AecRecord, object?> detail)
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        ObjectIdCollection ids;
        try { ids = open().Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("style_dictionary_unavailable",
                "This drawing has no style dictionary of that kind yet.", ex.Message);
        }

        foreach (ObjectId id in ids)
        {
            if (id.IsNull || id.IsErased) continue;

            AecRecord? rec;
            try { rec = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecRecord; }
            catch { continue; }
            if (rec is null) continue;

            var key = TryS(() => rec.Name) ?? id.Handle.ToString();
            rows.Add((key, new { identity = StyleIdentity(rec), detail = detail(rec) }));
        }

        return Page(rows, offset, limit);
    }

    /// <summary>
    /// Resolve a style by name, or fall back to the drawing's Standard style, and REFUSE to return
    /// an id whose record is not the exact style class for the object being built. Assigning a
    /// foreign style id is the one mistake that makes an AEC object structurally invalid.
    /// </summary>
    private static ObjectId ResolveStyle<TStyle>(
        AcadContext ctx, AecDict dict, string? name, Func<ObjectId> standard, string kind)
        where TStyle : AecRecord
    {
        ObjectId id;

        if (string.IsNullOrWhiteSpace(name))
        {
            try { id = standard(); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("style_not_found",
                    $"This drawing has no Standard {kind} style and none was named.",
                    ex.Message);
            }
        }
        else
        {
            bool exists;
            try { exists = dict.Has(name!, ctx.Tr); }
            catch { exists = false; }

            if (!exists)
                throw new AcadMcpException("style_not_found",
                    $"No {kind} style named '{name}' in this drawing.",
                    "Available: " + string.Join(", ", StyleNames(dict)));

            try { id = dict.GetAt(name!); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("style_not_found",
                    $"Could not open {kind} style '{name}'.", ex.Message);
            }
        }

        if (id.IsNull)
            throw new AcadMcpException("style_not_found", $"Resolved a null {kind} style id.");

        DBObject rec;
        try { rec = ctx.Tr.GetObject(id, OpenMode.ForRead); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("style_not_found", $"Could not read the {kind} style.", ex.Message);
        }

        if (rec is not TStyle)
            throw new AcadMcpException("wrong_style_type",
                $"'{name ?? "Standard"}' is a {ClassName(id)}, not a {kind} style.",
                "Refusing to build the object: a foreign style id produces an invalid AEC object.");

        return id;
    }

    private static List<string> StyleNames(AecDict dict)
    {
        var names = new List<string>();
        try
        {
            if (dict.NamesInUse is IEnumerable seq)
                foreach (var item in seq)
                    if (item is not null) names.Add(item.ToString() ?? string.Empty);
        }
        catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // ----------------------------------------------------------------------------- creation parts

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
                    $"'space' must be current, model or paper; got '{space}'.");
        }
    }

    private static void ApplyLayer(AcadContext ctx, Entity ent, JsonElement a)
    {
        var layer = Args.StrOpt(a, "layer");
        if (string.IsNullOrWhiteSpace(layer)) return;

        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layer))
            throw new AcadMcpException("layer_not_found", $"No layer named '{layer}' in this drawing.");

        ent.Layer = layer!;
    }

    private static void ApplyDescription(AecEntity ent, JsonElement a)
    {
        var description = Args.StrOpt(a, "description");
        if (string.IsNullOrWhiteSpace(description)) return;

        try { ent.Description = description!; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("description_rejected",
                "AutoCAD Architecture refused the description.", ex.Message);
        }
    }

    private static Vector3d NormalArg(JsonElement a)
    {
        if (!Has(a, "normal")) return Vector3d.ZAxis;

        var p = Args.Point(a, "normal");
        var v = new Vector3d(p.X, p.Y, p.Z);
        if (v.Length < MinVectorLength)
            throw new AcadMcpException("bad_argument", "'normal' must not be a zero-length vector.");

        return v.GetNormal();
    }

    private static double RotationArg(JsonElement a)
    {
        if (Has(a, "rotationRadians")) return Args.Dbl(a, "rotationRadians");
        if (Has(a, "rotation")) return Args.Dbl(a, "rotation") * Math.PI / 180.0;
        return 0.0;
    }

    private static double PositiveArg(JsonElement a, string name, double fallback)
    {
        var supplied = Has(a, name);
        var value = supplied ? Args.Dbl(a, name) : fallback;

        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            string? detail = supplied
                ? null
                : $"No '{name}' was supplied and the style's default ({fallback}) is not usable.";
            throw new AcadMcpException("bad_argument",
                $"'{name}' must be a positive number; got {value}.", detail);
        }

        return value;
    }

    /// <summary>
    /// Refuse a source curve that does not lie in the world XY plane. Autodesk.Aec.Geometry's
    /// Polyline2d is planar, so tracing a tilted curve would put the railing somewhere the caller
    /// did not ask for — an explicit refusal is better than silently wrong geometry.
    /// </summary>
    private static void RequireWorldXy(Vector3d normal, string what, string handle)
    {
        double z;
        try { z = normal.GetNormal().Z; }
        catch { z = 0; }

        if (z < 1.0 - 1e-6)
            throw new AcadMcpException("bad_path",
                $"{what} '{handle}' does not lie in the world XY plane with a +Z normal.",
                "A railing path is planar; flatten the source curve or draw it in plan.");
    }

    /// <summary>Add one segment of an AutoCAD Polyline to an AEC path, keeping arcs as arcs.</summary>
    private static bool AppendPolylineSegment(AecPolyline2d path, Polyline pl, int index)
    {
        SegmentType type;
        try { type = pl.GetSegmentType(index); }
        catch { return false; }

        try
        {
            switch (type)
            {
                case SegmentType.Line:
                    path.AddSegment(new AecSegment2d(pl.GetLineSegment2dAt(index)), true);
                    return true;
                case SegmentType.Arc:
                    path.AddSegment(new AecSegment2d(pl.GetArcSegment2dAt(index)), true);
                    return true;
                default:
                    // Coincident / Point / Empty carry no length; skipping them is correct.
                    return false;
            }
        }
        catch { return false; }
    }

    /// <summary>Build the railing path: trace an existing curve by handle, or a single straight run.</summary>
    private static (AecPolyline2d Path, object Source) RailingPath(AcadContext ctx, JsonElement a)
    {
        var pathHandle = Args.StrOpt(a, "pathHandle");

        if (!string.IsNullOrWhiteSpace(pathHandle))
        {
            var id = Acad.ToObjectId(ctx.Db, pathHandle!);
            var obj = ctx.Tr.GetObject(id, OpenMode.ForRead);
            var path = new AecPolyline2d();
            double elevation = 0;

            switch (obj)
            {
                case Polyline pl:
                {
                    // GetLineSegment2dAt / GetArcSegment2dAt return the polyline's OCS coordinates.
                    // Those only equal WCS XY when the polyline lies in the world XY plane, so a
                    // tilted source is refused rather than silently traced in the wrong place.
                    RequireWorldXy(pl.Normal, "Polyline", pathHandle!);
                    elevation = TryD(() => pl.Elevation) ?? 0;
                    var last = pl.NumberOfVertices - (pl.Closed ? 0 : 1);
                    var added = 0;
                    for (var i = 0; i < last; i++)
                        if (AppendPolylineSegment(path, pl, i)) added++;

                    if (added == 0)
                        throw new AcadMcpException("bad_path",
                            $"Polyline '{pathHandle}' has no usable line or arc segments.");
                    break;
                }
                case Line line:
                {
                    if (Math.Abs(line.StartPoint.Z - line.EndPoint.Z) > MinSpan)
                        throw new AcadMcpException("bad_path",
                            $"Line '{pathHandle}' is not horizontal; a railing path is planar.",
                            "Flatten the line, or draw the run you want at a single elevation.");

                    elevation = line.StartPoint.Z;
                    path.AddSegment(new AecSegment2d(
                        new Point2d(line.StartPoint.X, line.StartPoint.Y),
                        new Point2d(line.EndPoint.X, line.EndPoint.Y)), true);
                    break;
                }
                case Arc arc:
                {
                    // Arc.StartAngle / EndAngle are measured in the arc's own plane, so the +Z
                    // check is what makes the XY reference vector below correct.
                    RequireWorldXy(arc.Normal, "Arc", pathHandle!);
                    elevation = arc.Center.Z;
                    var arc2d = new CircularArc2d(
                        new Point2d(arc.Center.X, arc.Center.Y),
                        arc.Radius, arc.StartAngle, arc.EndAngle,
                        Vector2d.XAxis, false);
                    path.AddSegment(new AecSegment2d(arc2d), true);
                    break;
                }
                default:
                    throw new AcadMcpException("bad_path",
                        $"Handle '{pathHandle}' is a {ClassName(id)}; "
                        + "a railing path must be an LWPOLYLINE, LINE or ARC.");
            }

            return (path, new
            {
                kind = "pathHandle",
                pathHandle,
                sourceType = ClassName(id),
                // Autodesk.Aec.Geometry.Polyline2d is planar; a source elevation is reported, not applied.
                sourceElevation = elevation,
                elevationApplied = false,
            });
        }

        if (!Has(a, "startPoint") || !Has(a, "endPoint"))
            throw new AcadMcpException("bad_argument",
                "A railing needs a path: supply 'pathHandle' (an LWPOLYLINE, LINE or ARC to trace), "
                + "or both 'startPoint' and 'endPoint'.");

        var start = Args.Point(a, "startPoint");
        var end = Args.Point(a, "endPoint");
        var start2d = new Point2d(start.X, start.Y);
        var end2d = new Point2d(end.X, end.Y);

        if (start2d.GetDistanceTo(end2d) < MinSpan)
            throw new AcadMcpException("bad_argument",
                "'startPoint' and 'endPoint' are the same point in plan; the railing would have no length.");

        var straight = new AecPolyline2d();
        straight.AddSegment(new AecSegment2d(start2d, end2d), true);

        return (straight, new
        {
            kind = "points",
            startPoint = new[] { start.X, start.Y },
            endPoint = new[] { end.X, end.Y },
            sourceElevation = 0.0,
            elevationApplied = false,
        });
    }

    /// <summary>
    /// Rebuild a grid assembly's contents after its geometry changed. Best effort: a refusal here
    /// is reported rather than thrown, because AutoCAD regenerates the grid on the next regen too.
    /// </summary>
    private static string? UpdateGrid(AecGridAssembly g)
    {
        try { g.UpdateGridContents(); return null; }
        catch (System.Exception ex) { return ex.Message; }
    }

    // ----------------------------------------------------------------------------- implementations

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListWindowAssemblyStyles(JsonElement a) => Acad.Read<object?>(ctx =>
        ListStyleDictionary(ctx, a, () => new DictWindowAssemblyStyle(ctx.Db),
            rec => rec is AecWindowAssemblyStyle s ? WindowAssemblyStyleInfo(s) : null));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListCurtainWallStyles(JsonElement a) => Acad.Read<object?>(ctx =>
        ListStyleDictionary(ctx, a, () => new DictCurtainWallStyle(ctx.Db),
            rec => rec is AecCurtainWallStyle s ? CurtainWallStyleInfo(s) : null));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListCurtainWallUnitStyles(JsonElement a) => Acad.Read<object?>(ctx =>
        // CurtainWallUnitStyle declares no members of its own beyond GridAssemblyStyle's grid tree,
        // which is not projected here; identity is all there is to report.
        ListStyleDictionary(ctx, a, () => new DictCurtainWallUnitStyle(ctx.Db), _ => null));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListStairStyles(JsonElement a) => Acad.Read<object?>(ctx =>
        ListStyleDictionary(ctx, a, () => new DictStairStyle(ctx.Db),
            rec => rec is AecStairStyle s ? StairStyleInfo(s) : null));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListRailingStyles(JsonElement a) => Acad.Read<object?>(ctx =>
        ListStyleDictionary(ctx, a, () => new DictRailingStyle(ctx.Db),
            rec => rec is AecRailingStyle s ? RailingStyleInfo(s) : null));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListWindowAssemblies(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan<AecWindowAssembly>(ctx, a))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecWindowAssembly wa)
                    rows.Add((entry.Handle, WindowAssemblyInfo(ctx, wa)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListCurtainWalls(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan<AecCurtainWall>(ctx, a))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecCurtainWall cw)
                    rows.Add((entry.Handle, CurtainWallInfo(ctx, cw)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListCurtainWallUnits(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan<AecCurtainWallUnit>(ctx, a))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecCurtainWallUnit cu)
                    rows.Add((entry.Handle, CurtainWallUnitInfo(ctx, cu)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListStairs(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan<AecStair>(ctx, a))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecStair st)
                    rows.Add((entry.Handle, StairInfo(ctx, st)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListRailings(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in Scan<AecRailing>(ctx, a))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecRailing r)
                    rows.Add((entry.Handle, RailingInfo(ctx, r)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetWindowAssembly(JsonElement a) => Acad.Read<object?>(ctx =>
        WindowAssemblyInfo(ctx, Require<AecWindowAssembly>(ctx, a, "WindowAssembly")));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetCurtainWall(JsonElement a) => Acad.Read<object?>(ctx =>
        CurtainWallInfo(ctx, Require<AecCurtainWall>(ctx, a, "CurtainWallLayout")));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetStair(JsonElement a) => Acad.Read<object?>(ctx =>
        StairInfo(ctx, Require<AecStair>(ctx, a, "Stair")));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetRailing(JsonElement a) => Acad.Read<object?>(ctx =>
        RailingInfo(ctx, Require<AecRailing>(ctx, a, "Railing")));

    // --------------------------------------------------------------------------------- create

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateCurtainWall(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var start = Args.Point(a, "startPoint");
        var end = Args.Point(a, "endPoint");
        var normal = NormalArg(a);

        if (start.DistanceTo(end) < MinSpan)
            throw new AcadMcpException("bad_argument",
                "'startPoint' and 'endPoint' are the same point; the curtain wall would have no length.");

        Point3d? arcPoint = Has(a, "arcPoint") ? Args.Point(a, "arcPoint") : null;
        if (arcPoint is { } mid
            && (mid.DistanceTo(start) < MinSpan
                || mid.DistanceTo(end) < MinSpan))
            throw new AcadMcpException("bad_argument",
                "'arcPoint' must be distinct from both 'startPoint' and 'endPoint'.");

        var dict = new DictCurtainWallStyle(ctx.Db);
        var styleId = ResolveStyle<AecCurtainWallStyle>(
            ctx, dict, Args.StrOpt(a, "style"),
            () => DictCurtainWallStyle.GetStandardStyle(ctx.Db), "curtain wall");

        var style = (AecCurtainWallStyle)ctx.Tr.GetObject(styleId, OpenMode.ForRead);
        var height = PositiveArg(a, "height", TryD(() => style.DefaultBaseHeight) ?? 0);

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var cw = new AecCurtainWall();
        cw.SetDatabaseDefaults(ctx.Db);
        // SetToStandard is Aec Entity's own "give me valid AEC defaults" call; StyleId is assigned
        // after it so an explicitly named style is not overwritten by the standard one. The style
        // is set BEFORE the append: an AEC object appended with no style is structurally invalid.
        cw.SetToStandard(ctx.Db);
        cw.StyleId = styleId;

        btr.AppendEntity(cw);
        ctx.Tr.AddNewlyCreatedDBObject(cw, true);

        if (arcPoint is { } through)
            cw.SetArcBaseCurve(new CircularArc3d(start, through, end), normal);
        else
            cw.SetLinearBaseCurve(start, end, normal);

        cw.BaseHeight = height;

        ApplyLayer(ctx, cw, a);
        ApplyDescription(cw, a);

        var gridWarning = UpdateGrid(cw);

        // Read the finished object back. Anything that fails here throws, and Acad.Write aborts the
        // transaction, so a malformed curtain wall never reaches the drawing.
        double length;
        try { length = cw.Length; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "The curtain wall was built but its geometry could not be read back; nothing was saved.",
                ex.Message);
        }

        if (double.IsNaN(length) || length <= 0)
            throw new AcadMcpException("create_failed",
                $"The curtain wall came out with length {length}; nothing was saved.");

        return new
        {
            handle = cw.Handle.ToString(),
            type = "CurtainWall",
            objectClass = ClassName(cw.ObjectId),
            layer = TryS(() => cw.Layer),
            space = btr.Name,
            styleName = TryS(() => style.Name),
            styleHandle = styleId.Handle.ToString(),
            length,
            baseHeight = TryD(() => cw.BaseHeight),
            baseCurveType = TryS(() => cw.BaseCurveType.ToString()),
            startPoint = new[] { start.X, start.Y, start.Z },
            endPoint = new[] { end.X, end.Y, end.Z },
            normal = new[] { normal.X, normal.Y, normal.Z },
            grid = GridState(cw),
            gridUpdated = gridWarning is null,
            gridUpdateError = gridWarning,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateWindowAssembly(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var location = Args.Point(a, "location");
        var normal = NormalArg(a);
        var rotation = RotationArg(a);

        var dict = new DictWindowAssemblyStyle(ctx.Db);
        var styleId = ResolveStyle<AecWindowAssemblyStyle>(
            ctx, dict, Args.StrOpt(a, "style"),
            () => DictWindowAssemblyStyle.GetStandardStyle(ctx.Db), "window assembly");

        var style = (AecWindowAssemblyStyle)ctx.Tr.GetObject(styleId, OpenMode.ForRead);

        // "width" is the ACA UI term; the object calls the same dimension Length.
        var widthName = Has(a, "width") ? "width" : "length";
        var width = PositiveArg(a, widthName, TryD(() => style.DefaultWidth) ?? 0);
        var height = PositiveArg(a, "height", TryD(() => style.DefaultHeight) ?? 0);

        // Rise only means anything for shapes that have one (arched, gable, gothic…). Take it from
        // the argument when supplied, otherwise from the style, and never force a negative value.
        var rise = Has(a, "rise") ? Args.Dbl(a, "rise") : TryD(() => style.DefaultRise) ?? 0;
        if (double.IsNaN(rise) || double.IsInfinity(rise) || rise < 0)
            throw new AcadMcpException("bad_argument", $"'rise' must not be negative; got {rise}.");

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var wa = new AecWindowAssembly();
        wa.SetDatabaseDefaults(ctx.Db);
        wa.SetToStandard(ctx.Db);
        wa.StyleId = styleId;

        btr.AppendEntity(wa);
        ctx.Tr.AddNewlyCreatedDBObject(wa, true);

        wa.Normal = normal;
        wa.Location = location;
        wa.Rotation = rotation;
        wa.Length = width;
        wa.Height = height;

        string? riseWarning = null;
        if (rise > 0)
        {
            try { wa.Rise = rise; }
            catch (System.Exception ex)
            {
                // A rectangular shape has no rise; refusing it is not a reason to lose the assembly.
                riseWarning = ex.Message;
            }
        }

        ApplyLayer(ctx, wa, a);
        ApplyDescription(wa, a);

        var gridWarning = UpdateGrid(wa);

        double builtLength, builtHeight;
        try
        {
            builtLength = wa.Length;
            builtHeight = wa.Height;
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "The window assembly was built but its size could not be read back; nothing was saved.",
                ex.Message);
        }

        if (double.IsNaN(builtLength) || builtLength <= 0 || double.IsNaN(builtHeight) || builtHeight <= 0)
            throw new AcadMcpException("create_failed",
                $"The window assembly came out {builtLength} x {builtHeight}; nothing was saved.");

        return new
        {
            handle = wa.Handle.ToString(),
            type = "WindowAssembly",
            objectClass = ClassName(wa.ObjectId),
            layer = TryS(() => wa.Layer),
            space = btr.Name,
            styleName = TryS(() => style.Name),
            styleHandle = styleId.Handle.ToString(),
            length = builtLength,
            height = builtHeight,
            rise = TryD(() => wa.Rise),
            riseWarning,
            location = new[] { location.X, location.Y, location.Z },
            rotation,
            rotationDegrees = rotation * 180.0 / Math.PI,
            normal = new[] { normal.X, normal.Y, normal.Z },
            isAnchoredToWall = TryB(() => wa.IsAnchoredToWall),
            grid = GridState(wa),
            gridUpdated = gridWarning is null,
            gridUpdateError = gridWarning,
            note = "Free-standing placement. Anchoring into a wall is not implemented.",
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateRailing(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var dict = new DictRailingStyle(ctx.Db);
        var styleId = ResolveStyle<AecRailingStyle>(
            ctx, dict, Args.StrOpt(a, "style"),
            () => DictRailingStyle.GetStandardStyle(ctx.Db), "railing");

        var style = (AecRailingStyle)ctx.Tr.GetObject(styleId, OpenMode.ForRead);

        // Build the path before anything is appended, so a bad path costs nothing.
        var (path, source) = RailingPath(ctx, a);

        double pathLength;
        try { pathLength = path.Length; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("bad_path", "The railing path has no measurable length.", ex.Message);
        }

        if (double.IsNaN(pathLength) || pathLength <= 0)
            throw new AcadMcpException("bad_path", $"The railing path has length {pathLength}.");

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var railing = new AecRailing();
        railing.SetDatabaseDefaults(ctx.Db);
        railing.SetToStandard(ctx.Db);
        railing.StyleId = styleId;

        btr.AppendEntity(railing);
        ctx.Tr.AddNewlyCreatedDBObject(railing, true);

        // Pull the style's rail/post/baluster values onto the instance before the path is applied,
        // so the post distribution is solved against the right spacing.
        try { railing.SetToStyleValues(); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "The railing style values could not be applied; nothing was saved.", ex.Message);
        }

        try { railing.SetPath(path); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "AutoCAD Architecture refused the railing path; nothing was saved.", ex.Message);
        }

        ApplyLayer(ctx, railing, a);
        ApplyDescription(railing, a);

        // Read the path back off the finished object: if it did not take, abort rather than leave
        // a railing with no geometry in the drawing.
        int builtSegments;
        double builtLength;
        try
        {
            var built = railing.GetPath();
            builtSegments = built.SegmentCount;
            builtLength = built.Length;
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                "The railing was built but its path could not be read back; nothing was saved.",
                ex.Message);
        }

        if (builtSegments < 1 || double.IsNaN(builtLength) || builtLength <= 0)
            throw new AcadMcpException("create_failed",
                $"The railing came out with {builtSegments} path segment(s) and length {builtLength}; "
                + "nothing was saved.");

        var minimumLength = TryD(() => railing.MinimumLength);

        return new
        {
            handle = railing.Handle.ToString(),
            type = "Railing",
            objectClass = ClassName(railing.ObjectId),
            layer = TryS(() => railing.Layer),
            space = btr.Name,
            styleName = TryS(() => style.Name),
            styleHandle = styleId.Handle.ToString(),
            path = source,
            pathLength = builtLength,
            pathSegmentCount = builtSegments,
            minimumLength,
            shorterThanStyleMinimum = minimumLength is { } min && builtLength < min,
            guardrailHeight = TryD(() => railing.GuardrailHeight),
            handrailHeight = TryD(() => railing.HandrailHeight),
            postCount = TryI(() => railing.PostCount),
            balusterCount = TryI(() => railing.BalusterCount),
            note = "Free-standing railing. Attaching it to a stair is not implemented.",
        };
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListWindowAssemblyStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListCurtainWallStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListCurtainWallUnitStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListStairStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListRailingStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListWindowAssemblies(JsonElement a) => throw Unavailable();
    private static Task<object?> ListCurtainWalls(JsonElement a) => throw Unavailable();
    private static Task<object?> ListCurtainWallUnits(JsonElement a) => throw Unavailable();
    private static Task<object?> ListStairs(JsonElement a) => throw Unavailable();
    private static Task<object?> ListRailings(JsonElement a) => throw Unavailable();
    private static Task<object?> GetWindowAssembly(JsonElement a) => throw Unavailable();
    private static Task<object?> GetCurtainWall(JsonElement a) => throw Unavailable();
    private static Task<object?> GetStair(JsonElement a) => throw Unavailable();
    private static Task<object?> GetRailing(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateCurtainWall(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateWindowAssembly(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateRailing(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
