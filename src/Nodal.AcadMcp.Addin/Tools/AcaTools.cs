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
// Two reasons: (1) 'Entity', 'DBObject', 'Dictionary' and 'Attribute' all collide between
// Autodesk.Aec.*, Autodesk.AutoCAD.DatabaseServices and System, so a blanket using would be a
// minefield; (2) on a vanilla AutoCAD box these assemblies do not exist, so nothing outside the
// guarded region may name them.
#if ACA
using AecEntity = Autodesk.Aec.DatabaseServices.Entity;
using AecGeo = Autodesk.Aec.DatabaseServices.Geo;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecAnchorRef = Autodesk.Aec.DatabaseServices.AnchorToReference;
using AecAnchorCurve = Autodesk.Aec.DatabaseServices.AnchorEntityToCurve;
using AecMass = Autodesk.Aec.DatabaseServices.MassElement;

using AecWall = Autodesk.Aec.Arch.DatabaseServices.Wall;
using AecWallSide = Autodesk.Aec.Arch.DatabaseServices.WallSide;
using AecDoor = Autodesk.Aec.Arch.DatabaseServices.Door;
using AecDoorStyle = Autodesk.Aec.Arch.DatabaseServices.DoorStyle;
using AecWindow = Autodesk.Aec.Arch.DatabaseServices.Window;
using AecWindowStyle = Autodesk.Aec.Arch.DatabaseServices.WindowStyle;
using AecOpening = Autodesk.Aec.Arch.DatabaseServices.Opening;
using AecOpeningBase = Autodesk.Aec.Arch.DatabaseServices.OpeningBase;
using AecOpenFiller = Autodesk.Aec.Arch.DatabaseServices.OpenFiller;
using AecSillHead = Autodesk.Aec.Arch.DatabaseServices.SillHeadHeightUtility;
using AecSpace = Autodesk.Aec.Arch.DatabaseServices.Space;
using AecSlab = Autodesk.Aec.Arch.DatabaseServices.Slab;
using AecSlabBase = Autodesk.Aec.Arch.DatabaseServices.SlabBase;
using AecRoofSlab = Autodesk.Aec.Arch.DatabaseServices.RoofSlab;
using AecRoof = Autodesk.Aec.Arch.DatabaseServices.Roof;
using AecStair = Autodesk.Aec.Arch.DatabaseServices.Stair;
using AecRailing = Autodesk.Aec.Arch.DatabaseServices.Railing;
using AecCurtainWall = Autodesk.Aec.Arch.DatabaseServices.CurtainWallLayout;
using AecCurtainWallUnit = Autodesk.Aec.Arch.DatabaseServices.CurtainWallUnit;
using AecWindowAssembly = Autodesk.Aec.Arch.DatabaseServices.WindowAssembly;

using AecDataType = Autodesk.Aec.PropertyData.DataType;
using AecPropServices = Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices;
using AecPropSet = Autodesk.Aec.PropertyData.DatabaseServices.PropertySet;
using AecPropSetData = Autodesk.Aec.PropertyData.DatabaseServices.PropertySetData;
using AecPropSetDef = Autodesk.Aec.PropertyData.DatabaseServices.PropertySetDefinition;
using AecPropDef = Autodesk.Aec.PropertyData.DatabaseServices.PropertyDefinition;
using AecPropSetDefDict = Autodesk.Aec.PropertyData.DatabaseServices.DictionaryPropertySetDefinitions;
using AecScheduleTable = Autodesk.Aec.PropertyData.DatabaseServices.ScheduleTable;

