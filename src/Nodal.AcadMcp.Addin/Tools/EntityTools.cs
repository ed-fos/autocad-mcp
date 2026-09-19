using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// The entity surface: query, inspect, create, modify, transform and erase.
///
/// Every entity is addressed by its Handle hex string, never an ObjectId — handles are stable
/// across sessions and survive a round trip through JSON, ObjectIds do not.
/// </summary>
internal static class EntityTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;
    private const int MaxVerticesReported = 2000;
    private const int SummaryTextLength = 60;

    internal static void Register()
    {
        RegisterQuery();
        RegisterCreators();
        RegisterEditors();
    }

    // ---------------------------------------------------------------- query

    private static void RegisterQuery()
    {
        Router.Register("query_entities",
            "Entities in the drawing, filtered by layer(s) ('layer' or 'layers'), DXF type(s) " +
            "('type' or 'types': LINE, LWPOLYLINE, CIRCLE, ARC, TEXT, MTEXT, INSERT, POINT, ...) and " +
            "'space' (model|paper|current|all, default model). Paginated with 'limit' (default 500, " +
            "max 5000) and 'offset'; returns { items, total, offset, limit, truncated }.",
            mutating: false,
            args => Acad.Read<object?>(ctx =>
            {
                var layers = NameSet(args, "layer", "layers");
                var types = NameSet(args, "type", "types");
                var (limit, offset) = Page(args);
                var spaces = QuerySpaces(ctx, Args.StrOpt(args, "space", "model")!);

                var items = new List<object>();
                var total = 0;

                foreach (var space in spaces)
                {
                    var btr = (BlockTableRecord)ctx.Tr.GetObject(space.Id, OpenMode.ForRead);

                    foreach (ObjectId id in btr)
                    {
                        // The DXF-name test needs no open, so a type-only query on a huge drawing
                        // never pages 200k objects into the transaction.
                        if (!TypeMatches(id, types)) continue;

                        Entity? ent = null;
                        if (layers.Count > 0)
                        {
                            ent = ctx.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent is null || !layers.Contains(ent.Layer)) continue;
                        }

                        var index = total++;
                        if (index < offset || items.Count >= limit) continue;

                        ent ??= ctx.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent is not null) items.Add(Brief(ent, space.Name));
                    }
                }

                return new
                {
                    items,
                    total,
                    offset,
                    limit,
                    truncated = total > offset + items.Count,
                    spaces = spaces.Select(s => s.Name).ToList(),
                };
            }));

        Router.Register("get_entity",
            "Full property dump for one entity by 'handle', including type-specific geometry: Line " +
            "start/end/length, Circle centre/radius, Arc angles, LWPolyline vertices, DBText/MText " +
            "contents, BlockReference name/position/rotation/scale and its attributes.",
            mutating: false,
            args => Acad.Read<object?>(ctx =>
            {
                var ent = OpenEntity(ctx, Args.Str(args, "handle"), OpenMode.ForRead);
                return Full(ctx, ent);
            }));
    }

    // ------------------------------------------------------------- creation

    private static void RegisterCreators()
    {
        // Every creator accepts optional layer, colorIndex, linetype, linetypeScale and
        // space (model|paper|current, default current).
        Router.Register("create_line",
            "Draw a line. Args: start [x,y(,z)], end [x,y(,z)]; optional layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx => Fresh(ctx, new Line(Args.Point(args, "start"), Args.Point(args, "end")))));

        Router.Register("create_circle",
            "Draw a circle. Args: center [x,y(,z)], radius; optional layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx =>
            {
                var radius = Args.Dbl(args, "radius");
                if (radius <= 0)
                    throw new AcadMcpException("bad_argument", $"'radius' must be greater than zero; got {radius}.");
                return Fresh(ctx, new Circle(Args.Point(args, "center"), Vector3d.ZAxis, radius));
            }));

        Router.Register("create_arc",
            "Draw an arc counter-clockwise from startAngle to endAngle (DEGREES). Args: center, radius, " +
            "startAngle, endAngle; optional layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx =>
            {
                var radius = Args.Dbl(args, "radius");
                if (radius <= 0)
                    throw new AcadMcpException("bad_argument", $"'radius' must be greater than zero; got {radius}.");

                return Fresh(ctx, new Arc(
                    Args.Point(args, "center"),
                    radius,
                    Rad(Args.Dbl(args, "startAngle")),
                    Rad(Args.Dbl(args, "endAngle"))));
            }));

        Router.Register("create_polyline",
            "Draw an LWPOLYLINE. Args: vertices [[x,y],...] (2 or more); optional closed (bool), " +
            "bulges [b,...] aligned to vertices, elevation, layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx =>
            {
                var pts = Vertices(args, "vertices");
                if (pts.Count < 2)
                    throw new AcadMcpException("bad_argument", $"A polyline needs at least 2 vertices; got {pts.Count}.");

                var bulges = Doubles(args, "bulges");
                var pl = Fresh(ctx, new Polyline(pts.Count));

                for (var i = 0; i < pts.Count; i++)
                    pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), i < bulges.Length ? bulges[i] : 0.0, 0.0, 0.0);

                pl.Closed = Args.BoolOpt(args, "closed", false);
                pl.Elevation = Args.DblOpt(args, "elevation", pts[0].Z);
                return pl;
            }));

        Router.Register("create_text",
            "Place single-line text (DBText). Args: text, position [x,y(,z)]; optional height (default " +
            "TEXTSIZE), rotation (degrees), textStyle, layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx =>
            {
                var t = Fresh(ctx, new DBText());
                t.TextString = Args.Str(args, "text");
                t.Position = Args.Point(args, "position");
                t.Height = TextHeight(ctx, args);

                var rotation = Args.DblOpt(args, "rotation", 0.0);
                if (rotation != 0.0) t.Rotation = Rad(rotation);

                var style = Args.StrOpt(args, "textStyle");
                if (!string.IsNullOrWhiteSpace(style)) t.TextStyleId = RequireTextStyle(ctx, style!);

                return t;
            }));

        Router.Register("create_mtext",
            "Place multiline text (MTEXT). Args: text, position [x,y(,z)]; optional width (0 = unbounded), " +
            "height (default TEXTSIZE), rotation (degrees), textStyle, layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx =>
            {
                var m = Fresh(ctx, new MText());
                m.Contents = Args.Str(args, "text");
                m.Location = Args.Point(args, "position");
                m.TextHeight = TextHeight(ctx, args);
                m.Width = Math.Max(0.0, Args.DblOpt(args, "width", 0.0));

                var rotation = Args.DblOpt(args, "rotation", 0.0);
                if (rotation != 0.0) m.Rotation = Rad(rotation);

                var style = Args.StrOpt(args, "textStyle");
                if (!string.IsNullOrWhiteSpace(style)) m.TextStyleId = RequireTextStyle(ctx, style!);

                return m;
            }));

        Router.Register("create_point",
            "Place a POINT. Args: position [x,y(,z)]; optional layer, colorIndex, linetype, space.",
            mutating: true,
            args => Create(args, ctx => Fresh(ctx, new DBPoint(Args.Point(args, "position")))));
    }

    // ------------------------------------------------- modify / transform / erase

    private static void RegisterEditors()
    {
        Router.Register("modify_entity",
            "Change one entity by 'handle': layer, colorIndex (0=ByBlock, 256=ByLayer, 257=None), " +
            "linetype, linetypeScale, visible, and 'text' for DBText/MText/attributes. Only the " +
            "arguments you supply are touched. Entities on locked layers are opened anyway.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var handle = Args.Str(args, "handle");
                var ent = OpenEntity(ctx, handle, OpenMode.ForWrite);
                var changed = ApplyCommon(ctx, ent, args);

                var text = Args.StrOpt(args, "text");
                if (text is not null)
                {
                    switch (ent)
                    {
                        case MText m:
                            m.Contents = text;
                            changed.Add("text");
                            break;
                        case DBText t: // also covers AttributeReference
                            t.TextString = text;
                            changed.Add("text");
                            break;
                        default:
                            throw new AcadMcpException("bad_argument",
                                $"Entity '{handle}' is a {DxfType(ent.ObjectId)} and has no text contents to set.");
                    }
                }

                if (HasProperty(args, "visible"))
                {
                    ent.Visible = Args.BoolOpt(args, "visible", true);
                    changed.Add("visible");
                }

                return new
                {
                    handle = ent.Handle.ToString(),
                    type = DxfType(ent.ObjectId),
                    changed,
                    layer = ent.Layer,
                    colorIndex = ent.ColorIndex,
                    linetype = ent.Linetype,
                    summary = Summarise(ent),
                };
            }));

        Router.Register("move_entities",
            "Move entities by handle list. Args: handles [..], and either displacement [dx,dy(,dz)] " +
            "or a base point pair from [x,y(,z)] + to [x,y(,z)]. One undo step; a bad handle aborts the lot.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var moved = TransformCore(ctx, args, DisplacementOf(args), copy: false);
                return new { moved = moved.Count, items = moved };
            }));

        Router.Register("copy_entities",
            "Copy entities by handle list into their own space. Args: handles [..], and either " +
            "displacement [dx,dy(,dz)] or from + to points. Returns the new handles.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var copied = TransformCore(ctx, args, DisplacementOf(args), copy: true);
                return new { copied = copied.Count, items = copied };
            }));

        Router.Register("rotate_entities",
            "Rotate entities about a base point. Args: handles [..], basePoint [x,y(,z)], angle in " +
            "DEGREES (or angleRadians); optional axis [x,y,z] vector, default Z.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var basePoint = Args.Point(args, "basePoint");
                var axis = HasArray(args, "axis") ? VectorOf(Args.Point(args, "axis")) : Vector3d.ZAxis;
                if (axis.Length <= 0.0)
                    throw new AcadMcpException("bad_argument", "'axis' must be a non-zero vector.");

                var matrix = Matrix3d.Rotation(AngleRadians(args), axis, basePoint);
                var rotated = TransformCore(ctx, args, matrix, copy: false);
                return new { rotated = rotated.Count, items = rotated };
            }));

        Router.Register("scale_entities",
            "Scale entities uniformly about a base point. Args: handles [..], basePoint [x,y(,z)], factor.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var factor = Args.Dbl(args, "factor");
                if (factor == 0.0 || double.IsNaN(factor) || double.IsInfinity(factor))
                    throw new AcadMcpException("bad_argument", $"'factor' must be a non-zero finite number; got {factor}.");

                var matrix = Matrix3d.Scaling(factor, Args.Point(args, "basePoint"));
                var scaled = TransformCore(ctx, args, matrix, copy: false);
                return new { scaled = scaled.Count, items = scaled };
            }));

        Router.Register("delete_entities",
            "Erase entities by handle list. Args: handles [..]. Atomic: a bad handle erases nothing. " +
            "Returns how many were erased; one undo step, so Ctrl+Z in AutoCAD brings them all back.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var targets = Resolve(ctx, Args.StrArray(args, "handles"));
                var erased = new List<object>();

                foreach (var target in targets)
                {
                    var obj = ctx.Tr.GetObject(target.Id, OpenMode.ForWrite, false, true);
                    if (obj is not Entity ent)
                        throw new AcadMcpException("not_an_entity",
                            $"Handle '{target.Handle}' is not a graphical entity ({ClassName(target.Id)}).");

                    var type = DxfType(target.Id);
                    ent.Erase();
                    erased.Add(new { handle = target.Handle, type });
                }

                return new { erased = erased.Count, items = erased };
            }));
    }

    // ------------------------------------------------------------- transform core

    private static List<object> TransformCore(AcadContext ctx, JsonElement args, Matrix3d xform, bool copy)
    {
        var targets = Resolve(ctx, Args.StrArray(args, "handles"));
        var results = new List<object>();

        foreach (var target in targets)
        {
            var mode = copy ? OpenMode.ForRead : OpenMode.ForWrite;
            var obj = ctx.Tr.GetObject(target.Id, mode, false, true);
            if (obj is not Entity ent)
                throw new AcadMcpException("not_an_entity",
                    $"Handle '{target.Handle}' is not a graphical entity ({ClassName(target.Id)}).");

            if (copy)
            {
                var clone = ent.GetTransformedCopy(xform);
                if (ent.OwnerId.IsNull)
                    throw new AcadMcpException("no_owner", $"Entity '{target.Handle}' has no owning block record to copy into.");

                var owner = (BlockTableRecord)ctx.Tr.GetObject(ent.OwnerId, OpenMode.ForWrite);
                owner.AppendEntity(clone);
                ctx.Tr.AddNewlyCreatedDBObject(clone, true);

                results.Add(new
                {
                    sourceHandle = target.Handle,
                    handle = clone.Handle.ToString(),
                    type = DxfType(clone.ObjectId),
                    space = owner.Name,
                });
            }
            else
            {
                ent.TransformBy(xform);
                results.Add(new { handle = target.Handle, type = DxfType(target.Id) });
            }
        }

        return results;
    }

    /// <summary>Resolve every handle up front and de-duplicate, so a bad one aborts before any edit lands.</summary>
    private static List<(string Handle, ObjectId Id)> Resolve(AcadContext ctx, string[] handles)
    {
        if (handles.Length == 0)
            throw new AcadMcpException("bad_argument", "Argument 'handles' must contain at least one handle.");

        var seen = new HashSet<ObjectId>();
        var targets = new List<(string Handle, ObjectId Id)>();

        foreach (var handle in handles)
        {
            var id = Acad.ToObjectId(ctx.Db, handle);
            if (seen.Add(id)) targets.Add((handle, id));
        }

        return targets;
    }

    private static Matrix3d DisplacementOf(JsonElement args)
    {
        if (HasArray(args, "displacement"))
            return Matrix3d.Displacement(VectorOf(Args.Point(args, "displacement")));

        var from = Args.Point(args, "from");
        var to = Args.Point(args, "to");
        return Matrix3d.Displacement(from.GetVectorTo(to));
    }

    private static double AngleRadians(JsonElement args)
    {
        if (HasProperty(args, "angleRadians"))
            return Args.Dbl(args, "angleRadians");

        return Rad(Args.Dbl(args, "angle"));
    }

    // -------------------------------------------------------------- creation core

    private static Task<object?> Create(JsonElement args, Func<AcadContext, Entity> build) =>
        Acad.Write<object?>(ctx =>
        {
            var spaceId = TargetSpaceId(ctx, Args.StrOpt(args, "space", "current")!);
            var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

            var ent = build(ctx);

            // Append first: setting Layer/Linetype by NAME needs the entity to be database-resident,
            // otherwise the name cannot be resolved against a symbol table.
            btr.AppendEntity(ent);
            ctx.Tr.AddNewlyCreatedDBObject(ent, true);

            ApplyCommon(ctx, ent, args);

            return new
            {
                handle = ent.Handle.ToString(),
                type = DxfType(ent.ObjectId),
                layer = ent.Layer,
                colorIndex = ent.ColorIndex,
                space = btr.Name,
                summary = Summarise(ent),
            };
        });

    /// <summary>New entity, pre-loaded with the drawing's current defaults (layer, colour, text style).</summary>
    private static T Fresh<T>(AcadContext ctx, T entity) where T : Entity
    {
        entity.SetDatabaseDefaults(ctx.Db);
        return entity;
    }

    /// <summary>Applies the shared optional properties; returns the names of the ones that changed.</summary>
    private static List<string> ApplyCommon(AcadContext ctx, Entity ent, JsonElement args)
    {
        var changed = new List<string>();

        var layer = Args.StrOpt(args, "layer");
        if (!string.IsNullOrWhiteSpace(layer))
        {
            ent.Layer = RequireLayer(ctx, layer!);
            changed.Add("layer");
        }

        if (HasProperty(args, "colorIndex"))
        {
            ent.ColorIndex = ColorIndexOf(Args.Int(args, "colorIndex"));
            changed.Add("colorIndex");
        }

        var linetype = Args.StrOpt(args, "linetype");
        if (!string.IsNullOrWhiteSpace(linetype))
        {
            ent.Linetype = RequireLinetype(ctx, linetype!);
            changed.Add("linetype");
        }

        if (HasProperty(args, "linetypeScale"))
        {
            var scale = Args.Dbl(args, "linetypeScale");
            if (scale <= 0)
                throw new AcadMcpException("bad_argument", $"'linetypeScale' must be greater than zero; got {scale}.");
            ent.LinetypeScale = scale;
            changed.Add("linetypeScale");
        }

        return changed;
    }

    private static int ColorIndexOf(int value)
    {
        if (value is < 0 or > 257)
            throw new AcadMcpException("bad_argument",
                $"'colorIndex' must be 0-257 (0 = ByBlock, 1-255 = ACI, 256 = ByLayer, 257 = None); got {value}.");
        return value;
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

    private static string RequireLinetype(AcadContext ctx, string name)
    {
        var table = (LinetypeTable)ctx.Tr.GetObject(ctx.Db.LinetypeTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("not_found", $"No linetype named '{name}' is loaded in this drawing.",
                "Load it with the LINETYPE command first. 'ByLayer' and 'ByBlock' are always available.");

        var record = (LinetypeTableRecord)ctx.Tr.GetObject(table[name], OpenMode.ForRead);
        return record.Name;
    }

    private static ObjectId RequireTextStyle(AcadContext ctx, string name)
    {
        var table = (TextStyleTable)ctx.Tr.GetObject(ctx.Db.TextStyleTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("not_found", $"No text style named '{name}' in this drawing.");
        return table[name];
    }

    private static double TextHeight(AcadContext ctx, JsonElement args)
    {
        var height = Args.DblOpt(args, "height", 0.0);
        if (height > 0) return height;

        var fallback = ctx.Db.Textsize;
        return fallback > 0 ? fallback : 2.5;
    }

    // ---------------------------------------------------------------- spaces

    private static List<(string Name, ObjectId Id)> QuerySpaces(AcadContext ctx, string space)
    {
        var wanted = space.Trim().ToLowerInvariant();
        if (wanted is not ("model" or "paper" or "current" or "all"))
            throw new AcadMcpException("bad_argument",
                $"Unknown space '{space}'. Use 'model', 'paper', 'current' or 'all'.");

        var result = new List<(string Name, ObjectId Id)>();
        var blocks = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);

        if (wanted == "current")
        {
            var current = (BlockTableRecord)ctx.Tr.GetObject(ctx.Db.CurrentSpaceId, OpenMode.ForRead);
            result.Add((current.Name, ctx.Db.CurrentSpaceId));
            return result;
        }

        if (wanted is "model" or "all")
            result.Add((BlockTableRecord.ModelSpace, blocks[BlockTableRecord.ModelSpace]));

        if (wanted is "paper" or "all")
        {
            var layouts = new List<(string Name, ObjectId Id)>();
            foreach (ObjectId id in blocks)
            {
                var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout && !btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    layouts.Add((btr.Name, id));
            }
            result.AddRange(layouts.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    private static ObjectId TargetSpaceId(AcadContext ctx, string space)
    {
        var wanted = space.Trim().ToLowerInvariant();
        switch (wanted)
        {
            case "" or "current":
                return ctx.Db.CurrentSpaceId;

            case "model":
            {
                var blocks = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
                return blocks[BlockTableRecord.ModelSpace];
            }

            case "paper":
                return SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db);

            default:
                throw new AcadMcpException("bad_argument",
                    $"Unknown space '{space}'. Use 'model', 'paper' or 'current' when creating.");
        }
    }

    // ------------------------------------------------------------ description

    private static object Brief(Entity ent, string space) => new
    {
        handle = ent.Handle.ToString(),
        type = DxfType(ent.ObjectId),
        layer = ent.Layer,
        space,
        colorIndex = ent.ColorIndex,
        color = SafeColor(ent),
        linetype = ent.Linetype,
        bounds = Bounds(ent),
        summary = Summarise(ent),
    };

    private static object Full(AcadContext ctx, Entity ent) => new
    {
        handle = ent.Handle.ToString(),
        type = DxfType(ent.ObjectId),
        className = ClassName(ent.ObjectId),
        layer = ent.Layer,
        space = OwnerName(ctx, ent),
        colorIndex = ent.ColorIndex,
        color = SafeColor(ent),
        linetype = ent.Linetype,
        linetypeScale = ent.LinetypeScale,
        lineWeight = ent.LineWeight.ToString(),
        visible = ent.Visible,
        isErased = ent.IsErased,
        bounds = Bounds(ent),
        summary = Summarise(ent),
        geometry = Geometry(ctx, ent),
    };

    private static object? Geometry(AcadContext ctx, Entity ent)
    {
        try
        {
            switch (ent)
            {
                case Line line:
                    return new
                    {
                        start = XYZ(line.StartPoint),
                        end = XYZ(line.EndPoint),
                        length = line.Length,
                        angleRadians = line.Angle,
                        angleDegrees = Deg(line.Angle),
                        delta = XYZ(line.Delta),
                        normal = XYZ(line.Normal),
                    };

                case Arc arc:
                    return new
                    {
                        center = XYZ(arc.Center),
                        radius = arc.Radius,
                        startAngle = Deg(arc.StartAngle),
                        endAngle = Deg(arc.EndAngle),
                        totalAngle = Deg(arc.TotalAngle),
                        startPoint = XYZ(arc.StartPoint),
                        endPoint = XYZ(arc.EndPoint),
                        length = arc.Length,
                        normal = XYZ(arc.Normal),
                    };

                case Circle circle:
                    return new
                    {
                        center = XYZ(circle.Center),
                        radius = circle.Radius,
                        diameter = circle.Diameter,
                        circumference = circle.Circumference,
                        normal = XYZ(circle.Normal),
                    };

                case Polyline pl:
                    return PolylineGeometry(pl);

                case AttributeReference att:
                    return new
                    {
                        tag = att.Tag,
                        text = att.TextString,
                        position = XYZ(att.Position),
                        height = att.Height,
                        rotation = Deg(att.Rotation),
                        textStyle = att.TextStyleName,
                        isInvisible = att.Invisible,
                        isConstant = att.IsConstant,
                    };

                case MText mtext:
                    return new
                    {
                        contents = mtext.Contents,
                        plainText = SafePlainText(mtext),
                        position = XYZ(mtext.Location),
                        height = mtext.TextHeight,
                        width = mtext.Width,
                        actualWidth = mtext.ActualWidth,
                        actualHeight = mtext.ActualHeight,
                        rotation = Deg(mtext.Rotation),
                        attachment = mtext.Attachment.ToString(),
                        textStyle = mtext.TextStyleName,
                        columnCount = mtext.ColumnCount,
                    };

                case DBText text:
                    return new
                    {
                        text = text.TextString,
                        position = XYZ(text.Position),
                        alignmentPoint = XYZ(text.AlignmentPoint),
                        height = text.Height,
                        rotation = Deg(text.Rotation),
                        widthFactor = text.WidthFactor,
                        oblique = Deg(text.Oblique),
                        justify = text.Justify.ToString(),
                        horizontalMode = text.HorizontalMode.ToString(),
                        verticalMode = text.VerticalMode.ToString(),
                        textStyle = text.TextStyleName,
                        isDefaultAlignment = text.IsDefaultAlignment,
                    };

                case BlockReference block:
                    return new
                    {
                        name = SafeBlockName(block),
                        effectiveName = EffectiveBlockName(ctx, block),
                        position = XYZ(block.Position),
                        rotation = Deg(block.Rotation),
                        rotationRadians = block.Rotation,
                        scale = new { x = block.ScaleFactors.X, y = block.ScaleFactors.Y, z = block.ScaleFactors.Z },
                        normal = XYZ(block.Normal),
                        isDynamic = block.IsDynamicBlock,
                        attributes = AttributesOf(ctx, block),
                    };

                case DBPoint point:
                    return new { position = XYZ(point.Position), normal = XYZ(point.Normal) };

                case Curve curve:
                    // Splines, ellipses, 2d/3d polylines and anything else curve-shaped.
                    return new
                    {
                        startPoint = XYZ(curve.StartPoint),
                        endPoint = XYZ(curve.EndPoint),
                        closed = curve.Closed,
                        startParam = curve.StartParam,
                        endParam = curve.EndParam,
                    };

                default:
                    return null;
            }
        }
        catch (System.Exception ex)
        {
            // A degenerate entity can throw out of a perfectly ordinary property getter.
            return new { error = ex.Message };
        }
    }

    private static object PolylineGeometry(Polyline pl)
    {
        var count = pl.NumberOfVertices;
        var reported = Math.Min(count, MaxVerticesReported);
        var vertices = new List<object>(reported);

        for (var i = 0; i < reported; i++)
        {
            var pt = pl.GetPoint2dAt(i);
            vertices.Add(new
            {
                x = pt.X,
                y = pt.Y,
                bulge = pl.GetBulgeAt(i),
                startWidth = pl.GetStartWidthAt(i),
                endWidth = pl.GetEndWidthAt(i),
            });
        }

        return new
        {
            vertexCount = count,
            vertices,
            verticesTruncated = count > reported,
            closed = pl.Closed,
            length = SafeLength(pl),
            elevation = pl.Elevation,
            hasBulges = pl.HasBulges,
            hasWidth = pl.HasWidth,
            constantWidth = SafeConstantWidth(pl),
            normal = XYZ(pl.Normal),
        };
    }

    private static List<object> AttributesOf(AcadContext ctx, BlockReference block)
    {
        var list = new List<object>();
        try
        {
            foreach (ObjectId id in block.AttributeCollection)
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead, false, true) is not AttributeReference att) continue;
                list.Add(new
                {
                    handle = att.Handle.ToString(),
                    tag = att.Tag,
                    value = att.TextString,
                    position = XYZ(att.Position),
                    height = att.Height,
                    isInvisible = att.Invisible,
                    isConstant = att.IsConstant,
                });
            }
        }
        catch (System.Exception)
        {
            // An unresolved xref's attribute collection can refuse to enumerate; report what we got.
        }
        return list;
    }

    private static string Summarise(Entity ent)
    {
        try
        {
            switch (ent)
            {
                case Line l:
                    return $"Line {P(l.StartPoint)} -> {P(l.EndPoint)}, length {N(l.Length)}";
                case Arc a:
                    return $"Arc centre {P(a.Center)}, r {N(a.Radius)}, {N(Deg(a.StartAngle))} deg to {N(Deg(a.EndAngle))} deg";
                case Circle c:
                    return $"Circle centre {P(c.Center)}, r {N(c.Radius)}";
                case Polyline p:
                    return $"Polyline, {p.NumberOfVertices} vertices{(p.Closed ? ", closed" : "")}, length {N(SafeLength(p))}";
                case AttributeReference att:
                    return $"Attribute {att.Tag} = \"{Clip(att.TextString)}\"";
                case MText m:
                    return $"MText \"{Clip(m.Contents)}\" at {P(m.Location)}";
                case DBText t:
                    return $"Text \"{Clip(t.TextString)}\" at {P(t.Position)}";
                case BlockReference b:
                    return $"Block '{SafeBlockName(b)}' at {P(b.Position)}, rotation {N(Deg(b.Rotation))} deg";
                case DBPoint pt:
                    return $"Point {P(pt.Position)}";
                default:
                    return DxfType(ent.ObjectId);
            }
        }
        catch (System.Exception)
        {
            return DxfType(ent.ObjectId);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Entity OpenEntity(AcadContext ctx, string handle, OpenMode mode)
    {
        var id = Acad.ToObjectId(ctx.Db, handle);

        // forceOpenOnLockedLayer: the caller named this entity by handle, so a locked layer should
        // not turn into an opaque eOnLockedLayer failure.
        var obj = ctx.Tr.GetObject(id, mode, false, true);
        if (obj is not Entity ent)
            throw new AcadMcpException("not_an_entity",
                $"Handle '{handle}' is not a graphical entity ({ClassName(id)}).");

        return ent;
    }

    private static bool TypeMatches(ObjectId id, HashSet<string> types)
    {
        if (types.Count == 0) return true;

        var cls = id.ObjectClass;
        if (cls is null) return false;

        var dxf = cls.DxfName;
        if (!string.IsNullOrEmpty(dxf) && types.Contains(dxf)) return true;

        return types.Contains(cls.Name);
    }

    private static string DxfType(ObjectId id)
    {
        var cls = id.ObjectClass;
        if (cls is null) return "UNKNOWN";

        var dxf = cls.DxfName;
        return string.IsNullOrEmpty(dxf) ? cls.Name : dxf;
    }

    private static string ClassName(ObjectId id)
    {
        var cls = id.ObjectClass;
        return cls is null ? "UNKNOWN" : cls.Name;
    }

    private static string? OwnerName(AcadContext ctx, Entity ent)
    {
        try
        {
            if (ent.OwnerId.IsNull) return null;
            return ctx.Tr.GetObject(ent.OwnerId, OpenMode.ForRead) is BlockTableRecord btr ? btr.Name : null;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static object? Bounds(Entity ent)
    {
        try
        {
            var extents = ent.GeometricExtents;
            return new { min = XYZ(extents.MinPoint), max = XYZ(extents.MaxPoint) };
        }
        catch (System.Exception)
        {
            // Empty MText, empty block references and a few others genuinely have no extents.
            return null;
        }
    }

    private static string? SafeColor(Entity ent)
    {
        try { return ent.Color.ToString(); }
        catch (System.Exception) { return null; }
    }

    private static string SafeBlockName(BlockReference block)
    {
        try { return block.Name; }
        catch (System.Exception) { return "<unresolved>"; }
    }

    private static string? EffectiveBlockName(AcadContext ctx, BlockReference block)
    {
        try
        {
            if (!block.IsDynamicBlock || block.DynamicBlockTableRecord.IsNull) return null;
            return ctx.Tr.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr
                ? btr.Name : null;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static string? SafePlainText(MText mtext)
    {
        try { return mtext.Text; }
        catch (System.Exception) { return null; }
    }

    private static double SafeLength(Polyline pl)
    {
        try { return pl.Length; }
        catch (System.Exception) { return 0.0; }
    }

    private static double SafeConstantWidth(Polyline pl)
    {
        // Throws on a polyline whose segments carry differing widths.
        try { return pl.ConstantWidth; }
        catch (System.Exception) { return 0.0; }
    }

    private static HashSet<string> NameSet(JsonElement args, string singular, string plural)
    {
        var set = new HashSet<string>(Args.StrArrayOpt(args, plural), StringComparer.OrdinalIgnoreCase);

        var one = Args.StrOpt(args, singular);
        if (!string.IsNullOrWhiteSpace(one)) set.Add(one!);

        return set;
    }

    private static (int Limit, int Offset) Page(JsonElement args)
    {
        var limit = Args.IntOpt(args, "limit", DefaultLimit);
        if (limit <= 0) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;

        var offset = Args.IntOpt(args, "offset", 0);
        if (offset < 0) offset = 0;

        return (limit, offset);
    }

    private static bool HasProperty(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    private static bool HasArray(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Array;

    private static List<Point3d> Vertices(JsonElement args, string name)
    {
        if (!HasArray(args, name))
            throw new AcadMcpException("bad_argument", $"Argument '{name}' must be an array of [x, y] points.");

        var points = new List<Point3d>();
        var index = 0;

        foreach (var item in args.GetProperty(name).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array)
                throw new AcadMcpException("bad_argument", $"{name}[{index}] must be an array like [x, y] or [x, y, z].");

            var numbers = item.EnumerateArray()
                              .Where(x => x.ValueKind == JsonValueKind.Number)
                              .Select(x => x.GetDouble())
                              .ToArray();

            if (numbers.Length is < 2 or > 3)
                throw new AcadMcpException("bad_argument",
                    $"{name}[{index}] must have 2 or 3 numbers; got {numbers.Length}.");

            points.Add(new Point3d(numbers[0], numbers[1], numbers.Length > 2 ? numbers[2] : 0.0));
            index++;
        }

        return points;
    }

    private static double[] Doubles(JsonElement args, string name)
    {
        if (!HasArray(args, name)) return [];

        return args.GetProperty(name).EnumerateArray()
                   .Where(x => x.ValueKind == JsonValueKind.Number)
                   .Select(x => x.GetDouble())
                   .ToArray();
    }

    private static Vector3d VectorOf(Point3d p) => new(p.X, p.Y, p.Z);

    private static double[] XYZ(Point3d p) => [p.X, p.Y, p.Z];

    private static double[] XYZ(Vector3d v) => [v.X, v.Y, v.Z];

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string P(Point3d p) => $"({N(p.X)}, {N(p.Y)}, {N(p.Z)})";

    private static string Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var flat = text.Replace("\r", " ").Replace("\n", " ").Replace("\\P", " ");
        return flat.Length <= SummaryTextLength ? flat : flat[..SummaryTextLength] + "...";
    }
}
