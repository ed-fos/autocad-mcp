namespace Nodal.AcadMcp.Contracts;

/// <summary>
/// One parsed command-line invocation of the server exe. <see cref="Error"/> is set instead of
/// throwing, so the caller can print it with the usage text.
/// </summary>
public sealed record BridgeCommand
{
    public string Verb { get; init; } = string.Empty;
    public string? Tool { get; init; }
    /// <summary>Tool names for <c>tools</c>: empty lists every tool in one line each.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
    /// <summary>Inline JSON arguments for <c>call</c>.</summary>
    public string? Arguments { get; init; }
    /// <summary>A file holding the JSON arguments (given as <c>@path</c>).</summary>
    public string? ArgumentsFile { get; init; }
    /// <summary>The JSON arguments come from standard input (given as <c>-</c>).</summary>
    public bool ArgumentsFromStdin { get; init; }
    public int TimeoutSeconds { get; init; } = BridgeCli.DefaultTimeoutSeconds;
    public string? Error { get; init; }
}

/// <summary>
/// The server exe's command-line mode, for an agent that has no MCP tools. It uses the same
/// instance discovery and token as the MCP path, so nobody has to hand-build an HTTP client: every
/// session that did hit the same traps (a 403 without the token, PowerShell serialising
/// Get-Content's hidden properties, UTF-8 bodies). With no arguments, or anything that is not one
/// of these verbs, the exe starts the MCP server as before.
/// </summary>
public static class BridgeCli
{
    public const int DefaultTimeoutSeconds = 180;
    public const int MaxTimeoutSeconds = 3600;

    public const string Usage = """
        Nodal.AcadMcp.Server.exe call <tool> [<json> | @<file> | -] [--timeout <seconds>]
            Call one tool in the running AutoCAD and print its JSON reply. Arguments are a JSON
            object: inline, from a UTF-8 file (@args.json), or from standard input (-). Default {}.
            From Windows PowerShell use @file: it strips the quotes out of inline JSON.
            Exit code 0 when the tool worked, 1 when it reported an error, 2 when AutoCAD could
            not be reached or the command line is wrong.
        Nodal.AcadMcp.Server.exe tools [<name> ...]
            List every tool, one line each; with names, print those tools' full descriptions
            and input schemas.
        Nodal.AcadMcp.Server.exe ping
            Check that AutoCAD's bridge is up and which drawing is active.
        Nodal.AcadMcp.Server.exe guide
            Print the working rules for driving AutoCAD through this bridge.
        With no arguments it runs as an MCP server over stdio.
        """;

    private static readonly string[] Verbs = ["call", "tools", "ping", "guide", "help"];

    /// <summary>Null when the command line is not a command-line invocation (run the MCP server).</summary>
    public static BridgeCommand? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var verb = args[0].ToLowerInvariant() switch
        {
            "--help" or "-h" or "/?" => "help",
            var v => v,
        };
        if (!Verbs.Contains(verb)) return null;

        var positional = new List<string>();
        var timeout = DefaultTimeoutSeconds;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out timeout) || timeout < 1 || timeout > MaxTimeoutSeconds)
                    return Failed(verb, $"--timeout needs a whole number of seconds from 1 to {MaxTimeoutSeconds}.");
                i++;
            }
            else positional.Add(args[i]);
        }

        switch (verb)
        {
            case "call":
                if (positional.Count == 0 || positional[0].StartsWith('-') || positional[0].StartsWith('@'))
                    return Failed(verb, "call needs a tool name, e.g. call acad_status.");
                if (positional.Count > 2)
                    return Failed(verb, "Pass the arguments as one JSON object. From Windows PowerShell, put it in a file and pass @file.");
                var source = positional.Count == 2 ? positional[1] : null;
                return new BridgeCommand
                {
                    Verb = verb,
                    Tool = positional[0],
                    TimeoutSeconds = timeout,
                    ArgumentsFromStdin = source == "-",
                    ArgumentsFile = source is { Length: > 1 } && source[0] == '@' ? source[1..] : null,
                    Arguments = source is null || source == "-" || source[0] == '@' ? null : source,
                    Error = source == "@" ? "@ needs a file path, e.g. @args.json." : null,
                };
            case "tools":
                return new BridgeCommand { Verb = verb, Names = positional, TimeoutSeconds = timeout };
            default:
                return positional.Count > 0
                    ? Failed(verb, $"{verb} takes no arguments.")
                    : new BridgeCommand { Verb = verb, TimeoutSeconds = timeout };
        }
    }

    /// <summary>The first sentence of a tool description, for the one-line listing.</summary>
    public static string Summary(string description, int maxLength = 160)
    {
        var text = description.ReplaceLineEndings(" ").Trim();
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end >= 0) text = text[..(end + 1)];
        return text.Length <= maxLength ? text : text[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static BridgeCommand Failed(string verb, string error) => new() { Verb = verb, Error = error };
}
