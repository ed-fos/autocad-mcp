using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// Same aliasing discipline as AcaTools.cs: 'Entity', 'DBObject' and 'Dictionary' all collide
// between Autodesk.Aec.*, Autodesk.AutoCAD.DatabaseServices and System.Collections.Generic, and
// on a vanilla AutoCAD box these assemblies do not exist at all — so nothing outside #if ACA may
// name them.
#if ACA
using AecDict = Autodesk.Aec.DatabaseServices.Dictionary;
using AecRecord = Autodesk.Aec.DatabaseServices.DictionaryRecord;
using AecDbObj = Autodesk.Aec.DatabaseServices.DBObject;
using AecGeo = Autodesk.Aec.DatabaseServices.Geo;
using AecAnchorRef = Autodesk.Aec.DatabaseServices.AnchorToReference;
using AecAnchorCurve = Autodesk.Aec.DatabaseServices.AnchorEntityToCurve;
using AecRelation = Autodesk.Aec.DatabaseServices.RelationType;
using AecXOffset = Autodesk.Aec.DatabaseServices.CurveXOffsetType;
using AecXMeasure = Autodesk.Aec.DatabaseServices.CurveXMeasureToType;
using AecYOffset = Autodesk.Aec.DatabaseServices.CurveYOffsetType;
using AecYMeasure = Autodesk.Aec.DatabaseServices.CurveYMeasureToType;

