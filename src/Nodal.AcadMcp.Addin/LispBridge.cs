using System.Collections.Concurrent;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Nodal.AcadMcp.Addin;
using Nodal.AcadMcp.Contracts;

[assembly: CommandClass(typeof(LispBridge))]

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Evaluates arbitrary AutoLISP and returns its value.
///
/// WHY NOT Application.Invoke: acedInvoke only dispatches to ObjectARX/ADS functions registered
/// with acedDefun. AutoLISP built-ins (getvar, +, ...) and user defuns are NOT reachable through
/// it — it answers eInvalidInput for all of them, in every execution context.
///
/// So control is inverted. We expose source/result [LispFunction] callbacks, then ask AutoCAD to evaluate
/// the caller's source and hand the RESULT back into managed code. LISP is what calls us.
/// </summary>
public static class LispBridge
{
    private sealed class Pending(string source)
    {
        internal readonly string Source = source;
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static readonly ConcurrentDictionary<string, Pending> Requests = new();
    private static readonly SemaphoreSlim EvaluationGate = new(1, 1);

    // Keep the queued command short: large scripts otherwise get truncated by the command line.
    [LispFunction("nodal-mcp-source")]
    public static object? GetSource(ResultBuffer args)
    {
        var token = args?.AsArray().FirstOrDefault().Value?.ToString() ?? string.Empty;
        return Requests.TryGetValue(token, out var pending) ? pending.Source : "nil";
    }

    /// <summary>Called from LISP on success: (nodal-mcp-ok "token" value).</summary>
    [LispFunction("nodal-mcp-ok")]
    public static object? CaptureOk(ResultBuffer args)
    {
        var values = args?.AsArray() ?? [];
        if (values.Length == 0) return null;

        var token = values[0].Value?.ToString() ?? string.Empty;
        if (Requests.TryGetValue(token, out var pending))
        {
            // ObjectId conversion belongs on this AutoCAD callback thread, not the HTTP worker.
            try { pending.Completion.TrySetResult(Convert(values.Skip(1).ToArray())); }
            catch (System.Exception ex) { pending.Completion.TrySetException(ex); }
        }
        return null;
    }

    /// <summary>Called from LISP on failure: (nodal-mcp-err "token" "message").</summary>
    [LispFunction("nodal-mcp-err")]
    public static object? CaptureErr(ResultBuffer args)
    {
        var values = args?.AsArray() ?? [];
        if (values.Length == 0) return null;

        var token = values[0].Value?.ToString() ?? string.Empty;
        var message = values.Length > 1
            ? values[1].Value?.ToString() ?? "unknown AutoLISP error"
            : "unknown AutoLISP error";
        if (Requests.TryGetValue(token, out var pending))
            pending.Completion.TrySetException(new AcadMcpException("lisp_error", message));
        return null;
    }

    internal static async Task<(object? Value, string Document)> EvalAsync(string source, int timeoutMs)
    {
        await EvaluationGate.WaitAsync().ConfigureAwait(false);
        var token = "nmcp" + Guid.NewGuid().ToString("N");
        var pending = new Pending(source);
        Requests[token] = pending;
        var document = string.Empty;
        try
        {
            await MainThread.InvokeAsync(() =>
            {
                var doc = Acad.ActiveDocument;
                document = doc.Name;
                if (!doc.Editor.IsQuiescent)
                    throw new AcadMcpException("busy", "AutoCAD is in the middle of a command.", doc.CommandInProgress);
                doc.SendStringToExecute(BuildCall(token), true, false, false);
            }).ConfigureAwait(false);
            try { return (await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false), document); }
            catch (TimeoutException)
            {
                throw new AcadMcpException("lisp_timeout", $"AutoLISP did not return within {timeoutMs} ms.",
                    "The outcome is unknown; inspect the drawing before retrying a mutation. A running expression cannot be rolled back by this timeout.");
            }
        }
        finally
        {
            Requests.TryRemove(token, out _);
            EvaluationGate.Release();
        }
    }

