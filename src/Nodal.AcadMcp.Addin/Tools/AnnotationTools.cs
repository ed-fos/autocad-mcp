using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Annotation surface: text harvesting, dimensions, hatches, tables and leaders.
///
/// extract_text is the single highest-value read in this add-in — it is how an agent works out
/// what an unfamiliar drawing is actually about — so it reaches every text carrier AutoCAD has:
/// DBText, MText, block attribute values, attribute definitions, dimension text (override and
/// rendered), MLeader MText, feature control frames, and table cell contents.
/// </summary>
internal static class AnnotationTools
{
    // Anything unbounded is paged. 500 keeps a 200k-entity drawing from producing a 100MB response.
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;
    private const int DefaultMaxCells = 2000;

    internal static void Register()
    {
        RegisterExtractText();
        RegisterListDimensions();
        RegisterListHatches();
        RegisterListTables();
        RegisterListLeaders();
        RegisterCreateMLeader();
        RegisterCreateDimension();
    }

    // ------------------------------------------------------------------ extract_text

    private static void RegisterExtractText() => Router.Register(
        "extract_text",
        "Harvest every piece of text in the drawing: DBText, MText, block attribute values, "
      + "attribute definitions, dimension text, MLeader text, feature control frames and table "
      + "cells. Each item carries handle, kind, layer, owner space/block, raw text, a plain-text "
      + "version with MText formatting codes stripped, position and height. "
      + "Args: scope (model|paper|layouts|current|all, default layouts), layer, layers[], "
      + "kinds[] (DBText|MText|AttributeReference|AttributeDefinition|Dimension|MLeader|"
      + "FeatureControlFrame|TableCell), contains (case-insensitive substring), includeEmpty, "
      + "includeXrefs, includeAnonymousBlocks, offset, limit. "
      + "Returns {items,total,offset,limit,truncated}.",
        mutating: false,
        e => Acad.Read<object?>(ctx =>
        {
            var (scope, xrefs, anon, offset, limit) = CommonArgs(e);
            var layerFilter = LayerFilter(e);
            var kindFilter = NameSet(Args.StrArrayOpt(e, "kinds"));
            var contains = Args.StrOpt(e, "contains");
            var includeEmpty = Args.BoolOpt(e, "includeEmpty", false);

            var pager = new Pager(offset, limit);

            // One factory, one anonymous type: every item in "items" has the same JSON shape.
            void Emit(
                string kind, string handle, string layer, string owner,
                string raw, string plain, Point3d? position, double? height, double? rotation,
                string? tag, string? blockHandle, string? blockName,
                int? row, int? column, string? styleName)
            {
                if (!includeEmpty && string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(plain))
                    return;
                if (kindFilter.Count > 0 && !kindFilter.Contains(kind)) return;
                if (layerFilter.Count > 0 && !layerFilter.Contains(layer)) return;
                if (!string.IsNullOrEmpty(contains)
                    && plain.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0
                    && raw.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                pager.Add(() => new
                {
                    handle,
                    kind,
                    layer,
                    owner,
                    text = raw,
                    plainText = plain,
                    position = PointArray(position),
                    height,
                    rotation,
                    tag,
                    blockHandle,
                    blockName,
                    row,
                    column,
                    styleName,
                });
            }

            ForEachEntity(ctx, ResolveSpaces(ctx, scope, xrefs, anon), (owner, ent) =>
            {
                switch (ent)
                {
                    // AttributeDefinition and AttributeReference both derive from DBText,
                    // so they must be matched before the DBText arm.
                    case AttributeDefinition ad:
                    {
                        var raw = AttributeDefinitionText(ad);
                        Emit("AttributeDefinition", ad.Handle.ToString(), ad.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => ad.Position), Try(() => ad.Height), Try(() => ad.Rotation),
                             TryStr(() => ad.Tag), null, owner, null, null,
                             TryStr(() => ad.TextStyleName));
                        break;
                    }

                    case AttributeReference loose:
                    {
                        var raw = AttributeReferenceText(loose);
                        Emit("AttributeReference", loose.Handle.ToString(), loose.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => loose.Position), Try(() => loose.Height), Try(() => loose.Rotation),
                             TryStr(() => loose.Tag), null, null, null, null,
                             TryStr(() => loose.TextStyleName));
                        break;
                    }

                    case DBText t:
                    {
                        var raw = TryStr(() => t.TextString) ?? string.Empty;
                        Emit("DBText", t.Handle.ToString(), t.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => t.Position), Try(() => t.Height), Try(() => t.Rotation),
                             null, null, null, null, null, TryStr(() => t.TextStyleName));
                        break;
                    }

                    case MText m:
                    {
                        var raw = TryStr(() => m.Contents) ?? string.Empty;
                        Emit("MText", m.Handle.ToString(), m.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => m.Location), Try(() => m.TextHeight), Try(() => m.Rotation),
                             null, null, null, null, null, TryStr(() => m.TextStyleName));
                        break;
                    }

                    // Table derives from BlockReference — match it first.
                    case Table tb:
                    {
                        var handle = tb.Handle.ToString();
                        var layer = tb.Layer;
                        var styleName = TryStr(() => tb.TableStyleName);
                        var rows = Try(() => tb.NumRows) ?? 0;
                        var cols = Try(() => tb.NumColumns) ?? 0;

                        for (var r = 0; r < rows; r++)
                        {
                            for (var c = 0; c < cols; c++)
                            {
                                var raw = CellText(tb, r, c);
                                if (raw is null) continue;

                                Emit("TableCell", handle, layer, owner,
                                     raw, StripMTextFormatting(raw),
                                     Try(() => tb.AttachmentPoint(r, c)),
                                     Try(() => tb.GetTextHeight(r, c, 0)),
                                     null, null, null, null, r, c, styleName);
                            }
                        }
                        break;
                    }

                    case BlockReference br:
                    {
                        var blockHandle = br.Handle.ToString();
                        var blockName = TryStr(() => br.Name) ?? string.Empty;

                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            AttributeReference att;
                            try { att = (AttributeReference)ctx.Tr.GetObject(attId, OpenMode.ForRead); }
                            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                            catch (InvalidCastException) { continue; }

                            var raw = AttributeReferenceText(att);
                            Emit("AttributeReference", att.Handle.ToString(), att.Layer, owner,
                                 raw, StripMTextFormatting(raw),
                                 Try(() => att.Position), Try(() => att.Height), Try(() => att.Rotation),
                                 TryStr(() => att.Tag), blockHandle, blockName, null, null,
                                 TryStr(() => att.TextStyleName));
                        }
                        break;
                    }

                    case Dimension d:
                    {
                        var raw = TryStr(() => d.DimensionText) ?? string.Empty;
                        Emit("Dimension", d.Handle.ToString(), d.Layer, owner,
                             raw, DimensionDisplayText(d, raw),
                             Try(() => d.TextPosition), Try(() => d.Dimtxt), Try(() => d.TextRotation),
                             null, null, null, null, null, TryStr(() => d.DimensionStyleName));
                        break;
                    }

                    case MLeader ml:
                    {
                        var raw = MLeaderText(ml);
                        Emit("MLeader", ml.Handle.ToString(), ml.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => ml.TextLocation), Try(() => ml.TextHeight), null,
                             null, null, null, null, null, MLeaderStyleName(ctx, ml));
                        break;
                    }

                    case FeatureControlFrame fcf:
                    {
                        var raw = TryStr(() => fcf.Text) ?? string.Empty;
                        Emit("FeatureControlFrame", fcf.Handle.ToString(), fcf.Layer, owner,
                             raw, StripMTextFormatting(raw),
                             Try(() => fcf.Location), Try(() => fcf.Dimtxt), null,
                             null, null, null, null, null, TryStr(() => fcf.TextStyleName));
                        break;
                    }
                }
            });

            return pager.Result();
        }));

    // ------------------------------------------------------------------ list_dimensions

    private static void RegisterListDimensions() => Router.Register(
        "list_dimensions",
        "Every Dimension in scope (aligned, rotated, radial, diametric, arc, angular, ordinate) "
      + "with its measurement, text override, rendered text, style and defining geometry. "
      + "Angular measurements are in radians. "
      + "Args: scope, layer, layers[], includeXrefs, includeAnonymousBlocks, offset, limit. "
      + "Returns {items,total,offset,limit,truncated}.",
        mutating: false,
        e => Acad.Read<object?>(ctx =>
        {
            var (scope, xrefs, anon, offset, limit) = CommonArgs(e);
            var layerFilter = LayerFilter(e);
            var pager = new Pager(offset, limit);

            ForEachEntity(ctx, ResolveSpaces(ctx, scope, xrefs, anon), (owner, ent) =>
            {
                if (ent is not Dimension d) return;
                if (layerFilter.Count > 0 && !layerFilter.Contains(d.Layer)) return;

                var raw = TryStr(() => d.DimensionText) ?? string.Empty;

                pager.Add(() => new
                {
                    handle = d.Handle.ToString(),
                    dimensionType = d.GetType().Name,
                    dxfName = TryStr(() => d.ObjectId.ObjectClass.DxfName),
                    layer = d.Layer,
                    owner,
                    measurement = Try(() => d.Measurement),
                    dimensionText = raw,
                    displayText = DimensionDisplayText(d, raw),
                    usesDefaultText = string.IsNullOrEmpty(raw) || raw == "<>",
                    styleName = TryStr(() => d.DimensionStyleName),
                    prefix = NullIfEmpty(TryStr(() => d.Prefix)),
                    suffix = NullIfEmpty(TryStr(() => d.Suffix)),
                    postfix = NullIfEmpty(TryStr(() => d.Dimpost)),
                    textPosition = PointArray(Try(() => d.TextPosition)),
                    textRotation = Try(() => d.TextRotation),
                    textHeight = Try(() => d.Dimtxt),
                    usingDefaultTextPosition = Try(() => d.UsingDefaultTextPosition),
                    scale = Try(() => d.Dimscale),
                    geometry = DimensionGeometry(d),
                });
            });

            return pager.Result();
        }));

    // ------------------------------------------------------------------ list_hatches

    private static void RegisterListHatches() => Router.Register(
        "list_hatches",
        "Every Hatch and gradient fill in scope: pattern name and type, area (null when AutoCAD "
      + "refuses to compute it — Hatch.Area throws for non-solid and self-intersecting boundaries), "
      + "associativity, loop count and placement. "
      + "Args: scope, layer, layers[], pattern (case-insensitive substring), includeXrefs, "
      + "includeAnonymousBlocks, offset, limit. Returns {items,total,offset,limit,truncated}.",
        mutating: false,
        e => Acad.Read<object?>(ctx =>
        {
            var (scope, xrefs, anon, offset, limit) = CommonArgs(e);
            var layerFilter = LayerFilter(e);
            var pattern = Args.StrOpt(e, "pattern");
            var pager = new Pager(offset, limit);

            ForEachEntity(ctx, ResolveSpaces(ctx, scope, xrefs, anon), (owner, ent) =>
            {
                if (ent is not Hatch h) return;
                if (layerFilter.Count > 0 && !layerFilter.Contains(h.Layer)) return;

                var patternName = TryStr(() => h.PatternName) ?? string.Empty;
                if (!string.IsNullOrEmpty(pattern)
                    && patternName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                pager.Add(() => new
                {
                    handle = h.Handle.ToString(),
                    layer = h.Layer,
                    owner,
                    patternName,
                    patternType = TryStr(() => h.PatternType.ToString()),
                    hatchObjectType = TryStr(() => h.HatchObjectType.ToString()),
                    hatchStyle = TryStr(() => h.HatchStyle.ToString()),
                    isGradient = Try(() => h.IsGradient),
                    isSolidFill = Try(() => h.IsSolidFill),
                    gradientName = NullIfEmpty(TryStr(() => h.GradientName)),
                    // Guarded: Hatch.Area is documented to throw for patterns AutoCAD cannot integrate.
                    area = Try(() => h.Area),
                    associative = Try(() => h.Associative),
                    associatedObjectCount = AssociatedCount(h),
                    loopCount = Try(() => h.NumberOfLoops),
                    patternScale = Try(() => h.PatternScale),
                    patternAngle = Try(() => h.PatternAngle),
                    patternSpace = Try(() => h.PatternSpace),
                    patternDouble = Try(() => h.PatternDouble),
                    elevation = Try(() => h.Elevation),
                    origin = Try(() => h.Origin) is { } o ? new[] { o.X, o.Y } : null,
                    colorIndex = Try(() => h.ColorIndex),
                });
            });

            return pager.Result();
        }));

    // ------------------------------------------------------------------ list_tables

    private static void RegisterListTables() => Router.Register(
        "list_tables",
        "Every Table in scope with its full cell contents as a 2D array (rows of columns), "
      + "plain-text by default. Args: scope, layer, layers[], includeRaw (also return unstripped "
      + "cell text), maxCells (per table, default 2000), includeXrefs, includeAnonymousBlocks, "
      + "offset, limit. Returns {items,total,offset,limit,truncated}.",
        mutating: false,
        e => Acad.Read<object?>(ctx =>
        {
            var (scope, xrefs, anon, offset, limit) = CommonArgs(e);
            var layerFilter = LayerFilter(e);
            var includeRaw = Args.BoolOpt(e, "includeRaw", false);
            var maxCells = Math.Clamp(Args.IntOpt(e, "maxCells", DefaultMaxCells), 1, 100_000);
            var pager = new Pager(offset, limit);

            ForEachEntity(ctx, ResolveSpaces(ctx, scope, xrefs, anon), (owner, ent) =>
            {
                if (ent is not Table tb) return;
                if (layerFilter.Count > 0 && !layerFilter.Contains(tb.Layer)) return;

                pager.Add(() =>
                {
                    var rows = Math.Max(0, Try(() => tb.NumRows) ?? 0);
                    var cols = Math.Max(0, Try(() => tb.NumColumns) ?? 0);

                    var cells = new List<string[]>();
                    var cellsRaw = includeRaw ? new List<string[]>() : null;
                    var budget = maxCells;
                    var cellsTruncated = false;

                    for (var r = 0; r < rows; r++)
                    {
                        if (budget <= 0) { cellsTruncated = true; break; }

                        var line = new string[cols];
                        var rawLine = includeRaw ? new string[cols] : null;

                        for (var c = 0; c < cols; c++)
                        {
                            if (budget <= 0)
                            {
                                cellsTruncated = true;
                                line[c] = string.Empty;
                                if (rawLine is not null) rawLine[c] = string.Empty;
                                continue;
                            }
                            budget--;

                            var raw = CellText(tb, r, c) ?? string.Empty;
                            line[c] = StripMTextFormatting(raw);
                            if (rawLine is not null) rawLine[c] = raw;
                        }

                        cells.Add(line);
                        if (cellsRaw is not null && rawLine is not null) cellsRaw.Add(rawLine);
                    }

                    return new
                    {
                        handle = tb.Handle.ToString(),
                        layer = tb.Layer,
                        owner,
                        tableStyleName = TryStr(() => tb.TableStyleName),
                        rows,
                        columns = cols,
                        position = PointArray(Try(() => tb.Position)),
                        width = Try(() => tb.Width),
                        height = Try(() => tb.Height),
                        rotation = Try(() => tb.Rotation),
                        cells,
                        cellsRaw,
                        cellsTruncated,
                    };
                });
            });

            return pager.Result();
        }));

    // ------------------------------------------------------------------ list_leaders

    private static void RegisterListLeaders() => Router.Register(
        "list_leaders",
        "Every MLeader (multileader) and legacy Leader in scope, with its text, style, vertices "
      + "and — for legacy leaders — the handle of the annotation entity it points at. "
      + "Args: scope, layer, layers[], includeXrefs, includeAnonymousBlocks, offset, limit. "
      + "Returns {items,total,offset,limit,truncated}.",
        mutating: false,
        e => Acad.Read<object?>(ctx =>
        {
            var (scope, xrefs, anon, offset, limit) = CommonArgs(e);
            var layerFilter = LayerFilter(e);
            var pager = new Pager(offset, limit);

            // One factory so MLeader and Leader rows serialise to the same shape.
            object Row(
                string handle, string kind, string layer, string owner, string? styleName,
                string? contentType, string text, string plainText,
                double[]? textLocation, double? textHeight,
                int? leaderCount, int? leaderLineCount, int vertexCount,
                List<double[]> vertices, string? annotationHandle,
                bool? hasArrowHead, bool? hasHookLine) => new
                {
                    handle,
                    kind,
                    layer,
                    owner,
                    styleName,
                    contentType,
                    text,
                    plainText,
                    textLocation,
                    textHeight,
                    leaderCount,
                    leaderLineCount,
                    vertexCount,
                    vertices,
                    annotationHandle,
                    hasArrowHead,
                    hasHookLine,
                };

            ForEachEntity(ctx, ResolveSpaces(ctx, scope, xrefs, anon), (owner, ent) =>
            {
                switch (ent)
                {
                    case MLeader ml:
                    {
                        if (layerFilter.Count > 0 && !layerFilter.Contains(ml.Layer)) return;
                        pager.Add(() =>
                        {
                            var raw = MLeaderText(ml);
                            var verts = MLeaderVertices(ml);
                            return Row(
                                ml.Handle.ToString(), "MLeader", ml.Layer, owner,
                                MLeaderStyleName(ctx, ml),
                                TryStr(() => ml.ContentType.ToString()),
                                raw, StripMTextFormatting(raw),
                                PointArray(Try(() => ml.TextLocation)), Try(() => ml.TextHeight),
                                Try(() => ml.LeaderCount), Try(() => ml.LeaderLineCount),
                                verts.Count, verts, null, null, null);
                        });
                        break;
                    }

                    case Leader ld:
                    {
                        if (layerFilter.Count > 0 && !layerFilter.Contains(ld.Layer)) return;
                        pager.Add(() =>
                        {
                            var verts = LeaderVertices(ld);
                            var annotation = Try(() => ld.Annotation);
                            return Row(
                                ld.Handle.ToString(), "Leader", ld.Layer, owner,
                                TryStr(() => ld.DimensionStyleName),
                                TryStr(() => ld.AnnoType.ToString()),
                                string.Empty, string.Empty,
                                null, Try(() => ld.AnnoHeight),
                                null, null, verts.Count, verts,
                                TryStr(() => annotation is { IsNull: false } a ? a.Handle.ToString() : null),
                                Try(() => ld.HasArrowHead), Try(() => ld.HasHookLine));
                        });
                        break;
                    }
                }
            });

            return pager.Result();
        }));

    // ------------------------------------------------------------------ create_mleader

    // Every constructor and mutator used below is confirmed in acdbmgd.api.txt:
    //   MLeader.ContentType / .MText / .AddLeaderLine(Point3d) / .TextHeight, MText.Contents/.Location,
    //   Entity.SetDatabaseDefaults(Database), Transaction.AddNewlyCreatedDBObject(DBObject, bool).
    // Measured in AutoCAD 2027: AddLeaderLine takes the vertex nearest the landing and AddFirstVertex
    // prepends toward the arrow (AddLastVertex with a mid point gave runaway vertices). The landing
    // and dogleg come from the MText location; setting MLeader.TextLocation as well nudges the text
    // off textPoint, so it is left alone. On a non-annotative leader MLeader.Scale (MLEADERSCALE)
    // multiplies text, arrow and gaps; an annotative one takes them from its scale context.
    private static void RegisterCreateMLeader() => Router.Register(
        "create_mleader",
        "Create an MText multileader in the current space, arrow at arrowPoint and text at textPoint. "
      + "Args: text, arrowPoint [x,y(,z)] (the arrow tip), textPoint [x,y(,z)] (all required); "
      + "vertices [[x,y(,z)],...] (optional bend points between arrow and text, listed from the arrow); "
      + "textAngle 'horizontal' (text stays level whatever the leader's slope) | 'insert' | "
      + "'rightReading' (default: the style's); styleName (MLeader style), layer (must already exist). "
      + "Text size: textHeight in drawing units or paperHeight as plotted, not both; default the "
      + "style's text height (textHeight 0 also means the style's). "
      + "annotative (default: the style's setting): an annotative leader gets exactly one scale "
      + "context, annotationScale (a scale name such as '1/4\" = 1'-0\"' or a factor such as 48) or "
      + "else the current CANNOSCALE in model space, 1:1 in paper space, and its arrow, gaps and text "
      + "are the style's plotted sizes times that scale. A non-annotative leader given paperHeight, or "
      + "annotative:false on an annotative style, is sized for that scale through its overall scale "
      + "(MLEADERSCALE) instead and plots right at that one scale only. Annotative adds no other "
      + "scales, and the leader only shows in viewports at its scale unless ANNOALLVISIBLE is on. "
      + "Returns {handle, leaderLineIndex, vertexCount, annotative, annotationScale, textHeight "
      + "(drawing units), paperHeight, leaderScale}.",
        mutating: true,
        e => Acad.Write<object?>(ctx =>
        {
            var text = Args.Str(e, "text");
            var arrowPoint = Args.Point(e, "arrowPoint");
            var textPoint = Args.Point(e, "textPoint");
            var bends = PointList(e, "vertices");
            var textHeight = Args.DblOpt(e, "textHeight", 0.0);
            var paperHeight = NumberArg(e, "paperHeight");
            var textAngle = Args.StrOpt(e, "textAngle");
            var styleName = Args.StrOpt(e, "styleName");
            var layer = Args.StrOpt(e, "layer");

            var spaceId = ctx.Db.CurrentSpaceId;
            var space = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

            var leader = new MLeader();
            leader.SetDatabaseDefaults(ctx.Db);
            leader.ContentType = ContentType.MTextContent;

            if (!string.IsNullOrWhiteSpace(styleName))
                leader.MLeaderStyle = ResolveMLeaderStyle(ctx, styleName);

            var style = (MLeaderStyle)ctx.Tr.GetObject(leader.MLeaderStyle, OpenMode.ForRead);
            var styleAnnotative = style.Annotative == AnnotativeStates.True;
            var annotative = AnnotativeArg(e) ?? styleAnnotative;

            // Not annotative but sized as plotted: the whole leader is scaled for one scale.
            var scaleWhole = !annotative && (paperHeight is not null || styleAnnotative);
            if (!annotative && !scaleWhole && HasValue(e, "annotationScale"))
                throw new AcadMcpException("bad_argument",
                    "'annotationScale' has no effect on a non-annotative leader without 'paperHeight'.");

            var scale = annotative || scaleWhole ? ScaleFor(ctx, e, spaceId) : null;
            var factor = scale is null ? 1.0 : Factor(scale);
            var styleScale = style.Scale > 0 ? style.Scale : 1.0;

            var size = Sizing(() => AnnotationSizing.Resolve(
                "textHeight", textHeight > 0 ? textHeight : null, "paperHeight", paperHeight,
                factor, annotative || scaleWhole,
                fallbackPaper: style.TextHeight, fallbackModel: style.TextHeight * styleScale));

            ApplyLayer(ctx, leader, layer);

            // Deliberately not disposed. The declared API surface does not say whether the MText
            // setter copies or takes ownership, and disposing something AutoCAD owns is a crash
            // rather than an exception. Letting the finalizer handle it is safe either way.
            var mtext = new MText();
            mtext.SetDatabaseDefaults(ctx.Db);
            mtext.Contents = text;
            mtext.Location = textPoint;
            leader.MText = mtext;

            leader.Annotative = annotative ? AnnotativeStates.True : AnnotativeStates.False;
            if (scaleWhole) leader.Scale = factor;

            if (!string.IsNullOrWhiteSpace(textAngle))
                leader.TextAngleType = Enum.Parse<TextAngleType>(Sizing(() => AnnotationSizing.TextAngleName(textAngle!)));

            var (first, prepend) = AnnotationSizing.LeaderOrder(arrowPoint, bends);
            var lineIndex = leader.AddLeaderLine(first);
            foreach (var point in prepend) leader.AddFirstVertex(lineIndex, point);

            space.AppendEntity(leader);
            ctx.Tr.AddNewlyCreatedDBObject(leader, true);

            if (annotative && scale is not null) UseOnlyScale(ctx.Db, leader, scale);

            // After the MText (which carries TEXTSIZE) and after the scale context (switching
            // contexts rescales): TextHeight times the leader's overall scale is the drawn height.
            var overall = annotative || scaleWhole ? factor : (leader.Scale > 0 ? leader.Scale : 1.0);
            leader.TextHeight = size.Model / overall;

            return new
            {
                handle = leader.Handle.ToString(),
                leaderLineIndex = lineIndex,
                vertexCount = Try(() => leader.VerticesCount(lineIndex)),
                layer = leader.Layer,
                space = space.Name,
                annotative,
                annotationScale = scale?.Name,
                textHeight = size.Model,
                paperHeight = size.Paper,
                leaderScale = scaleWhole ? factor : (double?)null,
            };
        }));

    /// <summary>An optional array of [x,y(,z)] points; empty when absent.</summary>
    private static List<Point3d> PointList(JsonElement e, string name)
    {
        var points = new List<Point3d>();
        if (!HasValue(e, name)) return points;

        var array = e.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array)
            throw Args.WrongType(name, "an array of [x,y(,z)] points", array);

        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var c = Args.Coordinates(item, $"{name}[{i++}]");
            points.Add(new Point3d(c[0], c[1], c.Length > 2 ? c[2] : 0.0));
        }
        return points;
    }

    // ------------------------------------------------------------------ create_dimension

    // AlignedDimension(.ctor(Point3d, Point3d, Point3d, String, ObjectId)) and
    // RotatedDimension(.ctor(Double, Point3d, Point3d, Point3d, String, ObjectId)) are both
    // confirmed from the declared API surface. The other seven Dimension subclasses are deliberately NOT exposed:
    // their constructors are confirmable too, but their point semantics (chord vs far-chord,
    // jog angle, ordinate axis) are not, and a wrong guess would produce plausible-looking
    // garbage in a real drawing. Add them only with a live check.
    private static void RegisterCreateDimension() => Router.Register(
        "create_dimension",
        "Create a linear dimension in the current space between point1 and point2, with the "
      + "dimension line through linePoint. Args: point1, point2, linePoint (all [x,y(,z)], "
      + "required), type (aligned|rotated, default aligned), rotationDegrees (rotated only, "
      + "default 0), text (optional override; '<>' means the measured value), styleName "
      + "(optional dimension style), layer (optional, must already exist). "
      + "annotative (default: the dimension style's setting; AutoCAD does not apply it by itself): "
      + "an annotative dimension is flagged before it is added to the drawing, the only order "
      + "AutoCAD sizes correctly, and gets exactly one scale context: annotationScale (a scale name "
      + "such as '1/4\" = 1'-0\"' or a factor such as 48) or else the current CANNOSCALE in model "
      + "space, 1:1 in paper space. Its text, arrows and gaps are then the style's plotted sizes "
      + "times that scale. annotative:false on an annotative style sets a DIMSCALE override equal to "
      + "the scale instead, so the dimension plots at the style's sizes at that one scale. "
      + "Annotative adds no other scales, and the dimension only shows in viewports at its scale "
      + "unless ANNOALLVISIBLE is on. "
      + "Returns {handle, dimensionType, measurement, annotative, annotationScale, dimscale}.",
        mutating: true,
        e => Acad.Write<object?>(ctx =>
        {
            var p1 = Args.Point(e, "point1");
            var p2 = Args.Point(e, "point2");
            var linePoint = Args.Point(e, "linePoint");
            var type = (Args.StrOpt(e, "type", "aligned") ?? "aligned").Trim().ToLowerInvariant();
            var rotationDegrees = Args.DblOpt(e, "rotationDegrees", 0.0);
            var text = Args.StrOpt(e, "text");
            var styleName = Args.StrOpt(e, "styleName");
            var layer = Args.StrOpt(e, "layer");

            var spaceId = ctx.Db.CurrentSpaceId;
            var space = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);
            var styleId = string.IsNullOrWhiteSpace(styleName) ? ctx.Db.Dimstyle : ResolveDimStyle(ctx, styleName);

            var style = (DimStyleTableRecord)ctx.Tr.GetObject(styleId, OpenMode.ForRead);
            var styleAnnotative = style.Annotative == AnnotativeStates.True;
            var annotativeArg = AnnotativeArg(e);
            var annotative = annotativeArg ?? styleAnnotative;
            var dimscaleOverride = annotativeArg == false && styleAnnotative;

            if (!annotative && !dimscaleOverride && HasValue(e, "annotationScale"))
                throw new AcadMcpException("bad_argument",
                    "'annotationScale' has no effect on a non-annotative dimension with a non-annotative style.");

            var scale = annotative || dimscaleOverride ? ScaleFor(ctx, e, spaceId) : null;

            Dimension dim = type switch
            {
                "aligned" => new AlignedDimension(p1, p2, linePoint, text ?? string.Empty, styleId),
                "rotated" => new RotatedDimension(rotationDegrees * Math.PI / 180.0,
                                                  p1, p2, linePoint, text ?? string.Empty, styleId),
                _ => throw new AcadMcpException("bad_argument",
                         $"Unknown dimension type '{type}'. Use 'aligned' or 'rotated'."),
            };

            dim.SetDatabaseDefaults(ctx.Db);

            // SetDatabaseDefaults resets the style to the database current one, so re-apply after it.
            dim.DimensionStyle = styleId;

            if (text is not null) dim.DimensionText = text;
            ApplyLayer(ctx, dim, layer);

            // Before AppendEntity: flagged afterwards, AutoCAD divides DIMTXT by the scale and the
            // DIMSCALE/DIMTXT overrides then throw eInvalidInput.
            double? dimscale = null;
            if (annotative)
            {
                dim.Annotative = AnnotativeStates.True;
            }
            else if (dimscaleOverride)
            {
                dim.Annotative = AnnotativeStates.False;
                dim.Dimscale = (dimscale = Factor(scale!)).Value;
            }

            space.AppendEntity(dim);
            ctx.Tr.AddNewlyCreatedDBObject(dim, true);

            if (annotative && scale is not null) UseOnlyScale(ctx.Db, dim, scale);

            return new
            {
                handle = dim.Handle.ToString(),
                dimensionType = dim.GetType().Name,
                measurement = Try(() => dim.Measurement),
                styleName = TryStr(() => dim.DimensionStyleName),
                layer = dim.Layer,
                space = space.Name,
                annotative,
                annotationScale = scale?.Name,
                dimscale,
            };
        }));

    // ================================================================== MText formatting

    /// <summary>
    /// Strip MText/DBText control sequences down to readable text.
    ///
    /// Autodesk exposes MText.Text, which does the same job inside AutoCAD's formatting engine,
    /// but it is a per-object round trip through unmanaged code and is not defined for the other
    /// carriers (table cells, dimension overrides, attribute values) that all use the same code
    /// vocabulary. This does the whole vocabulary in one pass with no API risk.
    ///
    /// Field expressions (%&lt;\AcObjProp ...&gt;%) are left intact: their cached value is part of
    /// the payload and discarding them would lose information the caller may need.
    /// </summary>
    internal static string StripMTextFormatting(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        var i = 0;

        while (i < raw.Length)
        {
            var c = raw[i];

            if (c == '\\' && i + 1 < raw.Length)
            {
                var code = raw[i + 1];
                switch (code)
                {
                    // Escaped literals.
                    case '\\':
                    case '{':
                    case '}':
                        sb.Append(code);
                        i += 2;
                        continue;

                    case '~':                       // non-breaking space
                        sb.Append(' ');
                        i += 2;
                        continue;

                    case 'P':                       // hard line break
                    case 'N':                       // column break
                    case 'X':                       // dimension text above/below the dimension line
                        sb.Append('\n');
                        i += 2;
                        continue;

                    // Toggles with no argument.
                    case 'L':
                    case 'l':
                    case 'O':
                    case 'o':
                    case 'K':
                    case 'k':
                        i += 2;
                        continue;

                    // Argument codes: drop everything up to and including the terminating ';'.
                    //   \A alignment  \C colour index  \c true colour  \f \F font
                    //   \H height     \p paragraph     \Q oblique      \T tracking  \W width
                    case 'A':
                    case 'C':
                    case 'c':
                    case 'f':
                    case 'F':
                    case 'H':
                    case 'p':
                    case 'Q':
                    case 'T':
                    case 'W':
                    {
                        var end = raw.IndexOf(';', i + 2);
                        i = end < 0 ? raw.Length : end + 1;
                        continue;
                    }

                    // Stacked text: \S upper (^ | / | #) lower ;  — keep both halves, readable.
                    case 'S':
                    {
                        var end = raw.IndexOf(';', i + 2);
                        var body = end < 0 ? raw.Substring(i + 2) : raw.Substring(i + 2, end - (i + 2));
                        sb.Append(body.Replace("^", " ").Replace("#", "/"));
                        i = end < 0 ? raw.Length : end + 1;
                        continue;
                    }

                    default:
                        // Unknown escape: keep the payload character, drop the backslash.
                        sb.Append(code);
                        i += 2;
                        continue;
                }
            }

            // Grouping braces carry no text.
            if (c is '{' or '}')
            {
                i++;
                continue;
            }

            // %%x codes (shared with DBText).
            if (c == '%' && i + 2 < raw.Length && raw[i + 1] == '%')
            {
                switch (char.ToLowerInvariant(raw[i + 2]))
                {
                    case 'd': sb.Append('°'); i += 3; continue;   // degree U+00B0
                    case 'c': sb.Append('Ø'); i += 3; continue;   // diameter
                    case 'p': sb.Append('±'); i += 3; continue;   // plus/minus
                    case 'u':                                          // underline toggle
                    case 'o': i += 3; continue;                        // overline toggle
                    case '%': sb.Append('%'); i += 3; continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ================================================================== scope + traversal

    private static (string Scope, bool Xrefs, bool Anonymous, int Offset, int Limit) CommonArgs(JsonElement e)
    {
        var scope = Args.StrOpt(e, "scope", "layouts") ?? "layouts";
        var xrefs = Args.BoolOpt(e, "includeXrefs", false);
        var anonymous = Args.BoolOpt(e, "includeAnonymousBlocks", false);
        var offset = Math.Max(0, Args.IntOpt(e, "offset", 0));
        var limit = Math.Clamp(Args.IntOpt(e, "limit", DefaultLimit), 1, MaxLimit);
        return (scope, xrefs, anonymous, offset, limit);
    }

    private static HashSet<string> LayerFilter(JsonElement e)
    {
        var set = NameSet(Args.StrArrayOpt(e, "layers"));
        var single = Args.StrOpt(e, "layer");
        if (!string.IsNullOrWhiteSpace(single)) set.Add(single);
        return set;
    }

    private static HashSet<string> NameSet(string[] names) =>
        new(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which BlockTableRecords a scan covers.
    ///   model    — model space only
    ///   paper    — every paper space layout
    ///   layouts  — model space plus every paper space layout (default)
    ///   current  — whatever space the user is looking at
    ///   all      — the above plus block definitions
    /// Anonymous blocks are skipped by default: *D* holds a dimension's rendered geometry and
    /// *T* a table's rendered cells, so including them double-counts text already reported
    /// against the Dimension or Table itself.
    /// </summary>
    private static List<ObjectId> ResolveSpaces(AcadContext ctx, string scope, bool includeXrefs, bool includeAnonymous)
    {
        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        var ids = new List<ObjectId>();
        var key = scope.Trim().ToLowerInvariant();

        switch (key)
        {
            case "current":
                ids.Add(ctx.Db.CurrentSpaceId);
                return ids;

            case "model":
                if (!bt.Has(BlockTableRecord.ModelSpace))
                    throw new AcadMcpException("not_found", "This drawing has no model space block.");
                ids.Add(bt[BlockTableRecord.ModelSpace]);
                return ids;

            case "paper":
            case "layouts":
            case "all":
                break;

            default:
                throw new AcadMcpException("bad_argument",
                    $"Unknown scope '{scope}'. Use model, paper, layouts, current or all.");
        }

        var wantBlocks = key == "all";
        var paperOnly = key == "paper";

        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(btrId, OpenMode.ForRead); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
            catch (InvalidCastException) { continue; }

            if (btr.IsLayout)
            {
                var isModel = btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
                if (paperOnly && isModel) continue;
                ids.Add(btrId);
                continue;
            }

            if (!wantBlocks) continue;
            if (btr.IsFromExternalReference && !includeXrefs) continue;
            if (Try(() => btr.IsUnloaded) == true) continue;
            if (btr.IsAnonymous && !includeAnonymous) continue;

            ids.Add(btrId);
        }

        return ids;
    }

    private static void ForEachEntity(AcadContext ctx, List<ObjectId> spaces, Action<string, Entity> visit)
    {
        foreach (var spaceId in spaces)
        {
            if (spaceId.IsNull) continue;

            BlockTableRecord btr;
            try { btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
            catch (InvalidCastException) { continue; }

            var owner = btr.Name;

            foreach (ObjectId id in btr)
            {
                Entity ent;
                try { ent = (Entity)ctx.Tr.GetObject(id, OpenMode.ForRead); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }   // proxy / unloaded object enabler
                catch (InvalidCastException) { continue; }

                visit(owner, ent);
            }
        }
    }

    // ================================================================== per-entity readers

    private static string AttributeReferenceText(AttributeReference att)
    {
        if (Try(() => att.IsMTextAttribute) == true)
        {
            var multi = TryStr(() => att.MTextAttribute?.Contents);
            if (!string.IsNullOrEmpty(multi)) return multi;
        }
        return TryStr(() => att.TextString) ?? string.Empty;
    }

    private static string AttributeDefinitionText(AttributeDefinition def)
    {
        if (Try(() => def.IsMTextAttributeDefinition) == true)
        {
            var multi = TryStr(() => def.MTextAttributeDefinition?.Contents);
            if (!string.IsNullOrEmpty(multi)) return multi;
        }
        return TryStr(() => def.TextString) ?? string.Empty;
    }

    /// <summary>
    /// MLeader.MText hands back an MText the MLeader built for us. Its ownership is not
    /// documented in the declared API surface, so the contents are copied out immediately and the
    /// wrapper is left to its finalizer — disposing something AutoCAD may still own is a crash,
    /// not an exception.
    /// </summary>
    private static string MLeaderText(MLeader leader)
    {
        var raw = TryStr(() =>
            leader.ContentType == ContentType.MTextContent ? leader.MText?.Contents : null);
        return raw ?? string.Empty;
    }

    private static string? MLeaderStyleName(AcadContext ctx, MLeader leader)
    {
        try
        {
            var id = leader.MLeaderStyle;
            if (id.IsNull) return null;
            return ((MLeaderStyle)ctx.Tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    private static List<double[]> MLeaderVertices(MLeader leader)
    {
        var points = new List<double[]>();
        var lines = Try(() => leader.LeaderLineCount) ?? 0;

        for (var line = 0; line < lines; line++)
        {
            var count = Try(() => leader.VerticesCount(line)) ?? 0;
            for (var v = 0; v < count; v++)
            {
                var p = Try(() => leader.GetVertex(line, v));
                if (p is { } pt) points.Add(new[] { pt.X, pt.Y, pt.Z });
            }
        }

        return points;
    }

    private static List<double[]> LeaderVertices(Leader leader)
    {
        var points = new List<double[]>();
        var count = Try(() => leader.NumVertices) ?? 0;

        for (var v = 0; v < count; v++)
        {
            var p = Try(() => leader.VertexAt(v));
            if (p is { } pt) points.Add(new[] { pt.X, pt.Y, pt.Z });
        }

        return points;
    }

    private static string DimensionDisplayText(Dimension dim, string rawOverride)
    {
        var measured = Try(() => dim.Measurement);
        if (measured is { } m)
        {
            var formatted = TryStr(() => dim.FormatMeasurement(m, rawOverride));
            if (!string.IsNullOrEmpty(formatted)) return StripMTextFormatting(formatted);
        }
        return StripMTextFormatting(rawOverride);
    }

    private static Dictionary<string, object?> DimensionGeometry(Dimension dim)
    {
        // Keys are written camelCase because JsonSerializerOptions.PropertyNamingPolicy does not
        // apply to dictionary keys.
        var g = new Dictionary<string, object?>();

        switch (dim)
        {
            case RotatedDimension r:
                g["xLine1Point"] = PointArray(Try(() => r.XLine1Point));
                g["xLine2Point"] = PointArray(Try(() => r.XLine2Point));
                g["dimLinePoint"] = PointArray(Try(() => r.DimLinePoint));
                g["rotation"] = Try(() => r.Rotation);
                g["oblique"] = Try(() => r.Oblique);
                break;

            case AlignedDimension a:
                g["xLine1Point"] = PointArray(Try(() => a.XLine1Point));
                g["xLine2Point"] = PointArray(Try(() => a.XLine2Point));
                g["dimLinePoint"] = PointArray(Try(() => a.DimLinePoint));
                g["oblique"] = Try(() => a.Oblique);
                break;

            case RadialDimensionLarge rl:
                g["center"] = PointArray(Try(() => rl.Center));
                g["chordPoint"] = PointArray(Try(() => rl.ChordPoint));
                g["overrideCenter"] = PointArray(Try(() => rl.OverrideCenter));
                g["jogPoint"] = PointArray(Try(() => rl.JogPoint));
                g["jogAngle"] = Try(() => rl.JogAngle);
                break;

            case RadialDimension rd:
                g["center"] = PointArray(Try(() => rd.Center));
                g["chordPoint"] = PointArray(Try(() => rd.ChordPoint));
                g["leaderLength"] = Try(() => rd.LeaderLength);
                break;

            case DiametricDimension dd:
                g["chordPoint"] = PointArray(Try(() => dd.ChordPoint));
                g["farChordPoint"] = PointArray(Try(() => dd.FarChordPoint));
                g["leaderLength"] = Try(() => dd.LeaderLength);
                break;

            case ArcDimension arc:
                g["centerPoint"] = PointArray(Try(() => arc.CenterPoint));
                g["xLine1Point"] = PointArray(Try(() => arc.XLine1Point));
                g["xLine2Point"] = PointArray(Try(() => arc.XLine2Point));
                g["arcPoint"] = PointArray(Try(() => arc.ArcPoint));
                g["isPartial"] = Try(() => arc.IsPartial);
                g["hasLeader"] = Try(() => arc.HasLeader);
                break;

            case Point3AngularDimension p3:
                g["centerPoint"] = PointArray(Try(() => p3.CenterPoint));
                g["xLine1Point"] = PointArray(Try(() => p3.XLine1Point));
                g["xLine2Point"] = PointArray(Try(() => p3.XLine2Point));
                g["arcPoint"] = PointArray(Try(() => p3.ArcPoint));
                break;

            case LineAngularDimension2 la:
                g["xLine1Start"] = PointArray(Try(() => la.XLine1Start));
                g["xLine1End"] = PointArray(Try(() => la.XLine1End));
                g["xLine2Start"] = PointArray(Try(() => la.XLine2Start));
                g["xLine2End"] = PointArray(Try(() => la.XLine2End));
                g["arcPoint"] = PointArray(Try(() => la.ArcPoint));
                break;

            case OrdinateDimension od:
                g["definingPoint"] = PointArray(Try(() => od.DefiningPoint));
                g["leaderEndPoint"] = PointArray(Try(() => od.LeaderEndPoint));
                g["origin"] = PointArray(Try(() => od.Origin));
                g["usingXAxis"] = Try(() => od.UsingXAxis);
                break;
        }

        return g;
    }

    /// <summary>Cell text, or null when the cell holds nothing readable.</summary>
    private static string? CellText(Table table, int row, int col)
    {
        if (Try(() => table.IsEmpty(row, col)) == true) return null;

        // GetTextString(row, col, contentIndex) is the current API; it throws when the cell has
        // no content at that index, hence the guarded fallbacks.
        var text = TryStr(() => table.GetTextString(row, col, 0));
        if (string.IsNullOrEmpty(text)) text = TryStr(() => table.TextString(row, col));
        if (string.IsNullOrEmpty(text)) text = TryStr(() => table.Value(row, col)?.ToString());

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static int? AssociatedCount(Hatch hatch)
    {
        try
        {
            using var ids = hatch.GetAssociatedObjectIds();
            return ids?.Count ?? 0;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    // ================================================================== write helpers

    private static void ApplyLayer(AcadContext ctx, Entity entity, string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer)) return;

        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layer))
            throw new AcadMcpException("unknown_layer", $"No layer named '{layer}' in this drawing.");

        entity.Layer = layer;
    }

    private static ObjectId ResolveDimStyle(AcadContext ctx, string name)
    {
        var table = (DimStyleTable)ctx.Tr.GetObject(ctx.Db.DimStyleTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("unknown_style", $"No dimension style named '{name}'.");
        return table[name];
    }

    private static ObjectId ResolveMLeaderStyle(AcadContext ctx, string name)
    {
        var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        if (!dict.Contains(name))
            throw new AcadMcpException("unknown_style", $"No multileader style named '{name}'.");
        return dict.GetAt(name);
    }

    // ================================================================== annotative support
    //
    // Shared by create_text and create_mtext (EntityTools), create_dimension and create_mleader
    // (here) and insert_block (BlockTools). What AutoCAD 2027 does with objects made through the
    // API, measured in a drawing from the ACA sheet template at 1/4" = 1'-0" (factor 48):
    //   * DBText, MText and Dimension do NOT take the Annotative flag from their style; it has to
    //     be set on the entity. (MLeader does inherit it.)
    //   * A Dimension must be flagged BEFORE AppendEntity. Flagged afterwards its DIMTXT is divided
    //     by the scale (3/32" becomes 0.00195") and the text and arrows plot invisibly small.
    //   * Text height is not scaled for you: an annotative DBText or MText given 3/32" stays 3/32"
    //     in model space. The model height (paper × factor) has to be set.
    //   * MLeader.TextHeight IS in plotted units on an annotative leader (3/32" gives 4.5" text),
    //     but the MText handed to the leader carries TEXTSIZE, which wins over the style's height
    //     unless TextHeight is set again after the MText is assigned.
    //   * An annotative block reference gets no scale context on append and shows at definition
    //     size. Attributes have to be created AFTER AddContext, from the now-scaled BlockTransform;
    //     attributes created before it stay at plotted size.
    //   * Switching an object's context (AddContext + RemoveContext) rescales it proportionally,
    //     so sizes are set again once the context is settled.
    //   * Database.Cannoscale holds the Model tab's scale even while a layout is current; the
    //     CANNOSCALE system variable then reads the layout's 1:1.

    private const string ScaleCollection = "ACDB_ANNOTATIONSCALES";

    /// <summary>The 'annotative' argument: true, false, or null when absent (follow the style).</summary>
    internal static bool? AnnotativeArg(JsonElement args) =>
        HasValue(args, "annotative") ? Args.BoolOpt(args, "annotative", false) : null;

    /// <summary>An optional number argument, or null when absent.</summary>
    internal static double? NumberArg(JsonElement args, string name) =>
        HasValue(args, name) ? Args.Dbl(args, name) : null;

    internal static bool HasValue(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    /// <summary>Drawing units per plotted unit: 48 for 1/4" = 1'-0".</summary>
    internal static double Factor(AnnotationScale scale) => scale.DrawingUnits / scale.PaperUnits;

    /// <summary>
    /// The annotation scale an object created in <paramref name="spaceId"/> takes. Model space: the
    /// 'annotationScale' argument (a scale name or a factor), else the current scale. A layout: its
    /// 1:1 scale, since paper space plots 1:1 ('annotationScale' is refused there). A block
    /// definition: null, no context.
    /// </summary>
    internal static AnnotationScale? ScaleFor(AcadContext ctx, JsonElement args, ObjectId spaceId)
    {
        var scales = Scales(ctx.Db);
        JsonElement? given = HasValue(args, "annotationScale") ? args.GetProperty("annotationScale") : null;

        if (spaceId == SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db))
        {
            var current = CurrentModelScale(ctx, scales);
            return given is { } spec ? MatchScale(scales, spec, current.Name) : current;
        }

        var owner = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForRead);
        if (given is not null)
            throw new AcadMcpException("bad_argument",
                $"'annotationScale' only applies in model space; '{owner.Name}' is "
                + (owner.IsLayout ? "paper space, which always plots 1:1." : "a block definition."));

        if (!owner.IsLayout) return null;

        // With this layout current the CANNOSCALE variable names its 1:1 scale; otherwise prefer
        // one literally called 1:1 when a drawing lists several.
        var preferred = ctx.Db.CurrentSpaceId == spaceId ? AcApp.GetSystemVariable("CANNOSCALE") as string : "1:1";
        var index = Sizing(() => AnnotationSizing.Find(Infos(scales), 1.0, preferred));
        return index is { } i
            ? scales[i]
            : throw new AcadMcpException("not_found",
                "This drawing lists no 1:1 annotation scale, which an annotative object in paper space needs.",
                "Pass annotative: false, or add a 1:1 scale with SCALELISTEDIT.");
    }

    /// <summary>
    /// Leaves the entity with exactly one scale context. AddContext comes first, so a context the
    /// entity picked up on append (the current scale) can then be removed safely.
    /// </summary>
    internal static void UseOnlyScale(Database db, Entity entity, AnnotationScale scale)
    {
        if (!entity.HasContext(scale)) entity.AddContext(scale);

        foreach (ObjectContext context in db.ObjectContextManager.GetContextCollection(ScaleCollection))
        {
            if (!string.Equals(context.Name, scale.Name, StringComparison.Ordinal) && entity.HasContext(context))
                entity.RemoveContext(context);
        }
    }

    /// <summary>True when the entity carries this scale context and no other.</summary>
    internal static bool HasOnlyScale(Database db, Entity entity, AnnotationScale scale)
    {
        if (!entity.HasContext(scale)) return false;

        foreach (ObjectContext context in db.ObjectContextManager.GetContextCollection(ScaleCollection))
        {
            if (!string.Equals(context.Name, scale.Name, StringComparison.Ordinal) && entity.HasContext(context))
                return false;
        }
        return true;
    }

    /// <summary>Runs Contracts sizing logic, reporting its argument errors as bad_argument.</summary>
    internal static T Sizing<T>(Func<T> resolve)
    {
        try { return resolve(); }
        catch (ToolArgumentException ex) { throw new AcadMcpException(ToolArgumentException.Code, ex.Message, ex.Detail); }
    }

    private static List<AnnotationScale> Scales(Database db)
    {
        var list = new List<AnnotationScale>();
        foreach (ObjectContext context in db.ObjectContextManager.GetContextCollection(ScaleCollection))
            if (context is AnnotationScale scale) list.Add(scale);
        return list;
    }

    private static List<AnnotationSizing.Scale> Infos(List<AnnotationScale> scales) =>
        scales.Select(s => new AnnotationSizing.Scale(s.Name, s.DrawingUnits, s.PaperUnits)).ToList();

    /// <summary>
    /// In model space (the Model tab, or inside a layout viewport) the CANNOSCALE variable is the
    /// scale AutoCAD's own commands give new objects. With paper space current it reads 1:1, but
    /// Database.Cannoscale still holds the Model tab's scale.
    /// </summary>
    private static AnnotationScale CurrentModelScale(AcadContext ctx, List<AnnotationScale> scales)
    {
        var inModel = ctx.Db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db);
        var name = inModel ? AcApp.GetSystemVariable("CANNOSCALE") as string : null;
        name ??= ctx.Db.Cannoscale.Name;
        return scales.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal)) ?? ctx.Db.Cannoscale;
    }

    private static AnnotationScale MatchScale(List<AnnotationScale> scales, JsonElement spec, string? preferred)
    {
        var infos = Infos(scales);
        var index = spec.ValueKind switch
        {
            JsonValueKind.String => Sizing(() => AnnotationSizing.Find(infos, spec.GetString()!, preferred)),
            JsonValueKind.Number => Sizing(() => AnnotationSizing.Find(infos, spec.GetDouble(), preferred)),
            _ => throw Args.WrongType("annotationScale",
                     "a scale name such as '1/4\" = 1'-0\"' or a factor such as 48", spec),
        };

        if (index is { } i) return scales[i];

        var asText = spec.ValueKind == JsonValueKind.String ? spec.GetString() : spec.GetRawText();
        throw new AcadMcpException("not_found",
            $"No annotation scale in this drawing matches '{asText}'.",
            "Available: " + string.Join(", ", scales.Select(s =>
                $"{s.Name} (factor {Factor(s).ToString("0.####", CultureInfo.InvariantCulture)})"))
            + ". This tool does not add scales; SCALELISTEDIT does.");
    }

    // ================================================================== small utilities

    /// <summary>
    /// Counts every match but only materialises the requested page, so a 200k-entity drawing
    /// costs one pass and one page of memory.
    /// </summary>
    private sealed class Pager
    {
        private readonly List<object> _items = new();
        private readonly int _offset;
        private readonly int _limit;
        private int _total;

        internal Pager(int offset, int limit)
        {
            _offset = offset;
            _limit = limit;
        }

        /// <summary>
        /// Counts the match, then builds the JSON row only if it falls inside the requested page.
        /// The projector runs synchronously, so it is still inside the caller's transaction.
        /// </summary>
        internal void Add(Func<object> project)
        {
            var index = _total++;
            if (index >= _offset && _items.Count < _limit) _items.Add(project());
        }

        internal object Result() => new
        {
            items = _items,
            total = _total,
            offset = _offset,
            limit = _limit,
            truncated = _offset + _items.Count < _total,
        };
    }

    private static double[]? PointArray(Point3d? point) =>
        point is { } p ? new[] { p.X, p.Y, p.Z } : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Read a struct-valued property that AutoCAD may refuse to compute.</summary>
    private static T? Try<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    /// <summary>Read a string-valued property that AutoCAD may refuse to compute.</summary>
    private static string? TryStr(Func<string?> read)
    {
        try { return read(); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }
}
