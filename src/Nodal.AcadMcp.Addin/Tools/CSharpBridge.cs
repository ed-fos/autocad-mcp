#if ROSLYN
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Nodal.AcadMcp.Contracts;
#endif

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Compiles and runs arbitrary C# against the live drawing.
///
/// Uses AutoCAD's OWN Roslyn (4.10.0.0, already loaded in-process) rather than a NuGet copy —
/// a second version of Microsoft.CodeAnalysis in the same process is a guaranteed conflict.
/// AutoCAD ships Microsoft.CodeAnalysis(.CSharp) but NOT CSharp.Scripting, so this drives the
/// compiler directly instead of using CSharpScript.
///
/// Each evaluation compiles to an in-memory assembly in its own collectible AssemblyLoadContext,
/// so repeated calls do not accumulate assemblies for the life of the AutoCAD session.
/// </summary>
internal static class CSharpBridge
{
    internal static void Register()
    {
        Router.Register("run_csharp",
            "Compile and execute C# against the live drawing and RETURN ITS VALUE. "
          + "READ THIS FIRST: this is arbitrary code execution with no sandbox whatsoever. The "
          + "snippet runs in-process with AutoCAD's full privileges — it can read, overwrite or "
          + "delete any file this Windows account can reach, start processes, and change or "
          + "destroy anything in the open drawing. Nothing is filtered or reviewed before it runs. "
          + "Mechanics: pass 'code' — the BODY of a method with signature "
          + "(Document doc, Database db, Transaction tr, Editor ed), so use 'return <expr>;' to "
          + "produce a value. Those four names are taken: 'var doc = ...' is error CS0136. "
          + "Optional 'prelude': helper code (local functions, shared variables) compiled into "
          + "the same method body ahead of 'code', so a helper library need not be pasted into "
          + "every snippet; errors number prelude lines separately. The transaction is already "
          + "open and is committed for you when write:true (default) or aborted when write:false, "
          + "so the whole call is ONE undo step. Imported: System, System.Linq, "
          + "System.Collections.Generic, the AutoCAD ApplicationServices, DatabaseServices, "
          + "EditorInput, Geometry, Colors and PlottingServices namespaces, and four Aec ones when "
          + "ACA is loaded; add more with 'usings' (array of namespace names). PlotType and "
          + "ContentType mean the DatabaseServices types; qualify any other name two imported "
          + "namespaces share (Surface, TransactionManager), or it is error CS0104. Compile "
          + "errors come back as compile_error with a line for each. A runtime exception comes "
          + "back as script_error naming the line it was thrown at and, in 'detail', the library "
          + "call that threw and the snippet frames that reached it. Iterate a DBDictionary with "
          + "foreach (DBDictionaryEntry e in dict); dict.Cast<DBDictionaryEntry>() throws. "
          + "Return values are converted: primitives and strings pass through, ObjectId and Entity "
          + "become handle strings, Point3d/Vector3d become [x,y,z], collections become arrays, "
          + "anything else becomes its ToString(). Undo: the DRAWING edits made through the "
          + "supplied transaction are one undo step, but anything the snippet does outside that "
          + "transaction — files, processes, system variables — is not undoable at all. "
          + "PAPER-SPACE VIEWPORTS — two AutoCAD 2027 failures that lose all unsaved work, both "
          + "reproduced through this tool: (1) HANG: making a layout current "
          + "(LayoutManager.CurrentLayout) in the same call that erased viewports which already "
          + "existed on that layout spins AutoCAD forever. (2) CRASH: setting Viewport.On = true "
          + "on a viewport created in the same call crashes AutoCAD intermittently (access "
          + "violation in roughly 1 run in 4), either when the call commits or at a later "
          + "LayoutManager.CurrentLayout switch in the same call. Safe pattern: switch layouts "
          + "in a call of their own; create or erase viewports in the next call without "
          + "switching; then turn the new viewports On in a later call, with their layout current.",
            mutating: true,
            Nodal.AcadMcp.Contracts.UndoKind.ToolDefined,
            Run);
    }

#if !ROSLYN
    private static Task<object?> Run(System.Text.Json.JsonElement args) =>
        throw new AcadMcpException("roslyn_unavailable",
            "This build was compiled without Roslyn support.",
            "Microsoft.CodeAnalysis.CSharp.dll was not found in the AutoCAD install directory at build time.");
#else

