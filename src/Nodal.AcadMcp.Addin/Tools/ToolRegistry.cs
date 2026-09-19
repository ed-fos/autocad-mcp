namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>Single place every tool module is wired in from.</summary>
internal static class ToolRegistry
{
    private static bool _registered;

    internal static void RegisterAll()
    {
        if (_registered) return;
        _registered = true;

        // --- plain AutoCAD ---------------------------------------------------
        SessionTools.Register();
        EntityTools.Register();
        BlockTools.Register();
        AnnotationTools.Register();
        LayoutTools.Register();
        EscapeTools.Register();
        SolidTools.Register();
        CSharpBridge.Register();

        // --- AutoCAD Architecture -------------------------------------------
        // These self-gate: they register unconditionally so they always appear in
        // the tool list, and return "aca_unavailable" when ACA is not loaded.
        AcaTools.Register();
        AcaOpeningTools.Register();
        AcaSpaceTools.Register();
        AcaSlabRoofTools.Register();
        AcaMassTools.Register();
        AcaGridStructureTools.Register();
        AcaAssemblyTools.Register();
        AcaSectionTools.Register();

        // Must come last: replaces EscapeTools' run_lisp with the [LispFunction]
        // callback implementation. Application.Invoke cannot reach AutoLISP at all —
        // it only dispatches to ObjectARX functions registered via acedDefun.
        LispBridge.RegisterOverride();
    }
}
