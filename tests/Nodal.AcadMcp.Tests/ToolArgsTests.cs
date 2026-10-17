using System.Diagnostics;
using System.Text.Json;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// Argument extraction is the first thing every tool call does, so a wrong-typed argument has to
/// come back as a fast, specific bad_argument — never a hang, an internal_error, or (worst) a
/// silent fallback to the default that runs the tool on something the caller did not send.
/// </summary>
public class ToolArgsTests
{
    /// <summary>
    /// What Windows PowerShell's ConvertTo-Json emits for @{ code = (Get-Content -Raw f.cs) }:
    /// the string's ETS note properties come along, so 'code' is an object, not a string.
    /// </summary>
    private const string PowerShellBody = """
        {
            "code":  {
                         "value":  "return 1;\r\n",
                         "PSPath":  "C:\\Temp\\probe.cs",
                         "PSParentPath":  "C:\\Temp",
                         "PSChildName":  "probe.cs",
                         "PSDrive":  { "Name": "C", "Root": "C:\\" },
                         "PSProvider":  { "Name": "FileSystem" },
                         "ReadCount":  1
                     },
            "write":  false
        }
        """;

    private static JsonElement Body(string json)
    {
        Assert.Null(ToolArgs.TryParseBody(json, out var args));
        return args;
    }

    // ------------------------------------------------------------------ the PowerShell case

    [Fact]
    public void An_object_where_a_string_is_required_fails_fast_with_bad_argument()
    {
        var args = Body(PowerShellBody);

        var watch = Stopwatch.StartNew();
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(args, "code"));
        watch.Stop();

        Assert.Equal("bad_argument", ToolArgumentException.Code);
        Assert.Equal("Argument 'code' must be a string; got an object.", ex.Message);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_PowerShell_decorated_string_gets_a_hint_naming_the_fix()
    {
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(Body(PowerShellBody), "code"));

