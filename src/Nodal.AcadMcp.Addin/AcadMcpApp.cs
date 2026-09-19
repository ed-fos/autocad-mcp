using Autodesk.AutoCAD.Runtime;
using Nodal.AcadMcp.Addin;
using Nodal.AcadMcp.Addin.Tools;
using Nodal.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadMcpApp))]
[assembly: CommandClass(typeof(AcadMcpApp))]

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Add-in entry point. Loaded on demand by the Nodal.AcadMcp bundle, or by NETLOAD.
///
/// Initialize deliberately does NOT open the bridge. The bridge is an arbitrary-code-execution
/// endpoint (run_csharp, run_lisp), so it is opt-in per session: the user types NODALMCPSTART.
/// Autodesk takes the same position for its own AutoCAD MCP bundle, which ships
/// LoadOnAutoCADStartup="false". If you want it up automatically, say so explicitly — put
/// NODALMCPSTART in acaddoc.lsp — rather than having every install listen from boot.
/// </summary>
public sealed class AcadMcpApp : IExtensionApplication
{
    public void Initialize()
    {
        MainThread.Start();
        ToolRegistry.RegisterAll();
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
                  "  run_lisp, which execute arbitrary code. NODALMCPSTOP closes it.\n");
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
              $"{Router.List().Count} tools registered, pid {Environment.ProcessId}.\n"
            : "\nNodal AcadMcp: stopped. NODALMCPSTART opens the bridge for this session.\n");
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
