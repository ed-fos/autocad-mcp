using System.Text.RegularExpressions;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// What send_command and cancel_command put on AutoCAD's command line, kept AutoCAD-free so the
/// rules can be unit-tested.
/// </summary>
public static class CommandText
{
    /// <summary>
    /// Ctrl+C twice, with NO trailing newline: the first ends a transparent or nested command,
    /// the second the command under it. A newline after them reaches the bare Command: prompt,
    /// where Enter repeats the last command — so the cancel would start it again.
    /// </summary>
    public const string CancelSequence = "\x03\x03";

    /// <summary>
    /// True for a token meant as a cancel keystroke: Ctrl+C or Esc as a control character, or
    /// written in menu-macro caret notation ("^C", "^C^C"). SendStringToExecute does not
    /// translate caret notation — only the menu macro parser does — so "^C" arrives at the
    /// prompt as the two characters ^ and C; and send_command ends the line with Enter.
    /// </summary>
    public static bool LooksLikeCancel(string token) =>
        token.IndexOf('\x03') >= 0 || token.IndexOf('\x1b') >= 0 || CaretCancel.IsMatch(token.Trim());

    private static readonly Regex CaretCancel = new(@"^(\^[Cc])+$", RegexOptions.CultureInvariant);
}
