using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Paper space: layouts, their plot configuration, the viewports inside them — plus AutoCAD's
/// own native selection engine (ssget) exposed as a query tool.
/// </summary>
internal static class LayoutTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 10000;

    internal static void Register()
    {
        RegisterListLayouts();
        RegisterGetLayout();
        RegisterSetCurrentLayout();
        RegisterListViewports();
        RegisterCreateLayout();
        RegisterDeleteLayout();
        RegisterSelectEntities();
        RegisterGetCurrentSelection();
    }

    // ---------------------------------------------------------------- list_layouts

    private static void RegisterListLayouts()
    {
        Router.Register("list_layouts",
            "Every layout tab (including Model) with tab order, plot configuration and entity count. " +
            "Args: includeModel (default true), includeCounts (default true — set false to skip the " +
            "per-block entity walk on a very large drawing), limit (default 500), offset (default 0).",
            mutating: false,
            e => Acad.Read<object?>(ctx =>
            {
                var includeModel = Args.BoolOpt(e, "includeModel", true);
                var includeCounts = Args.BoolOpt(e, "includeCounts", true);
                var (limit, offset) = Paging(e);
                var current = CurrentLayoutName();

                var rows = new List<(string Key, object Data)>();

                foreach (var (name, id) in LayoutIds(ctx))
                {
                    var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);
                    if (!includeModel && layout.ModelType) continue;

                    var order = layout.TabOrder;
                    rows.Add((SortKey(order, name), new
                    {
                        name = layout.LayoutName,
                        handle = layout.Handle.ToString(),
                        tabOrder = order,
                        isModelSpace = layout.ModelType,
                        isCurrent = string.Equals(layout.LayoutName, current, StringComparison.OrdinalIgnoreCase),
                        tabSelected = layout.TabSelected,
                        annoAllVisible = layout.AnnoAllVisible,
                        blockHandle = HandleOf(layout.BlockTableRecordId),
                        entityCount = includeCounts ? CountEntities(ctx, layout.BlockTableRecordId) : (int?)null,
                        viewportCount = ViewportCount(layout),
                        plot = PlotInfo(layout),
                    }));
                }

                return Paged(rows, offset, limit);
            }));
    }

    // ------------------------------------------------------------------ get_layout

    private static void RegisterGetLayout()
    {
        Router.Register("get_layout",
            "One layout in full, with its viewports (centre, size, view centre, scale, lock state, " +
            "frozen layers). Args: name (optional — defaults to the current layout), " +
            "includeFrozenLayers (default true), resolveMediaName (default false — asks the plot " +
            "device for the human paper name, which can be slow), limit/offset page the viewports.",
            mutating: false,
            e => Acad.Read<object?>(ctx =>
            {
                var requested = Args.StrOpt(e, "name");
                var includeFrozen = Args.BoolOpt(e, "includeFrozenLayers", true);
                var resolveMedia = Args.BoolOpt(e, "resolveMediaName", false);
                var (limit, offset) = Paging(e);

                var name = requested is null ? CurrentLayoutName() : ResolveLayoutName(ctx, requested);
                var id = LayoutIdByName(ctx, name);
                var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);

                var rows = new List<(string Key, object Data)>();
                foreach (var vpId in ViewportIds(layout))
                {
                    if (ctx.Tr.GetObject(vpId, OpenMode.ForRead) is not Viewport vp) continue;
                    rows.Add((SortKey(vp.Number, vpId.Handle.ToString()),
                              ViewportInfo(ctx, vp, layout.LayoutName, includeFrozen)));
                }

                return new
                {
                    name = layout.LayoutName,
                    handle = layout.Handle.ToString(),
                    tabOrder = layout.TabOrder,
                    isModelSpace = layout.ModelType,
                    isCurrent = string.Equals(layout.LayoutName, CurrentLayoutName(), StringComparison.OrdinalIgnoreCase),
                    tabSelected = layout.TabSelected,
                    annoAllVisible = layout.AnnoAllVisible,
                    blockHandle = HandleOf(layout.BlockTableRecordId),
                    entityCount = CountEntities(ctx, layout.BlockTableRecordId),
                    limits = Extents2dInfo(layout),
                    extents = Extents3dInfo(layout),
                    plot = PlotInfo(layout),
                    localeMediaName = resolveMedia ? LocaleMediaName(layout) : null,
                    viewports = Paged(rows, offset, limit),
                };
            }));
    }

    // ---------------------------------------------------------- set_current_layout

    private static void RegisterSetCurrentLayout()
    {
        Router.Register("set_current_layout",
            "Switch the active layout tab by name ('Model' for model space). Args: name.",
            mutating: true,
            e => Acad.Write<object?>(ctx =>
            {
                var wanted = Args.Str(e, "name");
                var name = ResolveLayoutName(ctx, wanted);
                var previous = CurrentLayoutName();

                var id = LayoutIdByName(ctx, name);
                var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);
                var isModel = layout.ModelType;
                var handle = layout.Handle.ToString();
                var tabOrder = layout.TabOrder;

                if (!string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
                    LayoutManager.Current.CurrentLayout = name;

                return new
                {
                    name,
                    handle,
                    tabOrder,
                    isModelSpace = isModel,
                    previousLayout = previous,
                    changed = !string.Equals(previous, name, StringComparison.OrdinalIgnoreCase),
                };
            }));
    }

    // --------------------------------------------------------------- list_viewports

    private static void RegisterListViewports()
    {
        Router.Register("list_viewports",
            "Every paper space Viewport entity across all layouts. Args: layout (optional name " +
            "filter), includeFrozenLayers (default false), limit (default 500), offset (default 0).",
            mutating: false,
            e => Acad.Read<object?>(ctx =>
            {
                var only = Args.StrOpt(e, "layout");
                var includeFrozen = Args.BoolOpt(e, "includeFrozenLayers", false);
                var (limit, offset) = Paging(e);

                if (only is not null) only = ResolveLayoutName(ctx, only);

                var rows = new List<(string Key, object Data)>();

                foreach (var (name, id) in LayoutIds(ctx))
                {
                    var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);
                    if (layout.ModelType) continue;
                    if (only is not null && !string.Equals(layout.LayoutName, only, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var vpId in ViewportIds(layout))
                    {
                        if (ctx.Tr.GetObject(vpId, OpenMode.ForRead) is not Viewport vp) continue;
                        rows.Add((SortKey(layout.TabOrder, name) + "|" + SortKey(vp.Number, vpId.Handle.ToString()),
                                  ViewportInfo(ctx, vp, layout.LayoutName, includeFrozen)));
                    }
                }

                return Paged(rows, offset, limit);
            }));
    }

    // -------------------------------------------------------------- create_layout

    private static void RegisterCreateLayout()
    {
        Router.Register("create_layout",
            "Create a new layout tab. Args: name, setCurrent (default false).",
            mutating: true,
            e => Acad.Write<object?>(ctx =>
            {
                var name = Args.Str(e, "name").Trim();
                if (name.Length == 0)
                    throw new AcadMcpException("bad_argument", "Argument 'name' must not be blank.");

                var lm = LayoutManager.Current;
                if (lm.LayoutExists(name))
                    throw new AcadMcpException("layout_exists", $"A layout named '{name}' already exists.");

                ObjectId id;
                try { id = lm.CreateLayout(name); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw new AcadMcpException("create_failed",
                        $"AutoCAD refused to create layout '{name}'.", ex.ErrorStatus.ToString());
                }

                var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);
                var created = new
                {
                    name = layout.LayoutName,
                    handle = layout.Handle.ToString(),
                    tabOrder = layout.TabOrder,
                    blockHandle = HandleOf(layout.BlockTableRecordId),
                    isModelSpace = layout.ModelType,
                    plot = PlotInfo(layout),
                };

                var setCurrent = Args.BoolOpt(e, "setCurrent", false);
                if (setCurrent) lm.CurrentLayout = created.name;

                return new { created, isCurrent = setCurrent };
            }));
    }

    // -------------------------------------------------------------- delete_layout

    private static void RegisterDeleteLayout()
    {
        Router.Register("delete_layout",
            "Delete a layout tab and everything drawn on it. Model space cannot be deleted, nor the " +
            "last remaining paper layout. Args: name.",
            mutating: true,
            e => Acad.Write<object?>(ctx =>
            {
                var name = ResolveLayoutName(ctx, Args.Str(e, "name"));
                var id = LayoutIdByName(ctx, name);
                var layout = (Layout)ctx.Tr.GetObject(id, OpenMode.ForRead);

                if (layout.ModelType)
                    throw new AcadMcpException("cannot_delete_model", "Model space cannot be deleted.");

                var paperCount = 0;
                foreach (var (_, otherId) in LayoutIds(ctx))
                {
                    var other = (Layout)ctx.Tr.GetObject(otherId, OpenMode.ForRead);
                    if (!other.ModelType) paperCount++;
                }
                if (paperCount <= 1)
                    throw new AcadMcpException("last_layout",
                        $"'{name}' is the only paper layout; AutoCAD requires at least one.");

                // Snapshot before the object is erased — nothing may touch 'layout' after this point.
                var deleted = new
                {
                    name = layout.LayoutName,
                    handle = layout.Handle.ToString(),
                    tabOrder = layout.TabOrder,
                    entityCount = CountEntities(ctx, layout.BlockTableRecordId),
                };
                var wasCurrent = string.Equals(deleted.name, CurrentLayoutName(), StringComparison.OrdinalIgnoreCase);

                try { LayoutManager.Current.DeleteLayout(deleted.name); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw new AcadMcpException("delete_failed",
                        $"AutoCAD refused to delete layout '{deleted.name}'.", ex.ErrorStatus.ToString());
                }

                return new { deleted, wasCurrent, currentLayout = CurrentLayoutName() };
            }));
    }

    // ------------------------------------------------------------ select_entities

    private static void RegisterSelectEntities()
    {
        Router.Register("select_entities",
            "Run AutoCAD's own selection engine (ssget \"X\") over the whole drawing and return the " +
            "matching handles — far faster than walking a block. Args: types (DXF names, e.g. " +
            "[\"LINE\",\"LWPOLYLINE\"], wildcards allowed), layers (names, wildcards allowed), " +
            "colors (ACI indices, OR-ed), layout (name; 'Model' for model space), blocks (block " +
            "names for INSERTs), filter (raw DXF pairs as [[code, value], ...] merged in last), " +
            "includeDetails (default true — opens each returned entity for layer/colour), " +
            "limit (default 500), offset (default 0). Frozen and off layers are included.",
            mutating: false,
            e => Acad.Read<object?>(ctx =>
            {
                var (limit, offset) = Paging(e);
                var includeDetails = Args.BoolOpt(e, "includeDetails", true);

                var layoutArg = Args.StrOpt(e, "layout");
                if (layoutArg is not null) layoutArg = ResolveLayoutName(ctx, layoutArg);

                var values = BuildFilter(e, layoutArg);
                var filter = values.Count > 0 ? new SelectionFilter(values.ToArray()) : null;

                PromptSelectionResult res;
                try { res = filter is null ? ctx.Ed.SelectAll() : ctx.Ed.SelectAll(filter); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw new AcadMcpException("selection_failed",
                        "AutoCAD rejected the selection filter.", ex.ErrorStatus.ToString());
                }

                var ids = res.Status == PromptStatus.OK && res.Value is not null
                    ? res.Value.GetObjectIds()
                    : Array.Empty<ObjectId>();

                return new
                {
                    filter = values.Select(v => new { code = (int)v.TypeCode, value = v.Value?.ToString() }).ToList(),
                    status = res.Status.ToString(),
                    result = PageIds(ctx, ids, offset, limit, includeDetails),
                };
            }));
    }

    // ------------------------------------------------------ get_current_selection

    private static void RegisterGetCurrentSelection()
    {
        Router.Register("get_current_selection",
            "Whatever the user has selected in the AutoCAD window right now (the pickfirst set). " +
            "Args: includeDetails (default true), limit (default 500), offset (default 0).",
            mutating: false,
            e => Acad.Read<object?>(ctx =>
            {
                var (limit, offset) = Paging(e);
                var includeDetails = Args.BoolOpt(e, "includeDetails", true);

                PromptSelectionResult res;
                try { res = ctx.Ed.SelectImplied(); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw new AcadMcpException("selection_failed",
                        "Could not read the current selection.", ex.ErrorStatus.ToString());
                }

                // PromptStatus.Error simply means "nothing is selected" here — not a failure.
                var ids = res.Status == PromptStatus.OK && res.Value is not null
                    ? res.Value.GetObjectIds()
                    : Array.Empty<ObjectId>();

                return new
                {
                    status = res.Status.ToString(),
                    hasSelection = ids.Length > 0,
                    currentLayout = CurrentLayoutName(),
                    result = PageIds(ctx, ids, offset, limit, includeDetails),
                };
            }));
    }

    // ------------------------------------------------------------- filter building

    private static List<TypedValue> BuildFilter(JsonElement e, string? layoutName)
    {
        var values = new List<TypedValue>();

        var types = Args.StrArrayOpt(e, "types");
        if (types.Length == 0 && Args.StrOpt(e, "type") is { } single) types = [single];
        if (types.Length > 0)
            values.Add(new TypedValue(0, string.Join(",", types.Select(t => t.Trim().ToUpperInvariant()))));

        var blocks = Args.StrArrayOpt(e, "blocks");
        if (blocks.Length > 0)
            values.Add(new TypedValue(2, string.Join(",", blocks)));

        var layers = Args.StrArrayOpt(e, "layers");
        if (layers.Length == 0 && Args.StrOpt(e, "layer") is { } oneLayer) layers = [oneLayer];
        if (layers.Length > 0)
            values.Add(new TypedValue(8, string.Join(",", layers)));

        // DXF 62 is numeric, so a comma list does not work — several colours need an explicit OR group.
        var colors = IntArrayOpt(e, "colors");
        if (colors.Length == 0 && e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number)
            colors = [c.GetInt32()];

        if (colors.Length == 1)
        {
            values.Add(new TypedValue(62, (short)colors[0]));
        }
        else if (colors.Length > 1)
        {
            values.Add(new TypedValue(-4, "<OR"));
            foreach (var ci in colors) values.Add(new TypedValue(62, (short)ci));
            values.Add(new TypedValue(-4, "OR>"));
        }

        if (layoutName is not null)
            values.Add(new TypedValue(410, layoutName));

        foreach (var raw in RawFilter(e)) values.Add(raw);

        return values;
    }

    /// <summary>Raw escape hatch: [[code, value], ...] or [{"code":0,"value":"LINE"}, ...].</summary>
    private static List<TypedValue> RawFilter(JsonElement e)
    {
        var list = new List<TypedValue>();
        if (e.ValueKind != JsonValueKind.Object ||
            !e.TryGetProperty("filter", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            int code;
            JsonElement val;

            if (item.ValueKind == JsonValueKind.Array)
            {
                var parts = item.EnumerateArray().ToArray();
                if (parts.Length != 2 || parts[0].ValueKind != JsonValueKind.Number)
                    throw new AcadMcpException("bad_argument",
                        "Each 'filter' entry must be [dxfCode, value] or {code, value}.");
                code = parts[0].GetInt32();
                val = parts[1];
            }
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number &&
                     item.TryGetProperty("value", out var valEl))
            {
                code = codeEl.GetInt32();
                val = valEl;
            }
            else
            {
                throw new AcadMcpException("bad_argument",
                    "Each 'filter' entry must be [dxfCode, value] or {code, value}.");
            }

            list.Add(new TypedValue(code, CoerceDxf(code, val)));
        }
        return list;
    }

    /// <summary>DXF group codes are typed by range; ssget rejects a value of the wrong CLR type.</summary>
    private static object CoerceDxf(int code, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString()!;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean() ? (short)1 : (short)0;
        if (v.ValueKind != JsonValueKind.Number)
            throw new AcadMcpException("bad_argument", $"DXF code {code} needs a string or number value.");

        // 60-79 and 170-179 and 270-289 and 370-389 are 16-bit; 90-99 and 420-429 are 32-bit;
        // everything else numeric is a double.
        var isInt16 = code is (>= 60 and <= 79) or (>= 170 and <= 179) or (>= 270 and <= 289) or (>= 370 and <= 389);
        var isInt32 = code is (>= 90 and <= 99) or (>= 420 and <= 429) or 1071;

        if (isInt16) return (short)Args.Integer(v, $"filter (DXF {code})");
        if (isInt32) return Args.Integer(v, $"filter (DXF {code})");
        return v.GetDouble();
    }

    private static int[] IntArrayOpt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object ||
            !e.TryGetProperty(name, out var arr) || arr.ValueKind == JsonValueKind.Null)
            return [];

        return Args.Integers(arr, name);
    }

    // ------------------------------------------------------------------- shaping

    /// <summary>A proxy or damaged viewport must not take the whole layout down with it.</summary>
    private static object ViewportInfo(AcadContext ctx, Viewport vp, string layoutName, bool includeFrozen)
    {
        try
        {
            return ViewportInfoCore(ctx, vp, layoutName, includeFrozen);
        }
        catch (System.Exception ex)
        {
            return new
            {
                handle = HandleOf(vp.ObjectId),
                layout = layoutName,
                error = ex.Message,
            };
        }
    }

    private static object ViewportInfoCore(AcadContext ctx, Viewport vp, string layoutName, bool includeFrozen)
    {
        List<string>? frozen = null;
        if (includeFrozen) frozen = FrozenLayerNames(ctx, vp);

        string? annoScale = null;
        double? annoScaleValue = null;
        try
        {
            var scale = vp.AnnotationScale;
            if (scale is not null) { annoScale = scale.Name; annoScaleValue = scale.Scale; }
        }
        catch (System.Exception) { /* viewport has no annotation scale context */ }

        double? twist = null;
        try { twist = vp.TwistAngle; } catch (System.Exception) { }

        return new
        {
            handle = vp.Handle.ToString(),
            layout = layoutName,
            number = vp.Number,
            // Number 1 is the pseudo-viewport that represents the sheet itself, not a window on the model.
            isPaperSpaceViewport = vp.Number == 1,
            layer = vp.Layer,
            on = vp.On,
            locked = vp.Locked,
            center = new { x = vp.CenterPoint.X, y = vp.CenterPoint.Y, z = vp.CenterPoint.Z },
            width = vp.Width,
            height = vp.Height,
            viewCenter = new { x = vp.ViewCenter.X, y = vp.ViewCenter.Y },
            viewHeight = vp.ViewHeight,
            viewTarget = new { x = vp.ViewTarget.X, y = vp.ViewTarget.Y, z = vp.ViewTarget.Z },
            viewDirection = new { x = vp.ViewDirection.X, y = vp.ViewDirection.Y, z = vp.ViewDirection.Z },
            twistAngle = twist,
            customScale = vp.CustomScale,
            standardScale = vp.StandardScale.ToString(),
            usesCustomScale = vp.StandardScale == StandardScaleType.CustomScale,
            perspectiveOn = vp.PerspectiveOn,
            annotationScale = annoScale,
            annotationScaleValue = annoScaleValue,
            nonRectangularClip = vp.NonRectClipOn,
            clipEntityHandle = vp.NonRectClipOn ? HandleOf(vp.NonRectClipEntityId) : null,
            shadePlot = vp.ShadePlot.ToString(),
            plotStyleSheet = NullIfEmpty(vp.PlotStyleSheet),
            linkedToSheetView = vp.LinkedToSheetView,
            frozenLayers = frozen,
            frozenLayerCount = frozen?.Count,
        };
    }

    private static List<string>? FrozenLayerNames(AcadContext ctx, Viewport vp)
    {
        try
        {
            var ids = vp.GetFrozenLayers();
            if (ids is null) return [];

            var names = new List<string>();
            foreach (ObjectId id in ids)
            {
                try
                {
                    if (ctx.Tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord layer)
                        names.Add(layer.Name);
                }
                catch (System.Exception) { /* erased layer */ }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static object PlotInfo(PlotSettings ps)
    {
        try
        {
            var custom = ps.CustomPrintScale;
            return new
            {
                device = NullIfEmpty(ps.PlotConfigurationName),
                canonicalMediaName = NullIfEmpty(ps.CanonicalMediaName),
                plotStyleSheet = NullIfEmpty(ps.CurrentStyleSheet),
                paperWidth = ps.PlotPaperSize.X,
                paperHeight = ps.PlotPaperSize.Y,
                paperUnits = ps.PlotPaperUnits.ToString(),
                rotation = ps.PlotRotation.ToString(),
                plotArea = ps.PlotType.ToString(),
                plotViewName = NullIfEmpty(ps.PlotViewName),
                useStandardScale = ps.UseStandardScale,
                standardScale = ps.StdScaleType.ToString(),
                scaleFactor = ps.StdScale,
                customScaleNumerator = custom.Numerator,
                customScaleDenominator = custom.Denominator,
                scaleText = ps.UseStandardScale
                    ? ps.StdScaleType.ToString()
                    : $"{custom.Numerator:0.####}:{custom.Denominator:0.####}",
                centered = ps.PlotCentered,
                originX = ps.PlotOrigin.X,
                originY = ps.PlotOrigin.Y,
                marginMinX = ps.PlotPaperMargins.MinPoint.X,
                marginMinY = ps.PlotPaperMargins.MinPoint.Y,
                marginMaxX = ps.PlotPaperMargins.MaxPoint.X,
                marginMaxY = ps.PlotPaperMargins.MaxPoint.Y,
                shadePlot = ps.ShadePlot.ToString(),
                plotStylesApplied = ps.PlotPlotStyles,
                showPlotStyles = ps.ShowPlotStyles,
                plotTransparency = ps.PlotTransparency,
                plotHidden = ps.PlotHidden,
                printLineweights = ps.PrintLineweights,
                scaleLineweights = ps.ScaleLineweights,
                plotViewportBorders = ps.PlotViewportBorders,
                drawViewportsFirst = ps.DrawViewportsFirst,
                isModelTypeSettings = ps.ModelType,
            };
        }
        catch (System.Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    /// <summary>The human paper name ("ISO A1") behind the canonical one. Asks the plot device, so it can be slow.</summary>
    private static string? LocaleMediaName(PlotSettings ps)
    {
        try
        {
            var canonical = ps.CanonicalMediaName;
            if (string.IsNullOrEmpty(canonical)) return null;
            return PlotSettingsValidator.Current.GetLocaleMediaName(ps, canonical);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static object? Extents2dInfo(Layout layout)
    {
        try
        {
            var ex = layout.Limits;
            return new
            {
                minX = ex.MinPoint.X, minY = ex.MinPoint.Y,
                maxX = ex.MaxPoint.X, maxY = ex.MaxPoint.Y,
            };
        }
        catch (System.Exception) { return null; }
    }

    private static object? Extents3dInfo(Layout layout)
    {
        try
        {
            var ex = layout.Extents;
            return new
            {
                minX = ex.MinPoint.X, minY = ex.MinPoint.Y, minZ = ex.MinPoint.Z,
                maxX = ex.MaxPoint.X, maxY = ex.MaxPoint.Y, maxZ = ex.MaxPoint.Z,
            };
        }
        catch (System.Exception) { return null; }
    }

    // ------------------------------------------------------- entity id projection

    private static object PageIds(AcadContext ctx, ObjectId[] ids, int offset, int limit, bool includeDetails)
    {
        var total = ids.Length;
        if (offset > total) offset = total;
        var take = Math.Min(limit, total - offset);

        var items = new List<object>(take);
        var spaceCache = new Dictionary<ObjectId, string?>();

        for (var i = offset; i < offset + take; i++)
            items.Add(EntitySummary(ctx, ids[i], includeDetails, spaceCache));

        return new
        {
            items,
            total,
            offset,
            limit,
            truncated = offset + take < total,
        };
    }

    private static object EntitySummary(AcadContext ctx, ObjectId id, bool includeDetails,
                                        Dictionary<ObjectId, string?> spaceCache)
    {
        string? dxf = null;
        string? className = null;
        try { dxf = NullIfEmpty(id.ObjectClass.DxfName); className = id.ObjectClass.Name; }
        catch (System.Exception) { /* proxy with no runtime class */ }

        string? layer = null;
        int? colorIndex = null;
        string? color = null;
        string? space = null;

        if (includeDetails)
        {
            try
            {
                if (ctx.Tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                {
                    layer = ent.Layer;
                    colorIndex = ent.ColorIndex;
                    color = ent.Color.ToString();
                    space = SpaceName(ctx, ent.BlockId, spaceCache);
                }
            }
            catch (System.Exception) { /* erased or unreadable — handle is still worth returning */ }
        }

        return new
        {
            handle = id.Handle.ToString(),
            type = dxf,
            className,
            layer,
            colorIndex,
            color,
            space,
            isErased = id.IsErased,
        };
    }

    private static string? SpaceName(AcadContext ctx, ObjectId blockId, Dictionary<ObjectId, string?> cache)
    {
        if (blockId.IsNull) return null;
        if (cache.TryGetValue(blockId, out var cached)) return cached;

        string? name = null;
        try
        {
            if (ctx.Tr.GetObject(blockId, OpenMode.ForRead) is BlockTableRecord btr)
            {
                if (btr.IsLayout && !btr.LayoutId.IsNull &&
                    ctx.Tr.GetObject(btr.LayoutId, OpenMode.ForRead) is Layout layout)
                    name = layout.LayoutName;
                else
                    name = btr.Name;
            }
        }
        catch (System.Exception) { }

        cache[blockId] = name;
        return name;
    }

    // ------------------------------------------------------------ layout plumbing

    /// <summary>Name -> Layout ObjectId for every layout in the drawing, in dictionary order.</summary>
    private static List<(string Name, ObjectId Id)> LayoutIds(AcadContext ctx)
    {
        var result = new List<(string Name, ObjectId Id)>();
        var dict = (DBDictionary)ctx.Tr.GetObject(ctx.Db.LayoutDictionaryId, OpenMode.ForRead);

        foreach (DBDictionaryEntry entry in dict)
        {
            if (entry.Value.IsNull || entry.Value.IsErased) continue;
            result.Add((entry.Key, entry.Value));
        }
        return result;
    }

    private static ObjectId LayoutIdByName(AcadContext ctx, string name)
    {
        foreach (var (key, id) in LayoutIds(ctx))
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        throw new AcadMcpException("not_found", $"No layout named '{name}' in this drawing.");
    }

    /// <summary>Case-insensitive lookup that returns the layout's canonical spelling.</summary>
    private static string ResolveLayoutName(AcadContext ctx, string requested)
    {
        var all = LayoutIds(ctx);
        foreach (var (key, _) in all)
        {
            if (string.Equals(key, requested, StringComparison.OrdinalIgnoreCase)) return key;
        }

        var known = string.Join(", ", all.Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new AcadMcpException("not_found",
            $"No layout named '{requested}' in this drawing.", $"Layouts: {known}");
    }

    private static string CurrentLayoutName()
    {
        try { return LayoutManager.Current.CurrentLayout; }
        catch (System.Exception) { return string.Empty; }
    }

    private static List<ObjectId> ViewportIds(Layout layout)
    {
        var result = new List<ObjectId>();
        try
        {
            var ids = layout.GetViewports();
            if (ids is null) return result;
            foreach (ObjectId id in ids)
            {
                if (!id.IsNull && !id.IsErased) result.Add(id);
            }
        }
        catch (System.Exception) { /* model space and malformed layouts have none */ }
        return result;
    }

    private static int ViewportCount(Layout layout) => ViewportIds(layout).Count;

    private static int CountEntities(AcadContext ctx, ObjectId blockId)
    {
        if (blockId.IsNull) return 0;
        try
        {
            var btr = (BlockTableRecord)ctx.Tr.GetObject(blockId, OpenMode.ForRead);
            var n = 0;
            foreach (ObjectId _ in btr) n++;
            return n;
        }
        catch (System.Exception)
        {
            return -1;
        }
    }

    // ------------------------------------------------------------------ utilities

    private static (int Limit, int Offset) Paging(JsonElement e)
    {
        var limit = Math.Clamp(Args.IntOpt(e, "limit", DefaultLimit), 1, MaxLimit);
        var offset = Math.Max(0, Args.IntOpt(e, "offset", 0));
        return (limit, offset);
    }

    /// <summary>
    /// Ordering key carried alongside the payload — the payload is an anonymous type, so the
    /// compiler cannot infer a key selector over it.
    /// </summary>
    private static string SortKey(int order, string name) =>
        order.ToString("D8") + "|" + name.ToUpperInvariant();

    private static object Paged(List<(string Key, object Data)> rows, int offset, int limit)
    {
        var ordered = rows.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        var total = ordered.Count;
        if (offset > total) offset = total;
        var take = Math.Min(limit, total - offset);

        var items = new List<object>(take);
        for (var i = offset; i < offset + take; i++) items.Add(ordered[i].Data);

        return new
        {
            items,
            total,
            offset,
            limit,
            truncated = offset + take < total,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? HandleOf(ObjectId id)
    {
        try { return id.IsNull ? null : id.Handle.ToString(); }
        catch (System.Exception) { return null; }
    }
}
