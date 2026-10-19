using System.Text.Json;
using Nodal.AcadMcp.Contracts;
using Xunit;

namespace Nodal.AcadMcp.Tests;

/// <summary>
/// The server turns a result's "image" into MCP image content. It must only do that for a
/// well-formed success, and must never drop anything else from the reply.
/// </summary>
public class ToolResultImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static string Envelope(object result, bool ok = true) =>
        JsonSerializer.Serialize(new { ok, result }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [Fact]
    public void An_image_result_is_split_into_text_and_bytes()
    {
        var body = Envelope(new { path = "C:\\t\\p.png", width = 10, image = ToolResultImage.Describe("image/png", Png) });

        Assert.True(ToolResultImage.TrySplit(body, out var text, out var data, out var mime));
        Assert.Equal(Png, data);
        Assert.Equal("image/png", mime);

        using var doc = JsonDocument.Parse(text);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("C:\\t\\p.png", result.GetProperty("path").GetString());
        Assert.Equal(10, result.GetProperty("width").GetInt32());
        Assert.Equal(ToolResultImage.Placeholder, result.GetProperty("image").GetProperty("data").GetString());
        Assert.Equal(Png.Length, result.GetProperty("image").GetProperty("bytes").GetInt32());
        Assert.DoesNotContain(Convert.ToBase64String(Png), text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_without_an_image_passes_through()
    {
        var body = Envelope(new { path = "x", image = (object?)null });
        Assert.False(ToolResultImage.TrySplit(body, out var text, out _, out _));
        Assert.Equal(body, text);
    }

    [Theory]
    [InlineData("""{"ok":false,"error":{"code":"x","message":"y"},"result":{"image":{"mimeType":"image/png","data":"iVBORw=="}}}""")]
    [InlineData("""{"ok":true,"result":{"image":{"mimeType":"text/plain","data":"aGVsbG8="}}}""")]
    [InlineData("""{"ok":true,"result":{"image":{"mimeType":"image/png","data":"not base64!"}}}""")]
    [InlineData("""{"ok":true,"result":{"image":{"mimeType":"image/png","data":""}}}""")]
    [InlineData("""{"ok":true,"result":{"image":{"data":"iVBORw=="}}}""")]
    [InlineData("""{"ok":true,"result":{"image":"iVBORw=="}}""")]
    [InlineData("""{"ok":true,"result":[1,2]}""")]
    [InlineData("""not json""")]
    public void Anything_but_a_well_formed_success_image_is_left_alone(string body)
    {
        Assert.False(ToolResultImage.TrySplit(body, out var text, out var data, out _));
        Assert.Equal(body, text);
        Assert.Empty(data);
    }
}
