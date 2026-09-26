using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin.Tools;

internal static class AcaProjectTools
{
    internal static void Register()
    {
        Router.Register("create_project_construct", "Create a new option by copying the saved active construct through ACA's project API, in the SAME category/directory. Preserves drawing coordinates, relative xref base directory and project metadata. Requires its APJ path, the active source DWG path, and a new file stem (no extension). Does not change the current Project Navigator project. Refuses unsaved edits and existing destinations. File/project effects are not undoable; inspect any partial output before retrying.", true, UndoKind.NotUndoable,
            ToolSchema.Object(["expectedDocument", "expectedProject", "name"], ("expectedDocument", ToolSchema.Text()), ("expectedProject", ToolSchema.Text()), ("name", ToolSchema.Text())), args => { AcaSupport.Require(); return Create(args); });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<object?> Create(JsonElement args) => MainThread.InvokeAsync<object?>(() =>
    {
#if ACA
        var doc = Acad.ActiveDocument; WorkflowSupport.CheckDocument(doc, args);
        var name = Args.Str(args, "name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('.') || name.EndsWith(' ') || name.Length > 120 ||
            System.Text.RegularExpressions.Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new AcadMcpException("bad_argument", "name must be a simple file stem without dots, trailing spaces or path separators (maximum 120 characters).");
        var expectedProject = Args.Str(args, "expectedProject");
        if (!Path.IsPathFullyQualified(expectedProject) || !string.Equals(Path.GetExtension(expectedProject), ".apj", StringComparison.OrdinalIgnoreCase) || !File.Exists(expectedProject))
            throw new AcadMcpException("bad_argument", "expectedProject must name an existing APJ by its full path.");
        using var pointer = new Autodesk.Aec.Project.ProjectPointer(expectedProject, OpenMode.ForWrite, false);
        pointer.SaveOnClose = false;
        var project = pointer.Object;
        if (project is null) throw new AcadMcpException("no_project", "No ACA project is open.");
        var current = project.Configuration.ProjectFileName.Path;
        if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(expectedProject), StringComparison.OrdinalIgnoreCase))
            throw new AcadMcpException("wrong_project", "The opened ACA project differs from expectedProject.");
        if (Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0) throw new AcadMcpException("unsaved_changes", "Run checkpoint_drawing before copying the construct.");
        var source = project.GetConstructs().Cast<Autodesk.Aec.Project.ProjectFile>()
            .SingleOrDefault(f => string.Equals(Path.GetFullPath(f.DrawingFullPath), Path.GetFullPath(doc.Database.Filename), StringComparison.OrdinalIgnoreCase));
        if (source is null) throw new AcadMcpException("not_construct", "The active drawing is not a construct in the current project.");
        var directory = Path.GetDirectoryName(source.DrawingFullPath)!;
        var destination = Path.Combine(directory, name + ".dwg");
        if (File.Exists(destination) || File.Exists(Path.ChangeExtension(destination, ".xml"))) throw new AcadMcpException("already_exists", "The destination drawing or project metadata already exists.");
        var option = new Autodesk.Aec.Project.ProjectFile(project);
        option.CopyFrom(source); option.FileName = name;
        if (!project.AddConstruct(option, source.DrawingFullPath, Autodesk.Aec.Project.FileAddOption.Copy))
            throw new AcadMcpException("project_copy_failed", "ACA could not copy the construct. Inspect the destination for partial files before retrying.");
        var created = project.GetConstructs().Cast<Autodesk.Aec.Project.ProjectFile>()
            .SingleOrDefault(f => string.Equals(Path.GetFullPath(f.DrawingFullPath), destination, StringComparison.OrdinalIgnoreCase));
        if (created is null || !File.Exists(destination)) throw new AcadMcpException("verification_failed", "Copy returned without a registered construct; inspect project files before retrying.");
        string Regions(Autodesk.Aec.Project.ProjectFile file) => string.Join(";", file.Regions.Cast<Autodesk.Aec.Project.ProjectRegion>()
            .Select(r => string.Join(",", r.LevelIds.Order()) + "/" + string.Join(",", r.WingIds.Order())).Order());
        if (Regions(created) != Regions(source) || created.Category != source.Category)
            throw new AcadMcpException("verification_failed", "The copied construct's level/division assignments or category differ from the source.");
        pointer.SaveOnClose = true;
        using var readback = new Database(false, true);
        readback.ReadDwgFile(destination, FileOpenMode.OpenForReadAndAllShare, true, null); readback.CloseInput(true);
        using var tr = readback.TransactionManager.StartTransaction();
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(readback), OpenMode.ForRead);
        var count = 0; foreach (ObjectId _ in ms) count++;
        return new { path = destination, project = current, category = created.Category, source = source.DrawingFullPath, modelSpaceEntities = count, units = readback.Insunits.ToString(), preservesXrefBaseDirectory = true };
#else
        throw AcaSupport.Unavailable();
#endif
    });
}
