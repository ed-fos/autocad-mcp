using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// run_csharp's error reporting, end to end on the real runtime: the source ScriptSource.Build
/// produces is compiled the way the add-in compiles it (Roslyn 4.10, portable PDB, collectible
/// load context), run, and its exception described. Only the AutoCAD parameter types differ.
/// </summary>
public class ScriptSourceTests
{
    private const string Parameters = "string doc, int db";

    private static readonly KeyValuePair<string, string>[] NoAliases = [];

    [Fact]
    public void A_runtime_error_names_the_api_that_threw_and_the_code_line()
    {
        var (message, detail) = RunAndDescribe(prelude: null, code: "var a = 1;\nreturn int.Parse(doc);");

        Assert.Contains("(thrown by Int32.Parse, called from line 2)", message);
        Assert.StartsWith("System.FormatException: ", detail);
        Assert.Contains("at System.Int32.Parse", detail);
        Assert.Contains("at line 2", detail);
    }

    [Fact]
    public void A_prelude_helper_is_numbered_apart_from_the_code_that_called_it()
    {
        // The drawing session's hatch helper: the API call is in a prelude local function, and
        // the caller needs both the helper's line and the code line that reached it.
        var prelude = "var LOG = new System.Text.StringBuilder();\nint Ha(string s)\n{\n    return int.Parse(s);\n}";
        var (message, detail) = RunAndDescribe(prelude, "LOG.Append(1);\nvar n = 2;\nreturn Ha(doc);");

        Assert.Contains("(thrown by Int32.Parse, called from Ha() at prelude line 4, reached from line 3)", message);
        Assert.Contains("at Ha() at prelude line 4 | at line 3", detail);
    }

    [Fact]
    public void A_throw_in_the_snippet_itself_reports_its_line()
    {
        var (message, _) = RunAndDescribe(null, "var x = 1;\nif (x == 1) throw new InvalidOperationException(\"boom\");\nreturn x;");
        Assert.Equal("boom (at line 2)", message);
    }

    [Fact]
    public void A_lambda_frame_is_named_as_one()
    {
        var (message, _) = RunAndDescribe(null, "Func<int, int> g = i => 10 / i;\nvar zero = db - 1;\nreturn g(zero);");
        Assert.Equal("Attempted to divide by zero. (at a lambda at line 1, reached from line 3)", message);
    }

    [Fact]
    public void Frames_below_the_snippet_entry_point_are_left_out()
    {
        // Everything under NodalScriptHost.Run is the bridge (here, this test) and is noise.
        var (_, detail) = RunAndDescribe(null, "return int.Parse(doc);");
        Assert.DoesNotContain(nameof(ScriptSourceTests), detail);
        Assert.DoesNotContain(nameof(RunAndDescribe), detail);
    }

    [Fact]
    public void A_deep_library_stack_is_trimmed_to_the_frames_nearest_the_snippet()
    {
        var (_, detail) = RunAndDescribe(null,
            "int Deep(int n) => n == 0 ? int.Parse(doc) : Deep(n - 1);\nreturn new[] { 1 }.Select(i => Deep(12)).ToList();");

        Assert.Contains("more snippet frame(s)", detail);
        Assert.Contains("at Deep() at line 1", detail);
    }

    [Fact]
    public void Compile_errors_map_to_code_and_prelude_lines()
    {
        var errors = Compile("int Bad() { return \"s\"; }", "var doc = 1;\nreturn Undefined;", ["System"], NoAliases)
            .Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToDictionary(d => d.Id, Where);

        Assert.Equal("prelude line 1", errors["CS0029"]);
        Assert.Equal("line 1", errors["CS0136"]);   // 'doc' is a parameter
        Assert.Equal("line 2", errors["CS0103"]);
    }

    [Fact]
    public void An_error_in_a_using_maps_to_no_caller_line()
    {
        var error = Compile(null, "return 1;", ["System", "No.Such.Namespace"], NoAliases)
            .Diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Equal("CS0246", error.Id);
        Assert.Null(Where(error));
    }

    [Fact]
    public void An_alias_settles_a_name_two_imported_namespaces_share()
    {
        // The PlotType / ContentType situation: both namespaces declare Timer.
        string[] usings = ["System", "System.Threading", "System.Timers"];
        const string code = "Timer t = null;\nreturn t;";

        Assert.Contains(Compile(null, code, usings, NoAliases).Diagnostics, d => d.Id == "CS0104");

        var aliased = Compile(null, code, usings, [new("Timer", "System.Threading.Timer")]);
        Assert.DoesNotContain(aliased.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Without_a_prelude_no_prelude_section_is_emitted()
    {
        var source = ScriptSource.Build(["System"], NoAliases, Parameters, null, "return 1;");
        Assert.DoesNotContain(ScriptSource.PreludeFile, source);
        Assert.Contains("#line 1 \"code\"", source);
    }

    [Fact]
    public void Locate_names_code_and_prelude_lines_only()
    {
        Assert.Equal("line 7", ScriptSource.Locate(ScriptSource.CodeFile, 7));
        Assert.Equal("prelude line 2", ScriptSource.Locate(ScriptSource.PreludeFile, 2));
        Assert.Null(ScriptSource.Locate("NodalScript_1.cs", 3));
        Assert.Null(ScriptSource.Locate(null, 0));
    }

    // ------------------------------------------------------------------ helpers

    private static string? Where(Diagnostic d)
    {
        var mapped = d.Location.GetMappedLineSpan();
        return ScriptSource.Locate(mapped.Path, mapped.StartLinePosition.Line + 1);
    }

    private static readonly List<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

    private static EmitResult Compile(string? prelude, string code, string[] usings,
        KeyValuePair<string, string>[] aliases, Stream? pe = null, Stream? pdb = null)
    {
        var source = ScriptSource.Build(usings, aliases, Parameters, prelude, code);
        var compilation = CSharpCompilation.Create(
            "TestScript_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest),
                path: "TestScript.cs", encoding: Encoding.UTF8)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

        return compilation.Emit(pe ?? new MemoryStream(), pdb ?? new MemoryStream(),
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
    }

    private static (string Message, string Detail) RunAndDescribe(string? prelude, string code)
    {
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = Compile(prelude, code, ["System", "System.Linq", "System.Collections.Generic"], NoAliases, pe, pdb);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

        pe.Position = 0;
        pdb.Position = 0;
        var context = new AssemblyLoadContext("ScriptSourceTests", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(pe, pdb);
            var run = assembly.GetType(ScriptSource.HostType)!
                .GetMethod(ScriptSource.HostMethod, BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate<Func<string, int, object?>>();

            try
            {
                run("not a number", 1);
            }
            catch (Exception ex)
            {
                return ScriptSource.DescribeException(ex, assembly);
            }

            throw new Xunit.Sdk.XunitException("The snippet did not throw.");
        }
        finally
        {
            context.Unload();
        }
    }
}
