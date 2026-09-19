using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin;

internal sealed class AcadContext
{
    public required Document Doc { get; init; }
    public required Database Db { get; init; }
    public required Transaction Tr { get; init; }
    public required Editor Ed { get; init; }
}

/// <summary>Ergonomic entry points for tool modules. Every call lands on AutoCAD's main thread.</summary>
internal static class Acad
{
    internal static Document ActiveDocument =>
        AcApp.DocumentManager.MdiActiveDocument
        ?? throw new AcadMcpException("no_document", "No drawing is open in AutoCAD.");

    /// <summary>Read-only access. The transaction is aborted, so a handler cannot mutate by accident.</summary>
    internal static Task<T> Read<T>(Func<AcadContext, T> fn) => Run(fn, write: false);

    /// <summary>Mutating access: locks the document and commits as a single undo step.</summary>
    internal static Task<T> Write<T>(Func<AcadContext, T> fn) => Run(fn, write: true);

    private static Task<T> Run<T>(Func<AcadContext, T> fn, bool write) =>
        MainThread.InvokeAsync(() =>
        {
            var doc = ActiveDocument;

            if (write && doc.IsReadOnly)
                throw new AcadMcpException("read_only", $"Drawing '{doc.Name}' is open read-only.");

            // We run from Application.Idle (application context), so a write needs an explicit
            // document lock — AutoCAD only locks implicitly inside command context.
            DocumentLock? docLock = write ? doc.LockDocument() : null;
            try
            {
                // Document.TransactionManager (not Database.TransactionManager) so the change
                // joins the document's undo stack as one Ctrl+Z step. The managed API has no
                // StartUndoMark despite what most tutorials claim.
                using var tr = doc.TransactionManager.StartTransaction();

                var ctx = new AcadContext { Doc = doc, Db = doc.Database, Tr = tr, Ed = doc.Editor };
                var result = fn(ctx);

                if (write) tr.Commit();
                else tr.Abort();

                return result;
            }
            finally
            {
                docLock?.Dispose();
            }
        });

    /// <summary>Handle (hex string, stable across sessions) to ObjectId. Handles are our external ids.</summary>
    internal static ObjectId ToObjectId(Database db, string handle)
    {
        long value;
        try { value = Convert.ToInt64(handle, 16); }
        catch (Exception ex) { throw new AcadMcpException("bad_handle", $"'{handle}' is not a valid hex handle.", ex.Message); }

        if (!db.TryGetObjectId(new Handle(value), out var id) || id.IsNull)
            throw new AcadMcpException("not_found", $"No object with handle '{handle}' in this drawing.");

        return id;
    }
}
