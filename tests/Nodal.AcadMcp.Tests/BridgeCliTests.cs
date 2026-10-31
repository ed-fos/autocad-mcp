using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

public class BridgeCliTests
{
    [Theory]
    [InlineData]
    [InlineData("--Logging:LogLevel:Default=Debug")]
    [InlineData("serve")]
    public void Anything_but_a_verb_starts_the_mcp_server(params string[] args) => Assert.Null(BridgeCli.Parse(args));

    [Fact]
    public void A_call_without_arguments_sends_an_empty_object()
    {
        var cli = BridgeCli.Parse(["call", "acad_status"])!;
        Assert.Null(cli.Error);
        Assert.Equal("acad_status", cli.Tool);
        Assert.Null(cli.Arguments);
        Assert.Null(cli.ArgumentsFile);
        Assert.False(cli.ArgumentsFromStdin);
        Assert.Equal(BridgeCli.DefaultTimeoutSeconds, cli.TimeoutSeconds);
    }

    [Fact]
    public void Arguments_come_inline_from_a_file_or_from_stdin()
    {
        Assert.Equal("{\"name\":\"CLAYER\"}", BridgeCli.Parse(["call", "get_system_variable", "{\"name\":\"CLAYER\"}"])!.Arguments);
        Assert.Equal(@"C:\work\args.json", BridgeCli.Parse(["call", "run_csharp", @"@C:\work\args.json"])!.ArgumentsFile);
        Assert.True(BridgeCli.Parse(["call", "run_csharp", "-"])!.ArgumentsFromStdin);
    }

    [Fact]
    public void Verbs_and_timeout_are_case_insensitive_and_the_timeout_can_go_anywhere()
    {
        var cli = BridgeCli.Parse(["CALL", "--TIMEOUT", "600", "plot_layouts_to_pdf", "@a.json"])!;
        Assert.Null(cli.Error);
        Assert.Equal("plot_layouts_to_pdf", cli.Tool);
        Assert.Equal("a.json", cli.ArgumentsFile);
        Assert.Equal(600, cli.TimeoutSeconds);
    }

    [Theory]
    [InlineData("call")]
    [InlineData("call", "@args.json")]
    [InlineData("call", "run_lisp", "@")]
    [InlineData("call", "run_lisp", "{\"expression\":", "\"(+ 1 2)\"}")]
    [InlineData("call", "acad_status", "--timeout")]
    [InlineData("call", "acad_status", "--timeout", "0")]
    [InlineData("call", "acad_status", "--timeout", "soon")]
    [InlineData("ping", "extra")]
    [InlineData("guide", "extra")]
    public void Malformed_command_lines_are_reported_not_run(params string[] args)
    {
        var cli = BridgeCli.Parse(args);
        Assert.NotNull(cli);
        Assert.NotNull(cli!.Error);
    }

    [Fact]
    public void Tools_takes_any_number_of_names()
    {
        Assert.Empty(BridgeCli.Parse(["tools"])!.Names);
        Assert.Equal(["create_mleader", "plot_preview"], BridgeCli.Parse(["tools", "create_mleader", "plot_preview"])!.Names);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("help")]
    public void Help_has_its_usual_spellings(string arg) => Assert.Equal("help", BridgeCli.Parse([arg])!.Verb);

    [Theory]
    [InlineData("Create a line. Args: start, end.", "Create a line.")]
    [InlineData("No sentence break here", "No sentence break here")]
    [InlineData("Two\nlines. Then more.", "Two lines.")]
    public void Summaries_keep_the_first_sentence(string description, string expected) =>
        Assert.Equal(expected, BridgeCli.Summary(description));

    [Fact]
    public void Long_summaries_are_cut_with_an_ellipsis()
    {
        var summary = BridgeCli.Summary(new string('x', 400), 40);
        Assert.Equal(40, summary.Length);
        Assert.EndsWith("...", summary);
    }
}
