using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>Session-level introspection: what AutoCAD is this, and what is open?</summary>
internal static class SessionTools
{
    internal static void Register()
    {
        Router.Register("acad_status",
            "Version, profile, ACA availability and the active drawing's state.",
            mutating: false,
            _ => MainThread.InvokeAsync<object?>(() =>
            {
                var docs = AcApp.DocumentManager;
                var active = docs.MdiActiveDocument;

                return new
                {
                    acadVersion = SysVar("ACADVER"),
                    product = SysVar("PRODUCT"),
                    loginName = SysVar("LOGINNAME"),
                    processId = Environment.ProcessId,
                    acaAvailable = AcaSupport.IsAvailable,
                    bridgePort = HttpBridge.Port,
                    toolCount = Router.List().Count,
                    openDocuments = CountDocuments(docs),
                    active = active is null ? null : new
                    {
                        name = active.Name,
                        isReadOnly = active.IsReadOnly,
                        isNamedDrawing = active.IsNamedDrawing,
                        commandInProgress = string.IsNullOrEmpty(active.CommandInProgress)
                            ? null : active.CommandInProgress,
                        fileName = active.Database.Filename,
                        isModelSpace = Convert.ToInt16(SysVar("TILEMODE") ?? (short)1) == 1,
                    },
                };
            }));

        Router.Register("list_documents",
            "Every drawing currently open in this AutoCAD session.",
            mutating: false,
            _ => MainThread.InvokeAsync<object?>(() =>
            {
                var result = new List<object>();
                foreach (Document doc in AcApp.DocumentManager)
                {
                    result.Add(new
                    {
                        name = doc.Name,
                        fileName = doc.Database.Filename,
                        isReadOnly = doc.IsReadOnly,
                        isActive = ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument),
                    });
                }
                return result;
            }));

        Router.Register("list_layers",
            "All layers in the active drawing with colour, state and plot settings.",
            mutating: false,
            _ => Acad.Read<object?>(ctx =>
            {
                var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
                var layers = new List<(string Name, object Data)>();

                foreach (ObjectId id in table)
                {
                    var layer = (LayerTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                    layers.Add((layer.Name, new
                    {
                        name = layer.Name,
                        handle = layer.Handle.ToString(),
                        colorIndex = layer.Color.ColorIndex,
                        color = layer.Color.ToString(),
                        isOff = layer.IsOff,
                        isFrozen = layer.IsFrozen,
                        isLocked = layer.IsLocked,
                        isPlottable = layer.IsPlottable,
                        lineWeight = layer.LineWeight.ToString(),
                        isCurrent = id == ctx.Db.Clayer,
                    }));
                }

                return layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                             .Select(l => l.Data)
                             .ToList();
            }));
    }

    private static object? SysVar(string name)
    {
        try { return AcApp.GetSystemVariable(name); }
        catch { return null; }
    }

    private static int CountDocuments(DocumentCollection docs)
    {
        var n = 0;
        foreach (Document _ in docs) n++;
        return n;
    }
}
