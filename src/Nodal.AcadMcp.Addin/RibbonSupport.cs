using System.Runtime.CompilerServices;
using System.Windows.Input;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nodal.AcadMcp.Addin;

/// <summary>
/// Adds a "Nodal MCP" panel to the Add-ins ribbon tab, so the bridge can be opened and closed
/// without typing commands.
///
/// WHY THIS EXISTS AT ALL: the bridge is opt-in per session, which means the user has to do
/// something deliberate every time AutoCAD starts. Making that "click a button" rather than
/// "remember a command name" is what keeps the safe default usable — a security control people
/// route around is not a security control.
///
/// TIMING: the ribbon does not exist yet when Initialize() runs. ComponentManager raises
/// ItemInitialized as the UI comes up, so we subscribe and build on the first raise where the
/// ribbon is actually present, then unsubscribe.
/// </summary>
internal static class RibbonSupport
{
    private const string PanelSourceId = "NODAL_MCP_PANEL";
    private const string PanelTitle = "Nodal MCP";

    private static bool _built;

    /// <summary>
    /// Safe to call from Initialize(). Never throws: a missing or disabled ribbon must not stop
    /// the add-in loading, because the commands still work without it.
    /// </summary>
    internal static void Install()
    {
        try { InstallCore(); }
        catch
        {
            // Ribbon is cosmetic. NODALMCPSTART still works if this fails.
        }
    }

    // Isolated so the Autodesk.Windows type references are only resolved if we get here.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallCore()
    {
        if (ComponentManager.Ribbon is not null)
        {
            Build();
            return;
        }

        ComponentManager.ItemInitialized += OnItemInitialized;
    }

    private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon is null) return;

        ComponentManager.ItemInitialized -= OnItemInitialized;
        try { Build(); } catch { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Build()
    {
        if (_built) return;

        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null) return;

        var tab = FindAddInsTab(ribbon);
        if (tab is null)
        {
            // No Add-ins tab (it can be turned off, and its id/title are localised). Rather than
            // give up, make our own so the buttons are always reachable.
            tab = new RibbonTab { Title = "Nodal", Id = "NODAL_MCP_TAB" };
            ribbon.Tabs.Add(tab);
        }

        // A reload must not stack duplicate panels.
        foreach (var existing in tab.Panels)
        {
            if (existing.Source?.Id == PanelSourceId)
            {
                _built = true;
                return;
            }
        }

        var source = new RibbonPanelSource { Title = PanelTitle, Id = PanelSourceId };
        source.Items.Add(Button("Start", "NODALMCPSTART",
            "Open the MCP bridge for this AutoCAD session.\n\n" +
            "This lets an MCP client drive AutoCAD, including tools that execute arbitrary " +
            "code. It is deliberately not opened automatically."));
        source.Items.Add(Button("Stop", "NODALMCPSTOP",
            "Close the bridge and invalidate this session's token."));
        source.Items.Add(Button("Status", "NODALMCPSTATUS",
            "Report whether the bridge is listening, and on which port."));

        tab.Panels.Add(new RibbonPanel { Source = source });
        _built = true;
    }

    /// <summary>
    /// The Add-ins tab's id and title are both unreliable to hard-code — the id differs between
    /// releases and the title is localised — so match either, loosely.
    /// </summary>
    private static RibbonTab? FindAddInsTab(RibbonControl ribbon)
    {
        foreach (var tab in ribbon.Tabs)
        {
            var id = tab.Id ?? string.Empty;
            var title = (tab.Title ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

            if (id.IndexOf("ADDINS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.Equals("AddIns", StringComparison.OrdinalIgnoreCase))
                return tab;
        }
        return null;
    }

    private static RibbonButton Button(string text, string command, string tooltip) => new()
    {
        Text = text,
        ShowText = true,
        ShowImage = false,
        Size = RibbonItemSize.Large,
        Orientation = System.Windows.Controls.Orientation.Vertical,
        // Leading _ and trailing space: locale-independent, and the space submits the command.
        CommandParameter = "_" + command + " ",
        CommandHandler = CommandRelay.Instance,
        ToolTip = new RibbonToolTip { Title = text, Content = tooltip },
    };

    /// <summary>Sends the button's command to the command line, exactly as if typed.</summary>
    private sealed class CommandRelay : ICommand
    {
        internal static readonly CommandRelay Instance = new();

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            var macro = parameter as string;
            if (string.IsNullOrWhiteSpace(macro)) return;

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null) return;

            doc.SendStringToExecute(macro, true, false, true);
        }
    }
}