    private static string BuildCall(string token)
    {
        return "(progn (vl-load-com)"
             + "(setq nodal-mcp-r (vl-catch-all-apply (function (lambda () (eval (read (nodal-mcp-source \"" + token + "\")))))))"
             + "(if (vl-catch-all-error-p nodal-mcp-r)"
             +     "(nodal-mcp-err \"" + token + "\" (vl-catch-all-error-message nodal-mcp-r))"
             +     "(nodal-mcp-ok \"" + token + "\" nodal-mcp-r))"
             + "(setq nodal-mcp-r nil)(princ))\n";
    }

    private static object? Convert(TypedValue[] values)
    {
        var index = 0;
        return values.Length == 0 ? null : ConvertOne(values, ref index);
    }

    private static object? ConvertOne(TypedValue[] v, ref int i)
    {
        if (i >= v.Length) return null;

        var tv = v[i];
        switch ((LispDataType)tv.TypeCode)
        {
            case LispDataType.ListBegin:
                i++;
                var list = new List<object?>();
                while (i < v.Length && (LispDataType)v[i].TypeCode != LispDataType.ListEnd)
                {
                    if ((LispDataType)v[i].TypeCode == LispDataType.DottedPair) { i++; continue; }
                    list.Add(ConvertOne(v, ref i));
                }
                if (i < v.Length) i++;   // consume ListEnd
                return list;

            case LispDataType.Nil:
            case LispDataType.None:
            case LispDataType.Void:
            case LispDataType.ListEnd:
                i++; return null;

            case LispDataType.T_atom:
                i++; return true;

            case LispDataType.Text:
                i++; return tv.Value?.ToString();

            case LispDataType.Int16:
            case LispDataType.Int32:
                i++; return System.Convert.ToInt32(tv.Value);

            case LispDataType.Double:
            case LispDataType.Angle:
            case LispDataType.Orientation:
                i++; return System.Convert.ToDouble(tv.Value);

            case LispDataType.Point2d:
                i++; return tv.Value is Point2d p2 ? new[] { p2.X, p2.Y } : null;

            case LispDataType.Point3d:
                i++; return tv.Value is Point3d p3 ? new[] { p3.X, p3.Y, p3.Z } : null;

            case LispDataType.ObjectId:
                i++;
                try { return tv.Value is ObjectId id && !id.IsNull ? id.Handle.ToString() : null; }
                catch { return null; }

            default:
                i++; return tv.Value?.ToString();
        }
    }

    /// <summary>
    /// Re-registers run_lisp over the EscapeTools version. Router.Register is last-write-wins,
    /// so this must be called after EscapeTools.Register().
    /// </summary>
    internal static void RegisterOverride()
    {
        Router.Register("run_lisp",
            "Evaluate an AutoLISP expression in the active drawing and RETURN ITS VALUE. "
          + "READ THIS FIRST: this is arbitrary code execution with no sandbox of any kind. "
          + "AutoLISP can read, overwrite and delete any file this Windows account can reach, "
          + "launch other programs with (startapp), drive COM with (vlax-create-object), and "
          + "change or destroy anything in the open drawing. Nothing is filtered or reviewed "
          + "before it runs. Pass 'expression'. Values come back typed: integers and reals as "
          + "numbers, strings as strings, points as [x,y] or [x,y,z], lists as nested JSON "
          + "arrays, entity names as handle strings, nil as null, T as true. LISP errors are "
          + "caught and returned as an error envelope rather than popping a dialog. Optional "
          + "'timeoutMs' (default 15000). Undo: whatever the code itself produces — this is NOT "
          + "a single undo step.",
            mutating: true,
            UndoKind.ToolDefined,
            async args =>
            {
                var expression = Args.Str(args, "expression");
                if (string.IsNullOrWhiteSpace(expression))
                    throw new AcadMcpException("bad_argument", "Argument 'expression' must not be empty.");

                var timeout = Math.Clamp(Args.IntOpt(args, "timeoutMs", 15000), 250, 120_000);
                var (value, document) = await EvalAsync(expression, timeout).ConfigureAwait(false);
                return new { expression, value, document };
            });
    }
}
