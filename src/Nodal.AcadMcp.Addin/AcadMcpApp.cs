using Autodesk.AutoCAD.Runtime;
using Nodal.AcadMcp.Addin;
using Nodal.AcadMcp.Addin.Tools;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadMcpApp))]
[assembly: CommandClass(typeof(AcadMcpApp))]

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Add-in entry point. Loaded at AutoCAD startup by the Nodal.AcadMcp bundle, or by NETLOAD.
///
/// LOADING IS NOT LISTENING, and the distinction is the whole design:
///
///   * The assembly loads at startup so its commands and ribbon exist. That costs nothing
///     security-wise — Initialize() opens no socket and accepts no input.
///   * The BRIDGE, which is the arbitrary-code-execution endpoint (run_csharp, run_lisp), is
///     opt-in per session. Nothing listens until the user clicks Start on the ribbon or types
///     NODALMCPSTART.
///
/// An earlier revision tried to enforce this with LoadOnAutoCADStartup="false", copying
/// Autodesk's own MCP bundle. That does not work here: with the module unloaded AutoCAD has no
/// idea NODALMCPSTART exists, so the command that is supposed to demand-load it is itself
/// unknown, and a clean install is dead on arrival. Loading the module and withholding the
/// socket gets the same safety with none of that.
/// </summary>
public sealed class AcadMcpApp : IExtensionApplication
{
    public void Initialize()
    {
        MainThread.Start();
        ToolRegistry.RegisterAll();
        RibbonSupport.Install();
    }

    public void Terminate()
    {
        HttpBridge.Stop();
        MainThread.Stop();
    }

    [CommandMethod("NODALMCPSTART")]
    public void StartCommand()
    {
        if (HttpBridge.IsRunning)
        {
            Write($"\nNodal AcadMcp: already listening on port {HttpBridge.Port}.\n");
            return;
        }

        try
        {
            HttpBridge.Start(AcadMcpProtocol.DefaultPort);

            var note = HttpBridge.Port == AcadMcpProtocol.DefaultPort
                ? string.Empty
                : $"\n  (port {AcadMcpProtocol.DefaultPort} was taken, so this session took {HttpBridge.Port};" +
                  " the MCP server discovers it automatically.)";

            Write($"\nNodal AcadMcp listening on {AcadMcpProtocol.BaseUrl(HttpBridge.Port)} " +
                  $"({Router.List().Count} tools).{note}\n" +
                  "  This session can now be driven by an MCP client, including run_csharp and\n" +
                  "  run_lisp, which execute arbitrary code. NODALMCPSTOP closes it.\n" +
                  ConnectionNote());
        }
        catch (System.Exception ex)
        {
            Write($"\nNodal AcadMcp failed to start: {ex.Message}\n");
        }
    }

    [CommandMethod("NODALMCPSTOP")]
    public void StopCommand()
    {
        if (!HttpBridge.IsRunning)
        {
            Write("\nNodal AcadMcp: not running.\n");
            return;
        }

        HttpBridge.Stop();
        Write("\nNodal AcadMcp stopped. The session token is now invalid.\n");
    }

    [CommandMethod("NODALMCPSTATUS")]
    public void StatusCommand()
    {
        Write(HttpBridge.IsRunning
            ? $"\nNodal AcadMcp: listening on port {HttpBridge.Port}, " +
              $"{Router.List().Count} tools registered, pid {Environment.ProcessId}.\n" + ConnectionNote()
            : "\nNodal AcadMcp: stopped. NODALMCPSTART opens the bridge for this session.\n");
    }

    /// <summary>
    /// What people paste into their agent is this command-line text, usually just the URL. A bare
    /// URL answers 403 without the token, and in the drawing sessions that sent agents off to read
    /// the README, write their own HTTP clients, and once to drive AutoCAD over COM until it
    /// crashed. So the text says how to connect properly.
    /// </summary>
    private static string ConnectionNote()
    {
        var server = AcadMcpProtocol.DefaultServerPath;
        var exe = System.IO.File.Exists(server) ? $"\"{server}\"" : "Nodal.AcadMcp.Server.exe (in the release zip's server folder)";
        return "  To connect an agent:\n" +
               $"    MCP client: register {exe} as a stdio server.\n" +
               $"    No MCP tools: {exe} call <tool> @args.json (help for usage), or POST\n" +
               $"    {AcadMcpProtocol.BaseUrl(HttpBridge.Port)}<tool> with header {AcadMcpProtocol.TokenHeader} from\n" +
               $"    {AcadMcpProtocol.InstanceFile(Environment.ProcessId)}.\n" +
               $"    GET {AgentGuide.Route} has the working rules. Never drive AutoCAD over COM.\n";
    }

    /// <summary>
    /// The Editor is null before any document is open, which is exactly when a startup-time
    /// failure would need reporting — so fall back to a dialog rather than dropping the message.
    /// </summary>
    private static void Write(string message)
    {
        var editor = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is not null) editor.WriteMessage(message);
        else AcApp.ShowAlertDialog(message.Trim());
    }
}
