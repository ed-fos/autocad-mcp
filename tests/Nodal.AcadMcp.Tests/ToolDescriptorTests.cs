using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// The undo classification is a promise made to a model that will act on it. A tool advertised
/// as one-Ctrl+Z gets tried speculatively on a live production drawing, so the default must be
/// conservative and the escape hatches must be unable to inherit the reassuring answer.
/// </summary>
public class ToolDescriptorTests
{
    [Fact]
    public void A_read_only_tool_defaults_to_nothing_to_undo()
    {
        var descriptor = new ToolDescriptor("list_layers", "Reads layers.", mutating: false);
        Assert.Equal(UndoKind.None, descriptor.Undo);
    }

    [Fact]
    public void A_mutating_tool_defaults_to_one_undo_step()
    {
        // Every typed tool goes through Acad.Write, which commits exactly once.
        var descriptor = new ToolDescriptor("create_circle", "Draws a circle.", mutating: true);
        Assert.Equal(UndoKind.SingleStep, descriptor.Undo);
    }

    [Fact]
    public void An_escape_hatch_can_state_that_it_is_not_one_undo_step()
    {
        var descriptor = new ToolDescriptor(
            "run_lisp", "Evaluates AutoLISP.", true, UndoKind.ToolDefined);

        Assert.Equal(UndoKind.ToolDefined, descriptor.Undo);
        Assert.NotEqual(UndoKind.SingleStep, descriptor.Undo);
    }

    [Fact]
    public void A_profile_write_can_state_that_it_is_not_undoable_at_all()
    {
        var descriptor = new ToolDescriptor(
            "set_system_variable", "Writes a sysvar.", true, UndoKind.NotUndoable);

        Assert.Equal(UndoKind.NotUndoable, descriptor.Undo);
    }

    [Fact]
    public void The_undo_kinds_are_distinct()
    {
        var kinds = new[] { UndoKind.None, UndoKind.SingleStep, UndoKind.ToolDefined, UndoKind.NotUndoable };
        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }

    [Fact]
    public void Instance_files_live_under_the_per_user_profile()
    {
        // Not ProgramData or Temp: the file carries the session token, so it must land somewhere
        // another Windows account cannot read.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, AcadMcpProtocol.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("1234.json", AcadMcpProtocol.InstanceFile(1234));
    }
}
