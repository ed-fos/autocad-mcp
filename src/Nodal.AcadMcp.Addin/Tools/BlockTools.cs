using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Block definitions, block references, their attributes, and external references.
///
/// Every entity is addressed by its Handle hex string. Angles are radians on the wire
/// (matching the DWG itself); reads additionally echo a degrees value, and insert_block
/// accepts "rotationDegrees" for callers that would rather think in degrees.
/// </summary>
internal static class BlockTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;

    internal static void Register()
    {
        Router.Register("list_blocks",
            "Every block definition (BlockTableRecord) in the active drawing: name, handle, "
            + "isLayout / isFromExternalReference / isAnonymous / isDynamic, and how many references "
            + "exist in the drawing (dynamic blocks add their anonymous variants' references). "
            + "Optional: nameFilter (substring), includeLayouts, includeAnonymous, includeXrefs, limit, offset.",
            mutating: false,
            args => Acad.Read<object?>(ctx => ListBlocks(ctx, args)));

        Router.Register("get_block_definition",
            "The contents of one block definition, addressed by 'name' or 'handle'. Returns the block's "
            + "metadata, its AttributeDefinitions (tag, prompt, default, constant, invisible) and a paginated "
            + "list of the entities inside it. Optional: limit, offset.",
            mutating: false,
            args => Acad.Read<object?>(ctx => GetBlockDefinition(ctx, args)));

        Router.Register("list_block_references",
            "Every BlockReference (INSERT) in the drawing, optionally filtered by 'blockName'. Per instance: "
            + "handle, name (the real name — the dynamic block's name, not the anonymous *U variant), position, "
            + "rotation (radians) and rotationDegrees, scaleFactors, layer, owning block/space, and every "
            + "AttributeReference as a tag -> value map. Optional: includeAttributes, limit, offset.",
            mutating: false,
            args => Acad.Read<object?>(ctx => ListBlockReferences(ctx, args)));

        Router.Register("insert_block",
            "Insert a block reference. Required: 'name' and 'point' ([x,y] or [x,y,z]). Optional: 'rotation' "
            + "(radians) or 'rotationDegrees', 'scale' (a number or [x,y,z]) or scaleX/scaleY/scaleZ, 'layer', "
            + "'space' ('current' | 'model' | 'paper' | a block name), and 'attributes' as an object of "
            + "tag -> value. AttributeReferences are created from the definition's AttributeDefinitions. "
            + "Returns the new reference's handle. One undo step.",
            mutating: true,
            args => Acad.Write<object?>(ctx => InsertBlock(ctx, args)));

        Router.Register("set_block_attributes",
            "Set attribute values on one existing block reference. Required: 'handle' (the BlockReference) and "
            + "'attributes' as an object of tag -> value (tags match case-insensitively). Reports what was "
            + "updated, what was skipped as constant, and which requested tags this reference does not have. "
            + "One undo step.",
            mutating: true,
            args => Acad.Write<object?>(ctx => SetBlockAttributes(ctx, args)));

        Router.Register("list_xrefs",
            "External references attached to the active drawing: name, saved path, resolved file, xref status, "
            + "resolved/unloaded/nested flags, overlay vs attach, and how many times each is inserted. "
            + "Optional: limit, offset.",
            mutating: false,
            args => Acad.Read<object?>(ctx => ListXrefs(ctx, args)));
    }

    // ---------------------------------------------------------------- list_blocks

    private static object? ListBlocks(AcadContext ctx, JsonElement args)
    {
        var offset = Args.IntOpt(args, "offset", 0);
        var limit = Args.IntOpt(args, "limit", DefaultLimit);
        var nameFilter = Args.StrOpt(args, "nameFilter") ?? Args.StrOpt(args, "filter");
        var includeLayouts = Args.BoolOpt(args, "includeLayouts", true);
        var includeAnonymous = Args.BoolOpt(args, "includeAnonymous", true);
        var includeXrefs = Args.BoolOpt(args, "includeXrefs", true);

        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        var rows = new List<(string Name, object Data)>();

        foreach (ObjectId id in bt)
        {
            if (id.IsNull) continue;
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr) continue;

            var isXref = btr.IsFromExternalReference;

            if (!includeLayouts && btr.IsLayout) continue;
            if (!includeAnonymous && btr.IsAnonymous) continue;
            if (!includeXrefs && isXref) continue;
            if (!string.IsNullOrEmpty(nameFilter)
                && btr.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var direct = CountReferences(btr);
            var anonIds = AnonymousIds(btr);
            var anonRefs = 0;
            foreach (var anonId in anonIds)
            {
                if (ctx.Tr.GetObject(anonId, OpenMode.ForRead) is BlockTableRecord anonBtr)
                    anonRefs += CountReferences(anonBtr);
            }

            rows.Add((btr.Name, new
            {
                name = btr.Name,
                handle = btr.Handle.ToString(),
                isLayout = btr.IsLayout,
                isFromExternalReference = isXref,
                isOverlay = isXref && btr.IsFromOverlayReference,
                isAnonymous = btr.IsAnonymous,
                isDynamic = btr.IsDynamicBlock,
                isXrefDependent = btr.IsDependent,
                hasAttributeDefinitions = btr.HasAttributeDefinitions,
                explodable = btr.Explodable,
                units = btr.Units.ToString(),
                xrefStatus = btr.XrefStatus.ToString(),
                pathName = string.IsNullOrEmpty(btr.PathName) ? null : btr.PathName,
                comments = string.IsNullOrEmpty(btr.Comments) ? null : btr.Comments,
                referenceCount = direct + anonRefs,
                directReferenceCount = direct,
                anonymousVariantCount = anonIds.Count,
                anonymousReferenceCount = anonRefs,
            }));
        }

        var ordered = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                          .Select(r => r.Data)
                          .ToList();

        return Page(ordered, offset, limit);
    }

    // ------------------------------------------------------- get_block_definition

    private static object? GetBlockDefinition(AcadContext ctx, JsonElement args)
    {
        var offset = Args.IntOpt(args, "offset", 0);
        var limit = Args.IntOpt(args, "limit", DefaultLimit);

        var btr = ResolveBlockDefinition(ctx, args, OpenMode.ForRead);

        var attDefClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AttributeDefinition));
        var scanForAttDefs = btr.HasAttributeDefinitions;

        var contents = new List<(long Handle, ObjectId Id)>();
        var attDefs = new List<(string Tag, object Data)>();

        foreach (ObjectId id in btr)
        {
            if (id.IsNull) continue;
            contents.Add((id.Handle.Value, id));

            if (!scanForAttDefs || !IsDerivedFrom(id, attDefClass)) continue;
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad) continue;

            attDefs.Add((ad.Tag ?? string.Empty, new
            {
                handle = ad.Handle.ToString(),
                tag = ad.Tag,
                prompt = ad.Prompt,
                @default = ad.TextString,
                constant = ad.Constant,
                invisible = ad.Invisible,
                preset = ad.Preset,
                verifiable = ad.Verifiable,
                isMText = ad.IsMTextAttributeDefinition,
                lockPositionInBlock = ad.LockPositionInBlock,
                fieldLength = ad.FieldLength,
                layer = ad.Layer,
                height = ad.Height,
                rotation = ad.Rotation,
                position = new[] { ad.Position.X, ad.Position.Y, ad.Position.Z },
            }));
        }

        var (off, lim) = Normalize(offset, limit);
        var total = contents.Count;

        var items = new List<object>();
        foreach (var row in contents.OrderBy(c => c.Handle).Skip(off).Take(lim))
        {
            DBObject? obj = null;
            try { obj = ctx.Tr.GetObject(row.Id, OpenMode.ForRead); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            if (obj is null) continue;

            var ent = obj as Entity;
            items.Add(new
            {
                handle = row.Id.Handle.ToString(),
                type = obj.GetType().Name,
                dxfName = SafeDxfName(row.Id),
                layer = ent?.Layer,
                tag = (obj as AttributeDefinition)?.Tag ?? (obj as AttributeReference)?.Tag,
                nestedBlockName = (obj as BlockReference)?.Name,
            });
        }

        return new
        {
            block = new
            {
                name = btr.Name,
                handle = btr.Handle.ToString(),
                isLayout = btr.IsLayout,
                isFromExternalReference = btr.IsFromExternalReference,
                isAnonymous = btr.IsAnonymous,
                isDynamic = btr.IsDynamicBlock,
                hasAttributeDefinitions = btr.HasAttributeDefinitions,
                explodable = btr.Explodable,
                units = btr.Units.ToString(),
                origin = new[] { btr.Origin.X, btr.Origin.Y, btr.Origin.Z },
                pathName = string.IsNullOrEmpty(btr.PathName) ? null : btr.PathName,
                comments = string.IsNullOrEmpty(btr.Comments) ? null : btr.Comments,
                referenceCount = CountReferences(btr),
            },
            attributeDefinitions = attDefs.OrderBy(a => a.Tag, StringComparer.OrdinalIgnoreCase)
                                          .Select(a => a.Data)
                                          .ToList(),
            items,
            total,
            offset = off,
            limit = lim,
            truncated = off + items.Count < total,
        };
    }

    // ------------------------------------------------------ list_block_references

    private static object? ListBlockReferences(AcadContext ctx, JsonElement args)
    {
        var offset = Args.IntOpt(args, "offset", 0);
        var limit = Args.IntOpt(args, "limit", DefaultLimit);
        var filter = Args.StrOpt(args, "blockName") ?? Args.StrOpt(args, "name");
        var includeAttributes = Args.BoolOpt(args, "includeAttributes", true);

        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);

        // Effective name per definition. An anonymous dynamic variant (*U5) reports its
        // dynamic parent's name, so a caller filtering by the real name finds every instance.
        var defs = new List<BlockTableRecord>();
        var effectiveName = new Dictionary<ObjectId, string>();

        foreach (ObjectId id in bt)
        {
            if (id.IsNull) continue;
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr) continue;
            defs.Add(btr);
            effectiveName[btr.ObjectId] = btr.Name;
        }

        foreach (var btr in defs)
        {
            if (!btr.IsDynamicBlock) continue;
            foreach (var anonId in AnonymousIds(btr)) effectiveName[anonId] = btr.Name;
        }

        // Collect ids without opening a single reference: the name comes from the definition
        // we harvested it from and the handle comes off the ObjectId.
        var refs = new List<(string Name, long Handle, ObjectId Id)>();
        foreach (var btr in defs)
        {
            var name = effectiveName.TryGetValue(btr.ObjectId, out var n) ? n : btr.Name;
            if (filter is not null && !string.Equals(name, filter, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var rid in ReferenceIds(btr))
            {
                if (rid.IsNull || rid.IsErased) continue;
                refs.Add((name, rid.Handle.Value, rid));
            }
        }

        var (off, lim) = Normalize(offset, limit);
        var total = refs.Count;

        var ordered = refs.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(r => r.Handle)
                          .Skip(off)
                          .Take(lim);

        var items = new List<object>();
        foreach (var row in ordered)
        {
            BlockReference? br = null;
            try { br = ctx.Tr.GetObject(row.Id, OpenMode.ForRead) as BlockReference; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            if (br is null) continue;

            var isDynamic = br.IsDynamicBlock;
            var defId = isDynamic && !br.DynamicBlockTableRecord.IsNull
                ? br.DynamicBlockTableRecord
                : br.BlockTableRecord;

            items.Add(new
            {
                handle = br.Handle.ToString(),
                name = row.Name,
                referencedBlockName = br.Name,
                isDynamic,
                blockDefinitionHandle = defId.IsNull ? null : defId.Handle.ToString(),
                position = new[] { br.Position.X, br.Position.Y, br.Position.Z },
                rotation = br.Rotation,
                rotationDegrees = br.Rotation * 180.0 / Math.PI,
                scaleFactors = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                normal = new[] { br.Normal.X, br.Normal.Y, br.Normal.Z },
                layer = br.Layer,
                ownerBlock = SafeOwnerBlockName(br),
                attributeCount = AttributeCount(br),
                attributes = includeAttributes ? ReadAttributes(ctx.Tr, br) : null,
            });
        }

        return new
        {
            blockName = filter,
            items,
            total,
            offset = off,
            limit = lim,
            truncated = off + items.Count < total,
        };
    }

    // --------------------------------------------------------------- insert_block

    private static object? InsertBlock(AcadContext ctx, JsonElement args)
    {
        var name = Args.StrOpt(args, "name") ?? Args.StrOpt(args, "blockName")
            ?? throw new AcadMcpException("bad_argument", "Argument 'name' (the block name) is required.");

        var position = ReadPoint(args);
        var rotation = ReadRotation(args);
        var scale = ReadScale(args);
        var layer = Args.StrOpt(args, "layer");
        var values = ReadStringMap(args, "attributes", required: false);
        var space = Args.StrOpt(args, "space", "current");

        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(name))
            throw new AcadMcpException("not_found", $"No block named '{name}' in this drawing.");

        var defId = bt[name];
        var def = (BlockTableRecord)ctx.Tr.GetObject(defId, OpenMode.ForRead);

        if (def.IsLayout)
            throw new AcadMcpException("bad_argument",
                $"'{name}' is a layout block (model or paper space) and cannot be inserted.");

        if (def.BlockScaling == BlockScaling.Uniform && !scale.IsProportional())
            throw new AcadMcpException("bad_argument",
                $"Block '{name}' only allows uniform scaling; give a single 'scale' number.");

        if (!string.IsNullOrEmpty(layer))
        {
            var lt = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layer))
                throw new AcadMcpException("not_found", $"No layer named '{layer}' in this drawing.");
        }

        var spaceId = ResolveSpaceId(ctx, space);
        var owner = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var applied = new List<(string Tag, object Data)>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string handle;

        using (var br = new BlockReference(position, defId))
        {
            br.Rotation = rotation;
            br.ScaleFactors = scale;
            if (!string.IsNullOrEmpty(layer)) br.Layer = layer;

            owner.AppendEntity(br);
            ctx.Tr.AddNewlyCreatedDBObject(br, true);

            if (def.HasAttributeDefinitions)
            {
                foreach (ObjectId adId in def)
                {
                    if (adId.IsNull) continue;
                    if (ctx.Tr.GetObject(adId, OpenMode.ForRead) is not AttributeDefinition ad) continue;
                    if (ad.Constant) continue;   // constant attributes live in the definition, not the insert

                    var tag = ad.Tag ?? string.Empty;
                    var wanted = values.TryGetValue(tag, out var v) ? v : null;
                    if (wanted is not null) matched.Add(tag);

                    using var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    if (wanted is not null) SetAttributeValue(ar, wanted);

                    br.AttributeCollection.AppendAttribute(ar);
                    ctx.Tr.AddNewlyCreatedDBObject(ar, true);

                    applied.Add((tag, new
                    {
                        handle = ar.Handle.ToString(),
                        tag = ar.Tag,
                        value = ar.TextString,
                        fromArgument = wanted is not null,
                    }));
                }
            }

            handle = br.Handle.ToString();
        }

        var notFound = values.Keys.Where(k => !matched.Contains(k))
                                  .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        return new
        {
            handle,
            name = def.Name,
            blockDefinitionHandle = def.Handle.ToString(),
            isDynamic = def.IsDynamicBlock,
            space = owner.Name,
            spaceHandle = owner.Handle.ToString(),
            layer = string.IsNullOrEmpty(layer) ? null : layer,
            position = new[] { position.X, position.Y, position.Z },
            rotation,
            rotationDegrees = rotation * 180.0 / Math.PI,
            scaleFactors = new[] { scale.X, scale.Y, scale.Z },
            attributes = applied.OrderBy(a => a.Tag, StringComparer.OrdinalIgnoreCase)
                                .Select(a => a.Data)
                                .ToList(),
            attributesRequested = values.Count,
            attributesNotFound = notFound,
        };
    }

    // ------------------------------------------------------- set_block_attributes

    private static object? SetBlockAttributes(AcadContext ctx, JsonElement args)
    {
        var handle = Args.Str(args, "handle");
        var values = ReadStringMap(args, "attributes", required: true);

        var id = Acad.ToObjectId(ctx.Db, handle);
        if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockReference br)
            throw new AcadMcpException("wrong_type", $"Handle '{handle}' is not a block reference.");

        var updated = new List<(string Tag, object Data)>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedConstant = new List<string>();

        var coll = br.AttributeCollection;
        if (coll is not null)
        {
            foreach (ObjectId attId in coll)
            {
                if (attId.IsNull || attId.IsErased) continue;
                if (ctx.Tr.GetObject(attId, OpenMode.ForRead) is not AttributeReference probe) continue;

                var tag = probe.Tag ?? string.Empty;
                if (!values.TryGetValue(tag, out var wanted)) continue;

                matched.Add(tag);

                if (probe.IsConstant)
                {
                    skippedConstant.Add(tag);
                    continue;
                }

                // Re-opening the same id for write inside this transaction upgrades the object.
                if (ctx.Tr.GetObject(attId, OpenMode.ForWrite) is not AttributeReference ar) continue;

                var before = ar.TextString;
                var mtextSynced = SetAttributeValue(ar, wanted);

                updated.Add((tag, new
                {
                    handle = ar.Handle.ToString(),
                    tag = ar.Tag,
                    previousValue = before,
                    value = ar.TextString,
                    isMText = ar.IsMTextAttribute,
                    mtextSynced,
                }));
            }
        }

        var notFound = values.Keys.Where(k => !matched.Contains(k))
                                  .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        return new
        {
            handle = br.Handle.ToString(),
            name = EffectiveName(ctx.Tr, br),
            updatedCount = updated.Count,
            updated = updated.OrderBy(u => u.Tag, StringComparer.OrdinalIgnoreCase)
                             .Select(u => u.Data)
                             .ToList(),
            skippedConstant,
            notFound,
        };
    }

    // ----------------------------------------------------------------- list_xrefs

    private static object? ListXrefs(AcadContext ctx, JsonElement args)
    {
        var offset = Args.IntOpt(args, "offset", 0);
        var limit = Args.IntOpt(args, "limit", DefaultLimit);

        var graph = XrefGraphFacts(ctx.Db);
        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        var rows = new List<(string Name, object Data)>();

        foreach (ObjectId id in bt)
        {
            if (id.IsNull) continue;
            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr) continue;
            if (!btr.IsFromExternalReference) continue;

            var status = btr.XrefStatus.ToString();
            var isNested = false;
            string? notification = null;

            if (graph.TryGetValue(btr.ObjectId, out var fact))
            {
                isNested = fact.IsNested;
                notification = fact.Notification;
                if (!string.IsNullOrEmpty(fact.Status)) status = fact.Status;
            }

            string? resolvedPath = null;
            try
            {
                var xdb = btr.GetXrefDatabase(false);   // do not dispose: owned by the host database
                resolvedPath = xdb?.Filename;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            catch (System.Exception) { }

            rows.Add((btr.Name, new
            {
                name = btr.Name,
                handle = btr.Handle.ToString(),
                path = string.IsNullOrEmpty(btr.PathName) ? null : btr.PathName,
                resolvedPath,
                status,
                isResolved = btr.XrefStatus == XrefStatus.Resolved,
                notificationStatus = notification,
                isOverlay = btr.IsFromOverlayReference,
                attachType = btr.IsFromOverlayReference ? "overlay" : "attach",
                isUnloaded = btr.IsUnloaded,
                isNested = isNested,
                referenceCount = CountReferences(btr),
            }));
        }

        var ordered = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                          .Select(r => r.Data)
                          .ToList();

        return Page(ordered, offset, limit);
    }

    /// <summary>Nesting and live status straight from the host drawing's xref graph, keyed by BTR id.</summary>
    private static Dictionary<ObjectId, (bool IsNested, string Status, string Notification)> XrefGraphFacts(Database db)
    {
        var map = new Dictionary<ObjectId, (bool IsNested, string Status, string Notification)>();
        try
        {
            var graph = db.GetHostDwgXrefGraph(false);
            if (graph is null) return map;

            using (graph)
            {
                for (var i = 0; i < graph.NumNodes; i++)
                {
                    var node = graph.GetXrefNode(i);   // owned by the graph; do not dispose
                    if (node is null) continue;

                    var btrId = node.BlockTableRecordId;
                    if (btrId.IsNull) continue;   // the host drawing's own node

                    map[btrId] = (node.IsNested,
                                  node.XrefStatus.ToString(),
                                  node.XrefNotificationStatus.ToString());
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (System.Exception) { }
        return map;
    }

    // ------------------------------------------------------------------- plumbing

    private static BlockTableRecord ResolveBlockDefinition(AcadContext ctx, JsonElement args, OpenMode mode)
    {
        var handle = Args.StrOpt(args, "handle");
        if (!string.IsNullOrWhiteSpace(handle))
        {
            var id = Acad.ToObjectId(ctx.Db, handle);
            if (ctx.Tr.GetObject(id, mode) is not BlockTableRecord byHandle)
                throw new AcadMcpException("wrong_type", $"Handle '{handle}' is not a block definition.");
            return byHandle;
        }

        var name = Args.StrOpt(args, "name") ?? Args.StrOpt(args, "blockName");
        if (string.IsNullOrWhiteSpace(name))
            throw new AcadMcpException("bad_argument", "Supply either 'name' (the block name) or 'handle'.");

        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(name))
            throw new AcadMcpException("not_found", $"No block named '{name}' in this drawing.");

        return (BlockTableRecord)ctx.Tr.GetObject(bt[name], mode);
    }

    private static ObjectId ResolveSpaceId(AcadContext ctx, string? space)
    {
        if (string.IsNullOrWhiteSpace(space)) return ctx.Db.CurrentSpaceId;
        if (string.Equals(space, "current", StringComparison.OrdinalIgnoreCase)) return ctx.Db.CurrentSpaceId;

        var bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);

        if (string.Equals(space, "model", StringComparison.OrdinalIgnoreCase)
            || string.Equals(space, "modelspace", StringComparison.OrdinalIgnoreCase))
            return bt[BlockTableRecord.ModelSpace];

        if (string.Equals(space, "paper", StringComparison.OrdinalIgnoreCase)
            || string.Equals(space, "paperspace", StringComparison.OrdinalIgnoreCase))
            return bt[BlockTableRecord.PaperSpace];

        if (bt.Has(space)) return bt[space];

        throw new AcadMcpException("bad_argument",
            $"'space' must be 'current', 'model', 'paper' or an existing block name; got '{space}'.");
    }

    /// <summary>Writes the value and, for an MText attribute, keeps the MText body in step.</summary>
    private static bool SetAttributeValue(AttributeReference ar, string value)
    {
        ar.TextString = value;

        var synced = false;
        if (ar.IsMTextAttribute)
        {
            try
            {
                var mtext = ar.MTextAttribute;
                if (mtext is not null)
                {
                    mtext.Contents = value;
                    ar.MTextAttribute = mtext;
                    synced = true;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            catch (System.Exception) { }
        }

        try { ar.RecordGraphicsModified(true); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (System.Exception) { }

        return synced;
    }

    private static Dictionary<string, string> ReadAttributes(Transaction tr, BlockReference br)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coll = br.AttributeCollection;
        if (coll is null) return map;

        foreach (ObjectId id in coll)
        {
            if (id.IsNull || id.IsErased) continue;
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar)
                    map[ar.Tag ?? string.Empty] = ar.TextString ?? string.Empty;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            catch (System.Exception) { }
        }
        return map;
    }

    private static int AttributeCount(BlockReference br)
    {
        try
        {
            var coll = br.AttributeCollection;
            return coll is null ? 0 : coll.Count;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return 0; }
        catch (System.Exception) { return 0; }
    }

    private static string EffectiveName(Transaction tr, BlockReference br)
    {
        try
        {
            var id = br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull
                ? br.DynamicBlockTableRecord
                : br.BlockTableRecord;

            if (!id.IsNull && tr.GetObject(id, OpenMode.ForRead) is BlockTableRecord btr) return btr.Name;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (System.Exception) { }
        return br.Name;
    }

    private static string? SafeOwnerBlockName(BlockReference br)
    {
        try { return br.BlockName; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    private static string? SafeDxfName(ObjectId id)
    {
        try
        {
            var cls = id.ObjectClass;
            return cls?.DxfName;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    private static bool IsDerivedFrom(ObjectId id, Autodesk.AutoCAD.Runtime.RXClass target)
    {
        try
        {
            var cls = id.ObjectClass;
            return cls is not null && cls.IsDerivedFrom(target);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
        catch (System.Exception) { return false; }
    }

    private static List<ObjectId> ReferenceIds(BlockTableRecord btr)
    {
        var list = new List<ObjectId>();
        try
        {
            var ids = btr.GetBlockReferenceIds(true, false);
            if (ids is null) return list;
            using (ids)
            {
                foreach (ObjectId id in ids) list.Add(id);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (System.Exception) { }
        return list;
    }

    private static int CountReferences(BlockTableRecord btr)
    {
        try
        {
            var ids = btr.GetBlockReferenceIds(true, false);
            if (ids is null) return 0;
            using (ids) { return ids.Count; }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return 0; }
        catch (System.Exception) { return 0; }
    }

    private static List<ObjectId> AnonymousIds(BlockTableRecord btr)
    {
        var list = new List<ObjectId>();
        if (!btr.IsDynamicBlock) return list;
        try
        {
            var ids = btr.GetAnonymousBlockIds();
            if (ids is null) return list;
            using (ids)
            {
                foreach (ObjectId id in ids)
                {
                    if (!id.IsNull) list.Add(id);
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        catch (System.Exception) { }
        return list;
    }

    // ------------------------------------------------------------- argument shims

    private static Point3d ReadPoint(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("point", out _)) return Args.Point(args, "point");
            if (args.TryGetProperty("position", out _)) return Args.Point(args, "position");
            if (args.TryGetProperty("insertionPoint", out _)) return Args.Point(args, "insertionPoint");
        }
        return Args.Point(args, "point");   // throws the canonical "required" message
    }

    private static double ReadRotation(JsonElement args)
    {
        // A rotationDegrees of the wrong type must fail, not fall through to 'rotation'.
        var degrees = Args.DblOpt(args, "rotationDegrees", double.NaN);
        if (!double.IsNaN(degrees))
            return degrees * Math.PI / 180.0;

        return Args.DblOpt(args, "rotation", 0.0);
    }

    private static Scale3d ReadScale(JsonElement args)
    {
        double x = 1.0, y = 1.0, z = 1.0;

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("scale", out var s)
            && s.ValueKind != JsonValueKind.Null)
        {
            if (s.ValueKind == JsonValueKind.Number)
            {
                x = y = z = s.GetDouble();
            }
            else if (s.ValueKind == JsonValueKind.Array)
            {
                var nums = Args.Numbers(s, "scale");

                if (nums.Length == 1) { x = y = z = nums[0]; }
                else if (nums.Length == 2) { x = nums[0]; y = nums[1]; }
                else if (nums.Length == 3) { x = nums[0]; y = nums[1]; z = nums[2]; }
                else throw new AcadMcpException("bad_argument",
                    "Argument 'scale' must be a number or an array of 1 to 3 numbers.");
            }
            else
            {
                throw new AcadMcpException("bad_argument",
                    "Argument 'scale' must be a number or an array of 1 to 3 numbers.");
            }
        }

        x = Args.DblOpt(args, "scaleX", x);
        y = Args.DblOpt(args, "scaleY", y);
        z = Args.DblOpt(args, "scaleZ", z);

        if (x == 0.0 || y == 0.0 || z == 0.0)
            throw new AcadMcpException("bad_argument", "Scale factors must be non-zero.");

        return new Scale3d(x, y, z);
    }

    /// <summary>An object argument of string -> string, tolerant of numbers and booleans.</summary>
    private static Dictionary<string, string> ReadStringMap(JsonElement args, string name, bool required)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var obj)
            || obj.ValueKind == JsonValueKind.Null)
        {
            if (required)
                throw new AcadMcpException("bad_argument",
                    $"Argument '{name}' is required and must be an object of tag -> value.");
            return map;
        }

        if (obj.ValueKind != JsonValueKind.Object)
            throw new AcadMcpException("bad_argument",
                $"Argument '{name}' must be an object of tag -> value.");

        foreach (var prop in obj.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                // An object or array here is a caller mistake; its raw JSON is not a tag value.
                _ => throw Args.WrongType($"{name}.{prop.Name}", "a string, number or boolean", prop.Value),
            };
        }

        return map;
    }

    // ------------------------------------------------------------------ pagination

    private static (int Offset, int Limit) Normalize(int offset, int limit)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;
        return (offset, limit);
    }

    private static object Page(List<object> all, int offset, int limit)
    {
        var (off, lim) = Normalize(offset, limit);
        var items = all.Skip(off).Take(lim).ToList();

        return new
        {
            items,
            total = all.Count,
            offset = off,
            limit = lim,
            truncated = off + items.Count < all.Count,
        };
    }
}