using AecMember = Autodesk.Aec.Structural.DatabaseServices.Member;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture: AEC objects, their style/geometry, and the property-set data model.
///
/// JIT SAFETY. Every handler registered here is wrapped by <see cref="Reg"/>, which calls
/// <see cref="AcaSupport.Require"/> before touching the implementation. The implementations that
/// name Aec types are separate, [MethodImpl(NoInlining)] methods, so the JIT never has to resolve
/// an Aec type reference on a box where ACA is absent — it only ever resolves their signatures,
/// which are (JsonElement) -> Task&lt;object?&gt;.
///
/// NOT IMPLEMENTED / DIVERGENCES FROM THE BRIEF (API could not be confirmed from the declared API surface):
///  * Wall.Area(WallSide.Center, false) does not exist. AecArchMgd's 'enum WallSide' has exactly
///    two members, Left and Right. This module reports gross and net area for both faces instead.
///  * PropertySetDefinition has no per-definition "category"/"group" surface; only Definitions
///    (PropertyDefinitionCollection), AppliesToAll, AppliesToFilter, ClassificationFilter,
///    IsStyleBased, IsVisible, IsWriteable were confirmed, and those are what is returned.
///  * Creating a PropertySetDefinition is not offered. PropertyDefinitionCollection.Add exists,
///    but the DataType/format/unit wiring a new definition needs could not be confirmed safely
///    enough to ship as a mutating tool.
///  * Structural members come from AecStructureMgd's Member, which is a LayoutTool, not a Geo
///    with start/end points. Its endpoints are read off Aec Entity.BaseCurve, guarded.
/// </summary>
internal static class AcaTools
{
    internal static void Register()
    {
        Reg("list_aec_objects",
            "Every AutoCAD Architecture object in the drawing, grouped by type, with handle, layer, "
            + "style and key geometry. Args: scope (model|current|all, default model), type (filter, "
            + "e.g. Wall), deepScan (bool, opens every entity instead of pre-filtering on class name), "
            + "limit (default 500), offset.",
            mutating: false, ListAecObjects);

        Reg("list_walls",
            "Every AEC Wall with length, width, base height, endpoints, radius, justification, "
            + "cleanup group, style and face areas. Args: scope, limit, offset, deepScan.",
            mutating: false, ListWalls);

        Reg("get_wall",
            "One AEC Wall in full, by handle: geometry, justification, cleanup, style and the handles "
            + "of its openings. Args: handle.",
            mutating: false, GetWall);

        Reg("list_doors",
            "Every AEC Door. Door derives from OpenFiller/OpeningBase, so width/height/rise come from "
            + "OpeningBase and frame/leaf from OpenFiller; style adds door type, shape and thickness. "
            + "Args: scope, limit, offset, deepScan.",
            mutating: false, ListDoors);

        Reg("list_windows",
            "Every AEC Window, same OpenFiller/OpeningBase geometry as doors plus corner-window state "
            + "and window style (type, shape, sash, glass). Args: scope, limit, offset, deepScan.",
            mutating: false, ListWindows);

        Reg("list_spaces",
            "Every AEC Space with name, area, volumes, ceiling height, floor thickness, length/width "
            + "and style. Args: scope, limit, offset, deepScan.",
            mutating: false, ListSpaces);

        Reg("get_property_sets",
            "Every property set attached to an entity: set name, definition name, and each property as "
            + "name / value / data type / unit. Args: handle.",
            mutating: false, GetPropertySets);

        Reg("set_property_value",
            "Write one property-set value on an entity, as a single undoable step. Args: handle, set "
            + "(property set definition name), property, value.",
            mutating: true, SetPropertyValue);

        Reg("add_property_set",
            "Attach a property set definition to an entity. Args: handle, definition.",
            mutating: true, AddPropertySet);

        Reg("remove_property_set",
            "Detach a property set definition from an entity. Args: handle, definition.",
            mutating: true, RemovePropertySet);

        Reg("list_property_set_definitions",
            "Every PropertySetDefinition in the drawing with its property definitions. "
            + "Args: includeProperties (default true), limit, offset.",
            mutating: false, ListPropertySetDefinitions);

        Reg("list_schedule_tables",
            "Every ScheduleTable entity with title, size, style, update state and selection count. "
            + "Args: scope, includeData (default false, returns cell text), limit, offset.",
            mutating: false, ListScheduleTables);
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

    private static JsonElement Raw(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            throw new AcadMcpException("bad_argument", $"Argument '{name}' is required.");
        return v;
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

#if ACA

    // ------------------------------------------------------------------------- guarded accessors
    // AEC property getters throw for degenerate geometry (Radius on a straight wall, Area on an
    // invalid slab, style lookups across a broken xref). One bad property must never lose a whole
    // listing, and the bridge drops nulls (DefaultIgnoreCondition = WhenWritingNull), so a failed
    // read simply vanishes from the JSON.

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
    /// Cheap pre-filter: every ACA runtime class is registered as AecDbWall, AecDbDoor, AecDbSpace…
    /// so the name test rejects plain AutoCAD geometry without opening the object. deepScan skips
    /// this and opens everything, which is the escape hatch if that convention ever fails.
    /// </summary>
    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    private static string TypeNameOf(DBObject obj, ObjectId id) => obj switch
    {
        AecWall => "Wall",
        AecDoor => "Door",
        AecWindow => "Window",
        AecWindowAssembly => "WindowAssembly",
        AecOpening => "Opening",
        AecOpeningBase => "OpeningBase",
        AecSpace => "Space",
        AecRoofSlab => "RoofSlab",
        AecSlab => "Slab",
        AecSlabBase => "SlabBase",
        AecRoof => "Roof",
        AecStair => "Stair",
        AecRailing => "Railing",
        AecCurtainWall => "CurtainWall",
        AecCurtainWallUnit => "CurtainWallUnit",
        AecMass => "MassElement",
        AecMember => "Member",
        AecScheduleTable => "ScheduleTable",
        _ => ClassName(id),
    };

    /// <summary>
    /// Pass one: classify cheaply, so 'total' is exact without building a payload per entity in a
    /// 200k-object drawing. Pass two (the page slice) does the expensive property reads.
    /// </summary>
    private static List<(string Type, string Handle, ObjectId Id)> ScanAec(AcadContext ctx, string scope, bool deep)
    {
        var found = new List<(string Type, string Handle, ObjectId Id)>();

        foreach (var spaceId in TargetSpaces(ctx, scope))
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead); }
            catch { continue; }

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;

                var named = LooksAec(id);
                if (!deep && !named) continue;

                DBObject obj;
                try { obj = ctx.Tr.GetObject(id, OpenMode.ForRead); }
                catch { continue; }

                if (obj is AecEntity)
                    found.Add((TypeNameOf(obj, id), id.Handle.ToString(), id));
                else if (named)
                    // AEC-named but no managed wrapper loaded (proxy / object enabler gap).
                    found.Add((ClassName(id), id.Handle.ToString(), id));
            }
        }

        return found;
    }

    private static List<(string Type, string Handle, ObjectId Id)> ScanOfType(
        AcadContext ctx, JsonElement a, string type) =>
        ScanAec(ctx, Scope(a), Args.BoolOpt(a, "deepScan", false))
            .Where(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase))
            .ToList();

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

    /// <summary>Handle of whatever a Geo is anchored to — for an opening, its host wall.</summary>
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

    /// <summary>StringCollection is a framework type the declared API surface only names; enumerate it defensively.</summary>
    private static List<string> Strings(object? collection)
    {
        var list = new List<string>();
        try
        {
            if (collection is IEnumerable seq)
                foreach (var item in seq)
                    if (item is not null) list.Add(item.ToString() ?? string.Empty);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Property-set values arrive as boxed variants. Reduce them to JSON primitives so an exotic
    /// value (a graphic, a COM wrapper) can never blow up or recurse through the serialiser.
    /// </summary>
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

    // ------------------------------------------------------------------------------ summary views

    private static object? Summary(AcadContext ctx, DBObject obj) => obj switch
    {
        AecWall w => (object)new
        {
            length = TryD(() => w.Length),
            width = TryD(() => w.Width),
            baseHeight = TryD(() => w.BaseHeight),
            startPoint = TryPt(() => w.StartPoint),
            endPoint = TryPt(() => w.EndPoint),
            radius = TryD(() => w.Radius),
            justification = TryS(() => w.JustificationType.ToString()),
        },
        AecOpeningBase o => (object)new
        {
            width = TryD(() => o.Width),
            height = TryD(() => o.Height),
            rise = TryD(() => o.Rise),
            sillHeight = TryD(() => AecSillHead.GetSillHeight(o)),
            headHeight = TryD(() => AecSillHead.GetHeadHeight(o)),
            isAnchoredToWall = TryB(() => o.IsAnchoredToWall),
            hostHandle = HostHandle(ctx, o),
        },
        AecSpace s => (object)new
        {
            name = TryS(() => s.Name),
            area = TryD(() => s.Area),
            perimeter = TryD(() => s.Perimeter),
            ceilingHeight = TryD(() => s.CeilingHeight),
            baseVolume = TryD(() => s.BaseVolume),
        },
        AecSlabBase sb => (object)new
        {
            baseThickness = TryD(() => sb.BaseThickness),
            grossArea = TryD(() => sb.GrossArea),
            netArea = TryD(() => sb.NetArea),
            slope = TryD(() => sb.Slope),
            centroid = TryPt(() => sb.Centroid),
        },
        AecRoof r => (object)new
        {
            thickness = TryD(() => r.Thickness),
            plateHeight = TryD(() => r.PlateHeight),
            slope = TryD(() => r.Slope),
            rise = TryD(() => r.Rise),
            run = TryD(() => r.Run),
            overhang = TryD(() => r.Overhang),
        },
        AecStair st => (object)new
        {
            width = TryD(() => st.Width),
            treadCount = TryI(() => st.TreadCount),
            isRamp = TryB(() => st.IsRamp),
            justification = TryS(() => st.Justification.ToString()),
            riseAndTread = TryD(() => st.CalculatedRiseAndTread),
        },
        AecRailing rl => (object)new
        {
            guardrailHeight = TryD(() => rl.GuardrailHeight),
            handrailHeight = TryD(() => rl.HandrailHeight),
            balusterCount = TryI(() => rl.BalusterCount),
            mainPostCount = TryI(() => rl.MainPostCount),
        },
        AecCurtainWall cw => (object)new
        {
            length = TryD(() => cw.Length),
            baseHeight = TryD(() => cw.BaseHeight),
            baseCurveType = TryS(() => cw.BaseCurveType.ToString()),
        },
        AecCurtainWallUnit cu => (object)new
        {
            length = TryD(() => cu.Length),
            height = TryD(() => cu.Height),
        },
        AecWindowAssembly wa => (object)new
        {
            length = TryD(() => wa.Length),
            height = TryD(() => wa.Height),
            rise = TryD(() => wa.Rise),
            isAnchoredToWall = TryB(() => wa.IsAnchoredToWall),
            hostHandle = HostHandle(ctx, wa),
        },
        AecMass mass => (object)new
        {
            shapeType = TryS(() => mass.ShapeType.ToString()),
            width = TryD(() => mass.Width),
            depth = TryD(() => mass.Depth),
            height = TryD(() => mass.Height),
            volume = TryD(() => mass.Volume),
        },
        AecMember mem => (object)new
        {
            memberType = TryS(() => mem.MemberType.ToString()),
            length = TryD(() => mem.Length),
            midPoint = TryPt(() => mem.MidPoint),
            // Member is a LayoutTool, not a Geo with endpoints; they come off the base curve.
            // Cast through object: AecEntity.BaseCurve is declared as an unqualified 'Curve' in the
            // declared API surface, and Autodesk.Aec.Modeler also defines a Curve. Going via object
            // compiles either way and degrades to null at runtime if the cast is wrong.
            startPoint = TryPt(() => ((Curve)(object)mem.BaseCurve).StartPoint),
            endPoint = TryPt(() => ((Curve)(object)mem.BaseCurve).EndPoint),
            justification = TryS(() => mem.Justification.ToString()),
        },
        AecScheduleTable sched => (object)new
        {
            title = TryS(() => sched.Title),
            rowCount = TryI(() => sched.RowCount),
            columnCount = TryI(() => sched.ColumnCount),
            isOutOfDate = TryB(() => sched.IsOutOfDate),
        },
        _ => null,
    };

    // -------------------------------------------------------------------------- detail views

    private static object WallInfo(AcadContext ctx, AecWall w) => new
    {
        identity = Identity(ctx, w, "Wall"),
        length = TryD(() => w.Length),
        width = TryD(() => w.Width),
        instanceWidth = TryD(() => w.InstanceWidth),
        baseHeight = TryD(() => w.BaseHeight),
        startPoint = TryPt(() => w.StartPoint),
        midPoint = TryPt(() => w.MidPoint),
        endPoint = TryPt(() => w.EndPoint),
        location = TryPt(() => w.Location),
        radius = TryD(() => w.Radius),
        geometryType = TryS(() => w.WallGeometryType.ToString()),
        justification = TryS(() => w.JustificationType.ToString()),
        rotation = TryD(() => w.Rotation),
        cleanupGroup = TryS(() => RecordName(ctx, w.CleanupGroupDefinitionId)),
        cleanupRadius = TryD(() => w.CleanupRadius),
        startCleanupRadius = TryD(() => w.StartCleanupRadius),
        endCleanupRadius = TryD(() => w.EndCleanupRadius),
        dontCleanup = TryB(() => w.DontCleanup),
        useJustificationLineForCleanups = TryB(() => w.UseJustificationLineForCleanups),
        startingEndcapStyle = TryS(() => w.StartingEndcapStyleName),
        endingEndcapStyle = TryS(() => w.EndingEndcapStyleName),
        // WallSide has only Left and Right — there is no WallSide.Center in AecArchMgd.
        areaLeftGross = TryD(() => w.Area(AecWallSide.Left, false)),
        areaLeftNet = TryD(() => w.Area(AecWallSide.Left, true)),
        areaRightGross = TryD(() => w.Area(AecWallSide.Right, false)),
        areaRightNet = TryD(() => w.Area(AecWallSide.Right, true)),
        modifierCount = TryI(() => w.WallModifierCount),
        interferenceCount = TryI(() => w.WallInterferencesCount),
        openingHandles = TryO(() => Handles(w.GetOpeningsFor())),
    };

    private static object OpeningInfo(AcadContext ctx, AecOpeningBase o, string type)
    {
        var filler = o as AecOpenFiller;
        return new
        {
            identity = Identity(ctx, o, type),
            width = TryD(() => o.Width),
            height = TryD(() => o.Height),
            rise = TryD(() => o.Rise),
            innerWidth = TryD(() => o.InnerWidth),
            innerHeight = TryD(() => o.InnerHeight),
            roughWidth = TryD(() => o.RoughWidth),
            roughHeight = TryD(() => o.RoughHeight),
            sillHeight = TryD(() => AecSillHead.GetSillHeight(o)),
            headHeight = TryD(() => AecSillHead.GetHeadHeight(o)),
            verticalAlignment = TryS(() => AecSillHead.GetVerticalAlignment(o).ToString()),
            isAnchoredToWall = TryB(() => o.IsAnchoredToWall),
            hostHandle = HostHandle(ctx, o),
            endcapStyle = TryS(() => o.EndcapStyleName),
            location = TryPt(() => o.Location),
            rotation = TryD(() => o.Rotation),
            frameWidth = filler is null ? null : TryD(() => filler.FrameWidth),
            frameDepth = filler is null ? null : TryD(() => filler.FrameDepth),
            leafWidth = filler is null ? null : TryD(() => filler.LeafWidth),
            openPercent = filler is null ? null : TryD(() => filler.OpenPercent),
        };
    }

    private static object DoorStyleInfo(AecDoorStyle style) => new
    {
        name = TryS(() => style.Name),
        description = TryS(() => style.Description),
        doorType = TryS(() => style.DoorType.ToString()),
        shape = TryS(() => style.DoorShape.ToString()),
        thickness = TryD(() => style.DoorThickness),
        frameWidth = TryD(() => style.FrameWidth),
        frameDepth = TryD(() => style.FrameDepth),
        stopWidth = TryD(() => style.StopWidth),
        stopDepth = TryD(() => style.StopDepth),
        glassThickness = TryD(() => style.GlassThickness),
        numberOfLeaves = TryI(() => style.NumberOfLeaves),
        useDepthOfWall = TryB(() => style.UseDepthOfWall),
    };

    private static object DoorInfo(AcadContext ctx, AecDoor d)
    {
        var style = StyleRecord(ctx, d) as AecDoorStyle;
        return new
        {
            opening = OpeningInfo(ctx, d, "Door"),
            isCustomShape = TryB(() => d.IsCustomShape),
            style = style is null ? null : DoorStyleInfo(style),
        };
    }

    private static object WindowStyleInfo(AecWindowStyle style) => new
    {
        name = TryS(() => style.Name),
        description = TryS(() => style.Description),
        windowType = TryS(() => style.WindowType.ToString()),
        shape = TryS(() => style.WindowShape.ToString()),
        frameWidth = TryD(() => style.FrameWidth),
        frameDepth = TryD(() => style.FrameDepth),
        sashWidth = TryD(() => style.SashWidth),
        sashDepth = TryD(() => style.SashDepth),
        glassThickness = TryD(() => style.GlassThickness),
        useDepthOfWall = TryB(() => style.UseDepthOfWall),
    };

    private static object WindowInfo(AcadContext ctx, AecWindow w)
    {
        var style = StyleRecord(ctx, w) as AecWindowStyle;
        return new
        {
            opening = OpeningInfo(ctx, w, "Window"),
            isCustomShape = TryB(() => w.IsCustomShape),
            isCornerWindow = TryB(() => w.CornerWindowFlag),
            cornerWindowAngle = TryD(() => w.CornerWindowAngle),
            style = style is null ? null : WindowStyleInfo(style),
        };
    }

    private static object SpaceInfo(AcadContext ctx, AecSpace s) => new
    {
        identity = Identity(ctx, s, "Space"),
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
        length = TryD(() => s.Length),
        width = TryD(() => s.Width),
        justification = TryS(() => s.Justification.ToString()),
        geometryType = TryS(() => s.GeometryType.ToString()),
        associative = TryB(() => s.Associative),
        location = TryPt(() => s.Location),
        centroid = TryPt(() => s.CentroidWCS(false)),
    };

    // ------------------------------------------------------------------------------ property data

    private static object PropertyDefInfo(AecPropDef pd) => new
    {
        id = TryI(() => pd.Id),
        name = TryS(() => pd.Name),
        globalName = TryS(() => pd.GlobalName),
        description = TryS(() => pd.Description),
        dataType = TryS(() => pd.DataType.ToString()),
        unitType = TryS(() => pd.UnitType.ToString()),
        defaultValue = Scalar(TryO(() => pd.DefaultData)),
        formatString = TryS(() => pd.FormatString),
        automatic = TryB(() => pd.Automatic),
        isReadOnly = TryB(() => pd.IsReadOnly),
        isVisible = TryB(() => pd.IsVisible),
        displayOrder = TryI(() => pd.DisplayOrder),
        kind = pd.GetType().Name,
    };

    /// <summary>
    /// One attached property set. Properties come from the definition (which carries the names);
    /// if the definition cannot be opened we fall back to the set's own raw PropertySetData, which
    /// has ids and values but no names.
    /// </summary>
    private static object PropertySetInfo(AcadContext ctx, AecPropSet ps)
    {
        var properties = new List<object>();
        AecPropSetDef? def = null;

        try { def = ctx.Tr.GetObject(ps.PropertySetDefinition, OpenMode.ForRead) as AecPropSetDef; }
        catch { }

        if (def is not null)
        {
            foreach (AecPropDef pd in def.Definitions)
            {
                var id = TryI(() => pd.Id);
                properties.Add(new
                {
                    id,
                    name = TryS(() => pd.Name),
                    value = Scalar(TryO(() => id is null ? null : ps.GetAt(id.Value))),
                    type = TryS(() => pd.DataType.ToString()),
                    unitType = TryS(() => pd.UnitType.ToString()),
                    isReadOnly = TryB(() => pd.IsReadOnly),
                    isVisible = TryB(() => pd.IsVisible),
                    automatic = TryB(() => pd.Automatic),
                    description = TryS(() => pd.Description),
                });
            }
        }
        else
        {
            try
            {
                foreach (AecPropSetData psd in ps.PropertySetData)
                {
                    var id = TryI(() => psd.Id);
                    properties.Add(new
                    {
                        id,
                        name = TryS(() => id is null ? null : ps.PropertyIdToName(id.Value)),
                        value = Scalar(TryO(() => psd.GetData())),
                        type = TryS(() => psd.DataType.ToString()),
                        unitType = TryS(() => psd.UnitType.ToString()),
                    });
                }
            }
            catch { }
        }

        return new
        {
            handle = TryS(() => ps.Handle.ToString()),
            name = TryS(() => ps.Name),
            definitionName = TryS(() => ps.PropertySetDefinitionName),
            definitionHandle = TryS(() => ps.PropertySetDefinition.IsNull
                ? null
                : ps.PropertySetDefinition.Handle.ToString()),
            attachedToHandle = TryS(() => ps.ObjectAttachedTo.IsNull
                ? null
                : ps.ObjectAttachedTo.Handle.ToString()),
            propertyCount = properties.Count,
            properties,
        };
    }

    private static DBObject OpenEntity(AcadContext ctx, JsonElement a, OpenMode mode)
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);
        return ctx.Tr.GetObject(id, mode);
    }

    private static ObjectId DefinitionId(AcadContext ctx, string name)
    {
        var dict = new AecPropSetDefDict(ctx.Db);

        bool has;
        try { has = dict.Has(name, ctx.Tr); }
        catch { has = false; }

        if (!has)
            throw new AcadMcpException("definition_not_found",
                $"No property set definition named '{name}' in this drawing.",
                "Call list_property_set_definitions for the available names.");

        var id = dict.GetAt(name);
        if (id.IsNull)
            throw new AcadMcpException("definition_not_found", $"Property set definition '{name}' resolved to null.");

        return id;
    }

    /// <summary>Locate an attached property set by its definition name (or the set's own name).</summary>
    private static AecPropSet FindPropertySet(AcadContext ctx, DBObject obj, string setName, OpenMode mode)
    {
        ObjectIdCollection ids;
        try { ids = AecPropServices.GetPropertySets(obj); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("property_sets_unavailable",
                "Could not read the property sets on this object.", ex.Message);
        }

        var seen = new List<string>();

        foreach (ObjectId id in ids)
        {
            if (id.IsNull) continue;

            AecPropSet? ps;
            try { ps = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecPropSet; }
            catch { continue; }
            if (ps is null) continue;

            var defName = TryS(() => ps.PropertySetDefinitionName);
            var ownName = TryS(() => ps.Name);
            if (defName is not null) seen.Add(defName);

            if (string.Equals(defName, setName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ownName, setName, StringComparison.OrdinalIgnoreCase))
            {
                return mode == OpenMode.ForWrite
                    ? (AecPropSet)ctx.Tr.GetObject(id, OpenMode.ForWrite)
                    : ps;
            }
        }

        throw new AcadMcpException("property_set_not_attached",
            $"No property set '{setName}' is attached to this object.",
            seen.Count == 0 ? "No property sets are attached." : "Attached: " + string.Join(", ", seen));
    }

    // ----------------------------------------------------------------------------- implementations

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListAecObjects(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var filter = Args.StrOpt(a, "type");

        var found = ScanAec(ctx, Scope(a), Args.BoolOpt(a, "deepScan", false));

        var counts = new List<(string Key, object Data)>();
        foreach (var group in found.GroupBy(f => f.Type, StringComparer.OrdinalIgnoreCase))
            counts.Add((group.Key, new { type = group.Key, count = group.Count() }));

        if (!string.IsNullOrWhiteSpace(filter))
            found = found.Where(f => string.Equals(f.Type, filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // Sort and page on the cheap classification, then build payloads for the page only.
        var ordered = found.OrderBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(f => f.Handle, StringComparer.Ordinal)
                           .ToList();
        var slice = ordered.Skip(offset).Take(limit).ToList();

        var items = new List<object>();
        foreach (var entry in slice)
        {
            DBObject obj;
            try { obj = ctx.Tr.GetObject(entry.Id, OpenMode.ForRead); }
            catch { continue; }

            items.Add(new
            {
                identity = Identity(ctx, obj, entry.Type),
                geometry = Summary(ctx, obj),
            });
        }

        return new
        {
            items,
            total = ordered.Count,
            offset,
            limit,
            truncated = offset + slice.Count < ordered.Count,
            byType = counts.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => c.Data).ToList(),
            scope = Scope(a),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListWalls(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in ScanOfType(ctx, a, "Wall"))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecWall w)
                    rows.Add((entry.Handle, WallInfo(ctx, w)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetWall(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var obj = OpenEntity(ctx, a, OpenMode.ForRead);
        if (obj is not AecWall w)
            throw new AcadMcpException("wrong_type",
                $"Handle '{Args.Str(a, "handle")}' is a {ClassName(obj.ObjectId)}, not an AEC Wall.");

        return WallInfo(ctx, w);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListDoors(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in ScanOfType(ctx, a, "Door"))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecDoor d)
                    rows.Add((entry.Handle, DoorInfo(ctx, d)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListWindows(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in ScanOfType(ctx, a, "Window"))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecWindow w)
                    rows.Add((entry.Handle, WindowInfo(ctx, w)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListSpaces(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in ScanOfType(ctx, a, "Space"))
        {
            try
            {
                if (ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) is AecSpace s)
                    rows.Add((entry.Handle, SpaceInfo(ctx, s)));
            }
            catch { }
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> GetPropertySets(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var obj = OpenEntity(ctx, a, OpenMode.ForRead);

        ObjectIdCollection ids;
        try { ids = AecPropServices.GetPropertySets(obj); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("property_sets_unavailable",
                "Could not read the property sets on this object.", ex.Message);
        }

        var sets = new List<(string Key, object Data)>();
        foreach (ObjectId id in ids)
        {
            if (id.IsNull) continue;
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is AecPropSet ps)
                    sets.Add((TryS(() => ps.PropertySetDefinitionName) ?? id.Handle.ToString(),
                              PropertySetInfo(ctx, ps)));
            }
            catch { }
        }

        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            objectClass = ClassName(obj.ObjectId),
            type = TypeNameOf(obj, obj.ObjectId),
            setCount = sets.Count,
            sets = sets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => s.Data).ToList(),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetPropertyValue(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var setName = Args.Str(a, "set");
        var propName = Args.Str(a, "property");
        var raw = Raw(a, "value");

        var obj = OpenEntity(ctx, a, OpenMode.ForRead);
        var ps = FindPropertySet(ctx, obj, setName, OpenMode.ForWrite);

        int pid;
        try { pid = ps.PropertyNameToId(propName); }
        catch { pid = -1; }

        if (pid < 0)
            throw new AcadMcpException("property_not_found",
                $"Property set '{setName}' has no property named '{propName}'.",
                "Call get_property_sets for the available property names.");

        // The definition tells us which SetAt overload the value must land in.
        AecDataType? dataType = null;
        try
        {
            if (ctx.Tr.GetObject(ps.PropertySetDefinition, OpenMode.ForRead) is AecPropSetDef def)
                foreach (AecPropDef pd in def.Definitions)
                    if (pd.Id == pid) { dataType = pd.DataType; break; }
        }
        catch { }

        var oldValue = Scalar(TryO(() => ps.GetAt(pid)));
        var coerced = Coerce(raw, dataType);

        switch (coerced)
        {
            case int i: ps.SetAt(pid, i); break;
            case double d: ps.SetAt(pid, d); break;
            case bool b: ps.SetAt(pid, b); break;
            case string s: ps.SetAt(pid, s); break;
            default: ps.SetAt(pid, coerced); break;
        }

        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            set = TryS(() => ps.PropertySetDefinitionName) ?? setName,
            property = propName,
            propertyId = pid,
            dataType = dataType?.ToString(),
            oldValue,
            newValue = Scalar(TryO(() => ps.GetAt(pid))),
        };
    });

    /// <summary>
    /// JSON value -> the CLR type PropertySet.SetAt expects for that DataType. Written as an
    /// if/else chain rather than a switch so the nullable enum needs no special-casing.
    /// </summary>
    private static object Coerce(JsonElement v, AecDataType? dataType)
    {
        if (dataType is AecDataType.Integer or AecDataType.AutoIncrement)
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.Number) return (int)v.GetDouble();
            if (v.ValueKind == JsonValueKind.True) return 1;
            if (v.ValueKind == JsonValueKind.False) return 0;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var pi)) return pi;
            throw new AcadMcpException("bad_argument", "'value' must be an integer for this property.");
        }

        if (dataType is AecDataType.Real)
        {
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var pd)) return pd;
            throw new AcadMcpException("bad_argument", "'value' must be a number for this property.");
        }

        if (dataType is AecDataType.TrueFalse)
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble() != 0.0;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var pb)) return pb;
            throw new AcadMcpException("bad_argument", "'value' must be true or false for this property.");
        }

        // Text, List, AlphaIncrement, Graphic, or an unreadable definition: infer from the JSON.
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) ? (object)n : v.GetDouble(),
            JsonValueKind.Null => string.Empty,
            _ => v.ToString(),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> AddPropertySet(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var defName = Args.Str(a, "definition");
        var defId = DefinitionId(ctx, defName);
        var obj = OpenEntity(ctx, a, OpenMode.ForWrite);

        var already = false;
        try
        {
            foreach (ObjectId used in AecPropServices.GetPropertySetDefinitionsUsed(obj))
                if (used == defId) { already = true; break; }
        }
        catch { }

        if (!already)
        {
            try { AecPropServices.AddPropertySet(obj, defId); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("add_failed",
                    $"Could not attach '{defName}' to this object.",
                    ex.Message + " — the definition's AppliesTo filter may exclude this object type.");
            }
        }

        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            definition = defName,
            definitionHandle = TryS(() => defId.Handle.ToString()),
            alreadyAttached = already,
            attached = true,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> RemovePropertySet(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var defName = Args.Str(a, "definition");
        var defId = DefinitionId(ctx, defName);
        var obj = OpenEntity(ctx, a, OpenMode.ForWrite);

        var wasAttached = false;
        try
        {
            foreach (ObjectId used in AecPropServices.GetPropertySetDefinitionsUsed(obj))
                if (used == defId) { wasAttached = true; break; }
        }
        catch { }

        if (wasAttached)
        {
            try { AecPropServices.RemovePropertySet(obj, defId); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("remove_failed",
                    $"Could not detach '{defName}' from this object.", ex.Message);
            }
        }

        return new
        {
            handle = TryS(() => obj.Handle.ToString()),
            definition = defName,
            wasAttached,
            removed = wasAttached,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListPropertySetDefinitions(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeProperties = Args.BoolOpt(a, "includeProperties", true);

        var dict = new AecPropSetDefDict(ctx.Db);
        var rows = new List<(string Key, object Data)>();

        ObjectIdCollection records;
        try { records = dict.Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_property_dictionary",
                "This drawing has no property set definition dictionary.", ex.Message);
        }

        foreach (ObjectId id in records)
        {
            if (id.IsNull || id.IsErased) continue;

            AecPropSetDef? def;
            try { def = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecPropSetDef; }
            catch { continue; }
            if (def is null) continue;

            var properties = new List<object>();
            if (includeProperties)
            {
                try
                {
                    foreach (AecPropDef pd in def.Definitions)
                        properties.Add(PropertyDefInfo(pd));
                }
                catch { }
            }

            var name = TryS(() => def.Name) ?? id.Handle.ToString();
            rows.Add((name, new
            {
                handle = id.Handle.ToString(),
                name,
                description = TryS(() => def.Description),
                appliesToAll = TryB(() => def.AppliesToAll),
                appliesTo = Strings(TryO(() => def.AppliesToFilter)),
                isStyleBased = TryB(() => def.IsStyleBased),
                isVisible = TryB(() => def.IsVisible),
                isWriteable = TryB(() => def.IsWriteable),
                propertyCount = TryI(() => def.Definitions.Count),
                properties = includeProperties ? properties : null,
            }));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListScheduleTables(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeData = Args.BoolOpt(a, "includeData", false);
        var rows = new List<(string Key, object Data)>();

        foreach (var entry in ScanOfType(ctx, a, "ScheduleTable"))
        {
            AecScheduleTable? table;
            try { table = ctx.Tr.GetObject(entry.Id, OpenMode.ForRead) as AecScheduleTable; }
            catch { continue; }
            if (table is null) continue;

            var style = StyleRecord(ctx, table);

            object? cells = null;
            if (includeData)
            {
                var rowCount = TryI(() => table.RowCount) ?? 0;
                var colCount = TryI(() => table.ColumnCount) ?? 0;

                // Hard cap so a 5,000-row schedule cannot become the whole response.
                if (rowCount > 0 && colCount > 0 && (long)rowCount * colCount <= 5000)
                {
                    var grid = new List<object>();
                    for (var r = 0; r < rowCount; r++)
                    {
                        var line = new List<string?>();
                        for (var c = 0; c < colCount; c++)
                        {
                            var rr = r;
                            var cc = c;
                            line.Add(TryS(() => table.CellText(rr, cc)));
                        }
                        grid.Add(line);
                    }
                    cells = grid;
                }
            }

            var title = TryS(() => table.Title) ?? entry.Handle;
            rows.Add((title + "|" + entry.Handle, new
            {
                identity = Identity(ctx, table, "ScheduleTable"),
                styleName = TryS(() => style?.Name),
                title,
                rowCount = TryI(() => table.RowCount),
                columnCount = TryI(() => table.ColumnCount),
                tableWidth = TryD(() => table.TableWidth),
                tableHeight = TryD(() => table.TableHeight),
                scale = TryD(() => table.Scale),
                isOutOfDate = TryB(() => table.IsOutOfDate),
                automaticUpdate = TryB(() => table.AutomaticUpdate),
                addNewEntriesAutomatically = TryB(() => table.AddNewEntriesAutomatically),
                scanBlockReferences = TryB(() => table.ScanBlockReferences),
                scanExternalReferences = TryB(() => table.ScanExternalReferences),
                isExternalSource = TryB(() => table.ScheduleDrawing),
                externalDrawing = TryS(() => table.GetScheduleDrawingName()),
                layerWildcard = TryS(() => table.LayerWildcard),
                upperLeftPoint = TryPt(() => table.UpperLeftPoint),
                lowerRightPoint = TryPt(() => table.LowerRightPoint),
                selectedHandles = TryO(() => Handles(table.SelectionSet)),
                cells,
            }));
        }

        return Page(rows, offset, limit);
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListAecObjects(JsonElement a) => throw Unavailable();
    private static Task<object?> ListWalls(JsonElement a) => throw Unavailable();
    private static Task<object?> GetWall(JsonElement a) => throw Unavailable();
    private static Task<object?> ListDoors(JsonElement a) => throw Unavailable();
    private static Task<object?> ListWindows(JsonElement a) => throw Unavailable();
    private static Task<object?> ListSpaces(JsonElement a) => throw Unavailable();
    private static Task<object?> GetPropertySets(JsonElement a) => throw Unavailable();
    private static Task<object?> SetPropertyValue(JsonElement a) => throw Unavailable();
    private static Task<object?> AddPropertySet(JsonElement a) => throw Unavailable();
    private static Task<object?> RemovePropertySet(JsonElement a) => throw Unavailable();
    private static Task<object?> ListPropertySetDefinitions(JsonElement a) => throw Unavailable();
    private static Task<object?> ListScheduleTables(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
