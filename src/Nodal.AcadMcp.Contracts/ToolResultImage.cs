using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// Lets a tool hand back a picture the model can actually look at. A result carrying
/// <c>"image": { "mimeType": "image/png", "data": "&lt;base64&gt;" }</c> is split by the MCP server
/// into the JSON text (with the base64 removed, so it is not paid for twice) plus an image content
/// block. A raw-HTTP client still gets the whole envelope, base64 included.
/// </summary>
public static class ToolResultImage
{
    public const string Property = "image";
    public const string Placeholder = "(attached as image content)";

    /// <summary>The shape a tool returns under <see cref="Property"/>.</summary>
    public static object Describe(string mimeType, byte[] bytes) => new { mimeType, data = Convert.ToBase64String(bytes), bytes = bytes.Length };

    /// <summary>
    /// When <paramref name="body"/> is a successful envelope whose result has an image, returns
    /// true with the decoded bytes, its MIME type, and the body with the base64 replaced by a
    /// placeholder. Anything else — a failure, no image, a malformed one — returns false and the
    /// body should be passed through unchanged.
    /// </summary>
    public static bool TrySplit(string body, out string text, out byte[] data, out string mimeType)
    {
        text = body; data = []; mimeType = string.Empty;

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return false; }

        if (root is not JsonObject envelope ||
            envelope["ok"]?.GetValueKind() != JsonValueKind.True ||
            envelope["result"] is not JsonObject result ||
            result[Property] is not JsonObject image ||
            image["data"] is not JsonValue dataValue || dataValue.GetValueKind() != JsonValueKind.String ||
            image["mimeType"] is not JsonValue mimeValue || mimeValue.GetValueKind() != JsonValueKind.String)
            return false;

        var mime = mimeValue.GetValue<string>();
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;

        try { data = Convert.FromBase64String(dataValue.GetValue<string>()); }
        catch (FormatException) { data = []; return false; }
        if (data.Length == 0) return false;

        image["data"] = Placeholder;
        mimeType = mime;
        text = envelope.ToJsonString();
        return true;
    }
}
