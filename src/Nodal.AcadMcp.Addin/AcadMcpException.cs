namespace Nodal.AcadMcp.Addin;

/// <summary>An error we can report to the MCP client verbatim, with a stable machine-readable code.</summary>
internal sealed class AcadMcpException(string code, string message, string? detail = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}