using AecWall = Autodesk.Aec.Arch.DatabaseServices.Wall;
using AecDoor = Autodesk.Aec.Arch.DatabaseServices.Door;
using AecDoorStyle = Autodesk.Aec.Arch.DatabaseServices.DoorStyle;
using AecDoorDict = Autodesk.Aec.Arch.DatabaseServices.DictionaryDoorStyle;
using AecWindow = Autodesk.Aec.Arch.DatabaseServices.Window;
using AecWindowStyle = Autodesk.Aec.Arch.DatabaseServices.WindowStyle;
using AecWindowDict = Autodesk.Aec.Arch.DatabaseServices.DictionaryWindowStyle;
using AecOpening = Autodesk.Aec.Arch.DatabaseServices.Opening;
using AecOpeningBase = Autodesk.Aec.Arch.DatabaseServices.OpeningBase;
using AecOpenFiller = Autodesk.Aec.Arch.DatabaseServices.OpenFiller;
using AecOpeningAnchor = Autodesk.Aec.Arch.DatabaseServices.AnchorOpeningBaseToWall;
using AecShapeType = Autodesk.Aec.Arch.DatabaseServices.OpenShapeType;
using AecSillHead = Autodesk.Aec.Arch.DatabaseServices.SillHeadHeightUtility;
using AecEndcapDict = Autodesk.Aec.Arch.DatabaseServices.DictionaryOpeningEndcapStyle;
using AecEndcapStyle = Autodesk.Aec.Arch.DatabaseServices.OpeningEndcapStyle;
using AecStdSizeList = Autodesk.Aec.Arch.DatabaseServices.StandardSizeOpeningList;
using AecStdSize = Autodesk.Aec.Arch.DatabaseServices.StandardSizeOpening;
using AecArchUtility = Autodesk.Aec.Arch.DatabaseServices.Utility;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// AutoCAD Architecture doors, windows and plain wall openings — style discovery, creation with a
/// real wall anchor, resizing and clean deletion.
///
/// JIT SAFETY. Identical to AcaTools: every handler goes through <see cref="Reg"/>, which calls
/// <see cref="AcaSupport.Require"/> before entering the implementation. The implementations that
/// name Aec types are separate [MethodImpl(NoInlining)] methods whose signatures are only
/// (JsonElement) -> Task&lt;object?&gt;, so no Aec type reference is ever resolved on a vanilla box.
///
/// THE ANCHOR — the whole point of this module.
/// An AEC opening only cuts a wall when it carries an AnchorOpeningBaseToWall that references the
/// wall. Appending a Door to model space without one leaves an uncut wall and a floating door. The
/// creation sequence used here, every member of it present in the declared API surface:
///
///   1. resolve the style id (DictionaryDoorStyle / DictionaryWindowStyle; GetStandardStyle(db)
///      creates 'Standard' if the drawing has none), so the object is never appended unstyled;
///   2. new Door() / Window() / Opening(); Entity.SetDatabaseDefaults(db);
///      Aec Entity.SetToStandard(db); then Aec Entity.StyleId = styleId;
///   3. append to the wall's own BlockTableRecord (not a hard-coded model space) and
///      Transaction.AddNewlyCreatedDBObject, THEN apply the layer (Entity.Layer resolves the name
///      through the owning database, so it needs a resident object) and OpeningBase.Width / Height
///      / Rise;
///   4. refuse to go further on a zero-sized opening (RequireRealSize);
///   5. OPEN THE TARGET WALL ForWrite IN THIS TRANSACTION. This is the load-bearing step and the
///      one that is impossible to guess: without it SetAnchor silently no-ops — it returns without
///      throwing and the anchor is simply never bound. Holding only the wall's ObjectId is not
///      enough. (Behaviour cross-checked against ACA's shipped "Create House.cs" sample and
///      verified in a live session; no code from that sample is reproduced here.)
///   6. new AnchorOpeningBaseToWall(); SubSetDatabaseDefaults(db) then SetToStandard(db);
///      Geo.SetAnchor(anchor) — which takes ownership AND adds the anchor to the database itself,
///      so Transaction.AddNewlyCreatedDBObject must NOT be called on it (that throws
///      eNotInDatabase) — and ONLY THEN AnchorEntityToCurve.CurveId = wallId. Assigning CurveId
///      before SetAnchor binds nothing. Then the AnchorToCurveX offset positioning it along the
///      wall, AnchorToCurveY centring it in the wall thickness, and FlipX / FlipY for hinge and
///      swing side;
///   7. ONLY NOW sill or head height, through SillHeadHeightUtility (the purpose-built API) rather
///      than by poking AnchorToCurveZ. It writes through the wall anchor's Z offset — its own
///      GetVerticalAlignment returns a CurveZOffsetToType — so it has nothing to write to until
///      step 5/6 have run. Same for Opening.EndcapStyleName, which lands on the anchor's
///      OpeningEndcapStyleId;
///   8. VERIFY, then commit. The gate checks the ANCHOR's CurveId, not OpeningBase.IsAnchoredToWall
///      and not Geo.AnchorId: both of those read "not anchored" until the transaction commits even
///      when the anchoring fully succeeded, so gating on either rejects every valid opening. A
///      failed check throws, which skips Acad.Write's tr.Commit() and aborts the whole transaction.
///      The drawing therefore ends up either with a correctly anchored opening or with nothing at
///      all; a half-anchored opening can never be committed by these tools. Callers confirm after
///      the fact via list_doors (isAnchoredToWall) or Wall.GetOpeningsFor().
///
/// WHAT THE DECLARED API SURFACE DOES NOT OFFER (so it is not exposed here):
///  * There is no Door.SwingAngle. Swing is the anchor's business: FlipX mirrors the opening along
///    the wall (hinge jamb) and FlipY flips which face it swings to. OpenFiller.OpenPercent sets how
///    far the leaf is *drawn* open. Those three are what 'flipHinge', 'flipSwing' and 'openPercent'
///    drive; the leaf configuration itself (single/double/bifold) lives on DoorType in the style.
///  * OpenFiller.FrameWidth and FrameDepth are GET-ONLY on the instance — frame size is a style
///    property (DoorStyle/WindowStyle.FrameWidth/FrameDepth), so set_opening_size cannot change it.
///  * OpeningBase.RoughWidth / RoughHeight / InnerWidth / InnerHeight are get-only (derived).
///  * ACA has no opening *style* dictionary. A plain Opening is shaped by OpenShapeType, and the
///    only related dictionary is DictionaryOpeningEndcapStyle — which is what list_opening_styles
///    actually reports, alongside the valid shape names.
///  * WindowAssembly (curtain-wall unit) is anchored by AnchorWindowAssemblyToWall and is NOT an
///    OpeningBase, so it is out of scope for every tool here.
/// </summary>
internal static class AcaOpeningTools
{
    internal static void Register()
    {
        Reg("list_door_styles",
            "Every AEC DoorStyle in the drawing — name, handle, description, door type, shape, "
            + "thickness, frame and leaf count — so a caller can pick a valid 'style' for create_door. "
            + "Args: includeSizes (bool, default false, adds the style's standard size list), "
            + "limit (default 500), offset.",
            mutating: false, ListDoorStyles);

        Reg("list_window_styles",
            "Every AEC WindowStyle in the drawing — name, handle, description, window type, shape, "
            + "frame and sash — so a caller can pick a valid 'style' for create_window. "
            + "Args: includeSizes (bool, default false), limit (default 500), offset.",
            mutating: false, ListWindowStyles);

        Reg("list_opening_styles",
            "Plain wall openings have no style dictionary in ACA: they are shaped by OpenShapeType "
            + "and trimmed by an opening endcap style. This returns the endcap styles as the paged "
            + "items, plus 'shapeTypes' (the valid 'shape' values for create_opening) and the endcap "
            + "names ACA itself reports as available. Args: limit (default 500), offset.",
            mutating: false, ListOpeningStyles);

        Reg("create_door",
            "Create an AEC Door, style it, append it to the wall's own block and ANCHOR it to that "
            + "wall with AnchorOpeningBaseToWall so ACA cuts the opening. Position accepts EITHER "
            + "'distanceAlongWall' (a number measured along the wall baseline — the native anchor "
            + "parameter) OR 'position' ([x,y] / [x,y,z] absolute WCS point, projected onto the wall "
            + "baseline and converted to that distance; exact on curved walls too). Args: wallHandle, "
            + "distanceAlongWall | position, style (default the drawing's Standard door style), "
            + "width, height, rise, headHeight | sillHeight, measureFrom (start|end|middle, default "
            + "start), measureTo (center|start|end, default center), flipHinge (bool — mirrors the "
            + "door along the wall, i.e. the other hinge jamb), flipSwing (bool — swings to the other "
            + "wall face), openPercent, leafWidth, layer. One undo step. If the anchor does not take, "
            + "the whole transaction is rolled back and nothing is written.",
            mutating: true, CreateDoor);

        Reg("create_window",
            "Create an AEC Window, style it and anchor it into a wall exactly as create_door does. "
            + "Args: wallHandle, distanceAlongWall | position, style (default Standard window style), "
            + "width, height, rise, headHeight | sillHeight, measureFrom, measureTo, flipHinge, "
            + "flipSwing, openPercent, layer. One undo step; rolls back entirely if the anchor fails.",
            mutating: true, CreateWindow);

        Reg("create_opening",
            "Create a plain AEC wall Opening (no door or window leaf) and anchor it into a wall. "
            + "Openings carry no style — pass 'shape' (an OpenShapeType name, default Rectangular; "
            + "see list_opening_styles) and optionally 'endcapStyle'. Args: wallHandle, "
            + "distanceAlongWall | position, shape, width, height, rise, headHeight | sillHeight, "
            + "measureFrom, measureTo, flipHinge, flipSwing, endcapStyle, layer. One undo step; "
            + "rolls back entirely if the anchor fails.",
            mutating: true, CreateOpening);

        Reg("set_opening_size",
            "Resize an existing Door, Window or Opening by handle. Args: handle, width, height, rise, "
            + "headHeight, sillHeight, openPercent (doors/windows), leafWidth (doors/windows). Frame "
            + "width and depth are style properties and cannot be set on the instance. Returns before "
            + "and after values. The host wall is opened for write so ACA re-cuts it. One undo step.",
            mutating: true, SetOpeningSize);

        Reg("delete_opening",
            "Erase a Door, Window or Opening and its wall anchor cleanly, so the wall closes up. "
            + "Releases the anchor via Geo.ReleaseAnchor, erases the anchor object if it survives, "
            + "then erases the opening, with the host wall open for write. Args: handle. One undo step.",
            mutating: true, DeleteOpening);
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

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    private static double? DblOrNull(JsonElement e, string name) =>
        Has(e, name) ? Args.Dbl(e, name) : null;

    private static bool? BoolOrNull(JsonElement e, string name) =>
        Has(e, name) ? Args.BoolOpt(e, name, false) : null;

    private static (int Offset, int Limit) Paging(JsonElement a)
    {
        var limit = Args.IntOpt(a, "limit", 500);
        var offset = Args.IntOpt(a, "offset", 0);
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;
        if (offset < 0) offset = 0;
        return (offset, limit);
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
    // AEC getters throw for degenerate geometry and for styles that cannot be opened across a
    // broken xref. One bad property must never lose a whole listing, and the bridge drops nulls,
    // so a failed read simply vanishes from the JSON.

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

    private static string ClassName(ObjectId id)
    {
        try { return id.ObjectClass?.Name ?? "Unknown"; }
        catch (System.Exception) { return "Unknown"; }
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
        catch (System.Exception) { }
        return list;
    }

    // ------------------------------------------------------------------------------- shared reads

    /// <summary>Handle of the wall an opening is anchored to, read back through its live anchor.</summary>
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
        catch (System.Exception) { }
        return null;
    }

    private static string TypeNameOf(DBObject obj, ObjectId id) => obj switch
    {
        AecDoor => "Door",
        AecWindow => "Window",
        AecOpening => "Opening",
        AecOpenFiller => "OpenFiller",
        AecOpeningBase => "OpeningBase",
        AecWall => "Wall",
        _ => ClassName(id),
    };

    /// <summary>The full read-back of an opening, used as the result of every mutating tool.</summary>
    private static object OpeningReport(AcadContext ctx, AecOpeningBase o)
    {
        var filler = o as AecOpenFiller;
        string? styleName = null;
        try
        {
            if (!o.StyleId.IsNull)
                styleName = (ctx.Tr.GetObject(o.StyleId, OpenMode.ForRead) as AecRecord)?.Name;
        }
        catch (System.Exception) { }

        return new
        {
            handle = TryS(() => o.Handle.ToString()),
            type = TypeNameOf(o, o.ObjectId),
            objectClass = ClassName(o.ObjectId),
            layer = TryS(() => o.Layer),
            styleName,
            styleHandle = TryS(() => o.StyleId.IsNull ? null : o.StyleId.Handle.ToString()),
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
            isAnchored = TryB(() => o.IsAnchored),
            isAnchoredToWall = TryB(() => o.IsAnchoredToWall),
            anchorHandle = TryS(() => o.AnchorId.IsNull ? null : o.AnchorId.Handle.ToString()),
            hostWallHandle = HostHandle(ctx, o),
            location = TryPt(() => o.Location),
            rotation = TryD(() => o.Rotation),
            endcapStyle = TryS(() => o.EndcapStyleName),
            openPercent = filler is null ? null : TryD(() => filler.OpenPercent),
            leafWidth = filler is null ? null : TryD(() => filler.LeafWidth),
            frameWidth = filler is null ? null : TryD(() => filler.FrameWidth),
            frameDepth = filler is null ? null : TryD(() => filler.FrameDepth),
        };
    }

    /// <summary>The anchor's own parameters, so a caller can see where ACA actually put the opening.</summary>
    private static object? AnchorReport(AcadContext ctx, AecGeo geo)
    {
        try
        {
            if (geo.AnchorId.IsNull) return null;
            if (ctx.Tr.GetObject(geo.AnchorId, OpenMode.ForRead) is not AecAnchorCurve anchor) return null;

            return new
            {
                handle = TryS(() => anchor.Handle.ToString()),
                objectClass = ClassName(anchor.ObjectId),
                wallHandle = TryS(() => anchor.CurveId.IsNull ? null : anchor.CurveId.Handle.ToString()),
                referenceCount = TryI(() => anchor.ReferenceObjectCount),
                distanceAlongWall = TryD(() => anchor.AnchorX.OffsetDistance),
                measureFrom = TryS(() => anchor.AnchorX.OffsetType.ToString()),
                measureTo = TryS(() => anchor.AnchorX.MeasureToType.ToString()),
                offsetAcrossWall = TryD(() => anchor.AnchorY.OffsetDistance),
                offsetFromBase = TryD(() => anchor.AnchorZ.OffsetDistance),
                flipHinge = TryB(() => anchor.FlipX),
                flipSwing = TryB(() => anchor.FlipY),
                flipVertical = TryB(() => anchor.FlipZ),
                rotation = TryD(() => anchor.Rotation),
            };
        }
        catch (System.Exception) { return null; }
    }

    // ------------------------------------------------------------------------------ style lookup

    /// <summary>
    /// A named style, or the drawing's Standard one. <paramref name="standard"/> is a thunk, not an
    /// ObjectId, because DictionaryDoorStyle.GetStandardStyle CREATES the Standard style when the
    /// drawing has none — we do not want that side effect when the caller named a style.
    /// </summary>
    private static ObjectId ResolveStyleId(AcadContext ctx, AecDict dict, Func<ObjectId> standard,
                                           string? name, string kindLabel)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var standardId = standard();
            if (standardId.IsNull)
                throw new AcadMcpException("style_not_found",
                    $"This drawing has no Standard {kindLabel} style and one could not be created.",
                    $"Pass 'style' with a name from list_{kindLabel}_styles.");
            return standardId;
        }

