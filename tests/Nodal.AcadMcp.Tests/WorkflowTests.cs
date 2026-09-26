using System.Text.Json;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

public class WorkflowTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static readonly JsonElement Schema = ToolSchema.Object(["point", "operation"],
        ("point", ToolSchema.Point()), ("operation", ToolSchema.Choice("union", "subtract")),
        ("handles", ToolSchema.Array(ToolSchema.Text(), 1, 2)), ("minimum", ToolSchema.Number(0, 10)));

    [Theory]
    [InlineData("{\"point\":[0,1],\"operation\":\"union\"}")]
    [InlineData("{\"point\":[0,1,\"2\"],\"operation\":\"union\"}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"typo\"}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"union\",\"overwrite\":true}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"union\",\"operation\":\"subtract\"}")]
    [InlineData("{\"point\":[0,1,1e400],\"operation\":\"union\"}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"union\",\"handles\":[\"1\",\"2\",\"3\"]}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"union\",\"handles\":[\" \" ]}")]
    [InlineData("{\"point\":[0,1,2],\"operation\":\"union\",\"minimum\":-1}")]
    public void Invalid_workflow_arguments_fail_before_native_execution(string json) => Assert.NotNull(ToolSchema.Validate(Json(json), Schema));

    [Fact]
    public void Equivalent_escaped_enum_text_is_accepted() => Assert.Null(ToolSchema.Validate(Json("{\"point\":[0,1,2],\"operation\":\"un\\u0069on\"}"), Schema));

    [Fact]
    public void Receipts_ignore_object_property_order_but_detect_changed_coordinates()
    {
        Assert.Equal(WorkflowMath.Fingerprint(Json("{\"name\":\"a\",\"p\":[1,2,3]}")), WorkflowMath.Fingerprint(Json("{\"p\":[1,2,3],\"name\":\"a\"}")));
        Assert.NotEqual(WorkflowMath.Fingerprint(Json("{\"p\":[1,2,3]}")), WorkflowMath.Fingerprint(Json("{\"p\":[2,1,3]}")));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(45)]
    [InlineData(-45)]
    [InlineData(-90)]
    public void Joint_planes_coincide_in_world_coordinates(double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var dx = Math.Cos(angle); var dy = Math.Sin(angle);
        var incoming = WorkflowMath.MiterNormal(dx, dy, 1, 0);
        var outgoing = WorkflowMath.MiterNormal(1, 0, dx, dy);
        // End plane of the first member and start plane of the next share one WCS normal.
        Assert.Equal(incoming.Forward, outgoing.Forward * dx - outgoing.Left * dy, 10);
        Assert.Equal(incoming.Left, outgoing.Forward * dy + outgoing.Left * dx, 10);
    }

    [Fact]
    public void Reversals_and_degenerate_segments_fail()
    {
        Assert.Throws<ArgumentException>(() => WorkflowMath.MiterNormal(1, 0, -1, 0));
        Assert.Throws<ArgumentException>(() => WorkflowMath.MiterNormal(0, 0, 1, 0));
    }

    [Fact]
    public void Millimeters_are_converted_once_into_inches()
    {
        Assert.Equal(120 / 25.4, 120 * WorkflowMath.MillimetersPerUnit("Millimeters") / WorkflowMath.MillimetersPerUnit("Inches"), 10);
        Assert.Throws<ArgumentException>(() => WorkflowMath.MillimetersPerUnit("Unitless"));
    }

    [Fact]
    public void Schema_survives_tool_discovery_serialization()
    {
        var tool = new ToolDescriptor("test", "test", true) { InputSchema = Schema };
        var copy = JsonSerializer.SerializeToElement(tool, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(Schema.GetRawText(), copy.GetProperty("inputSchema").GetRawText());
    }
}
