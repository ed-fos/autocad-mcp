using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// The AutoCAD-free half of run_csharp: building the compilable source around the caller's
/// snippet, and turning a snippet's exception back into lines of the text the caller sent.
///
/// The snippet and its optional prelude sit behind #line directives naming them "code" and
/// "prelude". Roslyn then reports compile diagnostics against those names and lines, and,
/// compiled with a PDB, the runtime's stack frames carry them too, so neither side has to
/// subtract a generated preamble.
/// </summary>
public static class ScriptSource
{
    public const string HostType = "NodalScriptHost";
    public const string HostMethod = "Run";

    /// <summary>The #line file name of the caller's 'code' argument.</summary>
    public const string CodeFile = "code";

    /// <summary>The #line file name of the caller's 'prelude' argument.</summary>
    public const string PreludeFile = "prelude";

    /// <summary>Script frames listed before the rest are summarised.</summary>
    private const int MaxScriptFrames = 8;

    /// <summary>Library frames shown above the innermost script frame (the API that threw).</summary>
    private const int MaxLibraryFrames = 3;

    /// <summary>
    /// The source compiled for one call: usings, aliases, then a static method whose body is the
    /// prelude followed by the code, so both share the method's parameters and locals.
    /// </summary>
    public static string Build(
        IEnumerable<string> usings,
        IEnumerable<KeyValuePair<string, string>> aliases,
        string parameters,
        string? prelude,
        string code)
    {
        var sb = new StringBuilder();
        foreach (var u in usings.Distinct(StringComparer.Ordinal))
            sb.Append("using ").Append(u).AppendLine(";");

        // An alias outranks the using-namespace directives, so it settles a name that two
        // imported namespaces both declare instead of leaving it ambiguous (CS0104).
        foreach (var (alias, target) in aliases)
            sb.Append("using ").Append(alias).Append(" = ").Append(target).AppendLine(";");

        sb.AppendLine();
        sb.Append("public static class ").AppendLine(HostType);
        sb.AppendLine("{");
        sb.Append("    public static object ").Append(HostMethod).Append('(').Append(parameters).AppendLine(")");
        sb.AppendLine("    {");
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.Append("#line 1 \"").Append(PreludeFile).AppendLine("\"");
            sb.AppendLine(prelude);
        }
        sb.Append("#line 1 \"").Append(CodeFile).AppendLine("\"");
        sb.AppendLine(code);
        sb.AppendLine("#line default");
        sb.AppendLine("        return null;");   // unreachable when the body returns; keeps void bodies legal
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Where a mapped position (a diagnostic's or a stack frame's file and 1-based line) falls in
    /// what the caller sent: "line 3" of 'code', "prelude line 3", or null for generated source
    /// (the usings and the method around the snippet).
    /// </summary>
    public static string? Locate(string? file, int line) => file switch
    {
        CodeFile => $"line {line}",
        PreludeFile => $"prelude line {line}",
        _ => null,
    };

    /// <summary>
    /// A snippet's exception as a one-line message and a frame list. The message names where in
    /// the caller's text it happened; the detail lists the library call that threw and the
    /// snippet frames that reached it, innermost first. Frames below the snippet's entry point
    /// belong to the bridge and are left out.
    /// </summary>
    public static (string Message, string Detail) DescribeException(Exception ex, Assembly script)
    {
        var frames = new StackTrace(ex, fNeedFileInfo: true).GetFrames();

        var scriptIndexes = new List<int>();
        for (var i = 0; i < frames.Length; i++)
            if (frames[i].GetMethod()?.DeclaringType?.Assembly == script) scriptIndexes.Add(i);

        var lines = new List<string>();
        string? where = null;
        string? api = null;

        if (scriptIndexes.Count == 0)
        {
            // Thrown before any snippet frame ran, or without debug info: keep the top frames.
            foreach (var frame in frames.Take(MaxLibraryFrames)) lines.Add("at " + LibraryFrame(frame));
        }
        else
        {
            var first = scriptIndexes[0];
            var last = scriptIndexes[^1];

            var libraryStart = Math.Max(0, first - MaxLibraryFrames);
            if (libraryStart > 0) lines.Add($"... {libraryStart} deeper frame(s)");
            for (var i = libraryStart; i < first; i++) lines.Add("at " + LibraryFrame(frames[i]));

            var shown = 0;
            for (var i = first; i <= last; i++)
            {
                var isScript = frames[i].GetMethod()?.DeclaringType?.Assembly == script;
                if (isScript && ++shown > MaxScriptFrames) continue;
                lines.Add("at " + (isScript ? ScriptFrame(frames[i]) : LibraryFrame(frames[i])));
            }
            if (shown > MaxScriptFrames) lines.Add($"... {shown - MaxScriptFrames} more snippet frame(s)");

            where = ScriptFrame(frames[first]);
            if (last != first) where += ", reached from " + ScriptFrame(frames[last]);

            // The library call the snippet made, e.g. Hatch.set_PatternAngle: usually the
            // single most useful fact about an AutoCAD error such as eInvalidInput.
            if (first > 0 && frames[first - 1].GetMethod() is { } called)
                api = (called.DeclaringType is { } type ? type.Name + "." : string.Empty) + called.Name;
        }

        var header = ex.GetType().FullName + ": " + ex.Message;
        if (ex.InnerException is { } inner)
            header += " (caused by " + inner.GetType().FullName + ": " + inner.Message + ")";

        var message = where is null ? ex.Message
            : api is null ? $"{ex.Message} (at {where})"
            : $"{ex.Message} (thrown by {api}, called from {where})";
        return (message, string.Join(" | ", lines.Prepend(header)));
    }

    private static string ScriptFrame(StackFrame frame)
    {
        var place = Locate(frame.GetFileName(), frame.GetFileLineNumber()) ?? "an unknown line";

        var name = frame.GetMethod()?.Name ?? string.Empty;
        var local = LocalFunction.Match(name);
        if (local.Success) return $"{local.Groups[1].Value}() at {place}";
        if (Lambda.IsMatch(name)) return $"a lambda at {place}";
        return place;
    }

    private static string LibraryFrame(StackFrame frame)
    {
        var method = frame.GetMethod();
        if (method is null) return "<unknown frame>";
        var type = method.DeclaringType?.FullName;
        return type is null ? method.Name : type + "." + method.Name;
    }

    /// <summary>Compiler names: &lt;Run&gt;g__Helper|0_3 is local function Helper.</summary>
    private static readonly Regex LocalFunction = new(@"^<[^>]*>g__([^|]+)\|", RegexOptions.CultureInvariant);

    /// <summary>Compiler names: &lt;Run&gt;b__0_1 is a lambda.</summary>
    private static readonly Regex Lambda = new(@"^<[^>]*>b__", RegexOptions.CultureInvariant);
}
