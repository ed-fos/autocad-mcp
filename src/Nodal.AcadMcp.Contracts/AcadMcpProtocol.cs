namespace Nodal.AcadMcp.Contracts;

/// <summary>Wire contract shared by the in-process add-in and the out-of-process MCP server.</summary>
public static class AcadMcpProtocol
{
    /// <summary>
    /// Starting port. High enough to be outside the registered range and unlikely to collide.
    /// It is only a starting point: if it is already taken — a second AutoCAD, or anything else
    /// holding it — the bridge binds an ephemeral port instead and advertises the real one in
    /// its instance file, so nothing depends on this number being free.
    /// </summary>
    public const int DefaultPort = 48885;

    public const string Host = "127.0.0.1";

    public static string BaseUrl(int port) => $"http://{Host}:{port}/";

    /// <summary>POST {tool}, body = JSON args. GET _tools lists the surface; GET _ping probes liveness.</summary>
    public const string ListRoute = "_tools";
    public const string PingRoute = "_ping";

    /// <summary>
    /// Per-session shared secret, required on every request. The add-in mints one at bridge
    /// start and writes it to a per-user file; the server reads that file and echoes it back.
    /// Without this, any local process — or any web page the user visits, via a CORS-simple
    /// POST — could reach run_csharp. See SECURITY.md.
    /// </summary>
    public const string TokenHeader = "X-Nodal-Token";

    /// <summary>
    /// Directory holding one descriptor per live AutoCAD instance, named by process id.
    /// Under LOCALAPPDATA, which is already per-user on Windows, so the token is not
    /// readable by other accounts.
    /// </summary>
    public static string InstanceDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nodal.AcadMcp", "instances");

    public static string InstanceFile(int pid) =>
        System.IO.Path.Combine(InstanceDirectory, $"{pid}.json");
}

/// <summary>
/// How much of a tool's effect Ctrl+Z takes back. This is a promise made to the model, so it
/// has to be true: a model that believes a call is one keystroke from undone will try it
/// speculatively on a production drawing.
/// </summary>
public static class UndoKind
{
    /// <summary>Reads only; nothing to undo.</summary>
    public const string None = "none";

    /// <summary>Goes through Acad.Write, so the whole call commits as exactly one undo step.</summary>
    public const string SingleStep = "single-step";

    /// <summary>Runs caller-supplied code or a raw command: reversibility is whatever that produces.</summary>
    public const string ToolDefined = "tool-defined";

    /// <summary>Changes state that is not on the undo stack at all (e.g. system variables).</summary>
    public const string NotUndoable = "not-undoable";
}

public sealed record ToolError(string Code, string Message, string? Detail = null);

public sealed record ToolEnvelope
{
    public bool Ok { get; init; }
    public object? Result { get; init; }
    public ToolError? Error { get; init; }

    public static ToolEnvelope Success(object? result) => new() { Ok = true, Result = result };
    public static ToolEnvelope Failure(string code, string message, string? detail = null)
        => new() { Ok = false, Error = new ToolError(code, message, detail) };
}

/// <summary>
/// Undo defaults to SingleStep for mutating tools because that is what Acad.Write gives every
/// typed tool; only the escape hatches have to say otherwise, and they do so explicitly.
/// </summary>
public sealed record ToolDescriptor(string Name, string Description, bool Mutating, string Undo)
{
    public ToolDescriptor(string name, string description, bool mutating)
        : this(name, description, mutating, mutating ? UndoKind.SingleStep : UndoKind.None) { }
}

/// <summary>What the add-in writes to its instance file so the MCP server can find and authenticate to it.</summary>
public sealed record InstanceDescriptor
{
    public int Pid { get; init; }
    public int Port { get; init; }
    public string Token { get; init; } = string.Empty;
    public string StartedUtc { get; init; } = string.Empty;
    public string? Product { get; init; }
}
