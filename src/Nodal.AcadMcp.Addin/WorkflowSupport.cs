using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nodal.AcadMcp.Contracts;

namespace Nodal.AcadMcp.Addin;

internal static class WorkflowSupport
{
    internal static void CheckDocument(Document doc, JsonElement args, bool checkBusy = true)
    {
        var expected = Args.Str(args, "expectedDocument");
        if (!doc.IsNamedDrawing || !Path.IsPathFullyQualified(expected) ||
            !string.Equals(Path.GetFullPath(expected), Path.GetFullPath(doc.Database.Filename), StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("wrong_document", "expectedDocument must be the active drawing's full saved path.");
        if (checkBusy && !string.IsNullOrEmpty(doc.CommandInProgress))
            throw new AcadMcpException("busy", "Finish the active command before starting a modeling workflow.");
    }

    internal static Point3d Point(JsonElement p) => new(p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble());
    internal static double[] Xyz(Point3d p) => [p.X, p.Y, p.Z];
    internal static object Bounds(Extents3d e) => new { min = Xyz(e.MinPoint), max = Xyz(e.MaxPoint) };
    internal static T Get<T>(AcadContext ctx, string handle, OpenMode mode = OpenMode.ForRead) where T : DBObject =>
        ctx.Tr.GetObject(Acad.ToObjectId(ctx.Db, handle), mode) as T
        ?? throw new AcadMcpException("wrong_type", $"{handle} must be a {typeof(T).Name}.");
    internal static BlockTableRecord ModelSpace(AcadContext ctx) =>
        (BlockTableRecord)ctx.Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(ctx.Db), OpenMode.ForWrite);
    internal static void RequireLayer(AcadContext ctx, string layer)
    {
        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layer)) throw new AcadMcpException("not_found", $"Layer '{layer}' does not exist.");
        var record = (LayerTableRecord)ctx.Tr.GetObject(table[layer], OpenMode.ForRead);
        if (record.IsLocked || record.IsDependent) throw new AcadMcpException("invalid_layer", "Choose an unlocked local layer.");
    }

    internal static void RequireModelEntity(Entity entity)
    {
        if (entity.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(entity.Database))
            throw new AcadMcpException("wrong_space", "World-coordinate workflows require top-level model-space entities; nested block coordinates need an explicit reference transform.");
    }

    // The receipt and geometry commit together. Saving the drawing makes the receipt durable.
    // A retry detects erasure, but is not a substitute for validating subsequent geometry edits.
    internal static object Once(AcadContext ctx, string tool, JsonElement args, Func<(object Result, string[] Handles)> action)
    {
        CheckDocument(ctx.Doc, args);
        var key = Args.Str(args, "operationKey");
        var id = "NMCP_" + WorkflowMath.Fingerprint(JsonSerializer.SerializeToElement(new { tool, key }));
        var fingerprint = WorkflowMath.Fingerprint(args);
        var nod = (DBDictionary)ctx.Tr.GetObject(ctx.Db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(id))
        {
            var record = (Xrecord)ctx.Tr.GetObject(nod.GetAt(id), OpenMode.ForRead);
            using var data = record.Data;
            using var receipt = JsonDocument.Parse(string.Concat(data.AsArray().Select(v => (string)v.Value)));
            var root = receipt.RootElement;
            if (root.GetProperty("fingerprint").GetString() != fingerprint)
                throw new AcadMcpException("operation_conflict", "This operationKey was already used with different arguments.");
            foreach (var h in root.GetProperty("handles").EnumerateArray())
            {
                var objectId = Acad.ToObjectId(ctx.Db, h.GetString()!);
                if (objectId.IsErased) throw new AcadMcpException("stale_operation", "Previously created geometry has been erased; inspect before using a new key.");
            }
            return new { replayed = true, result = root.GetProperty("result").Clone() };
        }
        var (result, handles) = action();
        var json = JsonSerializer.Serialize(new { tool, key, fingerprint, handles, result });
        var chunks = Enumerable.Range(0, (json.Length + 199) / 200)
            .Select(i => new TypedValue((int)DxfCode.Text, json.Substring(i * 200, Math.Min(200, json.Length - i * 200)))).ToArray();
        nod.UpgradeOpen();
        using var buffer = new ResultBuffer(chunks);
        var entry = new Xrecord { Data = buffer };
        nod.SetAt(id, entry);
        ctx.Tr.AddNewlyCreatedDBObject(entry, true);
        return new { replayed = false, result };
    }

    internal static string NewFile(JsonElement args, string extension)
    {
        var path = Args.Str(args, "path");
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("bad_argument", $"path must be a full {extension} file path.");
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new AcadMcpException("already_exists", "The output file already exists; choose a new path.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new AcadMcpException("not_found", "The output directory must already exist.");
        return path;
    }
}