    /// <summary>Namespaces every snippet gets for free. Autodesk.AutoCAD.Runtime is deliberately
    /// NOT included: it declares its own 'Exception', which would shadow System.Exception.</summary>
    private static readonly string[] DefaultUsings =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "Autodesk.AutoCAD.ApplicationServices",
        "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.EditorInput",
        "Autodesk.AutoCAD.Geometry",
        "Autodesk.AutoCAD.Colors",
        "Autodesk.AutoCAD.PlottingServices",
    ];

    /// <summary>
    /// Names two default namespaces both declare, settled in favour of the type drafting code
    /// means. PlottingServices declares its own PlotType, so without this importing it would
    /// break every page-setup snippet that says PlotType.Window. The other type stays
    /// reachable by its full name.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] DefaultAliases =
    [
        new("PlotType", "Autodesk.AutoCAD.DatabaseServices.PlotType"),
    ];

    /// <summary>
    /// ACA namespaces safe to import by default.
    ///
    /// Autodesk.Aec.DatabaseServices and Autodesk.Aec.Geometry are deliberately EXCLUDED: ACA
    /// declares its own ObjectId, ObjectIdCollection and Profile, which collide with the AutoCAD
    /// types of the same name and make every snippet ambiguous. Import them explicitly via
    /// 'usings' when you actually need MassElement/LayoutGrid2d, and fully-qualify the clashes.
    /// </summary>
    private static readonly string[] AcaUsings =
    [
        "Autodesk.Aec.Arch.DatabaseServices",
        "Autodesk.Aec.Arch.Geometry",
        "Autodesk.Aec.PropertyData.DatabaseServices",
        "Autodesk.Aec.Structural.DatabaseServices",
    ];

    /// <summary>
    /// Autodesk.Aec.Arch.DatabaseServices declares a ContentType too, which made the MLeader
    /// one (MLeader.ContentType = ContentType.MTextContent) ambiguous whenever ACA was loaded.
    /// The alias keeps the meaning it has on plain AutoCAD.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] AcaAliases =
    [
        new("ContentType", "Autodesk.AutoCAD.DatabaseServices.ContentType"),
    ];

    private const string HostParameters = "Document doc, Database db, Transaction tr, Editor ed";

    private static int _counter;

    private static async Task<object?> Run(System.Text.Json.JsonElement args)
    {
        var code = Args.Str(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            throw new AcadMcpException("bad_argument", "Argument 'code' must not be empty.");

        var prelude = Args.StrOpt(args, "prelude");
        var write = Args.BoolOpt(args, "write", true);
        var extraUsings = Args.StrArrayOpt(args, "usings");

        var usings = new List<string>(DefaultUsings);
        var aliases = new List<KeyValuePair<string, string>>(DefaultAliases);
        if (AcaSupport.IsAvailable)
        {
            usings.AddRange(AcaUsings);
            aliases.AddRange(AcaAliases);
        }
        usings.AddRange(extraUsings);

        var source = ScriptSource.Build(usings, aliases, HostParameters, prelude, code);
        var name = "NodalScript_" + Interlocked.Increment(ref _counter);

        // ---- compile (off the main thread; it touches no AutoCAD state) ----
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest),
                path: name + ".cs", encoding: Encoding.UTF8)],
            BuildReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false));

        // The PDB is what gives a snippet's stack frames their 'code'/'prelude' line numbers.
        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(peStream, pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        if (!emit.Success)
        {
            var diagnostics = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new { id = d.Id, message = d.GetMessage(), where = WhereIs(d) })
                .ToList();

            throw new AcadMcpException("compile_error",
                $"C# did not compile ({diagnostics.Count} error(s)).",
                string.Join(" | ", diagnostics.Select(d => $"{d.id} ({d.where}): {d.message}")));
        }

        peStream.Position = 0;
        pdbStream.Position = 0;

        // Collectible so repeated evaluations do not pin assemblies for the session's lifetime.
        var alc = new AssemblyLoadContext(name, isCollectible: true);
        object? result;
        try
        {
            var assembly = alc.LoadFromStream(peStream, pdbStream);
            var method = assembly.GetType(ScriptSource.HostType)
                    ?.GetMethod(ScriptSource.HostMethod, BindingFlags.Public | BindingFlags.Static)
                ?? throw new AcadMcpException("internal_error", "Compiled script host was not found.");

            // A delegate rather than MethodInfo.Invoke, so the snippet's exception arrives as
            // itself, not wrapped in a TargetInvocationException.
            var snippet = method.CreateDelegate<Func<Document, Database, Transaction, Editor, object?>>();

            Func<AcadContext, object?> invoke = ctx =>
            {
                try
                {
                    // Enumerate deferred queries and read native handles while both the
                    // owning transaction and AutoCAD's main-thread context are still alive.
                    return Describe(snippet(ctx.Doc, ctx.Db, ctx.Tr, ctx.Ed), 0);
                }
                catch (System.Exception ex) when (ex is not AcadMcpException)
                {
                    // Read the frames now: they need the script assembly, which is unloaded
                    // once this call returns. Only strings leave, so nothing pins the context.
                    var (message, detail) = ScriptSource.DescribeException(ex, assembly);
                    throw new AcadMcpException("script_error", message, detail);
                }
            };

            result = write
                ? await Acad.Write(invoke).ConfigureAwait(false)
                : await Acad.Read(invoke).ConfigureAwait(false);

        }
        finally
        {
            try { alc.Unload(); } catch (System.Exception) { /* best effort */ }
        }

        return new { value = result, write, compiled = name };
    }

    /// <summary>
    /// Reference every assembly already loaded in the process. That automatically covers
    /// acdbmgd/acmgd/accoremgd, every Aec* assembly ACA has loaded, and the BCL — with exactly
    /// the versions AutoCAD is actually running, so a snippet can never bind to a different one.
    /// </summary>
    private static List<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;

            string location;
            try { location = asm.Location; }
            catch (System.Exception) { continue; }

            if (string.IsNullOrEmpty(location) || !seen.Add(location)) continue;

            try { refs.Add(MetadataReference.CreateFromFile(location)); }
            catch (System.Exception) { /* unreadable image; skip */ }
        }

        // The Aec assemblies may not be loaded yet when a snippet first needs them (they load
        // lazily, on first use of an ACA tool). Reference them straight off disk so ACA types are
        // always available. Paths already seen above are skipped, so nothing is referenced twice —
        // a duplicate reference would make every ACA type ambiguous.
        foreach (var dir in new[]
                 {
                     Path.Combine(AcadDirectory, "ACA"),
                     AcadDirectory,
                 })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "Aec*Mgd.dll"))
            {
                if (!seen.Add(file)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(file)); }
                catch (System.Exception) { /* not a managed image; skip */ }
            }
        }

        return refs;
    }

    /// <summary>Directory acdbmgd was loaded from — i.e. the live AutoCAD install.</summary>
    private static string AcadDirectory
    {
        get
        {
            try
            {
                var acdb = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => !a.IsDynamic &&
                                         string.Equals(a.GetName().Name, "acdbmgd", StringComparison.OrdinalIgnoreCase));
                var dir = acdb is null ? null : Path.GetDirectoryName(acdb.Location);
                return string.IsNullOrEmpty(dir) ? string.Empty : dir;
            }
            catch (System.Exception) { return string.Empty; }
        }
    }

    /// <summary>
    /// Where a compile error sits in what the caller sent. The #line directives map code and
    /// prelude lines; anything else is generated source, which is either a 'usings' entry or
    /// the method's closing lines, reached by an unbalanced brace in the snippet.
    /// </summary>
    private static string WhereIs(Diagnostic d)
    {
        try
        {
            var mapped = d.Location.GetMappedLineSpan();
            var where = ScriptSource.Locate(mapped.Path, mapped.StartLinePosition.Line + 1);
            if (where is not null) return where;

            var tree = d.Location.SourceTree;
            if (tree is null) return "generated source";
            var text = tree.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString();
            return text.StartsWith("using ", StringComparison.Ordinal)
                ? "usings"
                : "after the last line (check for an unbalanced brace)";
        }
        catch (System.Exception) { return "unknown line"; }
    }

    /// <summary>Convert a script's return value into something JSON-friendly.</summary>
    private static object? Describe(object? value, int depth)
    {
        if (value is null || depth > 4) return value is null ? null : value.ToString();

        switch (value)
        {
            case string or bool or int or long or short or byte or double or float or decimal:
                return value;

            case ObjectId id:
                try { return id.IsNull ? null : id.Handle.ToString(); } catch { return null; }

            case Handle h:
                return h.ToString();

            case Autodesk.AutoCAD.Geometry.Point3d p:
                return new[] { p.X, p.Y, p.Z };

            case Autodesk.AutoCAD.Geometry.Point2d p2:
                return new[] { p2.X, p2.Y };

            case Autodesk.AutoCAD.Geometry.Vector3d v:
                return new[] { v.X, v.Y, v.Z };

            case DBObject dbo:
                try { return dbo.ObjectId.IsNull ? dbo.GetType().Name : dbo.Handle.ToString(); }
                catch { return dbo.GetType().Name; }

            case IEnumerable seq:
                var list = new List<object?>();
                foreach (var item in seq)
                {
                    list.Add(Describe(item, depth + 1));
                    if (list.Count >= 2000) { list.Add("...truncated"); break; }
                }
                return list;

            default:
                return value.ToString();
        }
    }
#endif
}
