using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// AutoCAD Architecture types are named ONLY through explicit aliases inside #if ACA — same reason
// as AcaTools: 'Entity' and 'Dictionary' collide between Autodesk.Aec.*, Autodesk.AutoCAD.* and
// System, and on a vanilla AutoCAD box these assemblies do not exist at all.
#if ACA
using AecEntity = Autodesk.Aec.DatabaseServices.Entity;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecMass = Autodesk.Aec.DatabaseServices.MassElement;
using AecMassGroup = Autodesk.Aec.DatabaseServices.MassGroup;
using AecMassStyle = Autodesk.Aec.DatabaseServices.MassElementStyle;
using AecMassStyleDict = Autodesk.Aec.DatabaseServices.DictionaryMassElementStyle;
using AecProfileDict = Autodesk.Aec.DatabaseServices.DictionaryProfileDefinition;
using AecShape = Autodesk.Aec.DatabaseServices.ShapeType;
using AecOperation = Autodesk.Aec.DatabaseServices.Operation;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture conceptual massing: MassElement primitives and the MassGroup boolean tree.
///
/// JIT SAFETY — identical contract to <see cref="AcaTools"/>. Every handler is registered through
/// <see cref="Reg"/>, which calls <see cref="AcaSupport.Require"/> before entering an
/// implementation. The implementations that name Aec types are separate [MethodImpl(NoInlining)]
/// methods whose signature is only (JsonElement) -> Task&lt;object?&gt;, so a vanilla AutoCAD box
/// never has to resolve an Aec type reference.
///
/// CREATION SEQUENCE (verified against AecBaseMgd v8.9.51.0)
///   MassElement.Create(ShapeType)                 static factory — builds the shape body
///   mass.SetDatabaseDefaults(db)                  layer / colour / linetype from the drawing
///   mass.SetToStandard(db)                        AEC style — an AEC entity appended WITHOUT a
///                                                 style is an invalid object, so this (or an
///                                                 explicit StyleId) must succeed before append
///   mass.StyleId = <named or standard style>      belt and braces; DictionaryMassElementStyle
///                                                 .GetStandardStyle(db) is the documented source
///   btr.AppendEntity + tr.AddNewlyCreatedDBObject
///   dimensions / Location / Rotation LAST         set on the database-resident object, the same
///                                                 path set_mass_element_properties uses
/// StyleId is then re-read and must be non-null: if neither SetToStandard nor the style dictionary
/// actually left a style on the object, it is disposed and NOT appended — a style-less AEC object
/// can corrupt the drawing.
///
/// A mass group's boolean tree is checked for cycles before AddNode: a group that contains itself,
/// directly or through a nested group, would recurse during body evaluation.
///
/// NOT IMPLEMENTED / DIVERGENCES FROM THE BRIEF:
///  * ShapeType.BoundaryRepresentation is refused by create_mass_element. It is built from an
///    Autodesk.Aec.Modeler.Body through MassElement.SetBody(Body, bool). The separate typed
///    boolean_mass_elements workflow produces BRep results from existing mass bodies.
///  * MassElement.ParentMassing(ObjectId&amp;, Operation&amp;) and MassGroup.Contains(ObjectId,
///    Operation&amp;, bool) are deliberately unused: the declared ByRef signature does not distinguish 'out'
///    from 'ref' on a ByRef parameter, so binding to them is a coin flip. Group membership is
///    derived from MassGroup.Entities + MassGroup.GetOperation(ObjectId) instead, both of which
///    have unambiguous signatures.
///  * MassElement.ShapeType is GET-ONLY. Changing a shape goes through ChangeTo(ShapeType), which
///    is what set_mass_element_properties calls.
///  * MassGroup has no style dictionary in AecBaseMgd (there is no DictionaryMassGroupStyle), so
///    its SetToStandard call is best-effort and a failure is not fatal.
/// </summary>
internal static class AcaMassTools
{
    internal static void Register()
    {
        Reg("list_mass_shapes",
            "Discovery for the massing tools: every ShapeType a mass element can take, which of them "
            + "create_mass_element can build, and the mass element styles that exist in this drawing. "
            + "No arguments.",
            mutating: false, ListMassShapes);

        Reg("create_mass_element",
            "Create an ACA mass element (conceptual massing primitive) as one undoable step. Args: "
            + "shape (default Box — call list_mass_shapes), position [x,y,z] (alias 'location'), "
            + "width, depth, height, radius, rise, deviation, rotation (degrees) or rotationRadians, "
            + "normal [x,y,z], style (mass element style name, default Standard), profile (profile "
            + "definition name — required for Extrusion/Revolution and rejected for every other "
            + "shape), layer, description, subType, "
            + "useLayerKey (bool, apply the ACA layer key instead of the current layer), "
            + "space (model|paper|current, default model).",
            mutating: true, CreateMassElement);

        Reg("create_mass_group",
            "Create an ACA mass group — the boolean tree that combines mass elements into one solid — "
            + "as one undoable step. Args: elements (array of mass element handles to add), operation "
            + "(additive|subtractive|intersect|combine, default additive, applied to every element), "
            + "position [x,y,z], supportsAnchoring (bool), layer, description, space.",
            mutating: true, CreateMassGroup);

        Reg("add_to_mass_group",
            "Attach mass elements to a mass group, or change the boolean operation of ones already in "
            + "it. Args: handle (mass group), elements (array of handles), operation "
            + "(additive|subtractive|intersect|combine, default additive). Reports per element.",
            mutating: true, AddToMassGroup);

        Reg("remove_from_mass_group",
            "Detach mass elements from a mass group. Args: handle (mass group), elements (array of "
            + "handles), all (bool — remove every node instead). Reports per element.",
            mutating: true, RemoveFromMassGroup);

        Reg("list_mass_elements",
            "Every ACA mass element: handle, shape, style, width/depth/height/radius/rise, location, "
            + "rotation, volume, and the mass group it belongs to. Args: scope (model|current|all, "
            + "default model), shape (filter), deepScan (bool), membership (bool, default true), "
            + "limit (default 500), offset.",
            mutating: false, ListMassElements);

        Reg("list_mass_groups",
            "Every ACA mass group: handle, location, volume, node count and each member element with "
            + "its boolean operation. Args: scope, deepScan, limit (default 500), offset.",
            mutating: false, ListMassGroups);

        Reg("set_mass_element_properties",
            "Change a mass element in place, as one undoable step. Args: handle, plus any of shape "
            + "(re-shapes via ChangeTo), profile (alone, swaps the profile of an Extrusion or "
            + "Revolution), width, depth, height, radius, rise, deviation, "
            + "rotation (degrees) or rotationRadians, position [x,y,z], normal, style, layer, "
            + "description, subType. Returns what changed and what ACA refused.",
            mutating: true, SetMassElementProperties);
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

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    private static double? DblOrNull(JsonElement e, string name) =>
        Has(e, name) ? Args.Dbl(e, name) : null;

    private static Point3d? PointOrNull(JsonElement e, string name) =>
        Has(e, name) ? Args.Point(e, name) : null;

    private static Vector3d? VectorOrNull(JsonElement e, string name)
    {
        if (!Has(e, name)) return null;
        var p = Args.Point(e, name);
        var v = new Vector3d(p.X, p.Y, p.Z);

        // A zero-length normal would give the object a degenerate ECS, which is a corrupt AEC
        // object rather than a rejected argument. Refuse it here.
        if (v.Length < 1e-12)
            throw new AcadMcpException("bad_argument",
                $"Argument '{name}' must be a non-zero direction vector.",
                "A zero-length vector would leave the object with no valid coordinate system.");

        return v;
    }

    /// <summary>Rotation arrives in degrees (module convention) unless 'rotationRadians' is given.</summary>
    private static double? RotationRadians(JsonElement e)
    {
        if (Has(e, "rotationRadians")) return Args.Dbl(e, "rotationRadians");
        if (Has(e, "rotation")) return Rad(Args.Dbl(e, "rotation"));
        return null;
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

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

    // --------------------------------------------------------------- drawing helpers (ACA-free)

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
            BlockTable bt;
            try { bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead); }
            catch (System.Exception) { return spaces; }

            foreach (ObjectId id in bt)
            {
                try
                {
                    var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout) spaces.Add(id);
                }
                catch (System.Exception) { }
            }
            return spaces;
        }

        spaces.Add(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db));
        return spaces;
    }

    /// <summary>Which block table record a newly created object goes into.</summary>
    private static ObjectId TargetSpaceId(AcadContext ctx, string space)
    {
        switch (space.Trim().ToLowerInvariant())
        {
            case "" or "model":
                return SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db);

            case "current":
                return ctx.Db.CurrentSpaceId;

            case "paper":
                return SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db);

            default:
                throw new AcadMcpException("bad_argument",
                    $"Unknown space '{space}'. Use 'model', 'paper' or 'current' when creating.");
        }
    }

    /// <summary>
    /// Cheap pre-filter: every ACA runtime class is registered as AecDbMassElement, AecDbMassGroup…
    /// so the class-name test rejects plain AutoCAD geometry without opening the object.
    /// </summary>
    private static bool LooksAec(ObjectId id)
    {
        try { return id.ObjectClass?.Name?.StartsWith("Aec", StringComparison.OrdinalIgnoreCase) == true; }
        catch (System.Exception) { return false; }
    }

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch (System.Exception) { return "Unknown"; }
    }

    /// <summary>Set a layer by name. The layer must already exist — creating one is EntityTools' job.</summary>
    private static void ApplyLayer(AcadContext ctx, Entity ent, string layer)
    {
        LayerTable lt;
        try { lt = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("layer_unavailable", "Could not read the layer table.", ex.Message);
        }

        if (!lt.Has(layer))
            throw new AcadMcpException("layer_not_found",
                $"No layer named '{layer}' in this drawing.",
                "Create the layer first, or omit 'layer' to use the current one.");

        ent.Layer = layer;
    }

