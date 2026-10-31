using System.Collections.Concurrent;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Nodal.AcadMcp.Addin;
using Nodal.AcadMcp.Addin.Tools;
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

    /// <summary>How long the reply waits for an Idle tick to read the command line after the LISP returns.</summary>
    private const int CommandStateWaitMs = 1000;

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

    /// <summary>
    /// CommandLine is null when nothing was left active. It is read while the gate is still held,
    /// so a second run_lisp cannot start in between and be mistaken for this one's leftover.
    /// </summary>
    internal static async Task<(object? Value, string Document, CommandLineState? CommandLine)> EvalAsync(string source, int timeoutMs)
    {
        await EvaluationGate.WaitAsync().ConfigureAwait(false);
        var token = "nmcp" + Guid.NewGuid().ToString("N");
        var pending = new Pending(source);
        Requests[token] = pending;
        var document = string.Empty;
        try
        {
            // A 3D mouse runs SPACEMOUSEACTION for a moment after every nudge of the view; that is
            // not a command the caller started, so wait briefly for it instead of answering busy.
            for (var attempt = 0; ; attempt++)
            {
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
                    break;
                }
                catch (AcadMcpException ex) when (ex.Code == "busy" && ex.Detail == "SPACEMOUSEACTION" && attempt < 10)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
            object? value;
            try { value = await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new AcadMcpException("lisp_timeout", $"AutoLISP did not return within {timeoutMs} ms.",
                    "The outcome is unknown; inspect the drawing before retrying a mutation. A running expression cannot be rolled back by this timeout.");
            }
            catch (AcadMcpException ex) when (ex.Code == "lisp_error")
            {
                // The rest of the expression was skipped, possibly after a (command ...) had started.
                var leftover = await ReadCommandLineAsync().ConfigureAwait(false);
                if (leftover is null) throw;
                throw new AcadMcpException(ex.Code, ex.Message, leftover.Hint);
            }
            return (value, document, await ReadCommandLineAsync().ConfigureAwait(false));
        }
        finally
        {
            Requests.TryRemove(token, out _);
            EvaluationGate.Release();
        }
    }

    /// <summary>
    /// Reads CMDACTIVE/CMDNAMES on the next Idle tick. The completion fires inside nodal-mcp-ok,
    /// while the LISP is still running on the main thread, so that tick comes after it returns.
    /// Idle also fires while a command waits at a prompt (that is how later calls see 'busy'),
    /// but a drag prompt may hold it off: the wait is capped and reported as unknown, and the
    /// queued read then runs harmlessly whenever Idle next fires.
    /// </summary>
    private static async Task<CommandLineState?> ReadCommandLineAsync()
    {
        try
        {
            var (active, names) = await MainThread.InvokeAsync(EscapeTools.ReadCommandState)
                .WaitAsync(TimeSpan.FromMilliseconds(CommandStateWaitMs)).ConfigureAwait(false);
            return CommandLineState.FromSystemVariables(active, names);
        }
        catch (TimeoutException)
        {
            return CommandLineState.NotRead(CommandStateWaitMs);
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
    /// The only run_lisp. (EscapeTools used to register an Application.Invoke version first,
    /// which this replaced; that path could never reach AutoLISP and has been removed.)
    /// </summary>
    internal static void Register()
    {
        Router.Register("run_lisp",
            "Evaluate an AutoLISP expression in the active drawing and RETURN ITS VALUE. "
          + "READ THIS FIRST: this is arbitrary code execution with no sandbox of any kind. "
          + "AutoLISP can read, overwrite and delete any file this Windows account can reach, "
          + "launch other programs with (startapp), drive COM with (vlax-create-object), and "
          + "change or destroy anything in the open drawing. Nothing is filtered or reviewed "
          + "before it runs. Pass 'expression'. It is evaluated at the drawing's command line, "
          + "as if typed there, so (command ...) works (-PDFIMPORT, DIMLINEAR, -TEXT and -INSERT "
          + "have all run this way), and the call is refused as 'busy' while a command is "
          + "active. Give a command every input it asks for: one still waiting when the "
          + "expression ends stays at its prompt, and every later call is 'busy' until "
          + "cancel_command clears it. The reply then carries commandState 'pending', "
          + "pendingCommand (CMDNAMES, e.g. '-INSERT'), cmdActive and a hint to call "
          + "cancel_command (a lisp_error carries that hint as its detail); commandState "
          + "'unknown' means CMDACTIVE could not be read within 1 s (a drag prompt can delay "
          + "it), and no commandState means the command line was idle. A command that opens a "
          + "dialog or hits (pause) waits for a person and the call times out, so use the '-' "
          + "command-line form with FILEDIA 0. Values come back typed: "
          + "integers and reals as numbers, strings as strings, points as [x,y] or [x,y,z], "
          + "lists as nested JSON arrays, entity names as handle strings, nil as null, T as "
          + "true. LISP errors are caught and returned as an error envelope rather than popping "
          + "a dialog; the rest of the expression is skipped, but what already ran (a setvar, "
          + "say) stays. Optional 'timeoutMs' (default 15000); a timeout does not stop the "
          + "expression. Undo: whatever the code itself produces — this is NOT a single undo step.",
            mutating: true,
            UndoKind.ToolDefined,
            async args =>
            {
                var expression = Args.Str(args, "expression");
                if (string.IsNullOrWhiteSpace(expression))
                    throw new AcadMcpException("bad_argument", "Argument 'expression' must not be empty.");

                var timeout = Math.Clamp(Args.IntOpt(args, "timeoutMs", 15000), 250, 120_000);
                var (value, document, commandLine) = await EvalAsync(expression, timeout).ConfigureAwait(false);
                return new
                {
                    expression,
                    value,
                    document,
                    commandState = commandLine?.State,
                    pendingCommand = commandLine?.PendingCommand,
                    cmdActive = commandLine?.CmdActive,
                    hint = commandLine?.Hint,
                };
            });
    }
}