        Assert.NotNull(ex.Detail);
        Assert.Contains("PowerShell", ex.Detail);
        Assert.Contains("[IO.File]::ReadAllText", ex.Detail);
    }

    [Fact]
    public void An_ordinary_object_gets_no_PowerShell_hint()
    {
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(Body("""{"code":{"a":1}}"""), "code"));
        Assert.Null(ex.Detail);
    }

    [Theory]
    [InlineData("""{"code": 42}""", "the number 42")]
    [InlineData("""{"code": true}""", "the boolean true")]
    [InlineData("""{"code": ["return 1;"]}""", "an array")]
    public void Every_non_string_kind_is_named_in_the_error(string json, string described)
    {
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(Body(json), "code"));
        Assert.Equal($"Argument 'code' must be a string; got {described}.", ex.Message);
    }

    [Fact]
    public void Missing_and_null_both_report_as_required()
    {
        var missing = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(Body("{}"), "code"));
        var isNull = Assert.Throws<ToolArgumentException>(() => ToolArgs.Str(Body("""{"code":null}"""), "code"));

        Assert.Equal("Argument 'code' is required and must be a string.", missing.Message);
        Assert.Equal(missing.Message, isNull.Message);
    }

    [Fact]
    public void A_plain_string_is_returned_as_is()
    {
        Assert.Equal("return 1;", ToolArgs.Str(Body("""{"code":"return 1;"}"""), "code"));
    }

    // ------------------------------------------------------------------ optional arguments

    [Fact]
    public void A_wrong_typed_optional_string_is_an_error_not_the_default()
    {
        // Before: a layer sent as an object silently became the fallback layer.
        var args = Body("""{"layer":{"value":"A-Wall","PSPath":"x"}}""");
        Assert.Throws<ToolArgumentException>(() => ToolArgs.StrOpt(args, "layer", "0"));
    }

    [Fact]
    public void Absent_or_null_optionals_take_the_fallback()
    {
        Assert.Equal("0", ToolArgs.StrOpt(Body("{}"), "layer", "0"));
        Assert.Equal("0", ToolArgs.StrOpt(Body("""{"layer":null}"""), "layer", "0"));
        Assert.True(ToolArgs.BoolOpt(Body("{}"), "write", true));
        Assert.Equal(7, ToolArgs.IntOpt(Body("{}"), "limit", 7));
        Assert.Equal(1.5, ToolArgs.DblOpt(Body("{}"), "margin", 1.5));
        Assert.Empty(ToolArgs.StrArrayOpt(Body("{}"), "usings"));
    }

    [Fact]
    public void A_string_false_does_not_silently_mean_write_true()
    {
        // run_csharp defaults write to true; "false" as a string used to fall back to it and commit.
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.BoolOpt(Body("""{"write":"false"}"""), "write", true));
        Assert.Equal("Argument 'write' must be true or false; got a string.", ex.Message);
    }

    [Fact]
    public void A_number_sent_as_a_string_is_an_error()
    {
        Assert.Throws<ToolArgumentException>(() => ToolArgs.DblOpt(Body("""{"margin":"1.1"}"""), "margin", 1.0));
        Assert.Throws<ToolArgumentException>(() => ToolArgs.Dbl(Body("""{"radius":"5"}"""), "radius"));
    }

    // ------------------------------------------------------------------ integers

    [Fact]
    public void An_integral_double_is_accepted_as_an_integer()
    {
        // Python and JavaScript JSON writers emit 50.0 for a float that holds a whole count.
        Assert.Equal(50, ToolArgs.IntOpt(Body("""{"limit":50.0}"""), "limit", 7));
        Assert.Equal(3, ToolArgs.Int(Body("""{"index":3}"""), "index"));
    }

    [Fact]
    public void A_fractional_number_is_not_rounded_into_an_integer()
    {
        // Int used to call GetInt32 directly, which threw FormatException -> internal_error.
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Int(Body("""{"index":2.5}"""), "index"));
        Assert.Equal("Argument 'index' must be an integer; got the number 2.5.", ex.Message);
        Assert.Throws<ToolArgumentException>(() => ToolArgs.IntOpt(Body("""{"limit":2.5}"""), "limit", 7));
    }

    [Fact]
    public void Integer_arrays_check_every_item()
    {
        Assert.Equal(new[] { 1, 2, 256 }, ToolArgs.Integers(Body("""{"c":[1,2,256.0]}""").GetProperty("c"), "colors"));

        var ex = Assert.Throws<ToolArgumentException>(
            () => ToolArgs.Integers(Body("""{"c":[1,"2"]}""").GetProperty("c"), "colors"));
        Assert.Equal("Argument 'colors[1]' must be an integer; got a string.", ex.Message);
    }

    // ------------------------------------------------------------------ arrays and points

    [Fact]
    public void A_non_string_item_in_a_string_array_is_reported_by_index()
    {
        // Before: wrong-typed items were dropped, so a handle list could shrink silently.
        var args = Body("""{"handles":["1A2", {"value":"1A3","PSPath":"x"}]}""");

        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.StrArray(args, "handles"));
        Assert.Equal("Argument 'handles[1]' must be a string; got an object.", ex.Message);
        Assert.NotNull(ex.Detail);
    }

    [Fact]
    public void A_string_array_round_trips()
    {
        Assert.Equal(new[] { "1A2", "1A3" }, ToolArgs.StrArray(Body("""{"handles":["1A2","1A3"]}"""), "handles"));
    }

    [Fact]
    public void A_point_with_a_non_number_coordinate_is_an_error_not_a_shorter_point()
    {
        // Before: [10, "20", 0] dropped the string and became the point (10, 0, 0).
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Coords(Body("""{"p":[10,"20",0]}"""), "p"));
        Assert.Equal("Argument 'p[1]' must be a number; got a string.", ex.Message);
    }

    [Theory]
    [InlineData("""{"p":[1,2]}""", 2)]
    [InlineData("""{"p":[1,2,3]}""", 3)]
    public void Points_take_two_or_three_numbers(string json, int count)
    {
        Assert.Equal(count, ToolArgs.Coords(Body(json), "p").Length);
    }

    [Theory]
    [InlineData("""{"p":[1]}""", "Argument 'p' must have 2 or 3 numbers; got 1.")]
    [InlineData("""{"p":[1,2,3,4]}""", "Argument 'p' must have 2 or 3 numbers; got 4.")]
    [InlineData("""{"p":"1,2"}""", "Argument 'p' must be an array like [x, y] or [x, y, z]; got a string.")]
    public void Malformed_points_say_what_is_wrong(string json, string message)
    {
        var ex = Assert.Throws<ToolArgumentException>(() => ToolArgs.Coords(Body(json), "p"));
        Assert.Equal(message, ex.Message);
    }

    // ------------------------------------------------------------------ the body itself

    [Fact]
    public void An_empty_body_means_no_arguments()
    {
        Assert.Null(ToolArgs.TryParseBody("", out var args));
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
    }

    [Theory]
    [InlineData("[1,2]", "an array")]
    [InlineData("\"return 1;\"", "a string")]
    public void A_body_that_is_not_an_object_is_rejected(string body, string described)
    {
        // Otherwise every tool would see "no arguments" and run on its defaults.
        var error = ToolArgs.TryParseBody(body, out _);

        Assert.NotNull(error);
        Assert.Equal("bad_json", error.Code);
        Assert.Equal($"Request body must be a JSON object of tool arguments; got {described}.", error.Message);
    }

    [Fact]
    public void Invalid_json_is_rejected_with_the_parser_message()
    {
        var error = ToolArgs.TryParseBody("{\"code\":", out _);

        Assert.NotNull(error);
        Assert.Equal("bad_json", error.Code);
        Assert.False(string.IsNullOrEmpty(error.Detail));
    }

    [Fact]
    public void Parsed_arguments_outlive_the_parse()
    {
        // TryParseBody disposes its JsonDocument; the returned element must be a clone.
        Assert.Null(ToolArgs.TryParseBody("""{"code":"return 1;"}""", out var args));
        GC.Collect();
        Assert.Equal("return 1;", ToolArgs.Str(args, "code"));
    }
}