#if ACA

    // --------------------------------------------------------------------- guarded accessors
    // AEC property getters throw for degenerate geometry (Radius on a Box, Volume on a half-built
    // element, a style lookup across a broken xref). One bad property must never lose a whole
    // listing, and the bridge drops nulls, so a failed read simply vanishes from the JSON.

    private static string? TryS(Func<string?> f) { try { return f(); } catch (System.Exception) { return null; } }

    private static double? TryD(Func<double> f)
    {
        try
        {
            var v = f();
            return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
        }
        catch (System.Exception) { return null; }
    }

    private static int? TryI(Func<int> f) { try { return f(); } catch (System.Exception) { return null; } }

    private static bool? TryB(Func<bool> f) { try { return f(); } catch (System.Exception) { return null; } }

    private static double[]? TryPt(Func<Point3d> f)
    {
        try { var p = f(); return new[] { p.X, p.Y, p.Z }; }
        catch (System.Exception) { return null; }
    }

    private static double[]? TryVec(Func<Vector3d> f)
    {
        try { var v = f(); return new[] { v.X, v.Y, v.Z }; }
        catch (System.Exception) { return null; }
    }

    // --------------------------------------------------------------------------- enum parsing

    private static string Normalise(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray());

    private static AecShape ParseShape(string raw)
    {
        var wanted = Normalise(raw);

        foreach (var name in Enum.GetNames(typeof(AecShape)))
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return (AecShape)Enum.Parse(typeof(AecShape), name);

        throw new AcadMcpException("bad_argument",
            $"'{raw}' is not a mass element shape.",
            "Valid: " + string.Join(", ", Enum.GetNames(typeof(AecShape))));
    }

    private static AecOperation ParseOperation(JsonElement a, string name, AecOperation fallback)
    {
        var raw = Args.StrOpt(a, name, null);
        if (raw is null) return fallback;

        var wanted = Normalise(raw);
        foreach (var n in Enum.GetNames(typeof(AecOperation)))
        {
            if (!string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            var parsed = (AecOperation)Enum.Parse(typeof(AecOperation), n);

            // Operation.Unknown is what ACA reports for a node it could not classify; feeding it
            // back in would attach a node with no valid boolean op.
            if (parsed == AecOperation.Unknown)
                throw new AcadMcpException("bad_argument",
                    "'Unknown' is a report-only value, not a boolean operation you can apply.",
                    "Valid: additive, subtractive, intersect, combine.");

            return parsed;
        }

        throw new AcadMcpException("bad_argument",
            $"'{raw}' is not a mass group boolean operation.",
            "Valid: additive, subtractive, intersect, combine.");
    }

    /// <summary>Extrusion/Revolution are profile-driven; BoundaryRepresentation needs a Body.</summary>
    private static bool NeedsProfile(AecShape shape) =>
        shape == AecShape.Extrusion || shape == AecShape.Revolution;

    private static bool IsCreatable(AecShape shape) =>
        shape != AecShape.Unknown
        && shape != AecShape.Error
        && shape != AecShape.BoundaryRepresentation;

    // -------------------------------------------------------------------------- style lookups

    private static ObjectId MassStyleId(AcadContext ctx, string name)
    {
        var dict = new AecMassStyleDict(ctx.Db);

        bool has;
        try { has = dict.Has(name, ctx.Tr); }
        catch (System.Exception) { has = false; }

        if (!has)
            throw new AcadMcpException("style_not_found",
                $"No mass element style named '{name}' in this drawing.",
                "Call list_mass_shapes for the styles that exist.");

        ObjectId id;
        try { id = dict.GetAt(name); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("style_not_found",
                $"Mass element style '{name}' could not be opened.", ex.Message);
        }

        if (id.IsNull)
            throw new AcadMcpException("style_not_found", $"Mass element style '{name}' resolved to null.");

        return id;
    }

    /// <summary>The drawing's Standard mass element style; ObjectId.Null if ACA will not produce one.</summary>
    private static ObjectId StandardStyleId(AcadContext ctx)
    {
        try { return AecMassStyleDict.GetStandardStyle(ctx.Db); }
        catch (System.Exception) { return ObjectId.Null; }
    }

    private static ObjectId ProfileDefinitionId(AcadContext ctx, string name)
    {
        var dict = new AecProfileDict(ctx.Db);

        bool has;
        try { has = dict.Has(name, ctx.Tr); }
        catch (System.Exception) { has = false; }

        if (!has)
            throw new AcadMcpException("profile_not_found",
                $"No profile definition named '{name}' in this drawing.",
                "Profile definitions are AEC profiles created from closed polylines (ACA: Profile Definitions).");

        ObjectId id;
        try { id = dict.GetAt(name); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("profile_not_found",
                $"Profile definition '{name}' could not be opened.", ex.Message);
        }

        if (id.IsNull)
            throw new AcadMcpException("profile_not_found", $"Profile definition '{name}' resolved to null.");

        return id;
    }

    private static string? StyleNameOf(AcadContext ctx, AecEntity e)
    {
        try
        {
            if (e.StyleId.IsNull) return null;
            return (ctx.Tr.GetObject(e.StyleId, OpenMode.ForRead) as AecRecord)?.Name;
        }
        catch (System.Exception) { return null; }
    }

    private static string? StyleHandleOf(AecEntity e)
    {
        try { return e.StyleId.IsNull ? null : e.StyleId.Handle.ToString(); }
        catch (System.Exception) { return null; }
    }

    // ---------------------------------------------------------------------------- open helpers

    private static AecMass OpenMass(AcadContext ctx, string handle, OpenMode mode)
    {
        var id = Acad.ToObjectId(ctx.Db, handle);

        DBObject obj;
        try { obj = ctx.Tr.GetObject(id, mode); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("open_failed", $"Could not open '{handle}'.", ex.Message);
        }

        return obj as AecMass
            ?? throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC mass element.");
    }

    private static AecMassGroup OpenGroup(AcadContext ctx, string handle, OpenMode mode)
    {
        var id = Acad.ToObjectId(ctx.Db, handle);

        DBObject obj;
        try { obj = ctx.Tr.GetObject(id, mode); }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("open_failed", $"Could not open '{handle}'.", ex.Message);
        }

        return obj as AecMassGroup
            ?? throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC mass group.");
    }

    // -------------------------------------------------------------------------------- scanning

    private static List<ObjectId> ScanMass(AcadContext ctx, string scope, bool deep, bool wantGroups)
    {
        var found = new List<ObjectId>();

        foreach (var spaceId in TargetSpaces(ctx, scope))
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead); }
            catch (System.Exception) { continue; }

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                if (!deep && !LooksAec(id)) continue;

                DBObject obj;
                try { obj = ctx.Tr.GetObject(id, OpenMode.ForRead); }
                catch (System.Exception) { continue; }

                if (wantGroups ? obj is AecMassGroup : obj is AecMass)
                    found.Add(id);
            }
        }

        return found;
    }

    /// <summary>Handles of the entities currently attached to a mass group.</summary>
    private static List<ObjectId> MemberIds(AecMassGroup group)
    {
        var ids = new List<ObjectId>();
        try
        {
            var collection = group.Entities;
            if (collection is not null)
                foreach (ObjectId id in collection)
                    if (!id.IsNull) ids.Add(id);
        }
        catch (System.Exception) { }
        return ids;
    }

    private static string? OperationOf(AecMassGroup group, ObjectId id)
    {
        try { return group.GetOperation(id).ToString(); }
        catch (System.Exception) { return null; }
    }

    /// <summary>element handle -> (group handle, boolean operation). One scan of every mass group.</summary>
    private static System.Collections.Generic.Dictionary<string, (string Group, string? Operation)>
        GroupMembership(AcadContext ctx, string scope, bool deep)
    {
        var map = new System.Collections.Generic.Dictionary<string, (string Group, string? Operation)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var groupId in ScanMass(ctx, scope, deep, wantGroups: true))
        {
            AecMassGroup? group;
            try { group = ctx.Tr.GetObject(groupId, OpenMode.ForRead) as AecMassGroup; }
            catch (System.Exception) { continue; }
            if (group is null) continue;

            string groupHandle;
            try { groupHandle = groupId.Handle.ToString(); }
            catch (System.Exception) { continue; }

            foreach (var memberId in MemberIds(group))
            {
                try { map[memberId.Handle.ToString()] = (groupHandle, OperationOf(group, memberId)); }
                catch (System.Exception) { }
            }
        }

        return map;
    }

    // ----------------------------------------------------------------------------- description

    private static object DescribeMass(
        AcadContext ctx, AecMass mass, string? groupHandle, string? operation) => new
    {
        handle = TryS(() => mass.Handle.ToString()),
        type = "MassElement",
        objectClass = ClassName(mass.ObjectId),
        layer = TryS(() => mass.Layer),
        description = TryS(() => mass.Description),
        shapeType = TryS(() => mass.ShapeType.ToString()),
        shapeSubType = TryS(() => mass.ShapeSubType.ToString()),
        subType = TryS(() => mass.SubType),
        styleName = StyleNameOf(ctx, mass),
        styleHandle = StyleHandleOf(mass),
        profileHandle = TryS(() => mass.ProfileId.IsNull ? null : mass.ProfileId.Handle.ToString()),
        width = TryD(() => mass.Width),
        depth = TryD(() => mass.Depth),
        height = TryD(() => mass.Height),
        radius = TryD(() => mass.Radius),
        rise = TryD(() => mass.Rise),
        deviation = TryD(() => mass.Deviation),
        volume = TryD(() => mass.Volume),
        location = TryPt(() => mass.Location),
        normal = TryVec(() => mass.Normal),
        xDir = TryVec(() => mass.XDir),
        rotation = TryD(() => Deg(mass.Rotation)),
        rotationRadians = TryD(() => mass.Rotation),
        isAnchored = TryB(() => mass.IsAnchored),
        massGroupHandle = groupHandle,
        massGroupOperation = operation,
    };

    private static object DescribeGroup(AcadContext ctx, AecMassGroup group)
    {
        var members = new List<object>();

        foreach (var id in MemberIds(group))
        {
            string handle;
            try { handle = id.Handle.ToString(); }
            catch (System.Exception) { continue; }

            string? shape = null;
            string? styleName = null;
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is AecMass m)
                {
                    shape = TryS(() => m.ShapeType.ToString());
                    styleName = StyleNameOf(ctx, m);
                }
            }
            catch (System.Exception) { }

            members.Add(new
            {
                handle,
                objectClass = ClassName(id),
                shapeType = shape,
                styleName,
                operation = OperationOf(group, id),
            });
        }

        return new
        {
            handle = TryS(() => group.Handle.ToString()),
            type = "MassGroup",
            objectClass = ClassName(group.ObjectId),
            layer = TryS(() => group.Layer),
            description = TryS(() => group.Description),
            subType = TryS(() => group.SubType),
            styleName = StyleNameOf(ctx, group),
            styleHandle = StyleHandleOf(group),
            volume = TryD(() => group.Volume),
            location = TryPt(() => group.Location),
            normal = TryVec(() => group.Normal),
            rotation = TryD(() => Deg(group.Rotation)),
            rotationRadians = TryD(() => group.Rotation),
            nodeCount = TryI(() => group.NodeCount),
            supportsAnchoring = TryB(() => group.SupportsAnchoring),
            rootNodeHandle = TryS(() => group.RootNode.IsNull ? null : group.RootNode.Handle.ToString()),
            memberCount = members.Count,
            members,
        };
    }

    // ------------------------------------------------------------------------- property writes

    /// <summary>
    /// Apply one dimension. ACA rejects properties a given shape does not own (Radius on a Box), so
    /// a refusal is recorded and the rest of the request still lands.
    /// </summary>
    private static void SetDim(
        string name, double? value, Action<double> setter, List<string> changed, List<string> refused)
    {
        if (!value.HasValue) return;
        try
        {
            setter(value.Value);
            changed.Add(name);
        }
        catch (System.Exception ex)
        {
            refused.Add($"{name}: {ex.Message}");
        }
    }

    private static void ApplyMassGeometry(
        AcadContext ctx, AecMass mass, JsonElement a, List<string> changed, List<string> refused)
    {
        var location = PointOrNull(a, "position") ?? PointOrNull(a, "location");
        if (location.HasValue)
        {
            try { mass.Location = location.Value; changed.Add("location"); }
            catch (System.Exception ex) { refused.Add($"location: {ex.Message}"); }
        }

        var normal = VectorOrNull(a, "normal");
        if (normal.HasValue)
        {
            try { mass.Normal = normal.Value; changed.Add("normal"); }
            catch (System.Exception ex) { refused.Add($"normal: {ex.Message}"); }
        }

        var rotation = RotationRadians(a);
        if (rotation.HasValue)
        {
            try { mass.Rotation = rotation.Value; changed.Add("rotation"); }
            catch (System.Exception ex) { refused.Add($"rotation: {ex.Message}"); }
        }

        SetDim("width", DblOrNull(a, "width"), v => mass.Width = v, changed, refused);
        SetDim("depth", DblOrNull(a, "depth"), v => mass.Depth = v, changed, refused);
        SetDim("height", DblOrNull(a, "height"), v => mass.Height = v, changed, refused);
        SetDim("radius", DblOrNull(a, "radius"), v => mass.Radius = v, changed, refused);
        SetDim("rise", DblOrNull(a, "rise"), v => mass.Rise = v, changed, refused);
        SetDim("deviation", DblOrNull(a, "deviation"), v => mass.Deviation = v, changed, refused);

        var subType = Args.StrOpt(a, "subType", null);
        if (subType is not null)
        {
            try { mass.SubType = subType; changed.Add("subType"); }
            catch (System.Exception ex) { refused.Add($"subType: {ex.Message}"); }
        }

        var description = Args.StrOpt(a, "description", null);
        if (description is not null)
        {
            try { mass.Description = description; changed.Add("description"); }
            catch (System.Exception ex) { refused.Add($"description: {ex.Message}"); }
        }

        var layer = Args.StrOpt(a, "layer", null);
        if (layer is not null)
        {
            ApplyLayer(ctx, mass, layer);
            changed.Add("layer");
        }
    }

    // --------------------------------------------------------------------------- implementations

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMassShapes(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var shapes = new List<object>();

        foreach (var name in Enum.GetNames(typeof(AecShape)))
        {
            var shape = (AecShape)Enum.Parse(typeof(AecShape), name);
            shapes.Add(new
            {
                name,
                value = (int)shape,
                creatable = IsCreatable(shape),
                requiresProfile = NeedsProfile(shape),
                requiresBody = shape == AecShape.BoundaryRepresentation,
                note = shape switch
                {
                    AecShape.Unknown or AecShape.Error => "Reported by ACA for an unresolved shape; not creatable.",
                    AecShape.Extrusion or AecShape.Revolution =>
                        "Pass 'profile' — the name of a profile definition in this drawing.",
                    AecShape.BoundaryRepresentation =>
                        "Built from an Autodesk.Aec.Modeler.Body via MassElement.SetBody; not offered by this tool.",
                    _ => "Sized with width/depth/height; curved and pitched shapes also use radius and rise.",
                },
            });
        }

        // Style names the caller can hand to create_mass_element.
        var styles = new List<(string Key, object Data)>();
        try
        {
            var dict = new AecMassStyleDict(ctx.Db);
            var records = dict.Records;
            if (records is not null)
            {
                foreach (ObjectId id in records)
                {
                    if (id.IsNull) continue;
                    try
                    {
                        if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not AecMassStyle style) continue;
                        var styleName = style.Name ?? string.Empty;
                        styles.Add((styleName, new
                        {
                            name = styleName,
                            handle = id.Handle.ToString(),
                            description = TryS(() => style.Description),
                            materialHandle = TryS(() =>
                                style.MaterialId.IsNull ? null : style.MaterialId.Handle.ToString()),
                        }));
                    }
                    catch (System.Exception) { }
                }
            }
        }
        catch (System.Exception) { }

        return new
        {
            shapes,
            shapeCount = shapes.Count,
            styles = styles.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                           .Select(s => s.Data)
                           .ToList(),
            dimensionProperties = new[] { "width", "depth", "height", "radius", "rise", "deviation" },
            booleanOperations = Enum.GetNames(typeof(AecOperation)),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateMassElement(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var shapeArg = Args.StrOpt(a, "shape", "Box") ?? "Box";
        var shape = ParseShape(shapeArg);

        if (!IsCreatable(shape))
            throw new AcadMcpException("unsupported_shape",
                $"Shape '{shape}' cannot be created by this tool.",
                shape == AecShape.BoundaryRepresentation
                    ? "BoundaryRepresentation is built from an Aec Modeler Body via MassElement.SetBody, which is not exposed here."
                    : "Call list_mass_shapes for the shapes that are creatable.");

        var profileName = Args.StrOpt(a, "profile", null);

        // Only Extrusion and Revolution consume a profile. Handing a profile id to Create() for a
        // primitive shape is an unverified overload combination, so refuse it instead of guessing.
        if (profileName is not null && !NeedsProfile(shape))
            throw new AcadMcpException("bad_argument",
                $"Shape '{shape}' does not take a profile.",
                "Only Extrusion and Revolution are profile-driven — drop 'profile', or pick one of those shapes.");

        var profileId = profileName is null ? ObjectId.Null : ProfileDefinitionId(ctx, profileName);

        if (NeedsProfile(shape) && profileId.IsNull)
            throw new AcadMcpException("bad_argument",
                $"Shape '{shape}' needs a 'profile' argument (a profile definition name).");

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "model") ?? "model");
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        // Resolve the style BEFORE building anything, so a bad style name fails with nothing created.
        var styleArg = Args.StrOpt(a, "style", null);
        var styleId = styleArg is null ? ObjectId.Null : MassStyleId(ctx, styleArg);

        AecMass mass;
        try
        {
            mass = profileId.IsNull ? AecMass.Create(shape) : AecMass.Create(shape, profileId);
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("create_failed",
                $"AutoCAD Architecture refused to build a '{shape}' mass element.", ex.Message);
        }

        if (mass is null)
            throw new AcadMcpException("create_failed",
                $"MassElement.Create returned null for shape '{shape}'.");

        // Database defaults and a VALID AEC STYLE before the object joins the drawing.
        try { mass.SetDatabaseDefaults(ctx.Db); } catch (System.Exception) { }

        var standardApplied = true;
        try { mass.SetToStandard(ctx.Db); }
        catch (System.Exception) { standardApplied = false; }

        if (styleId.IsNull) styleId = StandardStyleId(ctx);

        if (!styleId.IsNull)
        {
            try { mass.StyleId = styleId; }
            catch (System.Exception ex)
            {
                if (!standardApplied)
                {
                    mass.Dispose();
                    throw new AcadMcpException("style_unavailable",
                        "Could not give the new mass element a style; nothing was added to the drawing.",
                        ex.Message);
                }
            }
        }
        else if (!standardApplied)
        {
            // No style from SetToStandard and none from the style dictionary. An AEC entity with no
            // style is an invalid object — refuse rather than append it.
            mass.Dispose();
            throw new AcadMcpException("style_unavailable",
                "This drawing yielded no mass element style, so the element was not created.",
                "Open the drawing in AutoCAD Architecture once so the AEC style dictionaries are initialised.");
        }

        // Hard gate, not an assumption: whatever SetToStandard reported, the object must actually
        // carry a style id before it joins the drawing. A style-less AEC entity can corrupt the DWG.
        bool hasStyle;
        try { hasStyle = !mass.StyleId.IsNull; }
        catch (System.Exception) { hasStyle = false; }

        if (!hasStyle)
        {
            mass.Dispose();
            throw new AcadMcpException("style_unavailable",
                "The new mass element ended up with no AEC style, so it was not added to the drawing.",
                "Pass 'style' with a name from list_mass_shapes, or open the drawing in AutoCAD "
                + "Architecture once so its AEC style dictionaries are initialised.");
        }

        btr.AppendEntity(mass);
        ctx.Tr.AddNewlyCreatedDBObject(mass, true);

        // Geometry last, on the database-resident object — the same path set_mass_element_properties
        // uses, so a value that works there works here.
        var changed = new List<string>();
        var refused = new List<string>();
        ApplyMassGeometry(ctx, mass, a, changed, refused);

        if (Args.BoolOpt(a, "useLayerKey", false))
        {
            try { mass.SetDefaultLayer(); changed.Add("layerKey"); }
            catch (System.Exception ex) { refused.Add($"useLayerKey: {ex.Message}"); }
        }

        string btrName;
        try { btrName = btr.Name; } catch (System.Exception) { btrName = "unknown"; }

        return new
        {
            handle = mass.Handle.ToString(),
            space = btrName,
            applied = changed,
            refused,
            element = DescribeMass(ctx, mass, null, null),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateMassGroup(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var operation = ParseOperation(a, "operation", AecOperation.Additive);
        var elementHandles = Args.StrArrayOpt(a, "elements");

        var spaceId = TargetSpaceId(ctx, Args.StrOpt(a, "space", "model") ?? "model");
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var group = new AecMassGroup();

        try { group.SetDatabaseDefaults(ctx.Db); } catch (System.Exception) { }

        // AecBaseMgd has no DictionaryMassGroupStyle: a mass group is style-less in ACA, so a failed
        // SetToStandard is expected and not fatal.
        try { group.SetToStandard(ctx.Db); } catch (System.Exception) { }

        btr.AppendEntity(group);
        ctx.Tr.AddNewlyCreatedDBObject(group, true);

        var changed = new List<string>();
        var refused = new List<string>();

        var location = PointOrNull(a, "position") ?? PointOrNull(a, "location");
        if (location.HasValue)
        {
            try { group.Location = location.Value; changed.Add("location"); }
            catch (System.Exception ex) { refused.Add($"location: {ex.Message}"); }
        }

        if (Has(a, "supportsAnchoring"))
        {
            var wanted = Args.BoolOpt(a, "supportsAnchoring", true);
            try { group.SupportsAnchoring = wanted; changed.Add("supportsAnchoring"); }
            catch (System.Exception ex) { refused.Add($"supportsAnchoring: {ex.Message}"); }
        }

        var description = Args.StrOpt(a, "description", null);
        if (description is not null)
        {
            try { group.Description = description; changed.Add("description"); }
            catch (System.Exception ex) { refused.Add($"description: {ex.Message}"); }
        }

        var layer = Args.StrOpt(a, "layer", null);
        if (layer is not null)
        {
            ApplyLayer(ctx, group, layer);
            changed.Add("layer");
        }

        var added = AttachElements(ctx, group, elementHandles, operation);

        string btrName;
        try { btrName = btr.Name; } catch (System.Exception) { btrName = "unknown"; }

        return new
        {
            handle = group.Handle.ToString(),
            space = btrName,
            applied = changed,
            refused,
            elements = added,
            group = DescribeGroup(ctx, group),
        };
    });

    /// <summary>
    /// Would attaching <paramref name="candidateId"/> to <paramref name="groupId"/> make the boolean
    /// tree reference itself? ACA allows a mass group to contain another mass group, so only a cycle
    /// is refused — a cycle in a body-evaluation tree is drawing corruption, not a recoverable error.
    /// Unknowable depth counts as a cycle: refusing costs the caller one node, guessing costs the DWG.
    /// </summary>
    private static bool WouldCycle(AcadContext ctx, ObjectId groupId, ObjectId candidateId, int depth)
    {
        if (candidateId == groupId) return true;
        if (depth > 16) return true;

        AecMassGroup? nested;
        try { nested = ctx.Tr.GetObject(candidateId, OpenMode.ForRead) as AecMassGroup; }
        catch (System.Exception) { return false; }

        // A plain mass element has no children, so it can never contain the group.
        if (nested is null) return false;

        foreach (var childId in MemberIds(nested))
            if (WouldCycle(ctx, groupId, childId, depth + 1))
                return true;

        return false;
    }

    /// <summary>Add (or re-operate) a set of elements on an already database-resident mass group.</summary>
    private static List<object> AttachElements(
        AcadContext ctx, AecMassGroup group, string[] handles, AecOperation operation)
    {
        var results = new List<object>();
        if (handles.Length == 0) return results;

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in MemberIds(group))
        {
            try { existing.Add(id.Handle.ToString()); }
            catch (System.Exception) { }
        }

        foreach (var handle in handles)
        {
            ObjectId id;
            try { id = Acad.ToObjectId(ctx.Db, handle); }
            catch (AcadMcpException ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
                continue;
            }

            // Self-reference (or a nested-group cycle) would make the group's body evaluation
            // recursive. Check before opening anything for write.
            if (WouldCycle(ctx, group.ObjectId, id, 0))
            {
                results.Add(new
                {
                    handle,
                    status = "failed",
                    error = "Refused: adding this would make the mass group contain itself, "
                            + "directly or through a nested group.",
                });
                continue;
            }

            AecEntity? entity;
            try { entity = ctx.Tr.GetObject(id, OpenMode.ForWrite) as AecEntity; }
            catch (System.Exception ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
                continue;
            }

            if (entity is null)
            {
                results.Add(new
                {
                    handle,
                    status = "failed",
                    error = $"Handle '{handle}' is a {ClassName(id)}, not an AEC entity; a mass group only takes AEC objects.",
                });
                continue;
            }

            // Compare on the canonical handle text, not the caller's spelling ("2ab" vs "2AB").
            string canonical;
            try { canonical = id.Handle.ToString(); }
            catch (System.Exception) { canonical = handle; }

            if (existing.Contains(canonical))
            {
                try
                {
                    group.SetOperation(id, operation);
                    results.Add(new { handle, status = "operationChanged", operation = operation.ToString() });
                }
                catch (System.Exception ex)
                {
                    results.Add(new { handle, status = "failed", error = ex.Message });
                }
                continue;
            }

            try
            {
                group.AddNode(entity, operation);
                existing.Add(canonical);
                results.Add(new { handle, status = "added", operation = operation.ToString() });
            }
            catch (System.Exception ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
            }
        }

        return results;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> AddToMassGroup(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var groupHandle = Args.Str(a, "handle");
        var elements = Args.StrArray(a, "elements");
        var operation = ParseOperation(a, "operation", AecOperation.Additive);

        if (elements.Length == 0)
            throw new AcadMcpException("bad_argument", "Argument 'elements' must list at least one handle.");

        var group = OpenGroup(ctx, groupHandle, OpenMode.ForWrite);
        var results = AttachElements(ctx, group, elements, operation);

        return new
        {
            handle = groupHandle,
            operation = operation.ToString(),
            elements = results,
            group = DescribeGroup(ctx, group),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> RemoveFromMassGroup(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var groupHandle = Args.Str(a, "handle");
        var removeAll = Args.BoolOpt(a, "all", false);
        var elements = Args.StrArrayOpt(a, "elements");

        if (!removeAll && elements.Length == 0)
            throw new AcadMcpException("bad_argument",
                "Give 'elements' (handles to detach) or 'all': true.");

        var group = OpenGroup(ctx, groupHandle, OpenMode.ForWrite);
        var results = new List<object>();
        var removed = 0;

        if (removeAll)
        {
            var before = MemberIds(group).Count;
            try { group.RemoveAllNodes(); }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("remove_failed",
                    $"Mass group '{groupHandle}' refused RemoveAllNodes.", ex.Message);
            }

            return new
            {
                handle = groupHandle,
                removedAll = true,
                removedCount = before,
                elements = results,
                group = DescribeGroup(ctx, group),
            };
        }

        foreach (var handle in elements)
        {
            ObjectId id;
            try { id = Acad.ToObjectId(ctx.Db, handle); }
            catch (AcadMcpException ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
                continue;
            }

            AecEntity? entity;
            try { entity = ctx.Tr.GetObject(id, OpenMode.ForWrite) as AecEntity; }
            catch (System.Exception ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
                continue;
            }

            if (entity is null)
            {
                results.Add(new
                {
                    handle,
                    status = "failed",
                    error = $"Handle '{handle}' is a {ClassName(id)}, not an AEC entity.",
                });
                continue;
            }

            try
            {
                group.RemoveNode(entity);
                removed++;
                results.Add(new { handle, status = "removed" });
            }
            catch (System.Exception ex)
            {
                results.Add(new { handle, status = "failed", error = ex.Message });
            }
        }

        return new
        {
            handle = groupHandle,
            removedAll = false,
            removedCount = removed,
            elements = results,
            group = DescribeGroup(ctx, group),
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMassElements(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var scope = Scope(a);
        var deep = Args.BoolOpt(a, "deepScan", false);
        var shapeFilter = Args.StrOpt(a, "shape", null);

        var membership = Args.BoolOpt(a, "membership", true)
            ? GroupMembership(ctx, scope, deep)
            : new System.Collections.Generic.Dictionary<string, (string Group, string? Operation)>(
                StringComparer.OrdinalIgnoreCase);

        var rows = new List<(string Key, object Data)>();

        foreach (var id in ScanMass(ctx, scope, deep, wantGroups: false))
        {
            AecMass? opened;
            try { opened = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecMass; }
            catch (System.Exception) { continue; }
            if (opened is null) continue;

            // Non-nullable local: the guarded accessors below capture it in lambdas.
            AecMass mass = opened;

            if (shapeFilter is not null)
            {
                var actual = TryS(() => mass.ShapeType.ToString());
                if (!string.Equals(Normalise(actual ?? string.Empty), Normalise(shapeFilter),
                                   StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            string handle;
            try { handle = id.Handle.ToString(); }
            catch (System.Exception) { continue; }

            string? groupHandle = null;
            string? operation = null;
            if (membership.TryGetValue(handle, out var entry))
            {
                groupHandle = entry.Group;
                operation = entry.Operation;
            }

            rows.Add((handle, DescribeMass(ctx, mass, groupHandle, operation)));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListMassGroups(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var scope = Scope(a);
        var deep = Args.BoolOpt(a, "deepScan", false);

        var rows = new List<(string Key, object Data)>();

        foreach (var id in ScanMass(ctx, scope, deep, wantGroups: true))
        {
            AecMassGroup? group;
            try { group = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecMassGroup; }
            catch (System.Exception) { continue; }
            if (group is null) continue;

            string handle;
            try { handle = id.Handle.ToString(); }
            catch (System.Exception) { continue; }

            rows.Add((handle, DescribeGroup(ctx, group)));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetMassElementProperties(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var handle = Args.Str(a, "handle");
        var mass = OpenMass(ctx, handle, OpenMode.ForWrite);

        var changed = new List<string>();
        var refused = new List<string>();

        // Shape first: ChangeTo rebuilds the body, so dimensions must be applied after it.
        var shapeArg = Args.StrOpt(a, "shape", null);
        if (shapeArg is not null)
        {
            var shape = ParseShape(shapeArg);

            if (!IsCreatable(shape))
                throw new AcadMcpException("unsupported_shape",
                    $"Shape '{shape}' cannot be applied by this tool.",
                    "Call list_mass_shapes for the shapes that are creatable.");

            var profileName = Args.StrOpt(a, "profile", null);

            if (profileName is not null && !NeedsProfile(shape))
                throw new AcadMcpException("bad_argument",
                    $"Shape '{shape}' does not take a profile.",
                    "Only Extrusion and Revolution are profile-driven — drop 'profile', or pick one of those shapes.");

            var profileId = profileName is null ? ObjectId.Null : ProfileDefinitionId(ctx, profileName);

            if (NeedsProfile(shape) && profileId.IsNull)
                throw new AcadMcpException("bad_argument",
                    $"Shape '{shape}' needs a 'profile' argument (a profile definition name).");

            try
            {
                if (profileId.IsNull) mass.ChangeTo(shape);
                else mass.ChangeTo(shape, profileId);
                changed.Add("shape");
            }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("shape_change_failed",
                    $"AutoCAD Architecture refused to change mass element '{handle}' to '{shape}'.",
                    ex.Message);
            }
        }
        else if (Args.StrOpt(a, "profile", null) is string profileOnly)
        {
            // 'profile' with no 'shape' used to be silently dropped. Swap the profile in place
            // through ChangeTo(currentShape, profileId) — but only for a profile-driven shape.
            var currentShape = AecShape.Unknown;
            try { currentShape = mass.ShapeType; }
            catch (System.Exception) { }

            if (!NeedsProfile(currentShape))
                throw new AcadMcpException("bad_argument",
                    $"This mass element is a '{currentShape}', which does not use a profile.",
                    "Pass 'shape': 'Extrusion' or 'Revolution' together with 'profile' to convert it.");

            var profileId = ProfileDefinitionId(ctx, profileOnly);
            try
            {
                mass.ChangeTo(currentShape, profileId);
                changed.Add("profile");
            }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("shape_change_failed",
                    $"AutoCAD Architecture refused to apply profile '{profileOnly}' to mass element '{handle}'.",
                    ex.Message);
            }
        }

        var styleArg = Args.StrOpt(a, "style", null);
        if (styleArg is not null)
        {
            var styleId = MassStyleId(ctx, styleArg);
            try { mass.StyleId = styleId; changed.Add("style"); }
            catch (System.Exception ex) { refused.Add($"style: {ex.Message}"); }
        }

        ApplyMassGeometry(ctx, mass, a, changed, refused);

        if (changed.Count == 0 && refused.Count == 0)
            throw new AcadMcpException("bad_argument",
                "Nothing to change.",
                "Pass at least one of shape, style, width, depth, height, radius, rise, deviation, "
                + "rotation, position, normal, layer, description, subType.");

        return new
        {
            handle,
            applied = changed,
            refused,
            element = DescribeMass(ctx, mass, null, null),
        };
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListMassShapes(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateMassElement(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateMassGroup(JsonElement a) => throw Unavailable();
    private static Task<object?> AddToMassGroup(JsonElement a) => throw Unavailable();
    private static Task<object?> RemoveFromMassGroup(JsonElement a) => throw Unavailable();
    private static Task<object?> ListMassElements(JsonElement a) => throw Unavailable();
    private static Task<object?> ListMassGroups(JsonElement a) => throw Unavailable();
    private static Task<object?> SetMassElementProperties(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
