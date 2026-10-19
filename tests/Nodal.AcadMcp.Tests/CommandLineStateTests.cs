using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// run_lisp answered a plain success after (command "_.-INSERT" ...) ran out of inputs, while
/// -INSERT sat at its prompt and every later call answered 'busy'. These pin what the reply now
/// says about the command line after the expression returns.
/// </summary>
public class CommandLineStateTests
{
    [Fact]
    public void An_idle_command_line_adds_nothing_to_the_reply() =>
        Assert.Null(CommandLineState.FromSystemVariables(0, ""));

    [Fact]
    public void A_waiting_command_is_named_and_points_at_cancel_command()
    {
        var state = CommandLineState.FromSystemVariables(1, "-INSERT");

        Assert.NotNull(state);
        Assert.Equal(CommandLineState.Pending, state.State);
        Assert.Equal("-INSERT", state.PendingCommand);
        Assert.Equal(1, state.CmdActive);
        Assert.StartsWith("-INSERT is still waiting", state.Hint);
        Assert.Contains("cancel_command", state.Hint);
    }

    [Fact]
    public void A_transparent_command_keeps_both_names()
    {
        var state = CommandLineState.FromSystemVariables(3, "-INSERT'ZOOM");

        Assert.Equal("-INSERT'ZOOM", state?.PendingCommand);
        Assert.Equal(3, state?.CmdActive);
    }

    [Fact]
    public void Active_without_names_is_still_reported()
    {
        // CMDACTIVE can be non-zero with an empty CMDNAMES (an ObjectARX command, say).
        var state = CommandLineState.FromSystemVariables(64, "  ");

        Assert.Equal(CommandLineState.Pending, state?.State);
        Assert.Null(state?.PendingCommand);
        Assert.StartsWith("A command is still waiting", state?.Hint);
    }

    [Fact]
    public void A_dialog_is_not_sent_to_cancel_command()
    {
        // Ctrl+C on the command line does not close a modal dialog.
        var state = CommandLineState.FromSystemVariables(9, "-INSERT");

        Assert.Equal(CommandLineState.Pending, state?.State);
        Assert.Contains("dialog", state?.Hint);
        Assert.Contains("cannot clear it", state?.Hint);
    }

    [Fact]
    public void A_read_that_timed_out_is_unknown_not_idle()
    {
        var state = CommandLineState.NotRead(1000);

        Assert.Equal(CommandLineState.Unknown, state.State);
        Assert.Null(state.PendingCommand);
        Assert.Null(state.CmdActive);
        Assert.Contains("1000 ms", state.Hint);
        Assert.Contains("cancel_command", state.Hint);
    }
}
