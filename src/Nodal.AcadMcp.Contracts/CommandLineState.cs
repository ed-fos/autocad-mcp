namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// What run_lisp reports about the command line once its expression has returned, kept
/// AutoCAD-free so the rules can be unit-tested.
///
/// A (command ...) given too few inputs leaves its command waiting at a prompt after the LISP
/// returns. The reply used to be a plain success, and every later run_lisp answered 'busy' until
/// cancel_command cleared it. Null means CMDACTIVE was 0 and the reply carries nothing extra.
/// </summary>
public sealed record CommandLineState(string State, string? PendingCommand, int? CmdActive, string Hint)
{
    public const string Pending = "pending";
    public const string Unknown = "unknown";

    /// <summary>CMDACTIVE bit for a modal dialog, which Ctrl+C on the command line does not close.</summary>
    public const int DialogBit = 8;

    /// <summary>From CMDACTIVE and CMDNAMES read after the expression returned; null when nothing is active.</summary>
    public static CommandLineState? FromSystemVariables(int cmdActive, string? cmdNames)
    {
        if (cmdActive == 0) return null;

        var names = string.IsNullOrWhiteSpace(cmdNames) ? null : cmdNames.Trim();
        var hint = (cmdActive & DialogBit) != 0
            ? $"A dialog is open (CMDACTIVE {cmdActive}{(names is null ? "" : ", CMDNAMES '" + names + "'")}). "
            + "Ctrl+C on the command line does not close it, so cancel_command cannot clear it. Close it in AutoCAD."
            : $"{(names is null ? "A command" : names)} is still waiting at its prompt (CMDACTIVE {cmdActive}), "
            + "so run_lisp answers 'busy' until it is cleared. Call cancel_command, or give it the "
            + "remaining inputs with send_command if leaving it open was intended.";

        return new CommandLineState(Pending, names, cmdActive, hint);
    }

    /// <summary>CMDACTIVE could not be read in time: the reply must not hang on this check.</summary>
    public static CommandLineState NotRead(int waitedMs) => new(Unknown, null, null,
        $"CMDACTIVE could not be read within {waitedMs} ms of the expression returning; a prompt "
      + "that drags a preview (an insertion point, say) can hold off AutoCAD's idle loop. If the "
      + "next run_lisp answers 'busy', a command was left waiting: call cancel_command.");
}
