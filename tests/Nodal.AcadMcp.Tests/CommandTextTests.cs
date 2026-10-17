using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// send_command could not clear a stuck -INSERT prompt: caret notation is typed literally, and a
/// real Ctrl+C was followed by the Enter every queued line ends with. These pin the two rules
/// that replaced it: such tokens are refused, and cancel_command's sequence has no Enter.
/// </summary>
public class CommandTextTests
{
    [Theory]
    [InlineData("^C")]
    [InlineData("^C^C")]
    [InlineData("^c^c")]
    [InlineData(" ^C^C ")]
    [InlineData("\u0003")]
    [InlineData("\u0003\u0003")]
    [InlineData("\u001b\u001b")]
    [InlineData("_.LINE\u0003")]
    public void Cancel_keystrokes_are_recognised(string token) =>
        Assert.True(CommandText.LooksLikeCancel(token));

    [Theory]
    [InlineData("_.LINE")]
    [InlineData("ESC")]          // could be a block or layer name; typed as text, as asked
    [InlineData("^")]
    [InlineData("C")]
    [InlineData("a^C")]
    [InlineData("^C^")]
    [InlineData("")]
    [InlineData("0,0")]
    public void Ordinary_input_is_not_a_cancel(string token) =>
        Assert.False(CommandText.LooksLikeCancel(token));

    [Fact]
    public void The_cancel_sequence_is_two_ctrl_c_with_no_enter()
    {
        // An Enter after the cancel reaches the bare prompt, where it repeats the last command.
        Assert.Equal("\u0003\u0003", CommandText.CancelSequence);
        Assert.DoesNotContain('\n', CommandText.CancelSequence);
        Assert.DoesNotContain('\r', CommandText.CancelSequence);
    }
}
