using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Nodal.AcadMcp.Contracts;
using AcRt = Autodesk.AutoCAD.Runtime;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// The escape hatches: raw AutoLISP, raw commands, system variables and view control.
///
/// Everything else in this add-in is a typed, bounded tool. This module is the deliberate hole in
/// that surface — it exists because no finite tool set covers AutoCAD, and pretending otherwise
/// just means the caller gives up. It is not safe, and it does not pretend to be.
///
/// Two kernel rules are consciously bent here, for one reason each:
///   * run_lisp and send_command are mutating but do NOT use Acad.Write. LISP and commands open
///     their own transactions and drive their own command loop; wrapping them in an outer
///     Transaction is the classic way to corrupt a drawing or hard-crash acad.exe. They take a
///     document lock and nothing more, so undo granularity is whatever the executed code produces.
///   * The zoom/regen tools change the view, not the drawing, so they are mutating:false. They
///     still take a document lock because the current view lives on a database object.
/// </summary>
internal static class EscapeTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;
    private const int MaxHandles = 5000;

    /// <summary>Output nesting cap for LISP lists. System.Text.Json bails out at depth 64.</summary>
    private const int MaxLispDepth = 24;

    /// <summary>Entity handles reported out of a LISP selection set before we stop listing them.</summary>
    private const int MaxSelectionHandles = 500;

    /// <summary>
    /// acedInvoke (Core.Application.Invoke) calls a LISP function BY NAME with already-evaluated
    /// arguments — it cannot evaluate an arbitrary expression, because a nested string in the
    /// argument list arrives in LISP as a string, not as a symbol. So we plant a one-line helper
    /// in the drawing's LISP namespace and hand it the source as a plain string argument.
    /// The marker variable is what we probe for; a defun's own symbol is not reliably readable.
    /// </summary>
    private const string HelperFunction = "nodal-mcp-eval";
    private const string HelperFlag = "NODAL-MCP-READY";
    private const string HelperDefun =
        "(defun nodal-mcp-eval (s) (eval (read s)))(setq NODAL-MCP-READY T)(princ)";

    /// <summary>How long we wait for AutoCAD to digest the queued helper defun.</summary>
    private const int HelperAttempts = 40;
    private const int HelperPollMs = 25;

    private static readonly string[] DefaultSysVars =
    [
        "ACADVER", "PRODUCT", "LOGINNAME", "DWGNAME", "DWGPREFIX", "DWGTITLED",
        "TILEMODE", "CTAB", "CVPORT", "CLAYER", "CECOLOR", "CELTYPE", "CELWEIGHT",
        "CANNOSCALE", "TEXTSTYLE", "DIMSTYLE", "CMLSTYLE", "INSUNITS", "MEASUREMENT",
        "LUNITS", "LUPREC", "AUNITS", "AUPREC", "LTSCALE", "PSLTSCALE",
        "OSMODE", "ORTHOMODE", "SNAPMODE", "GRIDMODE", "PDMODE", "PDSIZE",
        "FILEDIA", "CMDECHO", "CMDACTIVE", "EXPERT", "UCSNAME", "VIEWCTR", "VIEWSIZE",
    ];

    internal static void Register()
    {
        Router.Register("run_lisp",
            "Evaluate an AutoLISP expression in the active drawing and RETURN ITS VALUE. "
          + "READ THIS FIRST: this is arbitrary code execution with no sandbox of any kind. "
          + "AutoLISP can read, overwrite and delete any file this Windows account can reach, "
          + "start other programs with (startapp), drive COM/ActiveX with (vlax-create-object), "
          + "and change or destroy anything in the open drawing. Nothing is filtered, allow-listed "
          + "or reviewed before it runs — it executes with the full privileges of the logged-in "
          + "user, exactly as if they had typed it at the AutoCAD command line. Only send code a "
          + "competent operator would be happy to run on their own machine. "
          + "Mechanics: pass 'expression'. Several forms are allowed — they are wrapped in "
          + "(progn ...) and the value of the last one is returned (set wrapInProgn:false to "
          + "suppress that). Values come back typed: reals and integers as numbers, strings as "
          + "strings, points as [x,y] or [x,y,z], lists as JSON arrays, entity names as handle "
          + "strings, nil as null, T as true. Anything the code PRINTS to the command line is not "
          + "captured; only its return value is. (command ...) normally fails from here because "
          + "this runs outside AutoCAD's command context — use send_command for that. "
          + "Alternatively pass 'function' (a LISP function name) plus 'args' to call it directly, "
          + "without the (eval (read ...)) helper. Undo: whatever the code itself produces.",
            mutating: true,
            UndoKind.ToolDefined,
            RunLisp);

        Router.Register("send_command",
            "Send a command to the AutoCAD command line for things AutoLISP cannot reach. "
          + "Pass 'tokens' — an array where each element is one command-line input: a string "
          + "(command name, keyword or option), a number, or [x,y(,z)] for a point. 'command' "
          + "takes a single string instead. The command is tried synchronously through "
          + "Editor.Command; if AutoCAD refuses it (this bridge runs from the application context, "
          + "not a command context) it falls back to Document.SendStringToExecute, which QUEUES "
          + "the text and RETURNS NO VALUE — AutoCAD runs it after this call has already answered, "
          + "so a later failure is invisible here. The reply reports which path was taken under "
          + "'dispatch'. Force one with mode: 'command' or 'queue'. Queued text is whitespace "
          + "separated, so a token containing spaces will be read as several inputs. This is "
          + "arbitrary command execution: it can erase, overwrite, purge or save the drawing. "
          + "Prefix command names with _. for locale independence.",
            mutating: true,
            UndoKind.ToolDefined,
            SendCommand);

        Router.Register("get_system_variable",
            "Read one AutoCAD system variable by name (e.g. CLAYER, OSMODE, DIMSTYLE). Returns the "
          + "value typed, plus the CLR type AutoCAD stores it as — worth knowing before writing it "
          + "back. An unknown name is an error; use list_system_variables to probe several names "
          + "without failing the whole call.",
            mutating: false,
            GetSystemVariable);

        Router.Register("set_system_variable",
            "Write one AutoCAD system variable. 'value' may be a string, number, boolean or "
          + "[x,y(,z)] point; it is coerced to whatever CLR type the variable currently holds "
          + "(booleans become 1/0 for numeric variables), so you do not have to guess between "
          + "short, int, real and string. Read-only variables are refused by AutoCAD and that "
          + "error is surfaced verbatim. Returns the previous and the new value. System variables "
          + "are not on the undo stack — this cannot be taken back with Ctrl+Z. A small set of "
          + "security-relevant variables (SECURELOAD, TRUSTEDPATHS and similar) is refused here, "
          + "because they govern what AutoCAD will load and they persist in the user profile long "
          + "after this session ends.",
            mutating: true,
            UndoKind.NotUndoable,
            SetSystemVariable);

        Router.Register("list_system_variables",
            "Read many system variables in one round trip. Pass 'names' (an array of names); omit "
          + "it for a default snapshot of the variables that describe the drawing's current state "
          + "— units, current layer and styles, model vs paper space, snap and osnap settings. "
          + "Unknown names come back marked known:false instead of failing the call. Paginated: "
          + "'limit' (default 500) and 'offset'.",
            mutating: false,
            ListSystemVariables);

        Router.Register("zoom_extents",
            "Zoom the current viewport to the drawing extents — the equivalent of ZOOM Extents, "
          + "done through the view API rather than a command so it works from any context. Uses "
          + "paper-space extents when a layout is active and the paper viewport is current. "
          + "'margin' (default 1.0) multiplies the fitted window. EXTMIN/EXTMAX only ever grow as "
          + "objects are added, so after big deletions they can over-report; pass "
          + "recomputeExtents:true to have AutoCAD recompute them first — that writes new extents "
          + "into the drawing header and marks the drawing modified.",
            mutating: false,
            ZoomExtents);

        Router.Register("zoom_to_entities",
            "Zoom the current viewport to fit the given entity handles — the way to show a user "
          + "what a tool just found or changed. Handles that do not exist, and objects with no "
          + "geometry (empty text, some non-graphical objects), are reported back rather than "
          + "failing the call. 'margin' (default 1.1) leaves breathing room around the result; a "
          + "single point-like entity recentres the view at the current scale.",
            mutating: false,
            ZoomToEntities);

        Router.Register("regen",
            "Force a regeneration of the current viewport (REGEN) and refresh the screen. Use it "
          + "after a change AutoCAD has not redrawn — annotation scales, edited styles, or "
          + "anything altered through run_lisp. For REGENALL across every viewport, use "
          + "send_command with _.REGENALL.",
            mutating: false,
            Regen);
    }

    // ---------------------------------------------------------------- run_lisp

    private static async Task<object?> RunLisp(JsonElement args)
    {
        var function = Args.StrOpt(args, "function");

        if (!string.IsNullOrWhiteSpace(function))
        {
            var direct = BuildLispCall(function!, args);
            return await MainThread.InCommandContextAsync<object?>(() =>
            {
                var reply = InvokeLisp(direct);
                return new
                {
                    function,
                    value = reply.Value,
                    valueKind = reply.Kind,
                    returnedNothing = reply.Empty,
                    document = Acad.ActiveDocument.Name,
                };
            }).ConfigureAwait(false);
        }

        var expression = Args.Str(args, "expression");
        if (string.IsNullOrWhiteSpace(expression))
            throw new AcadMcpException("bad_argument", "Argument 'expression' must not be empty.");

        var wrap = Args.BoolOpt(args, "wrapInProgn", true);
        var source = wrap ? "(progn " + expression + "\n)" : expression;

        await EnsureLispHelper().ConfigureAwait(false);

        var call = new[]
        {
            new TypedValue((int)AcRt.LispDataType.Text, HelperFunction),
            new TypedValue((int)AcRt.LispDataType.Text, source),
        };

        return await MainThread.InCommandContextAsync<object?>(() =>
        {
            var reply = InvokeLisp(call);
            return new
            {
                expression,
                source,
                value = reply.Value,
                valueKind = reply.Kind,
                returnedNothing = reply.Empty,
                document = Acad.ActiveDocument.Name,
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Plant the (eval (read ...)) helper in this drawing's LISP namespace if it is not there.
    /// LISP namespaces are per-document, so this runs once per drawing, not once per session.
    /// </summary>
    private static async Task EnsureLispHelper()
    {
        if (await MainThread.InvokeAsync<bool>(() => HelperPresent()).ConfigureAwait(false)) return;

        await MainThread.InvokeAsync<int>(() =>
        {
            var doc = Acad.ActiveDocument;
            if (!doc.Editor.IsQuiescent)
                throw new AcadMcpException("busy",
                    "AutoCAD is in the middle of a command, so the AutoLISP helper cannot be defined.",
                    string.IsNullOrEmpty(doc.CommandInProgress) ? null : doc.CommandInProgress);

            // Queued, not immediate: defining a function needs the command loop, which only turns
            // once we hand the main thread back.
            doc.SendStringToExecute(HelperDefun + "\n", true, false, false);
            return 0;
        }).ConfigureAwait(false);

        for (var attempt = 0; attempt < HelperAttempts; attempt++)
        {
            await Task.Delay(HelperPollMs).ConfigureAwait(false);
            if (await MainThread.InvokeAsync<bool>(() => HelperPresent()).ConfigureAwait(false)) return;
        }

        throw new AcadMcpException("lisp_unavailable",
            "AutoCAD never evaluated the queued AutoLISP helper, so run_lisp cannot evaluate expressions.",
            "Check that AutoLISP is enabled (LISPSYS) and that no command or modal dialog is "
          + "holding the command line. Calling a named function with 'function' + 'args' does not "
          + "need the helper.");
    }

    private static bool HelperPresent()
    {
        try { return Acad.ActiveDocument.GetLispSymbol(HelperFlag) is not null; }
        catch (AcRt.Exception) { return false; }
        catch (System.Exception) { return false; }
    }

    /// <summary>acedInvoke: element 0 is the function name, the rest are its evaluated arguments.</summary>
    private static TypedValue[] BuildLispCall(string function, JsonElement args)
    {
        var call = new List<TypedValue> { new((int)AcRt.LispDataType.Text, function) };

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("args", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
                call.Add(ToLispArgument(item));
        }

        return call.ToArray();
    }

    private static TypedValue ToLispArgument(JsonElement item)
    {
        switch (item.ValueKind)
        {
            case JsonValueKind.String:
                return new TypedValue((int)AcRt.LispDataType.Text, item.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                if (item.TryGetInt32(out var whole))
                    return new TypedValue((int)AcRt.LispDataType.Int32, whole);
                return new TypedValue((int)AcRt.LispDataType.Double, item.GetDouble());

            case JsonValueKind.True:
                return new TypedValue((int)AcRt.LispDataType.T_atom);

            case JsonValueKind.False:
            case JsonValueKind.Null:
                return new TypedValue((int)AcRt.LispDataType.Nil);

            case JsonValueKind.Array:
                return new TypedValue((int)AcRt.LispDataType.Point3d, PointFrom(item));

            default:
                throw new AcadMcpException("bad_argument",
                    "LISP arguments must be strings, numbers, booleans, null or [x,y(,z)] points.",
                    item.ValueKind.ToString());
        }
    }

    private static (object? Value, string Kind, bool Empty) InvokeLisp(TypedValue[] call)
    {
        var doc = Acad.ActiveDocument;
        using var docLock = doc.LockDocument();
        using var request = new ResultBuffer(call);

        ResultBuffer? reply;
        try
        {
            reply = AcApp.Invoke(request);
        }
        catch (AcRt.Exception ex)
        {
            throw new AcadMcpException("lisp_error",
                "AutoLISP rejected or failed the call: " + ex.Message,
                ex.ErrorStatus.ToString());
        }

        if (reply is null) return (null, "Nil", true);

        using (reply)
        {
            var values = reply.AsArray();
            if (values.Length == 0) return (null, "Nil", true);

            var index = 0;
            var first = ReadLispValue(values, ref index, 0);
            var kind = ((AcRt.LispDataType)values[0].TypeCode).ToString();

            if (index >= values.Length) return (first, kind, false);

            var all = new List<object?> { first };
            while (index < values.Length) all.Add(ReadLispValue(values, ref index, 0));
            return (all, "ListBegin", false);
        }
    }

    private static object? ReadLispValue(TypedValue[] values, ref int index, int depth)
    {
        if (index >= values.Length) return null;

        var typed = values[index];
        var kind = (AcRt.LispDataType)typed.TypeCode;
        index++;

        switch (kind)
        {
            case AcRt.LispDataType.ListBegin:
                if (depth >= MaxLispDepth)
                {
                    SkipBalancedList(values, ref index);
                    return "<list truncated at depth " + MaxLispDepth + ">";
                }

                var items = new List<object?>();
                while (index < values.Length)
                {
                    var next = (AcRt.LispDataType)values[index].TypeCode;
                    if (next == AcRt.LispDataType.ListEnd) { index++; break; }
                    if (next == AcRt.LispDataType.DottedPair) { index++; continue; }
                    items.Add(ReadLispValue(values, ref index, depth + 1));
                }
                return items;

            case AcRt.LispDataType.ListEnd:
            case AcRt.LispDataType.DottedPair:
            case AcRt.LispDataType.Nil:
            case AcRt.LispDataType.None:
            case AcRt.LispDataType.Void:
                return null;

            case AcRt.LispDataType.T_atom:
                return true;

            default:
                return ToJsonValue(typed.Value);
        }
    }

    /// <summary>Consume a sublist we chose not to materialise, keeping the cursor aligned.</summary>
    private static void SkipBalancedList(TypedValue[] values, ref int index)
    {
        var depth = 1;
        while (index < values.Length && depth > 0)
        {
            var kind = (AcRt.LispDataType)values[index].TypeCode;
            if (kind == AcRt.LispDataType.ListBegin) depth++;
            else if (kind == AcRt.LispDataType.ListEnd) depth--;
            index++;
        }
    }

    // ------------------------------------------------------------ send_command

    private static Task<object?> SendCommand(JsonElement args)
    {
        var tokens = BuildCommandTokens(args);
        var mode = (Args.StrOpt(args, "mode", "auto") ?? "auto").Trim().ToLowerInvariant();

        if (mode is not ("auto" or "command" or "queue"))
            throw new AcadMcpException("bad_argument", "Argument 'mode' must be 'auto', 'command' or 'queue'.");

        var echo = Describe(tokens);

        return MainThread.InvokeAsync<object?>(() =>
        {
            var doc = Acad.ActiveDocument;
            using var docLock = doc.LockDocument();

            string? fallbackReason = null;

            if (mode != "queue")
            {
                try
                {
                    doc.Editor.Command(tokens);
                    return new
                    {
                        dispatch = "editor.command",
                        queued = false,
                        tokens = echo,
                        document = doc.Name,
                    };
                }
                catch (AcRt.Exception ex)
                {
                    if (mode == "command")
                        throw new AcadMcpException("command_failed", ex.Message, ex.ErrorStatus.ToString());

                    fallbackReason = ex.Message + " (" + ex.ErrorStatus + ")";
                }
            }

            var line = ToCommandLine(tokens);
            doc.SendStringToExecute(line, true, false, true);

            return new
            {
                dispatch = "sendStringToExecute",
                queued = true,
                tokens = echo,
                commandLine = line.TrimEnd('\n'),
                fallbackReason,
                note = "Queued: AutoCAD runs this after the call returns, so success or failure is not reported back.",
                document = doc.Name,
            };
        });
    }

    private static object[] BuildCommandTokens(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("tokens", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            var tokens = new List<object>();
            foreach (var item in list.EnumerateArray())
                tokens.Add(ToCommandToken(item));

            if (tokens.Count == 0)
                throw new AcadMcpException("bad_argument", "Argument 'tokens' must contain at least one input.");

            return tokens.ToArray();
        }

        var single = Args.Str(args, "command");
        if (string.IsNullOrWhiteSpace(single))
            throw new AcadMcpException("bad_argument", "Argument 'command' must not be empty.");

        return [single];
    }

    private static object ToCommandToken(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString() ?? string.Empty,
        JsonValueKind.Number => item.TryGetInt32(out var whole) ? whole : item.GetDouble(),
        JsonValueKind.Array => PointFrom(item),
        _ => throw new AcadMcpException("bad_argument",
                 "Command tokens must be strings, numbers or [x,y(,z)] points.",
                 item.ValueKind.ToString()),
    };

    private static string[] Describe(object[] tokens)
    {
        var text = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++) text[i] = TokenText(tokens[i]);
        return text;
    }

    private static string ToCommandLine(object[] tokens)
    {
        var line = new StringBuilder();
        foreach (var token in tokens)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append(TokenText(token));
        }
        line.Append('\n');
        return line.ToString();
    }

    private static string TokenText(object? token) => token switch
    {
        null => string.Empty,
        Point3d p => Num(p.X) + "," + Num(p.Y) + "," + Num(p.Z),
        double d => Num(d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => token.ToString() ?? string.Empty,
    };

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // -------------------------------------------------------- system variables

    private static Task<object?> GetSystemVariable(JsonElement args)
    {
        var name = Args.Str(args, "name");
        return MainThread.InvokeAsync<object?>(() => DescribeSysVar(name, throwIfUnknown: true));
    }

    /// <summary>
    /// System variables that govern what AutoCAD is willing to LOAD AND RUN, or where it will
    /// look for it. They are stored in the user profile, not the drawing, so a write here
    /// outlives the session, the drawing and this add-in being uninstalled — which turns one
    /// tool call into a permanent weakening of AutoCAD's own defences against malicious
    /// LISP/ARX. Nothing legitimate about driving CAD from an MCP client needs them.
    ///
    /// This is a guard rail, not a security boundary: anyone who can reach run_lisp can call
    /// (setvar) directly. It is here so that the DEFAULT surface cannot be turned into
    /// persistence by a single tool call, and so that doing it requires visible intent.
    /// </summary>
    private static readonly HashSet<string> ProtectedSysVars = new(StringComparer.OrdinalIgnoreCase)
    {
        "SECURELOAD",       // whether unsigned/untrusted executables load at all
        "TRUSTEDPATHS",     // directories exempted from that check
        "TRUSTEDDOMAINS",   // domains exempted for web-loaded content
        "ACADLSPASDOC",     // whether acad.lsp reloads per document
        "LISPSYS",          // AutoLISP engine selection
        "AUTOLOAD",         // demand-loading behaviour
        "SECUREREMOTEACCESS",
    };

    private static Task<object?> SetSystemVariable(JsonElement args)
    {
        var name = Args.Str(args, "name");
        var value = RequiredElement(args, "value");

        if (ProtectedSysVars.Contains(name.Trim()))
            throw new AcadMcpException("protected_sysvar",
                $"'{name.Trim().ToUpperInvariant()}' controls what AutoCAD will load and execute, and "
              + "the setting persists in the user profile after this session ends. This bridge will "
              + "not write it.",
                "Change it deliberately from the AutoCAD command line if you really intend to.");

        return MainThread.InvokeAsync<object?>(() =>
        {
            // Most system variables are document scoped, so writing one from the application
            // context needs the document lock a database edit would need.
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            DocumentLock? docLock = doc?.LockDocument();
            try
            {
                object? current;
                try
                {
                    current = AcApp.GetSystemVariable(name);
                }
                catch (AcRt.Exception ex)
                {
                    throw new AcadMcpException("unknown_sysvar",
                        $"AutoCAD has no readable system variable named '{name}'.", ex.ErrorStatus.ToString());
                }

                if (current is null)
                    throw new AcadMcpException("unknown_sysvar",
                        $"AutoCAD has no readable system variable named '{name}'.");

                var coerced = Coerce(current, value, name);

                try
                {
                    AcApp.SetSystemVariable(name, coerced);
                }
                catch (AcRt.Exception ex)
                {
                    throw new AcadMcpException("sysvar_write_failed",
                        $"AutoCAD refused to set '{name}': {ex.Message}. It may be read-only, or the "
                      + "value may be out of range.", ex.ErrorStatus.ToString());
                }

                object? updated = null;
                try { updated = AcApp.GetSystemVariable(name); }
                catch (AcRt.Exception) { }
                catch (System.Exception) { }

                var settled = updated ?? coerced;

                return new
                {
                    name,
                    previous = ToJsonValue(current),
                    value = ToJsonValue(settled),
                    type = settled.GetType().Name,
                    readBack = updated is not null,
                };
            }
            finally
            {
                docLock?.Dispose();
            }
        });
    }

    private static Task<object?> ListSystemVariables(JsonElement args)
    {
        var requested = Args.StrArrayOpt(args, "names");
        var names = requested.Length > 0 ? requested : DefaultSysVars;

        var offset = Math.Max(0, Args.IntOpt(args, "offset", 0));
        var limit = Args.IntOpt(args, "limit", DefaultLimit);
        if (limit <= 0) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;

        return MainThread.InvokeAsync<object?>(() =>
        {
            var page = names.Skip(offset).Take(limit).ToArray();
            var items = new List<object>(page.Length);
            foreach (var name in page) items.Add(DescribeSysVar(name, throwIfUnknown: false));

            return new
            {
                items,
                total = names.Length,
                offset,
                limit,
                truncated = offset + page.Length < names.Length,
                usedDefaultNames = requested.Length == 0,
            };
        });
    }

    private static object DescribeSysVar(string name, bool throwIfUnknown)
    {
        object? raw = null;
        string? failure = null;

        try
        {
            raw = AcApp.GetSystemVariable(name);
        }
        catch (AcRt.Exception ex) { failure = ex.ErrorStatus.ToString(); }
        catch (System.Exception ex) { failure = ex.Message; }

        if (raw is null)
        {
            if (throwIfUnknown)
                throw new AcadMcpException("unknown_sysvar",
                    $"AutoCAD has no readable system variable named '{name}'.", failure);

            return new { name, known = false, error = failure ?? "not readable in this context" };
        }

        return new { name, known = true, value = ToJsonValue(raw), type = raw.GetType().Name };
    }

    /// <summary>
    /// Coerce an incoming JSON value to the CLR type the variable already holds. AutoCAD rejects
    /// an int where it wants a short, so the current value is the only reliable schema we have.
    /// </summary>
    private static object Coerce(object current, JsonElement value, string name)
    {
        if (current is string)
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ToString();

        if (current is Point3d) return PointFrom(value);

        if (current is Point2d)
        {
            var point = PointFrom(value);
            return new Point2d(point.X, point.Y);
        }

        var number = AsNumber(value, name);

        if (current is short) return (short)Math.Round(number);
        if (current is int) return (int)Math.Round(number);
        if (current is long) return (long)Math.Round(number);
        if (current is double) return number;
        if (current is float) return (float)number;
        if (current is bool) return number != 0.0;

        throw new AcadMcpException("unsupported_sysvar",
            $"System variable '{name}' holds a {current.GetType().Name}, which this tool cannot set.");
    }

    private static double AsNumber(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1.0,
        JsonValueKind.False => 0.0,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any,
                                                  CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new AcadMcpException("bad_argument",
                 $"System variable '{name}' is numeric, so 'value' must be a number or a boolean."),
    };

    // ------------------------------------------------------------------- views

    private static Task<object?> ZoomExtents(JsonElement args)
    {
        var margin = Args.DblOpt(args, "margin", 1.0);
        var recompute = Args.BoolOpt(args, "recomputeExtents", false);

        return MainThread.InvokeAsync<object?>(() =>
        {
            var doc = Acad.ActiveDocument;
            using var docLock = doc.LockDocument();
            var db = doc.Database;

            if (recompute) db.UpdateExt(true);

            var paper = !db.TileMode && CurrentViewportNumber() == 1;
            var min = paper ? db.Pextmin : db.Extmin;
            var max = paper ? db.Pextmax : db.Extmax;

            if (min.X > max.X || min.Y > max.Y)
                throw new AcadMcpException("empty_extents",
                    "There are no extents to zoom to — "
                  + (paper ? "this layout is empty." : "model space is empty."),
                    "EXTMIN is greater than EXTMAX, which is how AutoCAD represents an empty space.");

            var view = ApplyView(doc, new Extents3d(min, max), margin);

            return new
            {
                space = paper ? "paper" : "model",
                recomputedExtents = recompute,
                min = Xyz(min),
                max = Xyz(max),
                view,
                document = doc.Name,
            };
        });
    }

    private static async Task<object?> ZoomToEntities(JsonElement args)
    {
        var handles = Args.StrArray(args, "handles");
        if (handles.Length == 0)
            throw new AcadMcpException("bad_argument", "Argument 'handles' must contain at least one handle.");
        if (handles.Length > MaxHandles)
            throw new AcadMcpException("too_many",
                $"'handles' holds {handles.Length} entries; the limit is {MaxHandles}.");

        var margin = Args.DblOpt(args, "margin", 1.1);

        var gathered = await Acad.Read(ctx => GatherExtents(ctx, handles)).ConfigureAwait(false);

        if (gathered.Box is not Extents3d box)
            throw new AcadMcpException("no_extents",
                "None of those handles produced any geometry to zoom to.",
                $"{gathered.Missing.Count} not found, {gathered.Skipped.Count} had no usable extents.");

        return await MainThread.InvokeAsync<object?>(() =>
        {
            var doc = Acad.ActiveDocument;
            using var docLock = doc.LockDocument();
            var view = ApplyView(doc, box, margin);

            return new
            {
                zoomed = gathered.Used,
                notFound = gathered.Missing,
                noGeometry = gathered.Skipped,
                min = Xyz(box.MinPoint),
                max = Xyz(box.MaxPoint),
                view,
                document = doc.Name,
            };
        }).ConfigureAwait(false);
    }

    private static (Extents3d? Box, int Used, List<string> Missing, List<string> Skipped)
        GatherExtents(AcadContext ctx, string[] handles)
    {
        Extents3d? box = null;
        var used = 0;
        var missing = new List<string>();
        var skipped = new List<string>();

        foreach (var handle in handles)
        {
            ObjectId id;
            try
            {
                id = Acad.ToObjectId(ctx.Db, handle);
            }
            catch (AcadMcpException)
            {
                missing.Add(handle);
                continue;
            }

            if (ctx.Tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                skipped.Add(handle);
                continue;
            }

            try
            {
                // Empty text, empty blocks and some non-graphical objects throw here.
                var extents = entity.GeometricExtents;
                if (box is Extents3d running)
                {
                    running.AddExtents(extents);
                    box = running;
                }
                else
                {
                    box = extents;
                }
                used++;
            }
            catch (AcRt.Exception)
            {
                skipped.Add(handle);
            }
            catch (System.Exception)
            {
                skipped.Add(handle);
            }
        }

        return (box, used, missing, skipped);
    }

    private static Task<object?> Regen(JsonElement args)
    {
        _ = args;
        return MainThread.InvokeAsync<object?>(() =>
        {
            var doc = Acad.ActiveDocument;
            using var docLock = doc.LockDocument();
            doc.Editor.Regen();
            doc.Editor.UpdateScreen();
            return new { regenerated = true, document = doc.Name };
        });
    }

    /// <summary>
    /// Fit the current view around a WCS box. A view is defined in Display Coordinate Space, so
    /// the box has to be pushed through the WCS-to-DCS transform first — otherwise a twisted or
    /// 3D view zooms to the wrong rectangle.
    /// </summary>
    private static object ApplyView(Document doc, Extents3d target, double margin)
    {
        if (margin <= 0.0 || double.IsNaN(margin)) margin = 1.0;

        var ed = doc.Editor;
        using var view = ed.GetCurrentView();

        var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        wcsToDcs = Matrix3d.Displacement(view.Target.GetAsVector()).PostMultiplyBy(wcsToDcs);
        wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                           .PostMultiplyBy(wcsToDcs);
        wcsToDcs = wcsToDcs.Inverse();

        var box = new Extents3d(target.MinPoint, target.MaxPoint);
        box.TransformBy(wcsToDcs);

        var width = box.MaxPoint.X - box.MinPoint.X;
        var height = box.MaxPoint.Y - box.MinPoint.Y;
        var centre = new Point2d((box.MaxPoint.X + box.MinPoint.X) * 0.5,
                                 (box.MaxPoint.Y + box.MinPoint.Y) * 0.5);

        var aspect = view.Height > 1e-12 ? view.Width / view.Height : 1.0;
        if (aspect <= 1e-12 || double.IsNaN(aspect)) aspect = 1.0;

        var degenerate = width <= 1e-9 && height <= 1e-9;
        if (degenerate)
        {
            // A single point has no size: keep the current scale and just recentre.
            height = view.Height > 1e-12 ? view.Height : 1.0;
            width = height * aspect;
        }
        else if (width / Math.Max(height, 1e-12) > aspect)
        {
            height = width / aspect;
        }
        else
        {
            width = height * aspect;
        }

        view.Width = width * margin;
        view.Height = height * margin;
        view.CenterPoint = centre;

        ed.SetCurrentView(view);
        ed.UpdateScreen();

        return new
        {
            center = new[] { centre.X, centre.Y },
            width = view.Width,
            height = view.Height,
            margin,
            recentredOnly = degenerate,
        };
    }

    private static int CurrentViewportNumber()
    {
        try { return Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"), CultureInfo.InvariantCulture); }
        catch (AcRt.Exception) { return 0; }
        catch (System.Exception) { return 0; }
    }

    // ------------------------------------------------------------------ shared

    private static JsonElement RequiredElement(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            throw new AcadMcpException("bad_argument", $"Argument '{name}' is required.");
        }
        return value;
    }

    private static Point3d PointFrom(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new AcadMcpException("bad_argument", "A point must be an array like [x, y] or [x, y, z].");

        var numbers = new List<double>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
                throw new AcadMcpException("bad_argument", "Point coordinates must be numbers.");
            numbers.Add(item.GetDouble());
        }

        if (numbers.Count is < 2 or > 3)
            throw new AcadMcpException("bad_argument",
                $"A point must have 2 or 3 numbers; got {numbers.Count}.");

        return new Point3d(numbers[0], numbers[1], numbers.Count > 2 ? numbers[2] : 0.0);
    }

    private static double[] Xyz(Point3d p) => [p.X, p.Y, p.Z];

    /// <summary>Anything AutoCAD hands back, reduced to something System.Text.Json can print.</summary>
    private static object? ToJsonValue(object? raw) => raw switch
    {
        null => null,
        string s => s,
        bool b => b,
        short v => (int)v,
        int v => v,
        long v => v,
        float v => (double)v,
        double v => v,
        Point3d p => new[] { p.X, p.Y, p.Z },
        Point2d p => new[] { p.X, p.Y },
        Vector3d v => new[] { v.X, v.Y, v.Z },
        ObjectId id => HandleOf(id),
        SelectionSet set => DescribeSelectionSet(set),
        _ => raw.ToString(),
    };

    private static string? HandleOf(ObjectId id)
    {
        try { return id.IsNull ? null : id.Handle.ToString(); }
        catch (AcRt.Exception) { return null; }
        catch (System.Exception) { return null; }
    }

    private static object DescribeSelectionSet(SelectionSet set)
    {
        var handles = new List<string>();
        var count = 0;

        try
        {
            var ids = set.GetObjectIds();
            count = ids.Length;
            foreach (var id in ids)
            {
                if (handles.Count >= MaxSelectionHandles) break;
                var handle = HandleOf(id);
                if (handle is not null) handles.Add(handle);
            }
        }
        catch (AcRt.Exception) { }
        catch (System.Exception) { }

        return new
        {
            selectionSet = true,
            count,
            handles,
            truncated = count > handles.Count,
        };
    }
}