        bool has;
        try { has = dict.Has(name, ctx.Tr); }
        catch (System.Exception) { has = false; }

        if (!has)
            throw new AcadMcpException("style_not_found",
                $"No {kindLabel} style named '{name}' in this drawing.",
                $"Call list_{kindLabel}_styles for the available names.");

        var id = dict.GetAt(name);
        if (id.IsNull)
            throw new AcadMcpException("style_not_found", $"{kindLabel} style '{name}' resolved to null.");

        return id;
    }

    /// <summary>
    /// The standard size list a door/window style carries, if it has one. Typed as the AEC
    /// DictionaryRecord rather than a DBObject: GetFromObject's parameter is an unqualified
    /// 'DBObject' in the declared API surface, and a DictionaryRecord satisfies either candidate.
    /// </summary>
    private static List<object>? StandardSizes(AcadContext ctx, AecRecord style)
    {
        try
        {
            var listId = AecStdSizeList.GetFromObject(style);
            if (listId.IsNull) return null;
            if (ctx.Tr.GetObject(listId, OpenMode.ForRead) is not AecStdSizeList list) return null;

            var sizes = new List<object>();
            foreach (AecStdSize size in list.Openings)
            {
                sizes.Add(new
                {
                    width = TryD(() => size.Width),
                    height = TryD(() => size.Height),
                    rise = TryD(() => size.Rise),
                    leaf = TryD(() => size.Leaf),
                });
            }
            return sizes;
        }
        catch (System.Exception) { return null; }
    }

    // --------------------------------------------------------------------------- positioning

    private static AecXOffset MeasureFrom(JsonElement a)
    {
        var s = Args.StrOpt(a, "measureFrom", "start") ?? "start";
        return s.Trim().ToLowerInvariant() switch
        {
            "" or "start" => AecXOffset.FromStartOfCurve,
            "end" => AecXOffset.FromEndOfCurve,
            "middle" or "mid" or "midpoint" or "center" or "centre" => AecXOffset.FromMidpointOfCurve,
            _ => throw new AcadMcpException("bad_argument",
                    $"'measureFrom' must be start, end or middle; got '{s}'."),
        };
    }

    private static AecXMeasure MeasureTo(JsonElement a)
    {
        var s = Args.StrOpt(a, "measureTo", "center") ?? "center";
        return s.Trim().ToLowerInvariant() switch
        {
            "" or "center" or "centre" or "middle" => AecXMeasure.ToCenter,
            "start" or "startedge" or "start_edge" => AecXMeasure.ToStartEdge,
            "end" or "endedge" or "end_edge" => AecXMeasure.ToEndEdge,
            _ => throw new AcadMcpException("bad_argument",
                    $"'measureTo' must be center, start or end; got '{s}'."),
        };
    }

    /// <summary>
    /// The distance along the wall baseline that the anchor's X offset wants. Either given directly
    /// as 'distanceAlongWall', or derived from an absolute WCS 'position' by projecting it onto the
    /// wall's own base curve — which measures along the arc on a curved wall, not across the chord.
    /// </summary>
    private static (double Distance, string Source) ResolveDistance(JsonElement a, AecWall wall)
    {
        if (DblOrNull(a, "distanceAlongWall") is double given)
            return (given, "distanceAlongWall");

        if (!Has(a, "position"))
            throw new AcadMcpException("bad_argument",
                "Give either 'distanceAlongWall' (a number measured along the wall baseline) or "
                + "'position' ([x,y] or [x,y,z], an absolute WCS point projected onto that baseline).");

        var pt = Args.Point(a, "position");

        // Preferred: the wall's real baseline, so a curved wall measures along its arc.
        try
        {
            var curve = wall.BaseCurve(false);
            if (curve is not null)
            {
                var onCurve = curve.GetClosestPointTo(pt);
                var interval = curve.GetInterval();
                var length = curve.GetLength(interval.LowerBound, onCurve.Parameter, 1e-9);
                if (!double.IsNaN(length) && !double.IsInfinity(length))
                    return (length, "position projected onto the wall base curve");
            }
        }
        catch (System.Exception) { }

        // Fallback: straight-line projection onto the start->end chord. Exact for a linear wall,
        // approximate for an arc — reported in the result so the caller knows which one was used.
        try
        {
            var start = wall.StartPoint;
            var chord = start.GetVectorTo(wall.EndPoint);
            var chordLength = chord.Length;
            if (chordLength > 1e-12)
                return (start.GetVectorTo(pt).DotProduct(chord.DivideBy(chordLength)),
                        "position projected onto the wall start-end chord (base curve unavailable)");
        }
        catch (System.Exception) { }

        throw new AcadMcpException("position_unresolved",
            "Could not project 'position' onto this wall's baseline.",
            "Pass 'distanceAlongWall' instead.");
    }

    // --------------------------------------------------------------------------- opening helpers

    private static AecWall OpenWall(AcadContext ctx, JsonElement a, OpenMode mode)
    {
        var handle = Args.Str(a, "wallHandle");
        var id = Acad.ToObjectId(ctx.Db, handle);

        if (ctx.Tr.GetObject(id, mode) is not AecWall wall)
            throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC Wall.",
                "Call list_walls for the wall handles in this drawing.");

        return wall;
    }

    private static AecOpeningBase OpenOpening(AcadContext ctx, JsonElement a, OpenMode mode)
    {
        var handle = Args.Str(a, "handle");
        var id = Acad.ToObjectId(ctx.Db, handle);

        if (ctx.Tr.GetObject(id, mode) is not AecOpeningBase opening)
            throw new AcadMcpException("wrong_type",
                $"Handle '{handle}' is a {ClassName(id)}, not an AEC Door, Window or Opening.",
                "WindowAssembly is anchored by a different class and is not handled by these tools.");

        return opening;
    }

    /// <summary>The block the wall itself lives in, so openings work in paper space and blocks too.</summary>
    private static BlockTableRecord OwnerBlock(AcadContext ctx, AecWall wall)
    {
        try
        {
            if (!wall.OwnerId.IsNull
                && ctx.Tr.GetObject(wall.OwnerId, OpenMode.ForWrite) is BlockTableRecord owner)
                return owner;
        }
        catch (System.Exception) { }

        return (BlockTableRecord)ctx.Tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db), OpenMode.ForWrite);
    }

    /// <summary>
    /// Validate the requested layer BEFORE anything is created, so a typo fails without having
    /// appended an object. Returns the name to apply later, or null when none was asked for.
    /// </summary>
    private static string? ValidateLayer(AcadContext ctx, JsonElement a)
    {
        var layer = Args.StrOpt(a, "layer");
        if (string.IsNullOrWhiteSpace(layer)) return null;

        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layer))
            throw new AcadMcpException("layer_not_found",
                $"No layer named '{layer}' in this drawing.",
                "Create the layer first, or omit 'layer' to let ACA's layer key standard place it.");

        return layer;
    }

    /// <summary>
    /// Apply the validated layer. Called only AFTER AppendEntity: Entity.Layer resolves the name
    /// through the owning database, so setting it on an object that is not database-resident yet
    /// throws eNoDatabase.
    /// </summary>
    private static void ApplyLayer(Entity entity, string? layer)
    {
        if (!string.IsNullOrWhiteSpace(layer)) entity.Layer = layer;
    }

    /// <summary>Width / height / rise. Plain size only — vertical position needs the anchor.</summary>
    private static void ApplyDimensions(JsonElement a, AecOpeningBase opening)
    {
        if (DblOrNull(a, "width") is double width)
        {
            if (width <= 0) throw new AcadMcpException("bad_argument", "'width' must be greater than zero.");
            opening.Width = width;
        }

        if (DblOrNull(a, "height") is double height)
        {
            if (height <= 0) throw new AcadMcpException("bad_argument", "'height' must be greater than zero.");
            opening.Height = height;
        }

        if (DblOrNull(a, "rise") is double rise) opening.Rise = rise;
    }

    /// <summary>
    /// Sill / head height. MUST run after the opening is anchored: SillHeadHeightUtility works
    /// through the wall anchor's Z offset (its GetVerticalAlignment returns a CurveZOffsetToType),
    /// so on an unanchored opening it has nothing to write to.
    /// </summary>
    private static void ApplyVerticalPosition(JsonElement a, AecOpeningBase opening, List<string> warnings)
    {
        var sill = DblOrNull(a, "sillHeight");
        var head = DblOrNull(a, "headHeight");

        if (sill is not null && head is not null)
            warnings.Add("Both 'sillHeight' and 'headHeight' were given; head height was applied last "
                         + "and wins, because ACA derives the sill from head minus height.");

        if (sill is double sillValue)
        {
            try { AecSillHead.SetSillHeight(opening, sillValue); }
            catch (System.Exception ex) { warnings.Add("Could not set sill height: " + ex.Message); }
        }

        if (head is double headValue)
        {
            try { AecSillHead.SetHeadHeight(opening, headValue); }
            catch (System.Exception ex) { warnings.Add("Could not set head height: " + ex.Message); }
        }
    }

    /// <summary>
    /// A zero-sized opening is an invalid object, and DoorStyle/WindowStyle carry no default width
    /// or height in the declared API surface — only the ACA drawing variables might, and only if
    /// SetToStandard picked them up. Refuse to commit one either way.
    /// </summary>
    private static void RequireRealSize(AecOpeningBase opening)
    {
        var width = TryD(() => opening.Width) ?? 0.0;
        var height = TryD(() => opening.Height) ?? 0.0;

        if (width > 1e-9 && height > 1e-9) return;

        throw new AcadMcpException("bad_argument",
            $"The new opening would be {width} wide by {height} high, which is not a valid object. "
            + "AEC door and window styles carry no default size, so pass 'width' and 'height'.",
            "The transaction was rolled back — nothing was written to the drawing.");
    }

    private static void ApplyFillerOptions(JsonElement a, AecOpeningBase opening, List<string> warnings)
    {
        if (opening is not AecOpenFiller filler)
        {
            if (Has(a, "openPercent") || Has(a, "leafWidth"))
                warnings.Add("'openPercent' and 'leafWidth' only apply to doors and windows; ignored.");
            return;
        }

        if (DblOrNull(a, "openPercent") is double open)
        {
            try { filler.OpenPercent = open; }
            catch (System.Exception ex) { warnings.Add("Could not set openPercent: " + ex.Message); }
        }

        if (DblOrNull(a, "leafWidth") is double leaf)
        {
            try { filler.LeafWidth = leaf; }
            catch (System.Exception ex) { warnings.Add("Could not set leafWidth: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------------------- THE ANCHOR

    /// <summary>
    /// Anchor a freshly appended opening into its wall, then prove it took. Every failure path
    /// throws, which skips Acad.Write's commit and rolls the whole transaction back — so the
    /// drawing never keeps an opening that is not properly cutting its wall.
    /// </summary>
    private static AecAnchorCurve AnchorToWall(AcadContext ctx, JsonElement a, AecOpeningBase opening,
                                     ObjectId wallId, double distance,
                                     AecXOffset measureFrom, AecXMeasure measureTo,
                                     List<string> warnings)
    {
        // THE LOAD-BEARING STEP. The target wall must be open ForWrite in THIS transaction before
        // SetAnchor, or the anchoring silently no-ops — SetAnchor returns without throwing and
        // AnchorId is simply never populated. Holding only the ObjectId is not enough.
        // Behaviour cross-checked against ACA's shipped "Create House.cs" sample and verified in
        // a live session; no code from that sample is reproduced here.
        var hostWall = ctx.Tr.GetObject(wallId, OpenMode.ForWrite);
        if (hostWall is not AecWall)
            throw new AcadMcpException("not_a_wall",
                $"Handle '{wallId.Handle}' is a {ClassName(wallId)}, not a Wall.");

        var anchor = new AecOpeningAnchor();
        var transferred = false;
        try
        {
            // SetToStandard + SubSetDatabaseDefaults come from Aec DBObject and are what give the
            // anchor a valid, database-consistent starting state. Reached through a type test
            // rather than directly: the declared API surface gives Anchor's base as a bare 'DBObject',
            // which is ambiguous between the Aec and AutoCAD types, and this compiles either way.
            // Individually guarded: an Anchor carries no style, so SetToStandard may legitimately
            // refuse. That must not abort an otherwise sound creation.
            if (anchor is AecDbObj aecAnchor)
            {
                try { aecAnchor.SetToStandard(ctx.Db); }
                catch (System.Exception ex) { warnings.Add("Anchor.SetToStandard failed: " + ex.Message); }

                try { aecAnchor.SubSetDatabaseDefaults(ctx.Db); }
                catch (System.Exception ex)
                {
                    warnings.Add("Anchor.SubSetDatabaseDefaults failed: " + ex.Message);
                }
            }
            else
            {
                warnings.Add("The anchor is not an Aec DBObject, so SetToStandard and "
                             + "SubSetDatabaseDefaults were not applied to it.");
            }

            // ORDER IS CRITICAL AND COUNTER-INTUITIVE.
            //
            // SetAnchor FIRST: it takes ownership of the anchor and adds it to the database itself
            // (AecBaseMgd.xml documents it as "Takes ownership of a new Anchor object and adds it
            // to the database"). Do NOT call Tr.AddNewlyCreatedDBObject on the anchor — SetAnchor
            // has already done the add, and the extra call throws eNotInDatabase.
            opening.SetAnchor(anchor);
            transferred = true;

            // CurveId AFTER SetAnchor. Assigning it beforehand — on an anchor that is not yet
            // database-resident — silently fails to bind, which is what made every earlier
            // attempt produce an unanchored opening.
            try { anchor.CurveId = wallId; }
            catch (System.Exception ex)
            {
                throw new AcadMcpException("anchor_failed",
                    "The anchor was created but could not be bound to the wall.", ex.Message);
            }
        }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("anchor_failed",
                "Could not attach an AnchorOpeningBaseToWall to the new opening; "
                + "the whole operation was rolled back and nothing was written to the drawing.",
                ex.Message);
        }
        finally
        {
            // Only ours to dispose while SetAnchor has not taken it.
            if (!transferred)
            {
                try { anchor.Dispose(); } catch (System.Exception) { }
            }
        }

        // Geo.AnchorId reads NULL until this transaction commits, even when the anchoring has
        // succeeded — verified live. So it cannot serve as a success test here, and an earlier
        // version of this code rejected perfectly good doors because of it. The anchor instance
        // we already hold is the live object; callers confirm by re-reading after the commit
        // (list_doors reports isAnchoredToWall and the wall reports GetOpeningsFor()).
        var live = anchor;

        try
        {
            if (live.CurveId.IsNull) live.CurveId = wallId;
        }
        catch (System.Exception ex) { warnings.Add("Could not set the anchor's CurveId: " + ex.Message); }

        // CurveId normally registers the reference itself; if it did not, do it explicitly. The
        // opening is owned by the wall, hence RelationType.OwnedBy.
        var references = TryI(() => live.ReferenceObjectCount) ?? 0;
        if (references == 0)
        {
            try { live.SetSingleReference(wallId, AecRelation.OwnedBy); }
            catch (System.Exception ex)
            {
                warnings.Add("AnchorToReference.SetSingleReference failed: " + ex.Message);
            }
        }

        // Position along the wall baseline. The three writes go through ONE AnchorX instance (in
        // case the property hands back a fresh wrapper each call); the verification then reads a
        // FRESH live.AnchorX, so a wrapper that does not write through is caught, not hidden.
        try
        {
            var ax = live.AnchorX;
            ax.OffsetType = measureFrom;
            ax.MeasureToType = measureTo;
            ax.OffsetDistance = distance;

            var applied = TryD(() => live.AnchorX.OffsetDistance);
            if (applied is null || Math.Abs(applied.Value - distance) > 1e-6 + 1e-6 * Math.Abs(distance))
                warnings.Add($"Requested distance along the wall was {distance}, but the anchor reports "
                             + $"{(applied is null ? "nothing readable" : applied.Value.ToString())} — "
                             + "ACA may have clamped it to the wall's length.");
        }
        catch (System.Exception ex)
        {
            warnings.Add("Could not set the along-wall offset: " + ex.Message
                         + " — the opening is anchored but sits at the anchor's default position.");
        }

        // Centre it in the wall thickness; anything else is a deliberate offset we do not expose.
        try
        {
            var ay = live.AnchorY;
            ay.OffsetType = AecYOffset.FromCenterCurve;
            ay.MeasureToType = AecYMeasure.ToCenter;
            ay.OffsetDistance = 0.0;
        }
        catch (System.Exception) { }

        // Hinge jamb and swing face. There is no Door.SwingAngle in the API — this is the real one.
        if (BoolOrNull(a, "flipHinge") is bool flipHinge)
        {
            try { live.FlipX = flipHinge; }
            catch (System.Exception ex) { warnings.Add("Could not set flipHinge: " + ex.Message); }
        }

        if (BoolOrNull(a, "flipSwing") is bool flipSwing)
        {
            try { live.FlipY = flipSwing; }
            catch (System.Exception ex) { warnings.Add("Could not set flipSwing: " + ex.Message); }
        }

        return live;
    }

    /// <summary>
    /// The gate. Throwing here aborts the transaction, so a wall is never left with an opening
    /// that does not cut it.
    ///
    /// It deliberately checks the ANCHOR's CurveId rather than OpeningBase.IsAnchoredToWall or
    /// Geo.AnchorId. Both of those read "not anchored" until the transaction commits, even on a
    /// fully successful anchoring — verified live — so using either as the gate rejects every
    /// valid door. CurveId is set directly by us and is readable immediately, which makes it the
    /// one meaningful pre-commit assertion available.
    /// </summary>
    private static void VerifyAnchored(AcadContext ctx, AecAnchorCurve anchor, ObjectId wallId)
    {
        ObjectId bound;
        try { bound = anchor.CurveId; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("anchor_failed",
                "The opening's anchor could not be read back, so the anchoring cannot be confirmed. "
                + "The whole transaction was rolled back — nothing was added to the drawing.",
                ex.Message);
        }

        if (bound.IsNull)
            throw new AcadMcpException("anchor_failed",
                "The opening's anchor is not bound to any wall, so it would not cut one. "
                + "The whole transaction was rolled back — nothing was added to the drawing.",
                "AnchorEntityToCurve.CurveId was still null after Geo.SetAnchor.");

        if (bound != wallId)
            throw new AcadMcpException("anchor_failed",
                $"The opening anchored to handle '{TryS(() => bound.Handle.ToString())}' instead of "
                + $"the requested wall '{TryS(() => wallId.Handle.ToString())}'. "
                + "The whole transaction was rolled back.");
    }

    // ----------------------------------------------------------------------------- style listings

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListDoorStyles(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeSizes = Args.BoolOpt(a, "includeSizes", false);

        var dict = new AecDoorDict(ctx.Db);
        var rows = new List<(string Key, object Data)>();

        ObjectIdCollection records;
        try { records = dict.Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                "This drawing has no door style dictionary.", ex.Message);
        }

        foreach (ObjectId id in records)
        {
            if (id.IsNull || id.IsErased) continue;

            AecDoorStyle? style;
            try { style = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecDoorStyle; }
            catch (System.Exception) { continue; }
            if (style is null) continue;

            var name = TryS(() => style.Name) ?? id.Handle.ToString();
            rows.Add((name, new
            {
                name,
                handle = id.Handle.ToString(),
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
                hasCustomShape = TryB(() => !style.CustomShapeId.IsNull),
                standardSizes = includeSizes ? StandardSizes(ctx, style) : null,
            }));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListWindowStyles(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var includeSizes = Args.BoolOpt(a, "includeSizes", false);

        var dict = new AecWindowDict(ctx.Db);
        var rows = new List<(string Key, object Data)>();

        ObjectIdCollection records;
        try { records = dict.Records; }
        catch (System.Exception ex)
        {
            throw new AcadMcpException("no_style_dictionary",
                "This drawing has no window style dictionary.", ex.Message);
        }

        foreach (ObjectId id in records)
        {
            if (id.IsNull || id.IsErased) continue;

            AecWindowStyle? style;
            try { style = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecWindowStyle; }
            catch (System.Exception) { continue; }
            if (style is null) continue;

            var name = TryS(() => style.Name) ?? id.Handle.ToString();
            rows.Add((name, new
            {
                name,
                handle = id.Handle.ToString(),
                description = TryS(() => style.Description),
                windowType = TryS(() => style.WindowType.ToString()),
                shape = TryS(() => style.WindowShape.ToString()),
                frameWidth = TryD(() => style.FrameWidth),
                frameDepth = TryD(() => style.FrameDepth),
                sashWidth = TryD(() => style.SashWidth),
                sashDepth = TryD(() => style.SashDepth),
                glassThickness = TryD(() => style.GlassThickness),
                useDepthOfWall = TryB(() => style.UseDepthOfWall),
                hasCustomShape = TryB(() => !style.CustomShapeId.IsNull),
                standardSizes = includeSizes ? StandardSizes(ctx, style) : null,
            }));
        }

        return Page(rows, offset, limit);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> ListOpeningStyles(JsonElement a) => Acad.Read<object?>(ctx =>
    {
        var (offset, limit) = Paging(a);
        var rows = new List<(string Key, object Data)>();

        try
        {
            var dict = new AecEndcapDict(ctx.Db);
            foreach (ObjectId id in dict.Records)
            {
                if (id.IsNull || id.IsErased) continue;

                AecEndcapStyle? style;
                try { style = ctx.Tr.GetObject(id, OpenMode.ForRead) as AecEndcapStyle; }
                catch (System.Exception) { continue; }
                if (style is null) continue;

                var name = TryS(() => style.Name) ?? id.Handle.ToString();
                rows.Add((name, new
                {
                    name,
                    handle = id.Handle.ToString(),
                    description = TryS(() => style.Description),
                    capCount = TryI(() => style.CapCount),
                    kind = "OpeningEndcapStyle",
                }));
            }
        }
        catch (System.Exception) { /* a drawing with no endcap dictionary just yields no rows */ }

        // Paged the same shape as every other list tool, with the shape enum alongside it.
        var ordered = rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                          .Select(r => r.Data)
                          .ToList();
        var slice = ordered.Skip(offset).Take(limit).ToList();

        var available = new List<string>();
        try { available = Strings(AecArchUtility.GetAvailableOpeningEndcapStyleNames(ctx.Db)); }
        catch (System.Exception) { }

        return new
        {
            items = slice,
            total = ordered.Count,
            offset,
            limit,
            truncated = offset + slice.Count < ordered.Count,
            note = "AutoCAD Architecture has no Opening style dictionary — a plain wall opening is "
                 + "defined by its OpenShapeType and optionally an opening endcap style. 'items' are "
                 + "the endcap styles; pass one of 'shapeTypes' as create_opening's 'shape' and an "
                 + "endcap name as 'endcapStyle'.",
            shapeTypes = Enum.GetNames(typeof(AecShapeType)).ToList(),
            availableEndcapStyleNames = available,
        };
    });

    // ---------------------------------------------------------------------------------- creation

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateDoor(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var warnings = new List<string>();

        var wall = OpenWall(ctx, a, OpenMode.ForWrite);
        var wallId = wall.ObjectId;
        var (distance, source) = ResolveDistance(a, wall);

        // Validate the positioning enums before anything is created, so a typo fails clean.
        var measureFrom = MeasureFrom(a);
        var measureTo = MeasureTo(a);

        var layer = ValidateLayer(ctx, a);

        var styleId = ResolveStyleId(ctx, new AecDoorDict(ctx.Db),
                                     () => AecDoorDict.GetStandardStyle(ctx.Db),
                                     Args.StrOpt(a, "style"), "door");

        var door = new AecDoor();
        try
        {
            door.SetDatabaseDefaults(ctx.Db);
            try { door.SetToStandard(ctx.Db); }
            catch (System.Exception ex) { warnings.Add("Door.SetToStandard failed: " + ex.Message); }
            door.StyleId = styleId;
        }
        catch (System.Exception)
        {
            try { door.Dispose(); } catch (System.Exception) { }
            throw;
        }

        var block = OwnerBlock(ctx, wall);
        block.AppendEntity(door);
        ctx.Tr.AddNewlyCreatedDBObject(door, true);

        ApplyLayer(door, layer);
        ApplyDimensions(a, door);
        RequireRealSize(door);
        var doorAnchor = AnchorToWall(ctx, a, door, wallId, distance, measureFrom, measureTo, warnings);
        ApplyVerticalPosition(a, door, warnings);
        ApplyFillerOptions(a, door, warnings);
        VerifyAnchored(ctx, doorAnchor, wallId);

        return new
        {
            created = true,
            positionedBy = source,
            requestedDistanceAlongWall = distance,
            wallHandle = TryS(() => wallId.Handle.ToString()),
            opening = OpeningReport(ctx, door),
            anchor = AnchorReport(ctx, door),
            warnings,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateWindow(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var warnings = new List<string>();

        var wall = OpenWall(ctx, a, OpenMode.ForWrite);
        var wallId = wall.ObjectId;
        var (distance, source) = ResolveDistance(a, wall);

        // Validate the positioning enums before anything is created, so a typo fails clean.
        var measureFrom = MeasureFrom(a);
        var measureTo = MeasureTo(a);

        var layer = ValidateLayer(ctx, a);

        var styleId = ResolveStyleId(ctx, new AecWindowDict(ctx.Db),
                                     () => AecWindowDict.GetStandardStyle(ctx.Db),
                                     Args.StrOpt(a, "style"), "window");

        var window = new AecWindow();
        try
        {
            window.SetDatabaseDefaults(ctx.Db);
            try { window.SetToStandard(ctx.Db); }
            catch (System.Exception ex) { warnings.Add("Window.SetToStandard failed: " + ex.Message); }
            window.StyleId = styleId;
        }
        catch (System.Exception)
        {
            try { window.Dispose(); } catch (System.Exception) { }
            throw;
        }

        var block = OwnerBlock(ctx, wall);
        block.AppendEntity(window);
        ctx.Tr.AddNewlyCreatedDBObject(window, true);

        ApplyLayer(window, layer);
        ApplyDimensions(a, window);
        RequireRealSize(window);
        var windowAnchor = AnchorToWall(ctx, a, window, wallId, distance, measureFrom, measureTo, warnings);
        ApplyVerticalPosition(a, window, warnings);
        ApplyFillerOptions(a, window, warnings);
        VerifyAnchored(ctx, windowAnchor, wallId);

        return new
        {
            created = true,
            positionedBy = source,
            requestedDistanceAlongWall = distance,
            wallHandle = TryS(() => wallId.Handle.ToString()),
            opening = OpeningReport(ctx, window),
            anchor = AnchorReport(ctx, window),
            warnings,
        };
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> CreateOpening(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var warnings = new List<string>();

        var wall = OpenWall(ctx, a, OpenMode.ForWrite);
        var wallId = wall.ObjectId;
        var (distance, source) = ResolveDistance(a, wall);

        // Validate the positioning enums before anything is created, so a typo fails clean.
        var measureFrom = MeasureFrom(a);
        var measureTo = MeasureTo(a);

        var shapeName = Args.StrOpt(a, "shape", "Rectangular") ?? "Rectangular";
        // TryParse also accepts bare numbers, so IsDefined has to back it up.
        if (!Enum.TryParse<AecShapeType>(shapeName.Trim(), true, out var shape)
            || !Enum.IsDefined(typeof(AecShapeType), shape))
            throw new AcadMcpException("bad_argument",
                $"'{shapeName}' is not a valid opening shape.",
                "Valid values: " + string.Join(", ", Enum.GetNames(typeof(AecShapeType))));

        if (shape == AecShapeType.Custom)
            throw new AcadMcpException("unsupported",
                "The Custom shape needs an AEC profile assigned to Opening.CustomShapeId, which these "
                + "tools do not create.",
                "Pick one of the built-in shapes from list_opening_styles.");

        var layer = ValidateLayer(ctx, a);

        var opening = new AecOpening();
        try
        {
            opening.SetDatabaseDefaults(ctx.Db);
            // An Opening has no style dictionary in ACA (the declared API surface has DictionaryDoorStyle,
            // DictionaryWindowStyle and DictionaryOpeningEndcapStyle, but no opening-style
            // dictionary at all), so SetToStandard is best-effort here: it is the call that gives
            // the object its database defaults, not a style assignment. A styleless Opening is the
            // correct, valid object — Aec Entity.StyleId stays null by design.
            try { opening.SetToStandard(ctx.Db); }
            catch (System.Exception ex) { warnings.Add("Opening.SetToStandard failed: " + ex.Message); }
            opening.ShapeType = shape;
        }
        catch (System.Exception)
        {
            try { opening.Dispose(); } catch (System.Exception) { }
            throw;
        }

        var block = OwnerBlock(ctx, wall);
        block.AppendEntity(opening);
        ctx.Tr.AddNewlyCreatedDBObject(opening, true);

        ApplyLayer(opening, layer);
        ApplyDimensions(a, opening);
        RequireRealSize(opening);

        var openingAnchor = AnchorToWall(ctx, a, opening, wallId, distance, measureFrom, measureTo, warnings);
        ApplyVerticalPosition(a, opening, warnings);

        // After the anchor: the endcap an opening actually uses lives on
        // AnchorOpeningBaseToWall.OpeningEndcapStyleId, which OpeningBase.EndcapStyleName writes
        // through — so there is nothing to write to until the anchor exists.
        var endcap = Args.StrOpt(a, "endcapStyle");
        if (!string.IsNullOrWhiteSpace(endcap))
        {
            try { opening.EndcapStyleName = endcap; }
            catch (System.Exception ex)
            {
                warnings.Add($"Could not apply endcap style '{endcap}': " + ex.Message);
            }
        }

        VerifyAnchored(ctx, openingAnchor, wallId);

        return new
        {
            created = true,
            shape = shape.ToString(),
            positionedBy = source,
            requestedDistanceAlongWall = distance,
            wallHandle = TryS(() => wallId.Handle.ToString()),
            opening = OpeningReport(ctx, opening),
            anchor = AnchorReport(ctx, opening),
            warnings,
        };
    });

    // -------------------------------------------------------------------------------- size edits

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> SetOpeningSize(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var warnings = new List<string>();

        if (!Has(a, "width") && !Has(a, "height") && !Has(a, "rise")
            && !Has(a, "headHeight") && !Has(a, "sillHeight")
            && !Has(a, "openPercent") && !Has(a, "leafWidth"))
            throw new AcadMcpException("bad_argument",
                "Give at least one of width, height, rise, headHeight, sillHeight, openPercent or leafWidth.");

        var opening = OpenOpening(ctx, a, OpenMode.ForWrite);
        var before = OpeningReport(ctx, opening);

        // Open the host wall for write so ACA re-cuts it inside this same undo step.
        var hostHandle = HostHandle(ctx, opening);
        if (hostHandle is not null)
        {
            try { ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, hostHandle), OpenMode.ForWrite); }
            catch (System.Exception ex) { warnings.Add("Could not open the host wall for write: " + ex.Message); }
        }
        else
        {
            warnings.Add("This opening is not anchored to a wall, so nothing was re-cut.");
        }

        ApplyDimensions(a, opening);
        ApplyVerticalPosition(a, opening, warnings);
        ApplyFillerOptions(a, opening, warnings);

        return new
        {
            updated = true,
            hostWallHandle = hostHandle,
            before,
            after = OpeningReport(ctx, opening),
            warnings,
        };
    });

    // ----------------------------------------------------------------------------------- deletion

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> DeleteOpening(JsonElement a) => Acad.Write<object?>(ctx =>
    {
        var warnings = new List<string>();

        var opening = OpenOpening(ctx, a, OpenMode.ForWrite);
        var report = OpeningReport(ctx, opening);
        var hostHandle = HostHandle(ctx, opening);

        var anchorId = ObjectId.Null;
        try { anchorId = opening.AnchorId; }
        catch (System.Exception) { anchorId = ObjectId.Null; }

        // Open the host wall for write first so it re-cleans as the opening goes away.
        if (hostHandle is not null)
        {
            try { ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, hostHandle), OpenMode.ForWrite); }
            catch (System.Exception ex) { warnings.Add("Could not open the host wall for write: " + ex.Message); }
        }

        var anchorReleased = false;
        if (!anchorId.IsNull)
        {
            try { opening.ReleaseAnchor(); anchorReleased = true; }
            catch (System.Exception ex) { warnings.Add("Geo.ReleaseAnchor failed: " + ex.Message); }
        }

        // ReleaseAnchor usually disposes of the anchor object; erase it only if it survived AND the
        // release actually succeeded. If ReleaseAnchor failed the opening still owns and references
        // the anchor — erasing it out from under a live owner is exactly the kind of dangling
        // reference that leaves a drawing inconsistent, so in that case it is left alone and
        // opening.Erase() below takes the anchor with it the way ACA intends.
        var anchorErased = false;
        if (!anchorId.IsNull && anchorReleased)
        {
            try
            {
                var anchorObj = ctx.Tr.GetObject(anchorId, OpenMode.ForWrite, openErased: true);
                if (!anchorObj.IsErased) { anchorObj.Erase(); anchorErased = true; }
            }
            catch (System.Exception ex)
            {
                warnings.Add("Could not erase the anchor object: " + ex.Message);
            }
        }

        opening.Erase();

        var erased = TryB(() => opening.IsErased) ?? false;
        if (!erased)
            throw new AcadMcpException("delete_failed",
                "The opening did not report itself erased, so the transaction was rolled back.");

        return new
        {
            deleted = true,
            hostWallHandle = hostHandle,
            anchorHandle = anchorId.IsNull ? null : TryS(() => anchorId.Handle.ToString()),
            anchorReleased,
            anchorErased,
            erasedOpening = report,
            warnings,
        };
    });

#else

    // Compiled on a machine without the ACA reference assemblies. Reg() always throws
    // "aca_unavailable" before reaching any of these, so they exist only to satisfy the compiler
    // and keep the tool names in the published surface.

    private static Task<object?> ListDoorStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListWindowStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> ListOpeningStyles(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateDoor(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateWindow(JsonElement a) => throw Unavailable();
    private static Task<object?> CreateOpening(JsonElement a) => throw Unavailable();
    private static Task<object?> SetOpeningSize(JsonElement a) => throw Unavailable();
    private static Task<object?> DeleteOpening(JsonElement a) => throw Unavailable();

    private static AcadMcpException Unavailable() =>
        new("aca_unavailable",
            "This build of the add-in was compiled without the AutoCAD Architecture assemblies.",
            "Rebuild on a machine with AutoCAD Architecture installed to enable the AEC tools.");

#endif
}
