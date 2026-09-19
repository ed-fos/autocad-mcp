using System.Text.Json;
using Nodal.AcadMcp.Contracts;

namespace Nodal.AcadMcp.Addin;

internal delegate Task<object?> ToolHandler(JsonElement args);

/// <summary>Name -> handler dispatch table. Tool modules register into this at load.</summary>
internal static class Router
{
    private static readonly Dictionary<string, (ToolDescriptor Desc, ToolHandler Handler)> Tools =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool whose undo behaviour is the default: one commit through Acad.Write, so
    /// Ctrl+Z takes the whole call back. Every typed tool is in this category.
    /// </summary>
    internal static void Register(string name, string description, bool mutating, ToolHandler handler) =>
        Tools[name] = (new ToolDescriptor(name, description, mutating), handler);

    /// <summary>
    /// Registers a tool that has to state its own undo behaviour — the escape hatches, which run
    /// caller-supplied code or write state that is not on the undo stack at all. The MCP server
    /// turns this into the reversibility sentence the model reads, so it must be honest.
    /// </summary>
    internal static void Register(string name, string description, bool mutating, string undo, ToolHandler handler) =>
        Tools[name] = (new ToolDescriptor(name, description, mutating, undo), handler);

    internal static IReadOnlyList<ToolDescriptor> List() =>
        Tools.Values.Select(t => t.Desc).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

    internal static async Task<ToolEnvelope> Invoke(string name, JsonElement args)
    {
        if (!Tools.TryGetValue(name, out var entry))
            return ToolEnvelope.Failure("unknown_tool", $"No tool named '{name}'.",
                "Call GET _tools for the available surface.");

        try
        {
            var result = await entry.Handler(args).ConfigureAwait(false);
            return ToolEnvelope.Success(result);
        }
        catch (AcadMcpException ex)
        {
            return ToolEnvelope.Failure(ex.Code, ex.Message, ex.Detail);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            // AutoCAD's own exception type carries an ErrorStatus worth surfacing verbatim.
            return ToolEnvelope.Failure("autocad_error", ex.Message, ex.ErrorStatus.ToString());
        }
        catch (Exception ex)
        {
            return ToolEnvelope.Failure("internal_error", ex.Message, ex.GetType().FullName);
        }
    }
}
